# Standard-library unittest runner; pytest is not required for this maintenance tool.
# ruff: noqa: PT009, PT027
from pathlib import Path
import tempfile
import unittest

from tools.migrate_public_domain import check_local_files, origin, rebase_urls, write_backup


class PublicDomainMigrationTests(unittest.TestCase):
    old = "https://old.example"
    new = "https://soms.example"

    def test_avatar_and_event_links_move_without_changing_score_data(self):
        payload = {
            "user": {"url": self.old + "/users/12", "avatar": self.old + "/file/avatars/a.png?v=2#image"},
            "pp": 2931.48,
            "mods": [{"acronym": "DT", "settings": {"speed_change": 1.3}}],
            "passed": True,
        }
        result = rebase_urls(payload, self.old, self.new)
        self.assertEqual(result["user"]["url"], self.new + "/users/12")
        self.assertEqual(result["user"]["avatar"], self.new + "/file/avatars/a.png?v=2#image")
        self.assertEqual(result["mods"], payload["mods"])
        self.assertEqual(result["pp"], payload["pp"])
        self.assertIs(result["passed"], True)
        self.assertEqual(payload["user"]["url"], self.old + "/users/12")
        self.assertEqual(rebase_urls(result, self.old, self.new), result)

    def test_external_origins_and_embedded_text_are_preserved(self):
        values = [
            "https://old.example.attacker.test/file/avatar.png",
            "https://old.example@attacker.test/file/avatar.png",
            "https://assets.ppy.sh/beatmaps/123/covers/cover.jpg",
            "https://another.example/?next=https://old.example/users/12",
            "My previous site: https://old.example",
            "http://old.example/file/avatar.png",
        ]
        self.assertEqual(rebase_urls(values, self.old, self.new), values)

    def test_only_origins_are_accepted(self):
        self.assertEqual(origin("https://OLD.example/"), self.old)
        for value in (
            "old.example",
            "https://old.example/path",
            "https://user:pass@old.example",
            "https://old.example?q=1",
        ):
            with self.subTest(value=value), self.assertRaises(ValueError):
                origin(value)

    def test_missing_assets_and_traversal_block_the_migration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            (root / "avatar.png").write_bytes(b"existing asset")
            check_local_files(self.new + "/file/avatar.png?v=2", self.new, root)
            with self.assertRaises(FileNotFoundError):
                check_local_files(self.new + "/file/missing.png", self.new, root)
            with self.assertRaises(ValueError):
                check_local_files(self.new + "/file/%2e%2e/outside.png", self.new, root)

    def test_backups_cannot_overwrite_existing_files_or_be_published(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "backup.json"
            write_backup(path, {"before": self.old})
            before = path.read_bytes()
            with self.assertRaises(FileExistsError):
                write_backup(path, {"before": "different"})
            self.assertEqual(path.read_bytes(), before)
            with self.assertRaises(ValueError):
                write_backup(Path(directory) / "static" / "backup.json", {})


if __name__ == "__main__":
    unittest.main()
