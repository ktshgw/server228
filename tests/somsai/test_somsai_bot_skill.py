# ruff: noqa: INP001
"""Division, archetype and match-scoped SOMSAI bot AI tests."""

from collections import Counter
from contextlib import asynccontextmanager
import json
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.features.somsai.models.somsai import SomsaiAction
from app.features.somsai.services.somsai_bot_service import (
    _official_request,
    _persona_candidates,
    bot_personas,
    prepare_draft_features,
)
from app.features.somsai.services.somsai_bot_skill import (
    ARCHETYPE_MODIFIERS,
    STANDARD_ARCHETYPES,
    choose_draft_slot,
    division_for,
    roll_ai_profile,
)


class SkillTests(unittest.TestCase):
    def test_every_mmr_resolves_to_a_contiguous_division(self):
        assert division_for(rating=0).name == "BRONZE I"
        assert division_for(rating=599).name == "BRONZE I"
        assert division_for(rating=600).name == "BRONZE II"
        assert division_for(rating=2999).name == "DIAMOND V"
        assert division_for(rating=3000).name == "ARCHSOM"
        for rating in range(0, 4001):
            division = division_for(rating=rating)
            assert division.low_mmr <= rating
            assert division.high_mmr is None or rating <= division.high_mmr

    def test_rank_name_and_score_floors_match_design(self):
        expected = {
            "ARCHSOM": (800_000, 95),
            "DIAMOND V": (800_000, 94.5),
            "DIAMOND IV": (775_000, 94),
            "DIAMOND III": (750_000, 93.5),
            "DIAMOND II": (725_000, 93),
            "DIAMOND I": (700_000, 92.5),
        }
        for name, values in expected.items():
            division = division_for(rank=name)
            assert (division.minimum_score, division.minimum_accuracy) == values

    def test_archetype_weights_and_weakness_rate(self):
        archetypes, weaknesses = Counter(), 0
        for seed in range(50_000):
            profile = roll_ai_profile(rank="GOLD III", seed=seed)
            archetypes[profile["archetype"]] += 1
            weaknesses += profile["weakness"] is not None
        for name, weight in STANDARD_ARCHETYPES:
            assert abs(archetypes[name] / 500 - weight) < 1.0
        assert 0.092 <= weaknesses / 50_000 <= 0.108

    def test_archsom_has_only_three_archetypes(self):
        values = Counter(roll_ai_profile(rank="ARCHSOM", seed=i)["archetype"] for i in range(20_000))
        assert set(values) == {"allrounder", "consistency", "high_bpm"}
        assert 0.085 < values["allrounder"] / 20_000 < 0.115
        assert 0.43 < values["consistency"] / 20_000 < 0.47

    def test_hidden_is_the_only_tb_mod_user_and_uses_hd_about_sixty_percent(self):
        hidden, hidden_hd = 0, 0
        for seed in range(40_000):
            profile = roll_ai_profile(rank="GOLD III", seed=seed)
            if profile["archetype"] == "hidden":
                hidden += 1
                hidden_hd += profile["tiebreaker_preference"] == "HD"
            else:
                assert profile["tiebreaker_preference"] is None
        assert hidden > 1000
        assert 0.56 <= hidden_hd / hidden <= 0.64

    def test_contradictory_weaknesses_are_never_rolled(self):
        forbidden = {
            "hidden": {"anti_hidden"},
            "nomod": {"anti_nomod"},
            "dt_aim": {"low_ar", "anti_aim"},
            "dt_tapping": {"low_ar", "anti_stream"},
            "aimer": {"anti_aim"},
            "tapping": {"anti_stream"},
            "precision": {"magnifying_glass"},
            "high_bpm": {"low_ar"},
        }
        for seed in range(100_000):
            profile = roll_ai_profile(rank="DIAMOND III" if seed % 2 else "ARCHSOM", seed=seed)
            assert profile["weakness"] not in forbidden.get(profile["archetype"], set())

    def test_profile_is_deterministic_only_when_a_test_seed_is_explicit(self):
        assert roll_ai_profile(rank="GOLD I", seed=42) == roll_ai_profile(rank="GOLD I", seed=42)
        profiles = {json.dumps(roll_ai_profile(rank="GOLD I"), sort_keys=True) for _ in range(30)}
        assert len(profiles) > 20

    def test_draft_uses_archetype_slots_and_bans_weakness(self):
        profile = roll_ai_profile(rank="DIAMOND III", seed=7)
        profile["slot_modifiers"] = {"DT1": 0.22, "DT2": -0.20}
        bot = {"ai_profile": profile}
        slots = [{"id": "DT1"}, {"id": "DT2"}]
        for seed in range(20):
            assert choose_draft_slot([bot], slots, banning=False, seed=seed)["id"] == "DT1"
            assert choose_draft_slot([bot], slots, banning=True, seed=seed)["id"] == "DT2"

    def test_documented_skillsets_have_their_expected_primary_slots(self):
        assert ARCHETYPE_MODIFIERS["aimer"]["NM1"] > 0
        assert ARCHETYPE_MODIFIERS["tapping"]["NM5"] > 0
        assert ARCHETYPE_MODIFIERS["flow_aim"]["HD3"] > 0
        assert ARCHETYPE_MODIFIERS["dt_aim"]["DT1"] > ARCHETYPE_MODIFIERS["dt_aim"]["DT2"]
        assert ARCHETYPE_MODIFIERS["dt_tapping"]["DT3"] > ARCHETYPE_MODIFIERS["dt_tapping"]["DT1"]

    def test_legacy_level_payload_is_still_accepted_for_old_clients(self):
        for level in ("expert", "impossible", "top1000"):
            assert SomsaiAction(action="custom_create", bot_level=level).bot_level == "top1000"


class MemoryRedis:
    def __init__(self):
        self.data = {}

    async def get(self, key):
        return self.data.get(key)

    async def set(self, key, value, _ex=None, **_kwargs):
        self.data[key] = value
        return True

    async def incr(self, key):
        self.data[key] = self.data.get(key, 0) + 1
        return self.data[key]

    async def expire(self, _key, _seconds):
        return True


class IdentityTests(unittest.IsolatedAsyncioTestCase):
    async def test_same_visual_player_gets_a_fresh_skillset_each_match(self):
        identity = [
            {"official_id": 1, "username": "same", "avatar_url": "x", "country_code": "JP", "global_rank": 5000}
        ]
        with patch(
            "app.features.somsai.services.somsai_bot_service._persona_candidates", new=AsyncMock(return_value=identity)
        ):
            first = await bot_personas(None, None, 0, "hard", 1, target_rank="GOLD V")
            second = await bot_personas(None, None, 0, "hard", 1, target_rank="GOLD V")
        assert first[0]["official_id"] == second[0]["official_id"]
        assert first[0]["ai_profile"] != second[0]["ai_profile"]
        assert "skill_profile" not in first[0]

    async def test_single_allrounder_fills_the_missing_freemod_role(self):
        identities = [
            {"official_id": 1, "username": "a", "avatar_url": "x", "country_code": "JP"},
            {"official_id": 2, "username": "b", "avatar_url": "x", "country_code": "JP"},
        ]
        profiles = [
            {"archetype": "allrounder", "freemod_preference": "HD", "slot_modifiers": {}},
            {"archetype": "hidden", "freemod_preference": "HD", "slot_modifiers": {}},
        ]
        with (
            patch(
                "app.features.somsai.services.somsai_bot_service._persona_candidates",
                new=AsyncMock(return_value=identities),
            ),
            patch("app.features.somsai.services.somsai_bot_service.roll_ai_profile", side_effect=profiles),
        ):
            bots = await bot_personas(None, None, 0, "hard", 2, target_rank="ARCHSOM")
        mods = [bot["ai_profile"]["freemod_preference"] for bot in bots]
        assert any("HD" in value and "HR" not in value for value in mods)
        assert any("HR" in value for value in mods)

    async def test_every_multi_bot_team_satisfies_freemod_roles(self):
        identities = [
            {"official_id": i, "username": str(i), "avatar_url": "x", "country_code": "JP"}
            for i in range(4)
        ]
        with patch(
            "app.features.somsai.services.somsai_bot_service._persona_candidates",
            new=AsyncMock(return_value=identities),
        ):
            for _ in range(100):
                bots = await bot_personas(None, None, 0, "hard", 4, target_rank="DIAMOND III")
                mods = [bot["ai_profile"]["freemod_preference"] for bot in bots]
                assert any("HD" in value and "HR" not in value for value in mods)
                assert any("HR" in value for value in mods)

    async def test_direct_leaderboard_identity_is_only_visual(self):
        division = division_for(rank="DIAMOND IV")
        redis = MemoryRedis()

        async def ranking(_fetcher, _redis, _url, *, params, **_kwargs):
            page = int(params["cursor[page]"])
            return {
                "ranking": [
                    {"global_rank": rank, "user": {"id": rank + 10_000, "username": f"P{rank}"}}
                    for rank in range((page - 1) * 50 + 1, page * 50 + 1)
                ]
            }

        with patch(
            "app.features.somsai.services.somsai_bot_service._official_request", new=AsyncMock(side_effect=ranking)
        ):
            values = await _persona_candidates(None, redis, 0, division)
        assert values
        assert all(division.rank_low <= value["global_rank"] <= division.rank_high for value in values)
        assert all("ai_profile" not in value for value in values)

    async def test_rooms_share_a_bounded_upstream_budget(self):
        fetcher, redis = AsyncMock(), MemoryRedis()
        for _ in range(32):
            await _official_request(fetcher, redis, "https://osu.ppy.sh/api/v2/test")
        with self.assertRaises(TimeoutError):  # noqa: PT027
            await _official_request(fetcher, redis, "https://osu.ppy.sh/api/v2/test")

    async def test_draft_enrichment_still_preserves_match_revision(self):
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
            return Mock(model_dump=lambda: {"star_rating": 7})

        fetcher = AsyncMock()
        fetcher.get_beatmap_raw.return_value = "[HitObjects]\n100,100,1000,1,0\n"
        with (
            patch("app.features.somsai.services.somsai_party_service.somsai_transaction", transaction),
            patch(
                "app.calculating.get_calculator",
                return_value=Mock(calculate_difficulty=AsyncMock(side_effect=calculate)),
            ),
        ):
            await prepare_draft_features(fetcher, MemoryRedis(), 1, 0, match.state["slots"])
        assert match.state["slots"][0]["bot_attributes"]["star_rating"] == 7
        assert match.revision == 7
