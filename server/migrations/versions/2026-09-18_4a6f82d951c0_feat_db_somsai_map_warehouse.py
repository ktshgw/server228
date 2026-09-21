"""feat(db): add somsai map warehouse

Revision ID: 4a6f82d951c0
Revises: ab7316d4c902
Create Date: 2026-09-18
"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

revision: str = "4a6f82d951c0"
down_revision: str | None = "ab7316d4c902"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "somsai_maps",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("slot", sa.String(4), nullable=False),
        sa.Column("category", sa.String(2), nullable=False),
        sa.Column("beatmap_id", sa.Integer(), nullable=False),
        sa.Column("beatmapset_id", sa.Integer(), nullable=False),
        sa.Column("ruleset_id", sa.Integer(), nullable=False),
        sa.Column("variant_id", sa.Integer(), nullable=False),
        sa.Column("checksum", sa.String(32), nullable=True),
        sa.Column("artist", sa.String(255), nullable=False),
        sa.Column("title", sa.String(255), nullable=False),
        sa.Column("version", sa.String(255), nullable=False),
        sa.Column("cover_url", sa.String(1000), nullable=True),
        sa.Column("mods", sa.JSON(), nullable=False),
        sa.Column("stats", sa.JSON(), nullable=False),
        sa.Column("eligible_ranks", sa.JSON(), nullable=False),
        sa.Column("eligibility_label", sa.String(255), nullable=False),
        sa.Column("source_kind", sa.String(32), nullable=False),
        sa.Column("source_url", sa.String(500), nullable=True),
        sa.Column("source_round", sa.String(160), nullable=True),
        sa.Column("created_at", sa.DateTime(), nullable=False),
        sa.Column("refreshed_at", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
        sa.UniqueConstraint("slot", "beatmap_id", name="uq_somsai_map_slot_beatmap"),
    )
    op.create_index("ix_somsai_map_beatmap_id", "somsai_maps", ["beatmap_id"])
    op.create_index("ix_somsai_map_slot", "somsai_maps", ["slot"])
    op.create_index("ix_somsai_map_slot_ruleset", "somsai_maps", ["slot", "ruleset_id", "variant_id"])


def downgrade() -> None:
    op.drop_index("ix_somsai_map_slot_ruleset", table_name="somsai_maps")
    op.drop_index("ix_somsai_map_slot", table_name="somsai_maps")
    op.drop_index("ix_somsai_map_beatmap_id", table_name="somsai_maps")
    op.drop_table("somsai_maps")
