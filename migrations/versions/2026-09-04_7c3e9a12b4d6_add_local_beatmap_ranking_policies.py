"""feat(db): add local beatmap ranking policies

Revision ID: 7c3e9a12b4d6
Revises: 57a4930b6961
Create Date: 2026-09-04 12:00:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "7c3e9a12b4d6"
down_revision: str | Sequence[str] | None = "57a4930b6961"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


beatmap_rank_status = sa.Enum(
    "GRAVEYARD",
    "WIP",
    "PENDING",
    "RANKED",
    "APPROVED",
    "QUALIFIED",
    "LOVED",
    name="beatmaprankstatus",
)
ranking_policy_scope = sa.Enum("BEATMAPSET", "BEATMAP", name="rankingpolicyscope")
ranking_policy_action = sa.Enum(
    "APPLY",
    "CLEAR",
    "INVALIDATE",
    "RESOLVE_EVENT",
    name="rankingpolicyaction",
)
ranking_event_type = sa.Enum("UPSTREAM_REVISION_CHANGED", name="rankingeventtype")

# Existing processed rows deliberately keep ``ranking_eligibility_snapshotted``
# false: their historic aggregate state predates the immutable policy epoch.
# ``scores.ranked`` is the only reliable submission-time eligibility evidence
# on a pending legacy row. It was true only for Ranked/Approved scores or when
# ENABLE_ALL_BEATMAP_PP was enabled (which requires the global leaderboard), so
# both new eligibility flags can safely be reconstructed from it. A false value
# cannot distinguish Loved/Qualified from unranked and remains conservative.
LEGACY_PENDING_SCORE_BACKFILL_SQL = """
UPDATE scores
SET
    leaderboard_eligible = 1,
    ranked_score_eligible = 1,
    ranking_eligibility_snapshotted = 1
WHERE processed = 0
  AND ranked = 1
  AND ranking_eligibility_snapshotted = 0
"""


def upgrade() -> None:
    """Create local ranking policy, audit, and operator inbox tables."""

    op.add_column(
        "lazer_users",
        sa.Column("is_owner", sa.Boolean(), server_default=sa.text("0"), nullable=False),
    )
    op.create_index(op.f("ix_lazer_users_is_owner"), "lazer_users", ["is_owner"], unique=False)
    op.add_column(
        "scores",
        sa.Column("leaderboard_eligible", sa.Boolean(), server_default=sa.text("0"), nullable=False),
    )
    op.add_column(
        "scores",
        sa.Column("ranked_score_eligible", sa.Boolean(), server_default=sa.text("0"), nullable=False),
    )
    op.add_column(
        "scores",
        sa.Column(
            "ranking_eligibility_snapshotted",
            sa.Boolean(),
            server_default=sa.text("0"),
            nullable=False,
        ),
    )
    op.execute(sa.text(LEGACY_PENDING_SCORE_BACKFILL_SQL))
    op.alter_column(
        "scores",
        "ranking_eligibility_snapshotted",
        existing_type=sa.Boolean(),
        server_default=sa.text("1"),
        existing_nullable=False,
    )
    op.add_column(
        "score_tokens",
        sa.Column("beatmap_checksum", sa.String(length=32), nullable=True),
    )
    # Consumed legacy tokens can be reconstructed from their immutable score.
    # Unconsumed tokens cannot: the client may have played an older revision,
    # so accepting them after an upstream update could award PP for the wrong
    # chart. Tokens are ephemeral; force those clients to start a new play.
    op.execute(
        sa.text(
            "UPDATE score_tokens AS token "
            "INNER JOIN scores AS score ON score.id = token.score_id "
            "SET token.beatmap_checksum = score.map_md5 "
            "WHERE token.beatmap_checksum IS NULL"
        )
    )
    op.execute(sa.text("DELETE FROM score_tokens WHERE beatmap_checksum IS NULL"))
    op.alter_column(
        "score_tokens",
        "beatmap_checksum",
        existing_type=sa.String(length=32),
        nullable=False,
    )

    op.create_table(
        "beatmapset_ranking_policies",
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("status", beatmap_rank_status, nullable=False),
        sa.Column("leaderboard_enabled", sa.Boolean(), nullable=False),
        sa.Column("pp_enabled", sa.Boolean(), nullable=False),
        sa.Column("revision_manifest", sa.JSON(), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("created_by_user_id", sa.Integer(), nullable=True),
        sa.Column("updated_by_user_id", sa.Integer(), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["beatmapset_id"], ["beatmapsets.id"], ondelete="CASCADE"),
        sa.ForeignKeyConstraint(["created_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.ForeignKeyConstraint(["updated_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("beatmapset_id"),
    )
    op.create_index(
        op.f("ix_beatmapset_ranking_policies_status"),
        "beatmapset_ranking_policies",
        ["status"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmapset_ranking_policies_is_active"),
        "beatmapset_ranking_policies",
        ["is_active"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmapset_ranking_policies_created_by_user_id"),
        "beatmapset_ranking_policies",
        ["created_by_user_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmapset_ranking_policies_updated_by_user_id"),
        "beatmapset_ranking_policies",
        ["updated_by_user_id"],
        unique=False,
    )

    op.create_table(
        "beatmap_ranking_policies",
        sa.Column("beatmap_id", sa.Integer(), nullable=False),
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("status", beatmap_rank_status, nullable=False),
        sa.Column("leaderboard_enabled", sa.Boolean(), nullable=False),
        sa.Column("pp_enabled", sa.Boolean(), nullable=False),
        sa.Column("blocks_set_policy", sa.Boolean(), nullable=False),
        sa.Column("ranked_checksum", sa.String(length=32), nullable=False),
        sa.Column("is_active", sa.Boolean(), nullable=False),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("invalidated_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("invalidation_reason", sa.Text(), nullable=True),
        sa.Column("created_by_user_id", sa.Integer(), nullable=True),
        sa.Column("updated_by_user_id", sa.Integer(), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["beatmap_id"], ["beatmaps.id"], ondelete="CASCADE"),
        sa.ForeignKeyConstraint(["beatmapset_id"], ["beatmapsets.id"], ondelete="CASCADE"),
        sa.ForeignKeyConstraint(["created_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.ForeignKeyConstraint(["updated_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("beatmap_id"),
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_beatmapset_id"),
        "beatmap_ranking_policies",
        ["beatmapset_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_status"),
        "beatmap_ranking_policies",
        ["status"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_ranked_checksum"),
        "beatmap_ranking_policies",
        ["ranked_checksum"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_blocks_set_policy"),
        "beatmap_ranking_policies",
        ["blocks_set_policy"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_is_active"),
        "beatmap_ranking_policies",
        ["is_active"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_invalidated_at"),
        "beatmap_ranking_policies",
        ["invalidated_at"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_created_by_user_id"),
        "beatmap_ranking_policies",
        ["created_by_user_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_policies_updated_by_user_id"),
        "beatmap_ranking_policies",
        ["updated_by_user_id"],
        unique=False,
    )

    op.create_table(
        "beatmap_ranking_audits",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("action", ranking_policy_action, nullable=False),
        sa.Column("scope", ranking_policy_scope, nullable=False),
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("beatmap_id", sa.Integer(), nullable=True),
        sa.Column("actor_user_id", sa.Integer(), nullable=True),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("before", sa.JSON(), nullable=True),
        sa.Column("after", sa.JSON(), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["actor_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index(op.f("ix_beatmap_ranking_audits_action"), "beatmap_ranking_audits", ["action"], unique=False)
    op.create_index(op.f("ix_beatmap_ranking_audits_scope"), "beatmap_ranking_audits", ["scope"], unique=False)
    op.create_index(
        op.f("ix_beatmap_ranking_audits_beatmapset_id"),
        "beatmap_ranking_audits",
        ["beatmapset_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_audits_beatmap_id"),
        "beatmap_ranking_audits",
        ["beatmap_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_audits_actor_user_id"),
        "beatmap_ranking_audits",
        ["actor_user_id"],
        unique=False,
    )

    op.create_table(
        "beatmap_ranking_events",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("event_type", ranking_event_type, nullable=False),
        sa.Column("scope", ranking_policy_scope, nullable=False),
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("beatmap_id", sa.Integer(), nullable=False),
        sa.Column("old_checksum", sa.String(length=32), nullable=True),
        sa.Column("new_checksum", sa.String(length=32), nullable=True),
        sa.Column("old_last_updated", sa.DateTime(timezone=True), nullable=True),
        sa.Column("new_last_updated", sa.DateTime(timezone=True), nullable=True),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("details", sa.JSON(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("resolved_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("resolved_by_user_id", sa.Integer(), nullable=True),
        sa.ForeignKeyConstraint(["resolved_by_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index(
        op.f("ix_beatmap_ranking_events_event_type"),
        "beatmap_ranking_events",
        ["event_type"],
        unique=False,
    )
    op.create_index(op.f("ix_beatmap_ranking_events_scope"), "beatmap_ranking_events", ["scope"], unique=False)
    op.create_index(
        op.f("ix_beatmap_ranking_events_beatmapset_id"),
        "beatmap_ranking_events",
        ["beatmapset_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_events_beatmap_id"),
        "beatmap_ranking_events",
        ["beatmap_id"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_events_resolved_at"),
        "beatmap_ranking_events",
        ["resolved_at"],
        unique=False,
    )
    op.create_index(
        op.f("ix_beatmap_ranking_events_resolved_by_user_id"),
        "beatmap_ranking_events",
        ["resolved_by_user_id"],
        unique=False,
    )


def downgrade() -> None:
    """Remove local ranking policy, audit, and operator inbox tables."""

    op.drop_table("beatmap_ranking_events")
    op.drop_table("beatmap_ranking_audits")
    op.drop_table("beatmap_ranking_policies")
    op.drop_table("beatmapset_ranking_policies")
    op.drop_column("scores", "ranking_eligibility_snapshotted")
    op.drop_column("scores", "ranked_score_eligible")
    op.drop_column("scores", "leaderboard_eligible")
    op.drop_column("score_tokens", "beatmap_checksum")
    op.drop_index(op.f("ix_lazer_users_is_owner"), table_name="lazer_users")
    op.drop_column("lazer_users", "is_owner")
