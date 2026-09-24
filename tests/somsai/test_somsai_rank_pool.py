"""SOMSAI rank matrix, official map links, and optional admin reasons."""

# The project executes this suite through unittest.
# ruff: noqa: PT009, PT027
import unittest

from app.models.negative_pp import NegativePPDelete
from app.features.somsai.models.somsai_admin import SomsaiMapDelete, SomsaiMapWrite
from app.features.somsai.services.somsai_rank_pool import average_rank, eligible_ranks, rank_from_rating, rule_for
from app.features.somsai.services.somsai_warehouse_service import parse_beatmap_id

from fastapi import HTTPException


class SomsaiRankPoolTest(unittest.TestCase):
    def test_rank_from_rating_boundaries(self):
        self.assertEqual(rank_from_rating(499), "BRONZE I")
        self.assertEqual(rank_from_rating(600), "BRONZE II")
        self.assertEqual(rank_from_rating(1000), "SILVER I")
        self.assertEqual(rank_from_rating(1942), "GOLD V")
        self.assertEqual(rank_from_rating(2500), "DIAMOND I")
        self.assertEqual(rank_from_rating(3000), "ARCHSOM")

    def test_lobby_rank_is_floor_of_discrete_rank_positions(self):
        self.assertEqual(average_rank([2900, 2700]), "DIAMOND IV")
        self.assertEqual(average_rank([2700, 2400]), "DIAMOND I")
        self.assertEqual(average_rank([2700, 2600]), "DIAMOND II")

    def test_sheet_seven_rules_and_disabled_slots(self):
        self.assertEqual(rule_for("DIAMOND III").best_of, 13)
        self.assertEqual(rule_for("DIAMOND III").bans_per_team, 2)
        self.assertIn("FM1", rule_for("PLATINUM I").slots)
        self.assertNotIn("FM1", rule_for("GOLD V").slots)
        self.assertNotIn("NM6", rule_for("SILVER V").slots)

    def test_eligibility_is_slot_specific(self):
        self.assertIn("GOLD I", eligible_ranks("NM1", 6.9))
        self.assertNotIn("SILVER I", eligible_ranks("NM1", 6.9))
        self.assertIn("ARCHSOM", eligible_ranks("NM6", 6.1))

    def test_official_beatmap_links(self):
        self.assertEqual(parse_beatmap_id("https://osu.ppy.sh/beatmaps/12345"), 12345)
        self.assertEqual(parse_beatmap_id("https://osu.ppy.sh/beatmapsets/321#osu/12345"), 12345)
        with self.assertRaises(HTTPException):
            parse_beatmap_id("https://example.com/beatmaps/12345")

    def test_admin_reasons_default_to_no_reason(self):
        self.assertEqual(NegativePPDelete().reason, "no reason")
        self.assertEqual(SomsaiMapDelete().reason, "no reason")
        self.assertEqual(SomsaiMapWrite(slot="NM1", url="https://osu.ppy.sh/beatmaps/1").reason, "no reason")
