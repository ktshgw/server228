import json
from pathlib import Path
import runpy
import unittest
from unittest.mock import Mock

import sqlalchemy as sa

INITIAL_MIGRATION_PATH = (
    Path(__file__).parents[1]
    / "migrations"
    / "versions"
    / "2026-09-04_7c3e9a12b4d6_add_local_beatmap_ranking_policies.py"
)
CORRECTIVE_MIGRATION_PATH = (
    Path(__file__).parents[1]
    / "migrations"
    / "versions"
    / "2026-09-04_d8f4a1c2b693_backfill_legacy_pending_score_eligibility.py"
)
FORCE_UNRANK_MIGRATION_PATH = (
    Path(__file__).parents[1] / "migrations" / "versions" / "2026-09-04_e1b6c4d8a205_add_force_unranked_policy.py"
)
GRADE_COUNTS_MIGRATION_PATH = (
    Path(__file__).parents[1] / "migrations" / "versions" / "2026-09-04_f3a7d9e2c641_add_complete_grade_counts.py"
)
UNRANK_STATUS_MIGRATION_PATH = (
    Path(__file__).parents[1]
    / "migrations"
    / "versions"
    / "2026-09-05_b8e2d4f1a603_fix_legacy_pending_unrank_policies.py"
)


class LegacyPendingScoreMigrationTests(unittest.TestCase):
    def test_pending_ranked_scores_receive_one_submission_eligibility_snapshot(self) -> None:
        for path in (INITIAL_MIGRATION_PATH, CORRECTIVE_MIGRATION_PATH):
            with self.subTest(migration=path.name):
                migration = runpy.run_path(str(path))
                rows = self._apply_backfill(sa.text(migration["LEGACY_PENDING_SCORE_BACKFILL_SQL"]))

                assert rows == [
                    (10, 1, 1, 1),
                    # ranked=false has no reliable legacy submission snapshot.
                    (11, 0, 0, 0),
                    (12, 0, 0, 0),
                    (13, 0, 0, 0),
                    # ranked=true records the legacy ENABLE_ALL_BEATMAP_PP epoch.
                    (14, 1, 1, 1),
                    # Current revision matching remains a separate worker-time gate.
                    (15, 1, 1, 1),
                    # Historic processed rows remain explicitly legacy/unsnapshotted.
                    (16, 0, 0, 0),
                ]

    def test_corrective_migration_extends_the_existing_head(self) -> None:
        migration = runpy.run_path(str(CORRECTIVE_MIGRATION_PATH))

        assert migration["down_revision"] == "c3d8a4e721bf"

    def test_initial_upgrade_backfills_before_new_rows_default_to_snapshotted(self) -> None:
        migration = runpy.run_path(str(INITIAL_MIGRATION_PATH))
        fake_op = Mock()
        fake_op.f.side_effect = lambda value: value
        migration["upgrade"].__globals__["op"] = fake_op

        migration["upgrade"]()

        calls = fake_op.method_calls
        execute_index = next(index for index, item in enumerate(calls) if item[0] == "execute")
        snapshot_default_index = next(
            index
            for index, item in enumerate(calls)
            if item[0] == "alter_column" and item.args[:2] == ("scores", "ranking_eligibility_snapshotted")
        )
        assert execute_index < snapshot_default_index
        assert str(calls[execute_index].args[0]).strip() == migration["LEGACY_PENDING_SCORE_BACKFILL_SQL"].strip()

    def test_force_unrank_migration_extends_head_and_adds_both_policy_columns(self) -> None:
        migration = runpy.run_path(str(FORCE_UNRANK_MIGRATION_PATH))
        fake_op = Mock()
        migration["upgrade"].__globals__["op"] = fake_op

        migration["upgrade"]()

        assert migration["down_revision"] == "d8f4a1c2b693"
        added = {(item.args[0], item.args[1].name) for item in fake_op.method_calls if item[0] == "add_column"}
        assert added == {
            ("beatmapset_ranking_policies", "force_unranked"),
            ("beatmap_ranking_policies", "force_unranked"),
        }

    def test_complete_grade_count_migration_extends_force_unrank_and_backfills(self) -> None:
        migration = runpy.run_path(str(GRADE_COUNTS_MIGRATION_PATH))
        fake_op = Mock()
        migration["upgrade"].__globals__["op"] = fake_op

        migration["upgrade"]()

        assert migration["down_revision"] == "e1b6c4d8a205"
        added = [item.args[1].name for item in fake_op.method_calls if item[0] == "add_column"]
        assert added == ["grade_b", "grade_c", "grade_d"]
        statements = [str(item.args[0]) for item in fake_op.method_calls if item[0] == "execute"]
        assert statements == [migration["GRADE_BACKFILL_SQL"]]
        assert "PARTITION BY user_id, gamemode, beatmap_id" in statements[0]
        assert "WHERE best_order = 1" in statements[0]
        assert "`rank`" in statements[0]

    def test_unrank_status_migration_distinguishes_rank_removal_from_direct_derank(self) -> None:
        migration = runpy.run_path(str(UNRANK_STATUS_MIGRATION_PATH))
        was_local_rank = migration["_snapshot_was_active_local_rank"]

        assert migration["down_revision"] == "a7d4c9e2f681"
        assert was_local_rank(
            {
                "status": 4,
                "is_active": True,
                "force_unranked": False,
                "blocks_set_policy": False,
            }
        )
        assert not was_local_rank(None)
        assert not was_local_rank(
            {
                "status": 0,
                "is_active": True,
                "force_unranked": True,
            }
        )

    def test_unrank_status_migration_recovers_existing_policy_rows(self) -> None:
        migration = runpy.run_path(str(UNRANK_STATUS_MIGRATION_PATH))
        engine = sa.create_engine("sqlite://")
        try:
            with engine.begin() as connection:
                connection.exec_driver_sql(
                    "CREATE TABLE beatmapsets (id INTEGER PRIMARY KEY, beatmap_status TEXT NOT NULL)"
                )
                connection.exec_driver_sql(
                    "CREATE TABLE beatmaps ("
                    "id INTEGER PRIMARY KEY, beatmapset_id INTEGER NOT NULL, "
                    "beatmap_status TEXT NOT NULL, checksum TEXT NOT NULL)"
                )
                connection.exec_driver_sql(
                    "CREATE TABLE beatmapset_ranking_policies ("
                    "beatmapset_id INTEGER PRIMARY KEY, status TEXT NOT NULL, "
                    "leaderboard_enabled BOOLEAN NOT NULL, pp_enabled BOOLEAN NOT NULL, "
                    "force_unranked BOOLEAN NOT NULL, is_active BOOLEAN NOT NULL, updated_at TEXT)"
                )
                connection.exec_driver_sql(
                    "CREATE TABLE beatmap_ranking_policies ("
                    "beatmap_id INTEGER PRIMARY KEY, status TEXT NOT NULL, "
                    "leaderboard_enabled BOOLEAN NOT NULL, pp_enabled BOOLEAN NOT NULL, "
                    "force_unranked BOOLEAN NOT NULL, blocks_set_policy BOOLEAN NOT NULL, "
                    "ranked_checksum TEXT NOT NULL, updated_at TEXT)"
                )
                connection.exec_driver_sql(
                    "CREATE TABLE beatmap_ranking_audits ("
                    "id INTEGER PRIMARY KEY, scope TEXT NOT NULL, beatmapset_id INTEGER NOT NULL, "
                    "beatmap_id INTEGER, before TEXT)"
                )
                connection.exec_driver_sql("INSERT INTO beatmapsets VALUES (10, 'GRAVEYARD'), (11, 'RANKED')")
                connection.exec_driver_sql(
                    "INSERT INTO beatmaps VALUES "
                    "(101, 10, 'QUALIFIED', 'qualified-md5'), (102, 11, 'RANKED', 'ranked-md5')"
                )
                connection.exec_driver_sql(
                    "INSERT INTO beatmapset_ranking_policies VALUES "
                    "(10, 'PENDING', 0, 0, 1, 1, NULL), (11, 'PENDING', 0, 0, 1, 1, NULL)"
                )
                connection.exec_driver_sql(
                    "INSERT INTO beatmap_ranking_policies VALUES "
                    "(101, 'PENDING', 0, 0, 1, 0, 'old-md5', NULL), "
                    "(102, 'PENDING', 0, 0, 1, 0, 'old-md5', NULL)"
                )
                local_rank_before = json.dumps(
                    {
                        "status": 4,
                        "is_active": True,
                        "force_unranked": False,
                        "blocks_set_policy": False,
                    }
                )
                connection.execute(
                    sa.text(
                        "INSERT INTO beatmap_ranking_audits "
                        "(id, scope, beatmapset_id, beatmap_id, before) VALUES "
                        "(1, 'BEATMAPSET', 10, NULL, :before), "
                        "(2, 'BEATMAP', 10, 101, :before)"
                    ),
                    {"before": local_rank_before},
                )
                fake_op = Mock()
                fake_op.get_bind.return_value = connection
                migration["upgrade"].__globals__["op"] = fake_op

                migration["upgrade"]()

                set_rows = connection.exec_driver_sql(
                    "SELECT beatmapset_id, status, force_unranked, is_active "
                    "FROM beatmapset_ranking_policies ORDER BY beatmapset_id"
                ).all()
                difficulty_rows = connection.exec_driver_sql(
                    "SELECT beatmap_id, status, leaderboard_enabled, pp_enabled, "
                    "force_unranked, blocks_set_policy, ranked_checksum "
                    "FROM beatmap_ranking_policies ORDER BY beatmap_id"
                ).all()
            assert [tuple(row) for row in set_rows] == [
                (10, "PENDING", 1, 0),
                (11, "GRAVEYARD", 1, 1),
            ]
            assert [tuple(row) for row in difficulty_rows] == [
                (101, "QUALIFIED", 1, 0, 0, 1, "qualified-md5"),
                (102, "GRAVEYARD", 0, 0, 1, 0, "old-md5"),
            ]
        finally:
            engine.dispose()

    @staticmethod
    def _apply_backfill(statement: sa.TextClause) -> list[tuple[int, int, int, int]]:
        engine = sa.create_engine("sqlite://")
        try:
            with engine.begin() as connection:
                connection.exec_driver_sql(
                    "CREATE TABLE beatmaps (id INTEGER PRIMARY KEY, checksum TEXT, beatmap_status TEXT NOT NULL)"
                )
                connection.exec_driver_sql(
                    "CREATE TABLE scores ("
                    "id INTEGER PRIMARY KEY, beatmap_id INTEGER NOT NULL, map_md5 TEXT NOT NULL, "
                    "processed BOOLEAN NOT NULL, ranked BOOLEAN NOT NULL, "
                    "leaderboard_eligible BOOLEAN NOT NULL DEFAULT 0, "
                    "ranked_score_eligible BOOLEAN NOT NULL DEFAULT 0, "
                    "ranking_eligibility_snapshotted BOOLEAN NOT NULL DEFAULT 0)"
                )
                connection.exec_driver_sql(
                    "INSERT INTO beatmaps (id, checksum, beatmap_status) VALUES "
                    "(1, 'ranked-md5', 'RANKED'), "
                    "(2, 'loved-md5', 'LOVED'), "
                    "(3, 'qualified-md5', 'QUALIFIED'), "
                    "(4, 'pending-md5', 'PENDING')"
                )
                connection.exec_driver_sql(
                    "INSERT INTO scores (id, beatmap_id, map_md5, processed, ranked) VALUES "
                    "(10, 1, 'ranked-md5', 0, 1), "
                    "(11, 2, 'loved-md5', 0, 0), "
                    "(12, 3, 'qualified-md5', 0, 0), "
                    "(13, 4, 'pending-md5', 0, 0), "
                    "(14, 4, 'pending-md5', 0, 1), "
                    "(15, 1, 'older-revision', 0, 1), "
                    "(16, 1, 'ranked-md5', 1, 1)"
                )

                connection.execute(statement)
                rows = connection.exec_driver_sql(
                    "SELECT id, leaderboard_eligible, ranked_score_eligible, ranking_eligibility_snapshotted "
                    "FROM scores ORDER BY id"
                ).all()
            return [tuple(row) for row in rows]
        finally:
            engine.dispose()


if __name__ == "__main__":
    unittest.main()
