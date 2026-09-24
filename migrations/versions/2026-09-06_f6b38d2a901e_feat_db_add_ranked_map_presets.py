"""feat(db): add Ranked map presets

Revision ID: f6b38d2a901e
Revises: 8b34250f9086
Create Date: 2026-09-06 01:20:37.457704

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "f6b38d2a901e"
down_revision: str | Sequence[str] | None = "8b34250f9086"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """Upgrade schema."""
    op.create_table(
        "matchmaking_map_presets",
        sa.Column("id", sa.Integer(), primary_key=True, autoincrement=True),
        sa.Column("name", sa.String(80), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("min_stars", sa.Float(), nullable=False),
        sa.Column("max_stars", sa.Float(), nullable=False),
        sa.Column("min_length", sa.Integer(), nullable=False),
        sa.Column("max_length", sa.Integer(), nullable=False),
        sa.Column("beatmap_ids", sa.JSON(), nullable=True),
        sa.Column("revision", sa.Integer(), nullable=False),
    )
    op.add_column("matchmaking_pools", sa.Column("beatmap_preset_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_matchmaking_pool_preset", "matchmaking_pools", "matchmaking_map_presets", ["beatmap_preset_id"], ["id"]
    )


def downgrade() -> None:
    """Downgrade schema."""
    op.drop_constraint("fk_matchmaking_pool_preset", "matchmaking_pools", type_="foreignkey")
    op.drop_column("matchmaking_pools", "beatmap_preset_id")
    op.drop_table("matchmaking_map_presets")
