"""feat(db): versioned SOMSAI tournament catalogues

Revision ID: 9f31a7c2d804
Revises: e66da9281556
"""

from alembic import op
import sqlalchemy as sa

revision = "9f31a7c2d804"
down_revision = "e66da9281556"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "somsai_pools",
        sa.Column("id", sa.Integer(), primary_key=True),
        sa.Column("name", sa.String(160), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("active", sa.Boolean(), nullable=False),
        sa.Column("rating_min", sa.Integer(), nullable=False),
        sa.Column("rating_max", sa.Integer(), nullable=False),
        sa.Column("best_of", sa.Integer(), nullable=False),
        sa.Column("bans_per_team", sa.Integer(), nullable=False),
        sa.Column("slots", sa.JSON(), nullable=False),
        sa.Column("revision", sa.Integer(), nullable=False),
        sa.Column("source_kind", sa.String(32), nullable=False),
        sa.Column("source_id", sa.Integer(), nullable=True),
        sa.Column("source_round", sa.String(160), nullable=True),
        sa.Column("source_url", sa.String(500), nullable=True),
        sa.Column("source_rank_min", sa.Integer(), nullable=True),
        sa.Column("source_rank_max", sa.Integer(), nullable=True),
        sa.Column("source_metadata", sa.JSON(), nullable=False),
    )
    op.create_index("ix_somsai_pool_mode_active", "somsai_pools", ["ruleset_id", "variant_id", "active"])


def downgrade() -> None:
    op.drop_table("somsai_pools")
