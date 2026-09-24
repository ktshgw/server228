"""Dodge progression and transaction persistence against an isolated database."""

# ruff: noqa: PT027
from datetime import UTC, datetime, timedelta
from typing import Any
import unittest

from app.database import (
    AdminAuditEvent,
    LoginSession,
    OAuthToken,
    RankedDodgePenalty,
    Room,
    TrustedDevice,
    User,
)
from app.models.room import MatchType, QueueMode, RoomStatus
from app.service.ranked_dodge_service import dodge_status, penalty_end, register_dodge
from tests.test_ranked_elo_admin import AsyncTestSession

from sqlalchemy import delete
from sqlalchemy.dialects import mysql
from sqlalchemy.orm import lazyload
from sqlmodel import Session, SQLModel, col, create_engine, select


class RankedDodgeTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(
            self.engine,
            tables=[
                SQLModel.metadata.tables[model.__tablename__]
                for model in (User, Room, RankedDodgePenalty, AdminAuditEvent, LoginSession, TrustedDevice, OAuthToken)
            ],
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        self.now = datetime(2026, 9, 6, tzinfo=UTC)
        for user_id in (100, 101):
            self.db.add(
                User(id=user_id, username=f"test{user_id}", email=f"{user_id}@example.invalid", pw_bcrypt="test")
            )
        for room_id in range(1, 20):
            self.db.add(
                Room(
                    id=room_id,
                    name="Ranked test",
                    type=MatchType.RANKED_PLAY,
                    status=RoomStatus.IDLE,
                    queue_mode=QueueMode.HOST_ONLY,
                    auto_skip=False,
                    auto_start_duration=0,
                    host_id=100,
                )
            )
        self.db.commit()

    async def test_first_dodge_and_duplicate_http_retry(self):
        result = await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.commit()
        assert result.level == 1
        assert result.expires_at == self.now + timedelta(minutes=5)
        duplicate = await register_dodge(self.session, 1, 100, now=self.now + timedelta(minutes=1))
        await self.session.commit()
        assert duplicate == result
        assert len(self.db.exec(select(RankedDodgePenalty)).all()) == 1
        assert len(self.db.exec(select(AdminAuditEvent)).all()) == 1

    async def test_other_player_leaving_cancelled_room_is_not_penalised(self):
        first = await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.commit()
        second = await register_dodge(self.session, 1, 101, now=self.now)
        await self.session.commit()
        assert first == second
        assert (await dodge_status(self.session, 101)).level == 0

    async def test_retry_after_room_cleanup_preserves_penalty(self):
        first = await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.commit()
        self.db.exec(delete(Room).where(col(Room.id) == 1))
        self.db.commit()
        repeated = await register_dodge(self.session, 1, 100, now=self.now + timedelta(hours=1))
        assert repeated == first
        assert (await dodge_status(self.session, 100)).level == 1

    async def test_deferred_first_write_survives_room_cleanup(self):
        self.db.exec(delete(Room).where(col(Room.id) == 1))
        self.db.commit()
        with self.assertRaises(ValueError):
            await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.rollback()
        saved = await register_dodge(self.session, 1, 100, now=self.now, allow_missing_room=True)
        await self.session.commit()
        assert saved.level == 1
        assert (await register_dodge(self.session, 1, 100, now=self.now)) == saved

    async def test_progression_survives_session_restart_and_long_bans(self):
        now = self.now
        for level in range(1, 15):
            result = await register_dodge(self.session, level, 100, now=now)
            await self.session.commit()
            assert result.level == level
            assert result.expires_at == penalty_end(now, level)
            assert result.account_banned == (level == 14)
            # Release the identity map to exercise a genuinely persisted history.
            self.db.expunge_all()
            restored = await dodge_status(self.session, 100)
            assert restored == result
            if result.expires_at:
                now = result.expires_at + timedelta(hours=1)
        active = self.db.exec(select(User.is_active).where(User.id == 100)).one()
        assert not active

    async def test_48_hours_after_expiry_resets_progression(self):
        first = await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.commit()
        assert first.expires_at is not None
        second = await register_dodge(self.session, 2, 100, now=first.expires_at + timedelta(hours=48))
        assert second.level == 1

    async def test_48_hours_after_start_does_not_reset_early(self):
        await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.commit()
        second = await register_dodge(self.session, 2, 100, now=self.now + timedelta(hours=48))
        assert second.level == 2

    async def test_rollback_does_not_consume_level_or_leave_audit(self):
        await register_dodge(self.session, 1, 100, now=self.now)
        await self.session.rollback()
        assert (await dodge_status(self.session, 100)).level == 0
        assert self.db.exec(select(AdminAuditEvent)).all() == []

    async def test_only_user_and_room_are_locked_without_eager_join(self):
        await register_dodge(self.session, 1, 100, now=self.now)
        queries = [str(stmt.compile(dialect=mysql.dialect())) for stmt in self.session.statements]
        locks = [query for query in queries if "FOR UPDATE" in query]
        assert len(locks) == 2
        assert all("JOIN" not in query for query in locks)

    async def test_non_ranked_room_is_rejected(self):
        room = self.db.exec(select(Room).options(lazyload("*")).where(Room.id == 1)).one()
        assert room is not None
        room.type = MatchType.HEAD_TO_HEAD
        self.db.commit()
        with self.assertRaises(ValueError):
            await register_dodge(self.session, 1, 100, now=self.now)

    def test_calendar_months_clamp_month_end(self):
        assert penalty_end(datetime(2026, 11, 30, tzinfo=UTC), 11) == datetime(2027, 2, 28, tzinfo=UTC)
        assert penalty_end(datetime(2028, 2, 29, tzinfo=UTC), 13) == datetime(2029, 2, 28, tzinfo=UTC)
