"""feat(db): add sync-stable local unrank policy

Revision ID: e1b6c4d8a205
Revises: d8f4a1c2b693
Create Date: 2026-09-04 21:00:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "e1b6c4d8a205"
down_revision: str | Sequence[str] | None = "d8f4a1c2b693"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """Add an explicit restrictive policy independent of Bancho revisions."""

    op.add_column(
        "beatmapset_ranking_policies",
        sa.Column("force_unranked", sa.Boolean(), server_default=sa.text("0"), nullable=False),
    )
    op.add_column(
        "beatmap_ranking_policies",
        sa.Column("force_unranked", sa.Boolean(), server_default=sa.text("0"), nullable=False),
    )


def downgrade() -> None:
    op.drop_column("beatmap_ranking_policies", "force_unranked")
    op.drop_column("beatmapset_ranking_policies", "force_unranked")
