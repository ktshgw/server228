from datetime import datetime
import importlib.util
from pathlib import Path
from types import ModuleType
import unittest
from unittest.mock import patch

import sqlalchemy as sa


def load_migration() -> ModuleType:
    path = (
        Path(__file__).resolve().parents[1]
        / "migrations"
        / "versions"
        / "2026-09-05_a7d4c9e2f681_remap_achievement_ids_to_official.py"
    )
    spec = importlib.util.spec_from_file_location("achievement_id_migration", path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load achievement ID migration")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class AchievementIdMigrationTests(unittest.TestCase):
    def test_mapping_is_bijective_and_contains_known_live_unlocks(self) -> None:
        migration = load_migration()
        mapping = dict(migration.LEGACY_TO_OFFICIAL)

        assert len(mapping) == 136
        assert len(set(mapping.values())) == 136
        assert mapping[1] == 55  # Rising Star
        assert mapping[3] == 57  # Building Confidence
        assert mapping[5] == 59  # These Clarion Skies
        assert mapping[6] == 60  # Above and Beyond
        assert mapping[11] == 63  # Totality
        assert mapping[92] == 122  # Time And A Half
        assert mapping[117] == 152  # No Time To Spare

    def test_upgrade_handles_cycles_and_collisions_and_downgrade_keeps_new_rows(self) -> None:
        migration = load_migration()
        engine = sa.create_engine("sqlite://")
        metadata = sa.MetaData()
        achievements = sa.Table(
            "lazer_user_achievements",
            metadata,
            sa.Column("id", sa.Integer, primary_key=True, autoincrement=True),
            sa.Column("user_id", sa.BigInteger, nullable=False),
            sa.Column("achievement_id", sa.Integer, nullable=False),
            sa.Column("achieved_at", sa.DateTime, nullable=True),
            sa.UniqueConstraint("user_id", "achievement_id"),
        )
        metadata.create_all(engine)
        early = datetime(2026, 9, 3)
        late = datetime(2026, 9, 4)

        with engine.begin() as connection:
            connection.execute(
                achievements.insert(),
                [
                    {"id": 1, "user_id": 1, "achievement_id": 1, "achieved_at": late},
                    {"id": 2, "user_id": 1, "achievement_id": 95, "achieved_at": late},
                    # 125 was not an implemented legacy ID. It is already the
                    # official ID that legacy 95 maps to, so these rows merge.
                    {"id": 3, "user_id": 1, "achievement_id": 125, "achieved_at": early},
                    {"id": 4, "user_id": 1, "achievement_id": 9999, "achieved_at": late},
                ],
            )
            with patch.object(migration.op, "execute", side_effect=connection.execute):
                migration.upgrade()

            upgraded = {
                row.achievement_id: row.achieved_at for row in connection.execute(sa.select(achievements)).all()
            }
            assert upgraded == {55: late, 125: early, 9999: late}

            connection.execute(achievements.insert().values(user_id=1, achievement_id=122, achieved_at=late))
            with patch.object(migration.op, "execute", side_effect=connection.execute):
                migration.downgrade()

            downgraded = {
                row.achievement_id: row.achieved_at for row in connection.execute(sa.select(achievements)).all()
            }
            assert downgraded == {1: late, 92: late, 95: early, 9999: late}
        engine.dispose()


if __name__ == "__main__":
    unittest.main()
