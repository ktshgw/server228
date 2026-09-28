"""Add persistent SOMSAI duel and private-custom invitations."""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "c7d91e4a2b63"
down_revision: str | None = "8f41d3a6127b"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "somsai_direct_invites",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("kind", sa.String(16), nullable=False),
        sa.Column("inviter_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("target_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("match_id", sa.Integer(), sa.ForeignKey("somsai_matches.id"), nullable=True),
        sa.Column("ruleset_id", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("variant_id", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("status", sa.String(16), nullable=False, server_default="pending"),
        sa.Column("expires_at", sa.DateTime(), nullable=False),
    )
    op.create_index("ix_somsai_direct_invites_inviter_id", "somsai_direct_invites", ["inviter_id"])
    op.create_index("ix_somsai_direct_invites_target_id", "somsai_direct_invites", ["target_id"])
    op.create_index("ix_somsai_direct_invites_expires_at", "somsai_direct_invites", ["expires_at"])


def downgrade() -> None:
    op.drop_table("somsai_direct_invites")
