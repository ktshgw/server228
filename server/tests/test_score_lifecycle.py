from contextlib import asynccontextmanager
from datetime import UTC, datetime
import json
from pathlib import Path
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.calculating.calculators.performance_server import PerformanceServerPerformanceCalculator
from app.database.score import Score
from app.models.error import ErrorType, RequestError
from app.models.mods import RANKED_MODS, APIMod, init_mods, mods_can_get_pp
from app.models.mods.performance import DEFAULT_RANKED_MODS
from app.models.score import GameMode
from app.router.lio import ReplayDataRequest, save_replay
from app.router.private.admin_panel import AdminContext, list_admin_user_scores
from app.router.v2.score import download_score_replay
from app.service.database_cleanup_service import FAILED_SOLO_SCORE_RETENTION, DatabaseCleanupService
from app.service.replay_retention_service import replay_mod_combination_key
from app.storage import StorageService
from app.tasks.recalculate_failed_score import (
    CLASSIC_PP_BACKFILL_MARKER_KEY,
    SCORE_RETRY_PROCESSING_KEY,
    SCORE_RETRY_QUEUE_KEY,
    enqueue_classic_pp_backfill,
)

from redis.asyncio import Redis
from sqlalchemy.dialects import mysql
from sqlalchemy.orm import Session, make_transient_to_detached
from sqlmodel.ext.asyncio.session import AsyncSession


class QueryResult:
    def __init__(self, rows: list[Any]):
        self.rows = rows

    def all(self) -> list[Any]:
        return self.rows

    def one(self) -> Any:
        return self.rows[0]


class RetryRedis:
    def __init__(self) -> None:
        self.values: dict[str, str] = {}
        self.lists = {
            SCORE_RETRY_QUEUE_KEY: [],
            SCORE_RETRY_PROCESSING_KEY: [],
        }

    async def get(self, key: str) -> str | None:
        return self.values.get(key)

    async def set(self, key: str, value: str) -> None:
        self.values[key] = value

    async def lpos(self, key: str, value: str) -> int | None:
        try:
            return self.lists[key].index(value)
        except ValueError:
            return None

    async def rpush(self, key: str, value: str) -> None:
        self.lists[key].append(value)


def replay_score(
    score_id: int,
    *,
    total_score: int = 1_000_000,
    mods: list[APIMod] | None = None,
    passed: bool = True,
    has_replay: bool = False,
    leaderboard_eligible: bool = True,
) -> SimpleNamespace:
    return SimpleNamespace(
        id=score_id,
        user_id=7,
        beatmap_id=123,
        gamemode=GameMode.OSU,
        mods=mods or [],
        total_score=total_score,
        passed=passed,
        leaderboard_eligible=leaderboard_eligible,
        room_id=None,
        playlist_item_id=None,
        has_replay=has_replay,
        replay_filename=f"replays/{score_id}_123_7_lazer_replay.osr",
    )


class ClassicScoreTests(unittest.IsolatedAsyncioTestCase):
    def test_private_ranked_policy_accepts_classic_with_settings(self) -> None:
        classic = {
            "acronym": "CL",
            "settings": {
                "no_slider_head_accuracy": True,
                "classic_note_lock": False,
            },
        }

        assert DEFAULT_RANKED_MODS[0]["CL"] == {}
        with patch.dict(RANKED_MODS, {0: {"CL": {}}}, clear=True):
            assert mods_can_get_pp(0, cast(list[APIMod], [classic]))

        deployed_config = json.loads((Path("config") / "ranked_mods.json").read_text(encoding="utf-8"))
        assert deployed_config["0"]["CL"] == {}

    async def test_performance_request_keeps_classic_settings(self) -> None:
        request_json: dict[str, Any] = {}

        class Response:
            status_code = 200
            text = '{"pp": 123.45}'

        class Client:
            def __init__(self, *_args: Any, **_kwargs: Any) -> None:
                pass

            async def __aenter__(self):
                return self

            async def __aexit__(self, *_args: object) -> None:
                pass

            async def post(self, _url: str, *, json: dict[str, Any]):
                request_json.update(json)
                return Response()

        score = SimpleNamespace(
            beatmap_id=123,
            map_md5="a" * 32,
            accuracy=0.98,
            max_combo=500,
            mods=[{"acronym": "CL", "settings": {"classic_note_lock": False}}],
            gamemode=GameMode.OSU,
            n300=500,
            n100=10,
            n50=0,
            nmiss=1,
            ngeki=0,
            nkatu=0,
            nlarge_tick_hit=20,
            nlarge_tick_miss=0,
            nsmall_tick_hit=0,
            nslider_tail_hit=20,
            nsmall_tick_miss=0,
        )

        with patch("app.calculating.calculators.performance_server.AsyncClient", Client):
            result = await PerformanceServerPerformanceCalculator().calculate_performance(
                "osu file",
                cast(Any, score),
            )

        assert result.pp == 123.45
        assert request_json["mods"] == score.mods

    async def test_existing_classic_zero_pp_score_is_enqueued_once(self) -> None:
        redis = RetryRedis()
        session = SimpleNamespace(
            exec=AsyncMock(
                return_value=QueryResult(
                    [
                        (40, [{"acronym": "CL", "settings": {"classic_note_lock": True}}]),
                        (41, [{"acronym": "HD"}]),
                    ]
                )
            )
        )

        @asynccontextmanager
        async def fake_with_db():
            yield session

        with patch("app.tasks.recalculate_failed_score.with_db", fake_with_db):
            first = await enqueue_classic_pp_backfill(cast(Redis, redis))
            second = await enqueue_classic_pp_backfill(cast(Redis, redis))

        assert first == 1
        assert second == 0
        assert redis.lists[SCORE_RETRY_QUEUE_KEY] == ["40"]
        assert redis.values[CLASSIC_PP_BACKFILL_MARKER_KEY] == "1"
        query = session.exec.await_args.args[0]
        sql = str(query.compile(dialect=mysql.dialect()))
        assert "scores.room_id IS NULL" in sql
        assert "scores.playlist_item_id IS NULL" in sql


class FailedScoreLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_rejected_upload_deletes_file_after_session_expires_score(self) -> None:
        for available in [False, True]:
            with self.subTest(has_replay=available):
                values = vars(replay_score(51, total_score=800_000, has_replay=available)).copy()
                replay_path = values.pop("replay_filename")
                score = Score(**values)
                make_transient_to_detached(score)
                with Session() as identity_session:
                    identity_session.add(score)
                    db = Mock(spec=AsyncSession)
                    db.exec = AsyncMock(return_value=QueryResult([replay_score(50), score]))
                    db.commit = AsyncMock(side_effect=lambda: identity_session.expire(score))
                    db.rollback = AsyncMock(side_effect=lambda: identity_session.expire(score))
                    storage = Mock(spec=StorageService)
                    storage.delete_file = AsyncMock()
                    storage.write_file = AsyncMock()

                    result = await save_replay(
                        ReplayDataRequest(score_id=51, user_id=7, beatmap_id=123, mreplay=""), db, storage
                    )

                assert result == {"stored": False}
                storage.delete_file.assert_awaited_once_with(replay_path)
                storage.write_file.assert_not_awaited()

    async def test_retired_replay_cannot_be_downloaded_even_if_file_cleanup_failed(self) -> None:
        score = replay_score(51, has_replay=False)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=SimpleNamespace(first=lambda: score))
        storage = Mock(spec=StorageService)
        storage.is_exists = AsyncMock(return_value=True)
        storage.read_file = AsyncMock(return_value=b"retired replay")

        try:
            await download_score_replay(51, db, None, storage)
        except RequestError as error:
            error_key = error.msg_key
        else:
            raise AssertionError("A replay marked unavailable must not be downloaded")
        assert error_key == ErrorType.REPLAY_FILE_NOT_FOUND.value[0]
        storage.read_file.assert_not_awaited()

    async def test_passed_replay_is_stored_and_marked_available(self) -> None:
        score = replay_score(51)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=QueryResult([score]))
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()
        storage.write_file = AsyncMock()

        with patch("app.router.lio.hub.emit") as emit:
            result = await save_replay(
                ReplayDataRequest(score_id=51, user_id=7, beatmap_id=123, mreplay="cmVwbGF5"),
                db,
                storage,
            )

        assert result == {"stored": True}
        storage.write_file.assert_awaited_once_with(
            score.replay_filename,
            b"replay",
            "application/x-osu-replay",
        )
        storage.delete_file.assert_not_awaited()
        assert score.has_replay is True
        db.commit.assert_awaited_once()
        emit.assert_called_once()

        query = db.exec.await_args.args[0]
        sql = str(query.compile(dialect=mysql.dialect()))
        assert "scores.user_id" in sql
        assert "scores.beatmap_id" in sql
        assert "ORDER BY scores.id" in sql
        assert "FOR UPDATE" in sql

    async def test_lower_score_in_same_mod_configuration_is_not_stored(self) -> None:
        winner = replay_score(50, total_score=1_100_000, has_replay=True)
        lower = replay_score(51, total_score=1_000_000)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=QueryResult([winner, lower]))
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()
        storage.write_file = AsyncMock()

        result = await save_replay(
            ReplayDataRequest(score_id=51, user_id=7, beatmap_id=123, mreplay="bm90IHNhdmVk"),
            db,
            storage,
        )

        assert result == {"stored": False}
        storage.write_file.assert_not_awaited()
        storage.delete_file.assert_awaited_once_with(lower.replay_filename)
        db.rollback.assert_awaited_once()
        assert winner.has_replay is True
        assert lower.has_replay is False

    async def test_higher_score_replaces_same_mod_configuration_replay(self) -> None:
        previous = replay_score(50, total_score=900_000, has_replay=True)
        winner = replay_score(51, total_score=1_000_000)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=QueryResult([previous, winner]))
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()
        storage.write_file = AsyncMock()

        result = await save_replay(
            ReplayDataRequest(score_id=51, user_id=7, beatmap_id=123, mreplay="bmV3IHJlcGxheQ=="),
            db,
            storage,
        )

        assert result == {"stored": True}
        storage.write_file.assert_awaited_once_with(
            winner.replay_filename,
            b"new replay",
            "application/x-osu-replay",
        )
        storage.delete_file.assert_awaited_once_with(previous.replay_filename)
        assert previous.has_replay is False
        assert winner.has_replay is True
        db.commit.assert_awaited_once()

    async def test_custom_speed_is_a_distinct_replay_mod_configuration(self) -> None:
        init_mods()
        default_dt = cast(list[APIMod], [{"acronym": "DT"}])
        explicit_default_dt = cast(list[APIMod], [{"acronym": "DT", "settings": {"speed_change": 1.5}}])
        custom_dt = cast(list[APIMod], [{"acronym": "DT", "settings": {"speed_change": 1.2}}])
        assert replay_mod_combination_key(GameMode.OSU, default_dt) == replay_mod_combination_key(
            GameMode.OSU,
            explicit_default_dt,
        )
        assert replay_mod_combination_key(GameMode.OSU, default_dt) != replay_mod_combination_key(
            GameMode.OSU,
            custom_dt,
        )

        existing_default = replay_score(50, total_score=1_100_000, mods=default_dt, has_replay=True)
        custom_speed = replay_score(51, total_score=1_000_000, mods=custom_dt)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=QueryResult([existing_default, custom_speed]))
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()
        storage.write_file = AsyncMock()

        result = await save_replay(
            ReplayDataRequest(score_id=51, user_id=7, beatmap_id=123, mreplay="Y3VzdG9tIHNwZWVk"),
            db,
            storage,
        )

        assert result == {"stored": True}
        storage.write_file.assert_awaited_once()
        storage.delete_file.assert_not_awaited()
        assert existing_default.has_replay is True
        assert custom_speed.has_replay is True

    async def test_failed_replay_is_deleted_instead_of_stored(self) -> None:
        score = replay_score(52, passed=False, has_replay=True)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=QueryResult([score]))
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()
        storage.write_file = AsyncMock()

        result = await save_replay(
            ReplayDataRequest(score_id=52, user_id=7, beatmap_id=123, mreplay="bm90IHNhdmVk"),
            db,
            storage,
        )

        assert result == {"stored": False}
        storage.delete_file.assert_awaited_once_with(score.replay_filename)
        storage.write_file.assert_not_awaited()
        assert score.has_replay is False
        db.commit.assert_awaited_once()

    async def test_expired_cleanup_targets_only_failed_solo_metadata(self) -> None:
        now = datetime(2026, 9, 4, 20, 0, tzinfo=UTC)
        score = SimpleNamespace(id=52, replay_filename="replays/52_123_7_lazer_replay.osr")
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(side_effect=[QueryResult([score]), QueryResult([])])
        db.execute = AsyncMock()
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()

        with patch("app.service.database_cleanup_service.utcnow", return_value=now):
            deleted = await DatabaseCleanupService.cleanup_expired_failed_solo_scores(db, storage)

        assert deleted == 1
        storage.delete_file.assert_awaited_once_with(score.replay_filename)
        assert db.execute.await_count == 2
        db.commit.assert_awaited_once()

        query = db.exec.await_args_list[0].args[0]
        compiled = query.compile(dialect=mysql.dialect())
        sql = str(compiled)
        assert "scores.passed IS false" in sql
        assert "scores.room_id IS NULL" in sql
        assert "scores.playlist_item_id IS NULL" in sql
        cutoff_values = [value for value in compiled.params.values() if isinstance(value, datetime)]
        assert now - FAILED_SOLO_SCORE_RETENTION in cutoff_values

    async def test_cleanup_keeps_metadata_when_replay_deletion_fails(self) -> None:
        score = SimpleNamespace(id=52, replay_filename="replays/52_123_7_lazer_replay.osr")
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(side_effect=[QueryResult([score]), QueryResult([])])
        db.execute = AsyncMock()
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock(side_effect=RuntimeError("storage unavailable"))

        deleted = await DatabaseCleanupService.cleanup_expired_failed_solo_scores(db, storage)

        assert deleted == 0
        db.execute.assert_not_awaited()
        db.commit.assert_not_awaited()
        db.rollback.assert_awaited_once()

    async def test_recent_failed_solo_replay_is_scrubbed_but_metadata_is_retained(self) -> None:
        score = SimpleNamespace(
            id=52,
            has_replay=True,
            replay_filename="replays/52_123_7_lazer_replay.osr",
        )
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(side_effect=[QueryResult([]), QueryResult([score])])
        db.execute = AsyncMock()
        db.commit = AsyncMock()
        db.rollback = AsyncMock()
        storage = Mock(spec=StorageService)
        storage.delete_file = AsyncMock()

        deleted = await DatabaseCleanupService.cleanup_expired_failed_solo_scores(db, storage)

        assert deleted == 0
        assert score.has_replay is False
        storage.delete_file.assert_awaited_once_with(score.replay_filename)
        db.execute.assert_not_awaited()
        db.commit.assert_awaited_once()

    async def test_admin_score_listing_excludes_failed_plays(self) -> None:
        staff = cast(
            Any,
            SimpleNamespace(is_owner=True, is_admin=False, is_bng=False, is_qat=False, is_gmt=False),
        )
        db = Mock(spec=AsyncSession)
        db.get = AsyncMock(return_value=SimpleNamespace(is_bot=False))
        db.exec = AsyncMock(side_effect=[QueryResult([0]), QueryResult([])])

        result = await list_admin_user_scores(
            user_id=7,
            context=AdminContext(user=staff, csrf_token="", session_digest=""),
            session=db,
            limit=100,
            offset=0,
        )

        assert result["items"] == []
        assert result["total"] == 0
        count_query = db.exec.await_args_list[0].args[0]
        page_query = db.exec.await_args_list[1].args[0]
        assert "scores.passed IS true" in str(count_query.compile(dialect=mysql.dialect()))
        assert "scores.passed IS true" in str(page_query.compile(dialect=mysql.dialect()))


if __name__ == "__main__":
    unittest.main()
