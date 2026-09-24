"""fix(db): preserve Bancho status when deranking local maps

Revision ID: b8e2d4f1a603
Revises: a7d4c9e2f681
Create Date: 2026-09-05 12:00:00.000000

"""

from collections.abc import Mapping, Sequence
import json
from typing import Any

from alembic import op
import sqlalchemy as sa

revision: str = "b8e2d4f1a603"
down_revision: str | Sequence[str] | None = "a7d4c9e2f681"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_RANKED_STATUS_VALUES = {1, 2, 4}
_LEADERBOARD_UPSTREAM_STATUSES = {"RANKED", "APPROVED", "QUALIFIED", "LOVED"}
_PP_UPSTREAM_STATUSES = {"RANKED", "APPROVED"}


def _decode_snapshot(value: Any) -> Mapping[str, Any] | None:
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            return None
    return value if isinstance(value, Mapping) else None


def _snapshot_was_active_local_rank(value: Any) -> bool:
    snapshot = _decode_snapshot(value)
    if snapshot is None:
        return False
    try:
        status = int(snapshot.get("status"))
    except (TypeError, ValueError):
        return False
    return (
        bool(snapshot.get("is_active"))
        and not bool(snapshot.get("force_unranked"))
        and not bool(snapshot.get("blocks_set_policy"))
        and status in _RANKED_STATUS_VALUES
    )


def upgrade() -> None:
    """Convert the former synthetic Pending result into the intended state."""

    connection = op.get_bind()
    set_rows = connection.execute(
        sa.text(
            "SELECT p.beatmapset_id, "
            "(SELECT a.before FROM beatmap_ranking_audits a "
            "WHERE a.scope = 'BEATMAPSET' AND a.beatmapset_id = p.beatmapset_id "
            "AND a.beatmap_id IS NULL ORDER BY a.id DESC LIMIT 1) AS audit_before "
            "FROM beatmapset_ranking_policies p "
            "WHERE p.force_unranked = 1 AND p.status = 'PENDING'"
        )
    ).mappings()
    for row in set_rows:
        if _snapshot_was_active_local_rank(row["audit_before"]):
            connection.execute(
                sa.text(
                    "UPDATE beatmapset_ranking_policies SET is_active = 0, updated_at = CURRENT_TIMESTAMP "
                    "WHERE beatmapset_id = :beatmapset_id"
                ),
                {"beatmapset_id": row["beatmapset_id"]},
            )
        else:
            connection.execute(
                sa.text(
                    "UPDATE beatmapset_ranking_policies "
                    "SET status = 'GRAVEYARD', leaderboard_enabled = 0, pp_enabled = 0, "
                    "updated_at = CURRENT_TIMESTAMP WHERE beatmapset_id = :beatmapset_id"
                ),
                {"beatmapset_id": row["beatmapset_id"]},
            )

    difficulty_rows = connection.execute(
        sa.text(
            "SELECT p.beatmap_id, b.beatmap_status AS upstream_status, b.checksum, "
            "(SELECT a.before FROM beatmap_ranking_audits a "
            "WHERE a.scope = 'BEATMAP' AND a.beatmap_id = p.beatmap_id "
            "ORDER BY a.id DESC LIMIT 1) AS audit_before "
            "FROM beatmap_ranking_policies p JOIN beatmaps b ON b.id = p.beatmap_id "
            "WHERE p.force_unranked = 1 AND p.status = 'PENDING'"
        )
    ).mappings()
    for row in difficulty_rows:
        if _snapshot_was_active_local_rank(row["audit_before"]):
            upstream_status = str(row["upstream_status"])
            connection.execute(
                sa.text(
                    "UPDATE beatmap_ranking_policies SET status = :status, "
                    "leaderboard_enabled = :leaderboard_enabled, pp_enabled = :pp_enabled, "
                    "force_unranked = 0, blocks_set_policy = 1, ranked_checksum = :checksum, "
                    "updated_at = CURRENT_TIMESTAMP WHERE beatmap_id = :beatmap_id"
                ),
                {
                    "beatmap_id": row["beatmap_id"],
                    "status": upstream_status,
                    "leaderboard_enabled": upstream_status in _LEADERBOARD_UPSTREAM_STATUSES,
                    "pp_enabled": upstream_status in _PP_UPSTREAM_STATUSES,
                    "checksum": row["checksum"],
                },
            )
        else:
            connection.execute(
                sa.text(
                    "UPDATE beatmap_ranking_policies "
                    "SET status = 'GRAVEYARD', leaderboard_enabled = 0, pp_enabled = 0, "
                    "updated_at = CURRENT_TIMESTAMP WHERE beatmap_id = :beatmap_id"
                ),
                {"beatmap_id": row["beatmap_id"]},
            )


def downgrade() -> None:
    # The old Pending value mixed two different operator intents, so restoring
    # it would knowingly corrupt the now-recovered Bancho inheritance state.
    pass
