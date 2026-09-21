import asyncio
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace
from typing import cast
import unittest

from app.database.score import Score, calculate_playtime
from app.database.statistics import UserStatistics, UserStatisticsModel, change_grade_count
from app.models.score import GameMode, HitResult, Rank

from sqlmodel.ext.asyncio.session import AsyncSession


class GradeCountTests(unittest.TestCase):
    def test_every_passed_grade_has_an_independent_counter(self) -> None:
        statistics = UserStatistics(user_id=1, mode=GameMode.OSU)
        expected_fields = {
            Rank.XH: "grade_ssh",
            Rank.X: "grade_ss",
            Rank.SH: "grade_sh",
            Rank.S: "grade_s",
            Rank.A: "grade_a",
            Rank.B: "grade_b",
            Rank.C: "grade_c",
            Rank.D: "grade_d",
        }

        for rank, field in expected_fields.items():
            with self.subTest(rank=rank):
                change_grade_count(statistics, rank, 1)
                assert getattr(statistics, field) == 1

        change_grade_count(statistics, Rank.F, 1)
        assert sum(getattr(statistics, field) for field in expected_fields.values()) == len(expected_fields)

        async def read_grade_counts() -> dict[str, int]:
            return await UserStatisticsModel.grade_counts(cast(AsyncSession, None), statistics)

        counts = asyncio.run(read_grade_counts())
        assert counts == {"ssh": 1, "ss": 1, "sh": 1, "s": 1, "a": 1, "b": 1, "c": 1, "d": 1}

    def test_grade_decrement_never_produces_negative_counts(self) -> None:
        statistics = UserStatistics(user_id=1, mode=GameMode.OSU, grade_b=1)

        change_grade_count(statistics, Rank.B, -1)
        change_grade_count(statistics, Rank.B, -1)

        assert statistics.grade_b == 0


class PlayCountEligibilityTests(unittest.TestCase):
    @staticmethod
    def _score(*, passed: bool, seconds: int, total_score: int, hits: int):
        started_at = datetime(2026, 9, 4, tzinfo=UTC)
        return SimpleNamespace(
            passed=passed,
            started_at=started_at,
            ended_at=started_at + timedelta(seconds=seconds),
            total_score=total_score,
            mods=[],
            n300=hits,
            n100=0,
            n50=0,
            ngeki=0,
            nkatu=0,
            nlarge_tick_hit=0,
            nlarge_tick_miss=0,
            nslider_tail_hit=0,
            nsmall_tick_hit=0,
            maximum_statistics={HitResult.GREAT: 100},
        )

    def test_valid_failed_play_counts_towards_play_count(self) -> None:
        playtime, is_valid = calculate_playtime(
            cast(Score, self._score(passed=False, seconds=20, total_score=10_000, hits=20)),
            beatmap_length=30,
        )

        assert playtime == 20
        assert is_valid is True

    def test_trivial_failed_play_does_not_count(self) -> None:
        _, is_valid = calculate_playtime(
            cast(Score, self._score(passed=False, seconds=5, total_score=1_000, hits=1)),
            beatmap_length=30,
        )

        assert is_valid is False
