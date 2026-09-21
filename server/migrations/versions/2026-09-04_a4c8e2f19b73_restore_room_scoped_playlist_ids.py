"""restore spectator-compatible room-scoped playlist ids

Revision ID: a4c8e2f19b73
Revises: f2a9c6d4810e, f3a7d9e2c641
Create Date: 2026-09-04 22:30:00.000000

The pinned spectator server inserts a room-local ``id`` and then resolves it
through ``db_id = LAST_INSERT_ID()``.  Revision 27eb30853d3d removed that
internal key, which makes every completed realtime match crash in
``AddPlaylistItemAsync``.  Restore the original dual-ID contract while keeping
all existing public playlist references unchanged.
"""

from collections.abc import Sequence

from alembic import op

revision: str = "a4c8e2f19b73"
down_revision: str | Sequence[str] | None = ("f2a9c6d4810e", "f3a7d9e2c641")
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

REFERENCE_REWRITES = (
    """
    UPDATE scores AS child
    JOIN room_playlists AS playlist
      ON playlist.room_id = child.room_id
     AND playlist.id = child.playlist_item_id
    SET child.playlist_item_id = playlist.db_id
    WHERE child.playlist_item_id IS NOT NULL
    """,
    """
    UPDATE score_tokens AS child
    JOIN room_playlists AS playlist
      ON playlist.room_id = child.room_id
     AND playlist.id = child.playlist_item_id
    SET child.playlist_item_id = playlist.db_id
    WHERE child.playlist_item_id IS NOT NULL
    """,
    """
    UPDATE multiplayer_events AS child
    JOIN room_playlists AS playlist
      ON playlist.room_id = child.room_id
     AND playlist.id = child.playlist_item_id
    SET child.playlist_item_id = playlist.db_id
    WHERE child.playlist_item_id IS NOT NULL
    """,
    """
    UPDATE playlist_best_scores AS child
    JOIN room_playlists AS playlist
      ON playlist.room_id = child.room_id
     AND playlist.id = child.playlist_id
    SET child.playlist_id = playlist.db_id
    WHERE child.playlist_id IS NOT NULL
    """,
)


def upgrade() -> None:
    """Restore the internal auto ID and preserve current IDs as public IDs."""
    op.drop_index(op.f("ix_room_playlists_id"), table_name="room_playlists")
    op.execute(
        """
        ALTER TABLE room_playlists
        DROP PRIMARY KEY,
        CHANGE COLUMN id db_id BIGINT NOT NULL AUTO_INCREMENT,
        ADD PRIMARY KEY (db_id),
        ADD COLUMN id BIGINT NULL AFTER db_id
        """
    )
    op.execute("UPDATE room_playlists SET id = db_id")
    op.execute("ALTER TABLE room_playlists MODIFY COLUMN id BIGINT NOT NULL")
    op.create_index(op.f("ix_room_playlists_id"), "room_playlists", ["id"], unique=False)
    op.create_unique_constraint("uq_room_playlists_room_id_id", "room_playlists", ["room_id", "id"])


def downgrade() -> None:
    """Return to one global ID without leaving child references dangling."""
    # Public IDs may overlap between rooms after this revision.  Translate all
    # composite references back to the globally unique internal key first.
    for statement in REFERENCE_REWRITES:
        op.execute(statement)

    op.drop_constraint("uq_room_playlists_room_id_id", "room_playlists", type_="unique")
    op.drop_index(op.f("ix_room_playlists_id"), table_name="room_playlists")
    op.execute(
        """
        ALTER TABLE room_playlists
        DROP PRIMARY KEY,
        DROP COLUMN id,
        CHANGE COLUMN db_id id BIGINT NOT NULL AUTO_INCREMENT,
        ADD PRIMARY KEY (id),
        ADD INDEX ix_room_playlists_id (id)
        """
    )
