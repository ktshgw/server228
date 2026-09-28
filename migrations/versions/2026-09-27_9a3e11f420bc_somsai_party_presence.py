"""Expire SOMSAI parties when client heartbeats stop."""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "9a3e11f420bc"
down_revision: str | None = "c7d91e4a2b63"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "somsai_activity",
        sa.Column("last_seen_at", sa.DateTime(), nullable=False, server_default=sa.text("CURRENT_TIMESTAMP")),
    )


def downgrade() -> None:
    op.drop_column("somsai_activity", "last_seen_at")
