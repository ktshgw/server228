from contextlib import asynccontextmanager
import json
from pathlib import Path
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import BestScore
from app.database.score import _process_score_pp
from app.models.mods import RANKED_MODS, APIMod, mods_can_get_pp
from app.models.mods.performance import DEFAULT_RANKED_MODS
from app.models.score import GameMode
from app.tasks.recalculate_failed_score import (
    ADJUSTED_MODS_PP_BACKFILL_MARKER_KEY,
    SCORE_RETRY_PROCESSING_KEY,
    SCORE_RETRY_QUEUE_KEY,
    enqueue_adjusted_mods_pp_backfill,
)
from tests.test_score_lifecycle import QueryResult, RetryRedis

from sqlalchemy.dialects import mysql


def deployed_mods() -> dict[int, Any]:
    return {
        int(mode): mods
        for mode, mods in json.loads(Path("config/ranked_mods.json").read_text(encoding="utf-8")).items()
        if not mode.startswith("$")
    }


class AdjustedModPolicyTests(unittest.TestCase):
    def test_speed_settings_are_ranked_in_all_four_modes(self) -> None:
        for policy in (DEFAULT_RANKED_MODS, deployed_mods()):
            with patch.dict(RANKED_MODS, policy, clear=True):
                for mode in range(4):
                    for acronym in ("DT", "NC"):
                        for speed in (1.01, 1.2, 1.3, 1.5, 1.75, 2):
                            with self.subTest(mode=mode, acronym=acronym, speed=speed):
                                mod: APIMod = {"acronym": acronym, "settings": {"speed_change": speed}}
                                assert mods_can_get_pp(mode, [mod])
                        assert mods_can_get_pp(mode, [{"acronym": acronym}])
                        assert mods_can_get_pp(mode, [{"acronym": acronym, "settings": {}}])
                    assert mods_can_get_pp(
                        mode, [{"acronym": "DT", "settings": {"speed_change": 1.3, "adjust_pitch": True}}]
                    )

    def test_da_is_unranked_with_default_settings_and_with_ranked_speed_mods(self) -> None:
        settings_by_mode = [
            {"circle_size": 6, "approach_rate": 10.5, "overall_difficulty": 9.2, "drain_rate": 3},
            {"scroll_speed": 1.5, "overall_difficulty": 8, "drain_rate": 4},
            {"circle_size": 5, "approach_rate": 10.5, "hard_rock_offsets": True},
            {"overall_difficulty": 9, "drain_rate": 2},
        ]
        for policy in (DEFAULT_RANKED_MODS, deployed_mods()):
            with patch.dict(RANKED_MODS, policy, clear=True):
                for mode, da_settings in enumerate(settings_by_mode):
                    mods = cast(
                        list[APIMod],
                        [
                            {
                                "acronym": "DA",
                                "settings": {
                                    **da_settings,
                                    "extended_limits": True,
                                },
                            }
                        ],
                    )
                    with self.subTest(mode=mode):
                        assert not mods_can_get_pp(mode, [{"acronym": "DA"}])
                        assert not mods_can_get_pp(mode, mods)
                        assert not mods_can_get_pp(mode, [*mods, {"acronym": "DT", "settings": {"speed_change": 1.3}}])
                        assert not mods_can_get_pp(mode, [*mods, {"acronym": "NC", "settings": {"speed_change": 1.8}}])
                        assert not mods_can_get_pp(mode, [*mods, {"acronym": "AT"}])

    def test_invalid_speed_and_other_unranked_settings_stay_ineligible(self) -> None:
        for policy in (DEFAULT_RANKED_MODS, deployed_mods()):
            with patch.dict(RANKED_MODS, policy, clear=True):
                for mode in range(4):
                    for acronym in ("DT", "NC"):
                        for speed in (0, -1, 1, 2.01, float("inf"), float("nan")):
                            with self.subTest(mode=mode, acronym=acronym, speed=speed):
                                assert not mods_can_get_pp(
                                    mode, [{"acronym": acronym, "settings": {"speed_change": speed}}]
                                )
                    assert not mods_can_get_pp(mode, [{"acronym": "HT", "settings": {"speed_change": 0.8}}])
                    assert not mods_can_get_pp(mode, [{"acronym": "DC", "settings": {"speed_change": 0.8}}])


class AdjustedModScoreTests(unittest.IsolatedAsyncioTestCase):
    async def test_adjusted_plays_reach_pp_calculator_and_best_scores(self) -> None:
        combinations: list[list[APIMod]] = [
            [{"acronym": "DT", "settings": {"speed_change": 1.3}}],
            [{"acronym": "NC", "settings": {"speed_change": 1.8}}],
        ]
        for mods in combinations:
            score = SimpleNamespace(
                id=123,
                user_id=7,
                beatmap_id=42,
                gamemode=GameMode.OSU,
                passed=True,
                ranked=True,
                pp=0,
                mods=mods,
                accuracy=0.98,
            )
            session = Mock()
            calculator = AsyncMock(return_value=(123.45, True))
            with (
                patch.dict(RANKED_MODS, deployed_mods(), clear=True),
                patch("app.database.score.pre_fetch_and_calculate_pp", calculator),
                patch("app.database.score.get_user_best_pp_in_beatmap", AsyncMock(return_value=None)),
            ):
                assert await _process_score_pp(cast(Any, score), session, Mock(), Mock())
            calculator.assert_awaited_once()
            assert calculator.await_args is not None
            assert calculator.await_args.args[0].mods == mods
            assert score.pp == 123.45
            best = session.add.call_args.args[0]
            assert isinstance(best, BestScore)
            assert best.score_id == score.id
            assert best.pp == 123.45

    async def test_da_cannot_reuse_cached_pp_or_create_a_pp_best(self) -> None:
        for pp in (0, 123.45):
            for mode in range(4):
                score = SimpleNamespace(
                    id=123,
                    gamemode=GameMode.from_int(mode),
                    passed=True,
                    ranked=True,
                    pp=pp,
                    mods=[{"acronym": "DA"}, {"acronym": "NC", "settings": {"speed_change": 1.2}}],
                )
                session = Mock()
                with (
                    patch.dict(RANKED_MODS, deployed_mods(), clear=True),
                    patch("app.database.score.pre_fetch_and_calculate_pp", AsyncMock()) as calculator,
                ):
                    assert await _process_score_pp(cast(Any, score), session, Mock(), Mock())
                calculator.assert_not_awaited()
                session.add.assert_not_called()
                assert score.pp == 0

    async def test_unranked_maps_failed_plays_and_autoplay_do_not_gain_pp(self) -> None:
        for passed, ranked, extra_mods in ((True, False, []), (False, True, []), (True, True, [{"acronym": "AT"}])):
            score = SimpleNamespace(
                id=123,
                gamemode=GameMode.OSU,
                passed=passed,
                ranked=ranked,
                pp=0,
                mods=[{"acronym": "DT", "settings": {"speed_change": 1.3}}, *extra_mods],
            )
            session = Mock()
            with (
                patch.dict(RANKED_MODS, deployed_mods(), clear=True),
                patch("app.database.score.pre_fetch_and_calculate_pp", AsyncMock()) as calculator,
            ):
                assert await _process_score_pp(cast(Any, score), session, Mock(), Mock())
            calculator.assert_not_awaited()
            session.add.assert_not_called()
            assert score.pp == 0

    async def test_backfill_is_filtered_paginated_and_idempotent(self) -> None:
        redis = RetryRedis()
        redis.lists[SCORE_RETRY_PROCESSING_KEY] = ["14"]
        session = SimpleNamespace(
            exec=AsyncMock(
                side_effect=[
                    QueryResult(
                        [
                            (10, GameMode.OSU, [{"acronym": "DT", "settings": {"speed_change": 1.3}}]),
                            (11, GameMode.TAIKO, [{"acronym": "DA", "settings": {"overall_difficulty": 8}}]),
                            (12, GameMode.OSU, [{"acronym": "DT"}]),
                            (13, GameMode.OSU, [{"acronym": "DA"}, {"acronym": "AT"}]),
                        ]
                    ),
                    QueryResult(
                        [
                            (14, GameMode.MANIA, [{"acronym": "NC", "settings": {"speed_change": 1.8}}]),
                            (15, GameMode.FRUITS, [{"acronym": "NC", "settings": {"speed_change": 1.2}}]),
                            (16, GameMode.OSU, [{"acronym": "DT", "settings": {"speed_change": 3}}]),
                        ]
                    ),
                ]
            )
        )

        @asynccontextmanager
        async def fake_with_db():
            yield session

        with (
            patch.dict(RANKED_MODS, deployed_mods(), clear=True),
            patch("app.tasks.recalculate_failed_score.with_db", fake_with_db),
            patch("app.tasks.recalculate_failed_score.UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE", 4),
        ):
            assert await enqueue_adjusted_mods_pp_backfill(redis) == 2
            assert await enqueue_adjusted_mods_pp_backfill(redis) == 0
        assert redis.lists[SCORE_RETRY_QUEUE_KEY] == ["10", "15"]
        assert redis.lists[SCORE_RETRY_PROCESSING_KEY] == ["14"]
        assert redis.values[ADJUSTED_MODS_PP_BACKFILL_MARKER_KEY] == "1"
        assert session.exec.await_count == 2
        sql = str(session.exec.await_args_list[0].args[0].compile(dialect=mysql.dialect()))
        for condition in (
            "scores.passed IS true",
            "scores.processed IS true",
            "scores.ranked IS true",
            "scores.pp =",
            "scores.room_id IS NULL",
            "scores.playlist_item_id IS NULL",
        ):
            assert condition in sql
