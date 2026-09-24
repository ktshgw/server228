"""feat(db): add admin panel audit events

Revision ID: 9f31c6e2a741
Revises: 7c3e9a12b4d6
Create Date: 2026-09-04 16:30:00.000000

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "9f31c6e2a741"
down_revision: str | Sequence[str] | None = "7c3e9a12b4d6"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "admin_audit_events",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("actor_user_id", sa.Integer(), nullable=True),
        sa.Column("actor_username", sa.String(length=32), nullable=False),
        sa.Column("action", sa.String(length=64), nullable=False),
        sa.Column("target_type", sa.String(length=32), nullable=False),
        sa.Column("target_id", sa.String(length=64), nullable=False),
        sa.Column("reason", sa.Text(), nullable=False),
        sa.Column("before", sa.JSON(), nullable=True),
        sa.Column("after", sa.JSON(), nullable=True),
        sa.Column("ip_address", sa.String(length=45), nullable=True),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["actor_user_id"], ["lazer_users.id"], ondelete="SET NULL"),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index(op.f("ix_admin_audit_events_actor_user_id"), "admin_audit_events", ["actor_user_id"])
    op.create_index(op.f("ix_admin_audit_events_actor_username"), "admin_audit_events", ["actor_username"])
    op.create_index(op.f("ix_admin_audit_events_action"), "admin_audit_events", ["action"])
    op.create_index(op.f("ix_admin_audit_events_target_type"), "admin_audit_events", ["target_type"])
    op.create_index(op.f("ix_admin_audit_events_target_id"), "admin_audit_events", ["target_id"])
    op.create_index(op.f("ix_admin_audit_events_created_at"), "admin_audit_events", ["created_at"])


def downgrade() -> None:
    op.drop_index(op.f("ix_admin_audit_events_created_at"), table_name="admin_audit_events")
    op.drop_index(op.f("ix_admin_audit_events_target_id"), table_name="admin_audit_events")
    op.drop_index(op.f("ix_admin_audit_events_target_type"), table_name="admin_audit_events")
    op.drop_index(op.f("ix_admin_audit_events_action"), table_name="admin_audit_events")
    op.drop_index(op.f("ix_admin_audit_events_actor_username"), table_name="admin_audit_events")
    op.drop_index(op.f("ix_admin_audit_events_actor_user_id"), table_name="admin_audit_events")
    op.drop_table("admin_audit_events")
