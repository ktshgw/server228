from contextlib import asynccontextmanager
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import User
from app.database.item_attempts_count import ItemAttemptsCount
from app.database.playlist_best_score import process_playlist_best_score
from app.dependencies.fetcher import Fetcher
from app.models.room import RoomCategory
from app.models.score import Rank, SoloScoreSubmissionInfo
from app.router.v2.score import submit_playlist_score, submit_score
from app.tasks.recalculate_failed_score import (
    SCORE_RETRY_PROCESSING_KEY,
    SCORE_RETRY_QUEUE_KEY,
    enqueue_missing_score_retries,
    enqueue_pending_score_import_retries,
)

from fastapi import BackgroundTasks
from redis.asyncio import Redis
from sqlalchemy.dialects import mysql
from sqlalchemy.exc import OperationalError
from sqlmodel.ext.asyncio.session import AsyncSession


class QueryResult:
    def __init__(self, *, first: Any = None, all_rows: list[Any] | None = None):
        self._first = first
        self._all = all_rows or []

    def first(self):
        return self._first

    def all(self):
        return self._all


class FakeRetryRedis:
    def __init__(self):
        self.lists = {
            SCORE_RETRY_QUEUE_KEY: ["1"],
            SCORE_RETRY_PROCESSING_KEY: ["2"],
        }

    async def lpos(self, key: str, value: str):
        try:
            return self.lists[key].index(value)
        except ValueError:
            return None

    async def rpush(self, key: str, value: str):
        self.lists[key].append(value)


def score_info() -> SoloScoreSubmissionInfo:
    return SoloScoreSubmissionInfo(
        rank=Rank.A,
        total_score=1234,
        total_score_without_mods=1234,
        accuracy=0.98,
        ruleset_id=0,
        passed=True,
    )


class ScoreSubmissionDurabilityTests(unittest.IsolatedAsyncioTestCase):
    async def test_deferred_score_submission_does_not_commit_or_schedule(self) -> None:
        score_token = SimpleNamespace(
            id=10,
            score_id=None,
            user_id=7,
            beatmap_id=123,
            beatmap_checksum="a" * 32,
        )
        score = SimpleNamespace(
            id=55,
            processed=False,
            beatmap_id=123,
            gamemode=0,
            passed=True,
            pp=0.0,
        )
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(return_value=QueryResult(first=score_token))
        session.get = AsyncMock(return_value=SimpleNamespace(id=123))
        session.commit = AsyncMock()
        session.refresh = AsyncMock()
        background = Mock(spec=BackgroundTasks)
        info = score_info()
        current_user = cast(User, SimpleNamespace(id=7))
        cache_service = SimpleNamespace(smart_preload_for_score=AsyncMock())

        with (
            patch("app.router.v2.score.get_beatmap_cache_service", return_value=cache_service),
            patch("app.router.v2.score.process_score", new=AsyncMock(return_value=score)),
            patch("app.router.v2.score.ScoreModel.transform", new=AsyncMock(return_value={"id": 55})),
        ):
            response, created = await submit_score(
                background,
                info,
                10,
                current_user,
                session,
                cast(Redis, Mock()),
                cast(Fetcher, Mock()),
                defer_commit=True,
            )

        assert response == {"id": 55}
        assert created
        assert score_token.score_id == 55
        session.commit.assert_not_awaited()
        session.refresh.assert_not_awaited()
        background.add_task.assert_not_called()

    async def test_duplicate_unprocessed_score_reschedules_finalizer(self) -> None:
        score = SimpleNamespace(id=55, user_id=7, processed=False)
        score_token = SimpleNamespace(id=10, score_id=55, user_id=7)
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(
            side_effect=[
                QueryResult(first=score_token),
                QueryResult(first=score),
            ]
        )
        session.commit = AsyncMock()
        session.rollback = AsyncMock()
        background = Mock(spec=BackgroundTasks)

        with patch("app.router.v2.score.ScoreModel.transform", new=AsyncMock(return_value={"id": 55})):
            response, created = await submit_score(
                background,
                score_info(),
                10,
                cast(User, SimpleNamespace(id=7)),
                session,
                cast(Redis, Mock()),
                cast(Fetcher, Mock()),
            )

        assert response == {"id": 55}
        assert not created
        session.commit.assert_not_awaited()
        session.rollback.assert_awaited_once()
        background.add_task.assert_called_once()
        assert background.add_task.call_args.args[1:3] == (55, 7)

    async def test_playlist_persistent_side_effects_commit_before_external_effects(self) -> None:
        events: list[str] = []
        item = SimpleNamespace(id=9)
        room = SimpleNamespace(id=4, category=RoomCategory.NORMAL)
        score_token = SimpleNamespace(id=10, user_id=7, playlist_item_id=9, room_id=4)
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(
            side_effect=[
                QueryResult(first=room),
                QueryResult(first=item),
                QueryResult(first=score_token),
            ]
        )

        async def commit() -> None:
            events.append("commit")

        async def playlist_db(*_args: Any) -> None:
            events.append("playlist_db")

        async def attempts_db(*_args: Any) -> None:
            events.append("attempts_db")

        session.commit = AsyncMock(side_effect=commit)
        session.get = AsyncMock(return_value=SimpleNamespace())
        current_user = SimpleNamespace(id=7, is_restricted=AsyncMock(return_value=False))
        redis = Mock()
        redis.exists = AsyncMock(side_effect=lambda _key: events.append("redis") or False)
        background = Mock(spec=BackgroundTasks)

        with (
            patch(
                "app.router.v2.score.submit_score",
                new=AsyncMock(return_value=({"id": 55, "total_score": 1234, "passed": True}, True)),
            ) as submit,
            patch("app.router.v2.score.process_playlist_best_score", new=AsyncMock(side_effect=playlist_db)),
            patch("app.router.v2.score.ItemAttemptsCount.get_or_create", new=AsyncMock(side_effect=attempts_db)),
            patch(
                "app.router.v2.score._schedule_score_finalization",
                side_effect=lambda *_args: events.append("schedule"),
            ),
            patch("app.router.v2.score.hub.emit", side_effect=lambda *_args: events.append("event")),
            patch("app.router.v2.score.ScoreModel.transform", new=AsyncMock(return_value={"id": 55})),
        ):
            await submit_playlist_score(
                background,
                session,
                4,
                9,
                10,
                score_info(),
                cast(User, current_user),
                cast(Redis, redis),
                cast(Fetcher, Mock()),
            )

        assert submit.await_args is not None
        assert submit.await_args.kwargs["defer_commit"] is True
        assert events == ["playlist_db", "attempts_db", "commit", "schedule", "event", "redis"]
        # FOR UPDATE must not lock joined beatmaps, sets or asynchronously loaded
        # playlist rows while a different transaction finalises another score.
        for call in session.exec.await_args_list[:2]:
            sql = str(call.args[0].compile(dialect=mysql.dialect()))
            assert "FOR UPDATE" in sql
            assert "JOIN" not in sql

    async def test_playlist_retries_deadlock_without_losing_submission(self) -> None:
        session = Mock(spec=AsyncSession)
        session.rollback = AsyncMock()
        session.refresh = AsyncMock()
        user = cast(User, SimpleNamespace(id=7))
        failure = OperationalError("locked query", {}, Exception(1213, "Deadlock"))
        with patch(
            "app.router.v2.score._submit_playlist_score_once", AsyncMock(side_effect=[failure, {"id": 55}])
        ) as submit:
            response = await submit_playlist_score(
                Mock(spec=BackgroundTasks),
                session,
                4,
                9,
                10,
                score_info(),
                user,
                cast(Redis, Mock()),
                cast(Fetcher, Mock()),
            )
        assert response == {"id": 55}
        assert submit.await_count == 2
        session.rollback.assert_awaited_once()
        session.refresh.assert_awaited_once_with(user)

    async def test_playlist_helper_flushes_without_committing(self) -> None:
        previous = SimpleNamespace(
            score=SimpleNamespace(passed=True),
            score_id=40,
            total_score=1000,
            attempts=2,
        )
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(return_value=QueryResult(first=previous))
        session.flush = AsyncMock()
        session.commit = AsyncMock()

        await process_playlist_best_score(4, 9, 7, 55, 1234, session)

        assert previous.score_id == 55
        assert previous.total_score == 1234
        assert previous.attempts == 3
        session.flush.assert_awaited_once()
        session.commit.assert_not_awaited()

    async def test_item_attempt_aggregate_flushes_without_committing(self) -> None:
        best = SimpleNamespace(
            attempts=3,
            total_score=1234,
            score=SimpleNamespace(pp=50.0, passed=True, accuracy=0.98),
        )
        aggregate = ItemAttemptsCount(room_id=4, user_id=7)
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(return_value=QueryResult(all_rows=[best]))
        session.flush = AsyncMock()
        session.commit = AsyncMock()

        await aggregate.update(session)

        assert aggregate.attempts == 3
        assert aggregate.completed == 1
        assert aggregate.total_score == 1234
        session.flush.assert_awaited_once()
        session.commit.assert_not_awaited()

    async def test_recovery_enqueue_deduplicates_queue_and_processing_list(self) -> None:
        redis = FakeRetryRedis()

        enqueued = await enqueue_missing_score_retries(cast(Any, redis), [1, 2, 3, 3])

        assert enqueued == 1
        assert redis.lists[SCORE_RETRY_QUEUE_KEY] == ["1", "3"]
        assert redis.lists[SCORE_RETRY_PROCESSING_KEY] == ["2"]

    async def test_import_pp_outbox_recovers_only_pending_rows(self) -> None:
        redis = FakeRetryRedis()
        session = SimpleNamespace(
            exec=AsyncMock(
                return_value=QueryResult(
                    all_rows=[
                        (10, 101, {"pp_pending": True}),
                        (11, 102, {"pp_pending": False}),
                        (12, None, {"pp_pending": True}),
                        (13, 103, {}),
                    ]
                )
            )
        )

        @asynccontextmanager
        async def fake_with_db():
            yield session

        with patch("app.tasks.recalculate_failed_score.with_db", fake_with_db):
            enqueued = await enqueue_pending_score_import_retries(cast(Any, redis))

        assert enqueued == 1
        assert redis.lists[SCORE_RETRY_QUEUE_KEY] == ["1", "101"]


if __name__ == "__main__":
    unittest.main()
