"""Rank continuity, mod/skillset specificity and bounded history lookups."""

import asyncio
from contextlib import asynccontextmanager
from copy import deepcopy
import json
from pathlib import Path
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.features.somsai.models.somsai import SomsaiAction
from app.features.somsai.services.somsai_bot_service import (
    _load_skill,
    _official_request,
    _persona_candidates,
    _upstream_budget,
    prepare_draft_features,
)
from app.features.somsai.services.somsai_bot_skill import (
    choose_draft_slot,
    comfort_for,
    history_candidates,
    map_features,
    rank_skill,
    score_sample,
    skill_profile,
)
from app.features.somsai.services.somsai_bot_tech import technical_features


def beatmap(stars=7):
    return {
        "id": 10,
        "difficulty_rating": stars,
        "bpm": 200,
        "hit_length": 100,
        "ar": 9,
        "cs": 4,
        "count_circles": 500,
        "count_sliders": 100,
        "count_spinners": 0,
    }


def sample(aim=0.8, mods=None):
    result = map_features(
        beatmap(), mods or [{"acronym": "DT"}], {"star_rating": 7, "aim_difficulty": aim, "speed_difficulty": 1 - aim}
    )
    return {**result, "ability": 7.3, "quality": 1, "accuracy": 0.99, "passed": True}


class SkillTests(unittest.TestCase):
    def test_technical_patterns_and_mods_are_independent_of_slot_labels(self):
        fixture = (
            Path(__file__).resolve().parents[2]
            / "vendor/osu-server-spectator/osu.Server.Spectator.Tests/Multiplayer/Fixtures/bot-tech.osu"
        )
        tech = technical_features(fixture.read_text())
        assert tech["rhythm"] > 0.7
        assert tech["angles"] > 0.3
        assert tech["slider_tech"] > 0.25
        for mods in ([], ["HD"], ["HR"], ["HD", "HR"], ["DT"]):
            plain = {**sample(), "mods": mods, "rhythm": 0, "angles": 0, "slider_tech": 0}
            technical = {**plain, **tech}
            specialist = skill_profile(3500, "hard", [technical] * 6)
            farmer = skill_profile(3500, "hard", [plain] * 6)
            assert comfort_for(specialist, technical, "hard") > comfort_for(farmer, technical, "hard") + 0.3
            assert comfort_for(farmer, plain, "hard") > comfort_for(farmer, technical, "hard") + 0.3
            slots = [
                {**beatmap(), "id": "NM4", "mods": mods, "bot_attributes": {"star_rating": 7, "technical": tech}},
                {
                    **beatmap(),
                    "id": "TB",
                    "mods": mods,
                    "bot_attributes": {"star_rating": 7, "technical": {"rhythm": 0, "angles": 0, "slider_tech": 0}},
                },
            ]
            assert (
                choose_draft_slot([{"level": "hard", "skill_profile": specialist}], slots, banning=False, seed=1)["id"]
                == "NM4"
            )
            assert (
                choose_draft_slot([{"level": "hard", "skill_profile": farmer}], slots, banning=False, seed=1)["id"]
                == "TB"
            )

    def test_history_includes_secondary_mods_outside_top_farm_scores(self):
        best = [{"id": i, "beatmap": beatmap(), "mods": ["DT"]} for i in range(1, 100)]
        best.append({"id": 100, "beatmap": beatmap(), "mods": ["HR"]})
        choices = history_candidates(best, [])
        assert len(choices) == 16
        assert any(score["mods"] == ["HR"] for score in choices)

    def test_rank_is_continuous_inside_and_across_tiers(self):
        assert rank_skill(1200, "hard") > rank_skill(8500, "hard") + 1
        assert abs(rank_skill(9999, "hard") - rank_skill(10000, "medium")) < 0.001
        assert rank_skill(25, "top1000") > rank_skill(850, "top1000")

    def test_legacy_tiers_merge_and_mrekk_remains_distinct(self):
        for level in ("expert", "impossible", "top1000"):
            assert SomsaiAction(action="custom_create", bot_level=level).bot_level == "top1000"
        assert SomsaiAction(action="custom_create", bot_level="mrekk").bot_level == "mrekk"

    def test_modded_scores_use_played_stars_and_failures_do_not_raise_ability(self):
        score = {"beatmap": beatmap(5), "mods": [{"acronym": "DT"}], "accuracy": 0.99, "passed": True}
        assert score_sample(score) is None
        good = score_sample(score, {"star_rating": 7.3})
        assert good["stars"] == 7.3
        assert good["bpm"] == 300
        bad = score_sample({**score, "accuracy": 0.8, "passed": False}, {"star_rating": 7.3})
        assert bad["ability"] < good["ability"] - 2

    def test_aim_dt_is_not_stream_dt_and_generalists_like_both(self):
        aim, stream = sample(0.85), sample(0.25)
        profile = skill_profile(3500, "hard", [deepcopy(aim) for _ in range(6)])
        assert comfort_for(profile, aim, "hard") > comfort_for(profile, stream, "hard") + 0.3
        generalist = skill_profile(3500, "hard", [deepcopy(aim), deepcopy(stream)] * 4)
        assert abs(comfort_for(generalist, aim, "hard") - comfort_for(generalist, stream, "hard")) < 0.12
        assert comfort_for(generalist, stream, "hard") > comfort_for(profile, stream, "hard")

    def test_draft_picks_strength_and_bans_weakness_within_same_mod(self):
        slots = [
            {
                **beatmap(),
                "id": "DT1",
                "status": "available",
                "mods": [{"acronym": "DT"}],
                "bot_attributes": {"star_rating": 7, "aim_difficulty": 0.85, "speed_difficulty": 0.15},
            },
            {
                **beatmap(),
                "id": "DT2",
                "status": "available",
                "mods": [{"acronym": "DT"}],
                "bot_attributes": {"star_rating": 7, "aim_difficulty": 0.25, "speed_difficulty": 0.75},
            },
        ]
        for preferred, expected in ((0.85, "DT1"), (0.25, "DT2")):
            bot = {"level": "hard", "skill_profile": skill_profile(3500, "hard", [sample(preferred)] * 6)}
            for seed in range(20):
                assert choose_draft_slot([bot], slots, banning=False, seed=seed)["id"] == expected
                assert choose_draft_slot([bot], slots, banning=True, seed=seed)["id"] != expected
        # Swapping labels must not swap the learned preference.
        slots[0]["id"], slots[1]["id"] = slots[1]["id"], slots[0]["id"]
        assert choose_draft_slot([bot], slots, banning=False, seed=0)["id"] == "DT1"

    def test_easy_maps_remain_well_below_four_digit_skill_even_if_unfamiliar(self):
        profile = skill_profile(8500, "hard", [sample(0.85)] * 6)
        easy = map_features(beatmap(4), [{"acronym": "HD"}])
        hard = map_features(beatmap(9), [])
        assert comfort_for(profile, easy, "hard") - easy["stars"] > 1.8
        assert hard["stars"] - comfort_for(profile, hard, "hard") > 2


class MemoryRedis:
    def __init__(self):
        self.data = {}

    async def get(self, key):
        return self.data.get(key)

    async def set(self, key, value, ex=None):
        assert ex is None or ex > 0
        self.data[key] = value

    async def incr(self, key):
        self.data[key] = self.data.get(key, 0) + 1
        return self.data[key]

    async def expire(self, key, seconds):
        assert key in self.data
        assert seconds > 0


class HistoryTests(unittest.IsolatedAsyncioTestCase):
    async def test_top1000_all_ranks_and_pages_remain_available_with_warm_cache(self):
        redis = MemoryRedis()

        async def ranking(_fetcher, _redis, _url, *, params, **_kwargs):
            page = int(params["cursor[page]"])
            return {
                "ranking": [
                    {"global_rank": rank, "user": {"id": rank + 10000, "username": f"Player{rank}"}}
                    for rank in range((page - 1) * 50 + 1, page * 50 + 1)
                ]
            }

        found = set()
        with patch("app.features.somsai.services.somsai_bot_service._official_request", new=AsyncMock(side_effect=ranking)) as upstream:
            for rank in range(1, 1000, 50):
                with patch("app.features.somsai.services.somsai_bot_service.random.randint", return_value=rank):
                    candidates = await _persona_candidates(None, redis, 0, "top1000")
                    found.update(p["global_rank"] for p in candidates)
                    assert candidates == await _persona_candidates(None, redis, 0, "top1000")
            assert upstream.await_count == 20
        assert found == set(range(1, 1000))
        with patch("app.features.somsai.services.somsai_bot_service.random.randint", return_value=999):
            del redis.data["somsai:bot-personas:v3:osu:top1000:20"]
            with patch("app.features.somsai.services.somsai_bot_service._official_request", new=AsyncMock(side_effect=TimeoutError)):
                assert all(p["official_id"] for p in await _persona_candidates(None, redis, 0, "top1000"))
        assert "somsai:bot-personas:v3:osu:top1000:20" not in redis.data

    async def test_rooms_share_a_bounded_upstream_budget(self):
        fetcher, redis = AsyncMock(), MemoryRedis()
        for _ in range(32):
            await _official_request(fetcher, redis, "https://osu.ppy.sh/api/v2/test")
        with self.assertRaises(TimeoutError):  # noqa: PT027 - unittest suite
            await _official_request(fetcher, redis, "https://osu.ppy.sh/api/v2/test")
        assert fetcher.request_api.await_count == 32
        for _ in range(12):
            await _upstream_budget(redis, "draft")
        for _ in range(4):
            await _official_request(fetcher, redis, "https://osu.ppy.sh/api/v2/rankings/osu/performance")
        with self.assertRaises(TimeoutError):  # noqa: PT027
            await _upstream_budget(redis, "draft")

    async def test_draft_enrichment_happens_outside_lock_and_preserves_revision(self):
        match = Mock(stage="waiting", state={"slots": [{"id": "DT2", "beatmap_id": 10}]}, revision=7)
        session = AsyncMock()
        session.get.return_value = match
        session.add = Mock()
        locked = False

        @asynccontextmanager
        async def transaction():
            nonlocal locked
            locked = True
            yield session
            locked = False

        async def calculate(*_args, **_kwargs):
            assert not locked
            return Mock(model_dump=lambda: {"star_rating": 7, "aim_difficulty": 1, "speed_difficulty": 3})

        fetcher = AsyncMock()
        fetcher.get_beatmap_raw.return_value = "[HitObjects]\n100,100,1000,1,0\n"
        calculator = Mock(calculate_difficulty=AsyncMock(side_effect=calculate))
        with (
            patch("app.features.somsai.services.somsai_party_service.somsai_transaction", transaction),
            patch("app.calculating.get_calculator", return_value=calculator),
        ):
            await prepare_draft_features(fetcher, MemoryRedis(), 1, 0, match.state["slots"])
        assert match.state["slots"][0]["bot_attributes"]["speed_difficulty"] == 3
        assert match.revision == 7
        session.add.assert_called_once_with(match)

    async def test_profile_uses_attributes_and_cache_without_mass_fetches(self):
        async def request(url, **kwargs):
            if url.endswith("/attributes"):
                assert kwargs["method"] == "POST"
                return {"attributes": {"star_rating": 7, "aim_difficulty": 3, "speed_difficulty": 1}}
            return [{"id": 20, "beatmap": beatmap(5), "accuracy": 0.995, "mods": [{"acronym": "DT"}], "passed": True}]

        fetcher = AsyncMock()
        fetcher.request_api.side_effect = request
        redis = MemoryRedis()
        persona = {"global_rank": 1200, "official_id": 123}
        result = await _load_skill(fetcher, redis, 0, "hard", persona, asyncio.Semaphore(4))
        assert len(result["samples"]) == 1
        assert result["samples"][0]["stars"] == 7
        assert result["samples"][0]["aim_ratio"] == 0.75
        assert fetcher.request_api.await_count == 3
        second = await _load_skill(fetcher, redis, 0, "hard", {**persona, "global_rank": 8500}, asyncio.Semaphore(4))
        assert second["comfort"] < result["comfort"]
        assert fetcher.request_api.await_count == 3

    async def test_upstream_failure_falls_back_to_rank(self):
        fetcher = AsyncMock()
        fetcher.request_api.side_effect = TimeoutError()
        result = await _load_skill(
            fetcher, MemoryRedis(), 0, "hard", {"global_rank": 1200, "official_id": 123}, asyncio.Semaphore(4)
        )
        assert result["samples"] == []
        assert result["comfort"] == rank_skill(1200, "hard")
        assert json.loads(json.dumps(result)) == result
