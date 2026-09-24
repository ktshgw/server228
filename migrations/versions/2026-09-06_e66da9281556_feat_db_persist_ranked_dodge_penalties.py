"""feat(db): persist Ranked dodge penalties

Revision ID: e66da9281556
Revises: f6b38d2a901e
Create Date: 2026-09-06 03:26:29.453173

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "e66da9281556"
down_revision: str | Sequence[str] | None = "f6b38d2a901e"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """Upgrade schema."""
    op.create_table(
        "ranked_dodge_penalties",
        sa.Column("room_id", sa.Integer(), primary_key=True),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("level", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("expires_at", sa.DateTime(), nullable=True),
        sa.Column("account_banned", sa.Boolean(), nullable=False),
    )
    op.create_index("ix_ranked_dodge_user_created", "ranked_dodge_penalties", ["user_id", "created_at"])


def downgrade() -> None:
    """Downgrade schema."""
    op.drop_table("ranked_dodge_penalties")
