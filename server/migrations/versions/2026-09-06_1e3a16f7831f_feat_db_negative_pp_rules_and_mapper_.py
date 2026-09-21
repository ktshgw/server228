"""Negative PP rules and mapper credits; reviewed autogenerate output.

Revision ID: 1e3a16f7831f
Revises: bc803fd64e29
"""

from alembic import op
import sqlalchemy as sa

revision = "1e3a16f7831f"
down_revision = "bc803fd64e29"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "negative_pp_rules",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("kind", sa.String(16), nullable=False),
        sa.Column("target_id", sa.Integer(), nullable=False),
        sa.Column("label", sa.String(300), nullable=False),
        sa.Column("reason", sa.String(500), nullable=False),
        sa.Column("created_by", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.UniqueConstraint("kind", "target_id", name="uq_negative_pp_target"),
    )
    op.create_index("ix_negative_pp_rules_target_id", "negative_pp_rules", ["target_id"])
    op.create_table(
        "beatmap_mapper_credits",
        sa.Column("beatmap_id", sa.Integer(), sa.ForeignKey("beatmaps.id", ondelete="CASCADE"), primary_key=True),
        sa.Column("mapper_id", sa.Integer(), primary_key=True),
        sa.Column("username", sa.String(255), nullable=False),
    )
    op.create_index("ix_beatmap_mapper_credits_mapper_id", "beatmap_mapper_credits", ["mapper_id"])
    op.add_column("beatmaps", sa.Column("owners_known", sa.Boolean(), nullable=False, server_default=sa.false()))


def downgrade() -> None:
    op.drop_column("beatmaps", "owners_known")
    op.drop_table("beatmap_mapper_credits")
    op.drop_table("negative_pp_rules")
