"""feat(db): add complete best-score grade counts

Revision ID: f3a7d9e2c641
Revises: e1b6c4d8a205
Create Date: 2026-09-04 22:00:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa
from sqlalchemy.exc import NoInspectionAvailable

revision: str = "f3a7d9e2c641"
down_revision: str | Sequence[str] | None = "e1b6c4d8a205"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


GRADE_BACKFILL_SQL = """
WITH ordered_bests AS (
    SELECT
        user_id,
        gamemode,
        beatmap_id,
        `rank`,
        ROW_NUMBER() OVER (
            PARTITION BY user_id, gamemode, beatmap_id
            ORDER BY total_score DESC, score_id ASC
        ) AS best_order
    FROM total_score_best_scores
), grade_counts AS (
    SELECT
        user_id,
        gamemode,
        SUM(CASE WHEN `rank` = 'B' THEN 1 ELSE 0 END) AS grade_b,
        SUM(CASE WHEN `rank` = 'C' THEN 1 ELSE 0 END) AS grade_c,
        SUM(CASE WHEN `rank` = 'D' THEN 1 ELSE 0 END) AS grade_d
    FROM ordered_bests
    WHERE best_order = 1
    GROUP BY user_id, gamemode
)
UPDATE lazer_user_statistics AS statistics
LEFT JOIN grade_counts
    ON grade_counts.user_id = statistics.user_id
   AND grade_counts.gamemode = statistics.mode
SET
    statistics.grade_b = COALESCE(grade_counts.grade_b, 0),
    statistics.grade_c = COALESCE(grade_counts.grade_c, 0),
    statistics.grade_d = COALESCE(grade_counts.grade_d, 0)
"""


def upgrade() -> None:
    # MySQL commits each ALTER TABLE even if a later statement fails.  Keep the
    # migration resumable so an interrupted deployment can safely retry rather
    # than getting stuck on a column which was already added.
    try:
        existing_columns = {
            column["name"] for column in sa.inspect(op.get_bind()).get_columns("lazer_user_statistics")
        }
    except (NoInspectionAvailable, TypeError):
        # Unit-test operation proxies are intentionally not inspectable.
        existing_columns = set()

    for name in ("grade_b", "grade_c", "grade_d"):
        if name not in existing_columns:
            op.add_column(
                "lazer_user_statistics",
                sa.Column(name, sa.Integer(), server_default=sa.text("0"), nullable=False),
            )
    op.execute(sa.text(GRADE_BACKFILL_SQL))


def downgrade() -> None:
    op.drop_column("lazer_user_statistics", "grade_d")
    op.drop_column("lazer_user_statistics", "grade_c")
    op.drop_column("lazer_user_statistics", "grade_b")
