"""feat(db): add hashed user recovery words

Revision ID: b5d1f7a4c902
Revises: a4c8e2f19b73
Create Date: 2026-09-04 22:30:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "b5d1f7a4c902"
down_revision: str | Sequence[str] | None = "a4c8e2f19b73"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """Create one non-reversible recovery-word record per user."""

    op.create_table(
        "user_recovery_words",
        sa.Column("user_id", sa.Integer(), nullable=False),
        sa.Column("word_hash", sa.String(length=60), nullable=False),
        sa.Column("created_at", sa.DateTime(), server_default=sa.text("CURRENT_TIMESTAMP"), nullable=False),
        sa.Column("updated_at", sa.DateTime(), server_default=sa.text("CURRENT_TIMESTAMP"), nullable=False),
        sa.ForeignKeyConstraint(["user_id"], ["lazer_users.id"], ondelete="CASCADE"),
        sa.PrimaryKeyConstraint("user_id"),
    )


def downgrade() -> None:
    op.drop_table("user_recovery_words")
