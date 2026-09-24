# ruff: noqa: PT009, PT027
import unittest
from unittest.mock import patch

from app.config import settings
from app.models.mods import init_mods, mods_can_get_pp
from app.models.mods.performance import DEFAULT_RANKED_MODS, RANKED_MODS
from app.models.score import GameMode
from app.features.somsai.services.somsai_map_stats import display_stats
from app.features.somsai.services.somsai_match_service import match_action, members_of, room_event
from tests.test_somsai_core import SomsaiCoreTests

from fastapi import HTTPException


class AssistPolicyTests(unittest.TestCase):
    def setUp(self):
        init_mods()

    def test_partitions_and_unranked_combinations(self):
        with (
            patch.object(settings, "soms_osu_assist_modes", True),
            patch.dict(RANKED_MODS, DEFAULT_RANKED_MODS, clear=True),
        ):
            for acronym, expected in [("RX", GameMode.OSURX), ("AP", GameMode.OSUAP)]:
                mods = [{"acronym": acronym}, {"acronym": "DT", "settings": {"speed_change": 1.5}}]
                self.assertEqual(GameMode.OSU.to_special_mode(mods), expected)
                self.assertTrue(mods_can_get_pp(0, mods))
                self.assertFalse(mods_can_get_pp(0, [*mods, {"acronym": "DA"}]))
                self.assertTrue(expected.is_official())
                self.assertEqual(int(expected), 0)
                for mode in (GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
                    self.assertEqual(mode.to_special_mode(mods), mode)
                    self.assertFalse(mods_can_get_pp(int(mode), mods))
            self.assertFalse(mods_can_get_pp(0, [{"acronym": "RX"}, {"acronym": "AP"}]))
            self.assertEqual(GameMode.OSU.to_special_mode([]), GameMode.OSU)

    def test_pool_clock_and_native_stars(self):
        raw = "[Difficulty]\nCircleSize:4\nApproachRate:9\nOverallDifficulty:8\nHPDrainRate:6\n"
        slot = {"category": "DT", "mods": [{"acronym": "DT"}], "bpm": 180, "total_length": 150}
        result = display_stats(raw, slot, {"star_rating": 6.42}, 0)
        self.assertEqual(result["stars"], 6.42)
        self.assertEqual(result["bpm"], 270)
        self.assertEqual(result["length"], 100)
        self.assertAlmostEqual(result["ar"], 10 + 1 / 3)
        self.assertAlmostEqual(result["od"], 9 + 7 / 9)
        for category in ("FM", "TB"):
            result = display_stats(raw, {**slot, "category": category}, {"star_rating": 5}, 0)
            self.assertEqual((result["bpm"], result["length"], result["ar"], result["od"]), (180, 150, 9, 8))


class ReadyToggleTests(SomsaiCoreTests):
    async def test_unready_is_idempotent_and_blocks_start_with_stale_native_state(self):
        match = await self.make_match()
        await self.ready_first_map(match)
        await match_action(self.session, match, 10, "unready")
        await match_action(self.session, match, 10, "unready")
        self.assertNotIn(10, match.state["ready"])
        self.assertIn(11, match.state["ready"])
        with self.assertRaises(HTTPException):
            await room_event(
                self.session,
                match.room_id,
                "started",
                match.state["playlist_item_id"],
                members_of(match),
                ready=members_of(match),
            )
        self.assertEqual(match.stage, "ready")
        await match_action(self.session, match, 10, "ready")
        self.assertEqual(set(match.state["ready"]), set(members_of(match)))
