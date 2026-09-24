"""feat(db): support SOMS ranked multiplayer

Revision ID: 8b34250f9086
Revises: b8e2d4f1a603
Create Date: 2026-09-05 23:06:01.665167

"""

from collections.abc import Sequence

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "8b34250f9086"
down_revision: str | Sequence[str] | None = "b8e2d4f1a603"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    """Upgrade schema."""
    # Additive schema support; existing scores, PP and ranking policies are
    # untouched. The old schema only keyed stats by user, losing other pools.
    columns = (
        sa.Column("variant_id", sa.SmallInteger(), nullable=False, server_default="0"),
        sa.Column("type", sa.String(32), nullable=False, server_default="quick_play"),
        sa.Column("ranked", sa.Boolean(), nullable=False, server_default="0"),
        sa.Column("lobby_size", sa.Integer(), nullable=False, server_default="2"),
        sa.Column("rating_search_radius", sa.Integer(), nullable=False, server_default="150"),
        sa.Column("rating_search_radius_max", sa.Integer(), nullable=False, server_default="9999"),
        sa.Column("rating_search_radius_exp", sa.Integer(), nullable=False, server_default="15"),
        sa.Column("use_dmr", sa.Boolean(), nullable=False, server_default="0"),
    )
    for column in columns:
        op.add_column("matchmaking_pools", column)
    op.create_unique_constraint(
        "uq_matchmaking_pool_identity", "matchmaking_pools", ["ruleset_id", "variant_id", "name", "type"]
    )
    # The user FK needs an index before replacing its former primary key.
    op.create_index("ix_matchmaking_stats_user", "matchmaking_user_stats", ["user_id"])
    op.execute(
        "ALTER TABLE matchmaking_user_stats DROP PRIMARY KEY, ADD COLUMN id INT NOT NULL AUTO_INCREMENT PRIMARY KEY"
    )
    op.create_unique_constraint("uq_matchmaking_user_pool", "matchmaking_user_stats", ["user_id", "pool_id"])
    op.add_column(
        "matchmaking_pool_beatmaps", sa.Column("rating_sig", sa.Float(), nullable=False, server_default="150")
    )
    op.alter_column(
        "matchmaking_pool_beatmaps", "rating", existing_type=sa.Integer(), type_=sa.Float(), existing_nullable=False
    )
    op.add_column(
        "matchmaking_pool_beatmaps",
        sa.Column(
            "mods_key",
            sa.String(64),
            sa.Computed("sha2(cast(coalesce(mods, json_array()) as char), 256)", persisted=True),
        ),
    )
    op.create_unique_constraint(
        "uq_matchmaking_pool_beatmap_mods", "matchmaking_pool_beatmaps", ["pool_id", "beatmap_id", "mods_key"]
    )
    op.execute(
        "ALTER TABLE rooms MODIFY type "
        "ENUM('PLAYLISTS','HEAD_TO_HEAD','TEAM_VERSUS','MATCHMAKING','RANKED_PLAY') NOT NULL"
    )
    op.create_table(
        "matchmaking_room_events",
        sa.Column("id", sa.BigInteger(), primary_key=True, autoincrement=True),
        sa.Column("room_id", sa.Integer(), sa.ForeignKey("rooms.id"), nullable=False),
        sa.Column("event_type", sa.String(64), nullable=False),
        sa.Column("playlist_item_id", sa.Integer(), nullable=True),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=True),
        sa.Column("event_detail", sa.JSON(), nullable=True),
        sa.Column("created_at", sa.DateTime(), server_default=sa.func.now(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), server_default=sa.func.now(), nullable=False),
    )
    op.create_index("ix_matchmaking_room_events_room_id", "matchmaking_room_events", ["room_id"])
    op.create_table(
        "matchmaking_user_elo_history",
        sa.Column("id", sa.BigInteger(), primary_key=True, autoincrement=True),
        sa.Column("room_id", sa.Integer(), sa.ForeignKey("rooms.id"), nullable=False),
        sa.Column("pool_id", sa.Integer(), sa.ForeignKey("matchmaking_pools.id"), nullable=False),
        sa.Column("user_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("opponent_id", sa.Integer(), sa.ForeignKey("lazer_users.id"), nullable=False),
        sa.Column("result", sa.String(8), nullable=False),
        sa.Column("elo_before", sa.Integer(), nullable=False),
        sa.Column("elo_after", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(), server_default=sa.func.now(), nullable=False),
        sa.Column("updated_at", sa.DateTime(), server_default=sa.func.now(), nullable=False),
    )
    op.create_index("ix_matchmaking_user_elo_history_room_id", "matchmaking_user_elo_history", ["room_id"])
    op.create_index("ix_matchmaking_user_elo_history_user_id", "matchmaking_user_elo_history", ["user_id"])


def downgrade() -> None:
    """Downgrade schema."""
    # A downgrade cannot restore the old one-pool-per-user key without losing
    # real match history. Require an operator-reviewed export instead.
    raise RuntimeError("SOMS ranked history requires an explicit data-preserving downgrade")
