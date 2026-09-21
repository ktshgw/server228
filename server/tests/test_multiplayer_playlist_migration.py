from pathlib import Path
import unittest

from app.database.playlists import Playlist

ROOT = Path(__file__).resolve().parents[1]
MIGRATION = ROOT / "migrations/versions/2026-09-04_a4c8e2f19b73_restore_room_scoped_playlist_ids.py"


class MultiplayerPlaylistMigrationTests(unittest.TestCase):
    def test_orm_uses_hidden_db_id_as_identity_and_room_scoped_public_id(self) -> None:
        assert [column.name for column in Playlist.__table__.primary_key.columns] == ["db_id"]  # pyright: ignore[reportAttributeAccessIssue]
        assert "db_id" in Playlist._EXCLUDED_DATABASE_FIELDS
        assert not Playlist.__table__.c.id.primary_key  # pyright: ignore[reportAttributeAccessIssue]
        unique_columns = {
            tuple(column.name for column in constraint.columns)
            for constraint in Playlist.__table__.constraints  # pyright: ignore[reportAttributeAccessIssue]
            if constraint.name == "uq_room_playlists_room_id_id"
        }
        assert unique_columns == {("room_id", "id")}

    def test_merge_revision_restores_spectator_dual_id_contract(self) -> None:
        source = MIGRATION.read_text(encoding="utf-8")

        assert 'revision: str = "a4c8e2f19b73"' in source
        assert '("f2a9c6d4810e", "f3a7d9e2c641")' in source
        assert "CHANGE COLUMN id db_id BIGINT NOT NULL AUTO_INCREMENT" in source
        assert "ADD COLUMN id BIGINT NULL" in source
        assert "uq_room_playlists_room_id_id" in source

    def test_downgrade_translates_every_composite_playlist_reference(self) -> None:
        source = MIGRATION.read_text(encoding="utf-8")

        for table in ("scores", "score_tokens", "multiplayer_events", "playlist_best_scores"):
            assert f"UPDATE {table} AS child" in source
        assert "playlist.room_id = child.room_id" in source
        assert "= playlist.db_id" in source
