"""Ranked Elo edits against an isolated in-memory SQL database, never the server DB."""

# The project's test runner is unittest; pytest is not a project dependency.
# ruff: noqa: PT027

from copy import deepcopy
from datetime import timedelta
import secrets
from typing import Any
import unittest

from app.database import AdminAuditEvent, MatchmakingPool, MatchmakingUserStats, Room, RoomParticipatedUser
from app.helpers import utcnow
from app.models.room import MatchType, QueueMode, RoomStatus
from app.service.ranked_elo_admin_service import (
    RankedEloConflictError,
    RankedEloNotFoundError,
    change_ranked_elo,
    current_elo,
    elo_version,
    list_ranked_elo,
)

from sqlalchemy.dialects import mysql
from sqlalchemy.orm import lazyload
from sqlmodel import Session, SQLModel, create_engine, select


class AsyncTestSession:
    """Execute the service's SQL in SQLite without adding an async test DB dependency."""

    def __init__(self, session: Session):
        self.session = session
        self.statements: list[Any] = []

    async def exec(self, statement: Any) -> Any:
        self.statements.append(statement)
        return self.session.exec(statement)

    async def get(self, model: Any, identity: Any) -> Any:
        return self.session.get(model, identity)

    def add(self, item: Any) -> None:
        self.session.add(item)

    async def flush(self) -> None:
        self.session.flush()

    async def commit(self) -> None:
        self.session.commit()

    async def rollback(self) -> None:
        self.session.rollback()


class RankedEloAdminTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(
            self.engine,
            tables=[
                SQLModel.metadata.tables[model.__tablename__]
                for model in (MatchmakingPool, MatchmakingUserStats, Room, RoomParticipatedUser, AdminAuditEvent)
            ],
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        self.db.add_all(
            [
                MatchmakingPool(id=1, name="osu ranked", type="ranked_play", ranked=True),
                MatchmakingPool(id=2, name="mania ranked", ruleset_id=3, variant_id=4, type="ranked_play", ranked=True),
                MatchmakingPool(id=3, name="quick", type="quick_play", ranked=True),
                MatchmakingPool(id=4, name="not ranked", type="ranked_play", ranked=False),
            ]
        )
        self.original_data = {
            "initial_rating": {"mu": 1234.56, "sig": 150},
            "contest_count": 8,
            "approximate_posterior": {"mu": 1427.12, "sig": 87.65},
            "future_field": {"keep": True},
        }
        self.stats = MatchmakingUserStats(
            user_id=42, pool_id=1, first_placements=3, total_points=27, elo_data=deepcopy(self.original_data)
        )
        self.db.add_all(
            [
                self.stats,
                MatchmakingUserStats(user_id=42, pool_id=2, elo_data=deepcopy(self.original_data)),
                MatchmakingUserStats(user_id=43, pool_id=1, elo_data=deepcopy(self.original_data)),
            ]
        )
        self.db.commit()
        self.version = elo_version(self.stats)
        self.csrf = secrets.token_urlsafe(16)

    def all_stats(self) -> list[MatchmakingUserStats]:
        self.db.expire_all()
        return list(self.db.exec(select(MatchmakingUserStats).options(lazyload("*"))).all())

    async def test_edit_preserves_rating_state_and_other_players_and_pools(self) -> None:
        before, after = await change_ranked_elo(self.session, 42, 1, 2000, self.version)
        await self.session.commit()
        for item in self.all_stats():
            expected = deepcopy(self.original_data)
            if (item.user_id, item.pool_id) == (42, 1):
                expected["approximate_posterior"]["mu"] = 2000
                assert item.first_placements == 3
                assert item.total_points == 27
                assert item.updated_at is not None
            assert item.elo_data == expected
        assert before["elo"] == 1427.12
        assert after["elo"] == 2000
        assert before["version"] != after["version"]
        lock_query = self.session.statements[-1]
        assert "FOR UPDATE" in str(lock_query.compile(dialect=mysql.dialect()))
        assert lock_query.get_execution_options()["populate_existing"] is True

    async def test_initial_rating_is_created_only_for_the_selected_pool(self) -> None:
        before, after = await change_ranked_elo(self.session, 44, 2, 0, elo_version(None))
        await self.session.commit()
        created = [item for item in self.all_stats() if item.user_id == 44]
        assert len(created) == 1
        assert created[0].pool_id == 2
        assert created[0].elo_data == {
            "initial_rating": {"mu": 0, "sig": 150},
            "contest_count": 0,
            "approximate_posterior": {"mu": 0, "sig": 150},
        }
        assert before["elo"] is None
        assert after["elo"] == 0
        assert after["contest_count"] == 0

    async def test_listing_does_not_create_stats_and_excludes_non_ranked_pools(self) -> None:
        items = await list_ranked_elo(self.session, 44)
        assert [item["pool_id"] for item in items] == [1, 2]
        assert all(item["elo"] is None for item in items)
        assert len(self.all_stats()) == 3
        items = await list_ranked_elo(self.session, 42)
        assert [item["elo"] for item in items] == [1427.12, 1427.12]

    async def test_stale_form_cannot_overwrite_a_changed_rating(self) -> None:
        await change_ranked_elo(self.session, 42, 1, 1900, self.version)
        await self.session.commit()
        with self.assertRaisesRegex(RankedEloConflictError, "refresh the player"):
            await change_ranked_elo(self.session, 42, 1, 2000, self.version)
        await self.session.rollback()
        assert next(current_elo(item) for item in self.all_stats() if (item.user_id, item.pool_id) == (42, 1)) == 1900

    async def test_even_unchanged_rounded_rating_with_new_contest_is_stale(self) -> None:
        data = deepcopy(self.original_data)
        data["contest_count"] += 1
        self.stats.elo_data = data
        self.db.commit()
        with self.assertRaisesRegex(RankedEloConflictError, "refresh the player"):
            await change_ranked_elo(self.session, 42, 1, 2000, self.version)

    async def test_non_ranked_or_missing_pool_rejected(self) -> None:
        for pool_id in (3, 4, 999):
            with self.subTest(pool=pool_id), self.assertRaises(RankedEloNotFoundError):
                await change_ranked_elo(self.session, 42, pool_id, 2000, elo_version(None))

    async def test_active_ranked_room_blocks_edit_but_finished_or_left_room_does_not(self) -> None:
        room = Room(
            id=1,
            name="Ranked",
            type=MatchType.RANKED_PLAY,
            status=RoomStatus.PLAYING,
            queue_mode=QueueMode.HOST_ONLY,
            auto_skip=False,
            auto_start_duration=0,
            host_id=2,
        )
        participant = RoomParticipatedUser(id=1, room_id=1, user_id=42)
        self.db.add_all([room, participant])
        self.db.commit()
        with self.assertRaisesRegex(RankedEloConflictError, "active Ranked room"):
            await change_ranked_elo(self.session, 42, 1, 2000, self.version)
        participant.left_at = utcnow()
        self.db.commit()
        _, after = await change_ranked_elo(self.session, 42, 1, 2000, self.version)
        await self.session.commit()
        participant.left_at = None
        room.ends_at = utcnow() - timedelta(minutes=1)
        self.db.commit()
        await change_ranked_elo(self.session, 42, 1, 2100, after["version"])

    async def test_no_op_edit_rejected(self) -> None:
        _, after = await change_ranked_elo(self.session, 42, 1, 2000, self.version)
        await self.session.commit()
        with self.assertRaisesRegex(RankedEloConflictError, "already set"):
            await change_ranked_elo(self.session, 42, 1, 2000, after["version"])

