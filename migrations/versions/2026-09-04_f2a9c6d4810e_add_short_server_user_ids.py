"""add short server-facing user ids

Revision ID: f2a9c6d4810e
Revises: d8f4a1c2b693
Create Date: 2026-09-04 21:00:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "f2a9c6d4810e"
down_revision: str | Sequence[str] | None = "d8f4a1c2b693"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column("lazer_users", sa.Column("server_id", sa.Integer(), nullable=True))
    bind = op.get_bind()
    user_ids = list(
        bind.execute(sa.text("SELECT id FROM lazer_users WHERE is_bot = 0 ORDER BY join_date ASC, id ASC")).scalars()
    )
    for server_id, user_id in enumerate(user_ids, start=1):
        bind.execute(
            sa.text("UPDATE lazer_users SET server_id = :server_id WHERE id = :user_id"),
            {"server_id": server_id, "user_id": user_id},
        )
    op.create_index("ix_lazer_users_server_id", "lazer_users", ["server_id"], unique=True)


def downgrade() -> None:
    op.drop_index("ix_lazer_users_server_id", table_name="lazer_users")
    op.drop_column("lazer_users", "server_id")
