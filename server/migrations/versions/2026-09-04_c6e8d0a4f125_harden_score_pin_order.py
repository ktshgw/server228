"""feat(db): harden persistent profile score pin ordering

Revision ID: c6e8d0a4f125
Revises: b5d1f7a4c902
Create Date: 2026-09-04 23:40:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "c6e8d0a4f125"
down_revision: str | Sequence[str] | None = "b5d1f7a4c902"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

PIN_ORDER_REPAIR_SQL = "UPDATE scores SET pinned_order = 0 WHERE pinned_order < 0"
PIN_ORDER_COMPACT_SQL = """
UPDATE scores AS target
JOIN (
    SELECT
        id,
        ROW_NUMBER() OVER (
            PARTITION BY user_id, gamemode
            ORDER BY pinned_order, id
        ) AS compact_order
    FROM scores
    WHERE pinned_order > 0
) AS ordered ON ordered.id = target.id
SET target.pinned_order = ordered.compact_order
WHERE target.pinned_order <> ordered.compact_order
"""
PIN_ORDER_CHECK_NAME = "ck_scores_pinned_order_nonnegative"


def _check_constraint_exists(name: str) -> bool:
    """Make MySQL's non-transactional DDL safe to resume after a partial run."""

    if op.get_context().as_sql:
        return False
    constraints = sa.inspect(op.get_bind()).get_check_constraints("scores")
    return any(constraint.get("name") == name for constraint in constraints)


def upgrade() -> None:
    """Make the existing lazer score-pin field safe for website writes too."""

    op.execute(sa.text(PIN_ORDER_REPAIR_SQL))
    op.execute(sa.text(PIN_ORDER_COMPACT_SQL))
    op.alter_column(
        "scores",
        "pinned_order",
        existing_type=sa.Integer(),
        existing_nullable=False,
        server_default=sa.text("0"),
    )
    if not _check_constraint_exists(PIN_ORDER_CHECK_NAME):
        op.create_check_constraint(PIN_ORDER_CHECK_NAME, "scores", "pinned_order >= 0")


def downgrade() -> None:
    if op.get_context().as_sql or _check_constraint_exists(PIN_ORDER_CHECK_NAME):
        op.drop_constraint(PIN_ORDER_CHECK_NAME, "scores", type_="check")
    op.alter_column(
        "scores",
        "pinned_order",
        existing_type=sa.Integer(),
        existing_nullable=False,
        server_default=None,
    )
