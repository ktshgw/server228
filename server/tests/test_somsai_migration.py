"""New tournament schema preserves old rooms and matches the ORM metadata."""

# ruff: noqa: PT009
import importlib.util
from io import StringIO
from pathlib import Path
import unittest

# Register all ORM tables before comparing metadata.
import app.database  # noqa: F401

from alembic.migration import MigrationContext
from alembic.operations import Operations
import sqlalchemy as sa
from sqlmodel import SQLModel


def migration_module():
    path = Path(__file__).parents[1] / "migrations/versions/2026-09-06_a472c8f019d3_feat_db_somsai_core.py"
    spec = importlib.util.spec_from_file_location("somsai_core_migration", path)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SomsaiMigrationTests(unittest.TestCase):
    def test_upgrade_matches_models_preserves_existing_room_and_downgrades(self):
        module = migration_module()
        engine = sa.create_engine("sqlite://")
        self.addCleanup(engine.dispose)
        with engine.begin() as connection:
            connection.exec_driver_sql("PRAGMA foreign_keys=ON")
            connection.exec_driver_sql("CREATE TABLE lazer_users (id INTEGER PRIMARY KEY)")
            connection.exec_driver_sql("CREATE TABLE rooms (id INTEGER PRIMARY KEY, name VARCHAR(100) NOT NULL)")
            connection.exec_driver_sql("INSERT INTO rooms (id, name) VALUES (42, 'existing room')")
            context = MigrationContext.configure(connection)
            with Operations.context(context):
                module.upgrade()
            self.assertEqual(connection.execute(sa.text("SELECT id FROM somsai_lock")).scalars().all(), [1])
            self.assertEqual(connection.execute(sa.text("SELECT * FROM rooms")).one(), (42, "existing room", 0))
            inspector = sa.inspect(connection)
            tables = [name for name in inspector.get_table_names() if name.startswith("somsai_")]
            self.assertEqual(len(tables), 9)
            for name in tables:
                expected = SQLModel.metadata.tables[name]
                with self.subTest(table=name):
                    columns = {column["name"]: column for column in inspector.get_columns(name)}
                    self.assertEqual(set(columns), set(expected.columns.keys()))
                    for column in expected.columns:
                        self.assertEqual(columns[column.name]["nullable"], column.nullable)
                    actual_indexes = {
                        index["name"]: tuple(index["column_names"]) for index in inspector.get_indexes(name)
                    }
                    self.assertEqual(
                        actual_indexes, {index.name: tuple(c.name for c in index.columns) for index in expected.indexes}
                    )
                    actual_fks = {
                        (tuple(fk["constrained_columns"]), fk["referred_table"], tuple(fk["referred_columns"]))
                        for fk in inspector.get_foreign_keys(name)
                    }
                    expected_fks = {
                        (
                            tuple(element.parent.name for element in fk.elements),
                            fk.referred_table.name,
                            tuple(element.column.name for element in fk.elements),
                        )
                        for fk in expected.foreign_key_constraints
                    }
                    self.assertEqual(actual_fks, expected_fks)
                    actual_unique = {tuple(item["column_names"]) for item in inspector.get_unique_constraints(name)}
                    expected_unique = {
                        tuple(c.name for c in item.columns)
                        for item in expected.constraints
                        if isinstance(item, sa.UniqueConstraint)
                    }
                    self.assertEqual(actual_unique, expected_unique)
            with Operations.context(context):
                module.downgrade()
            self.assertEqual(set(sa.inspect(connection).get_table_names()), {"lazer_users", "rooms"})
            self.assertEqual(connection.execute(sa.text("SELECT * FROM rooms")).one(), (42, "existing room"))

    def test_mysql_upgrade_ddl_compiles_with_singleton_and_old_room_default(self):
        output = StringIO()
        context = MigrationContext.configure(dialect_name="mysql", opts={"as_sql": True, "output_buffer": output})
        with Operations.context(context):
            migration_module().upgrade()
        sql = output.getvalue()
        self.assertIn("ALTER TABLE rooms ADD COLUMN tournament_mode BOOL NOT NULL DEFAULT false", sql)
        self.assertIn("INSERT INTO somsai_lock (id) VALUES (1)", sql)
        self.assertIn("CONSTRAINT uq_somsai_rating UNIQUE (user_id, ruleset_id, variant_id, format)", sql)
