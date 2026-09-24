"""fix(db): backfill legacy pending score eligibility

Revision ID: d8f4a1c2b693
Revises: c3d8a4e721bf
Create Date: 2026-09-04 20:00:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "d8f4a1c2b693"
down_revision: str | Sequence[str] | None = "c3d8a4e721bf"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


# 7c3e9a12b4d6 originally added all three columns with false defaults.
# Repeating the guarded data repair in a forward migration fixes databases
# which reached that revision before its in-place backfill was added.
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
    """Repair only still-pending rows with trustworthy legacy evidence."""

    op.execute(sa.text(LEGACY_PENDING_SCORE_BACKFILL_SQL))


def downgrade() -> None:
    """Keep reconstructed snapshots; their origin cannot be distinguished safely."""
