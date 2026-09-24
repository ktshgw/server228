"""Concrete SOMSAI queue, pool-rank and MMR policy scenarios."""

# ruff: noqa: PT009
from datetime import timedelta
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from app.database.somsai import SomsaiQueue, SomsaiRating
from app.helpers import utcnow
from app.service.somsai_rank_pool import average_rank, rank_from_rating, rule_for
from app.service.somsai_rating_service import k_factor, settle_ratings
from app.service.somsai_service import queue_entries_compatible, search_radius


def queue_entry(user_id: int, rating: float, waited_seconds: float, now) -> SomsaiQueue:
    return SomsaiQueue(
        reservation_id=f"reservation-{user_id}",
        captain_id=user_id,
        members=[user_id],
        format="1v1",
        rating=rating,
        joined_at=now - timedelta(seconds=waited_seconds),
        expires_at=now + timedelta(seconds=90),
    )


class FakeSession:
    def add(self, _row) -> None:
        pass


class SomsaiMatchmakingPolicyTests(unittest.IsolatedAsyncioTestCase):
    def test_search_radius_wait_schedule_and_hard_cap(self):
        self.assertEqual(search_radius(0), 150)
        self.assertEqual(search_radius(5 * 60), 150)
        self.assertEqual(search_radius(7.5 * 60), 325)
        self.assertEqual(search_radius(10 * 60), 500)
        self.assertEqual(search_radius(60 * 60), 500)

    def test_provisional_and_established_k_factors(self):
        self.assertEqual(k_factor(1500, 9), 64)
        self.assertEqual(k_factor(1500, 10), 32.8)
        self.assertAlmostEqual(k_factor(2000, 20), 27.4285714286)

    def test_expanded_search_must_be_available_to_both_players(self):
        now = utcnow()
        veteran = queue_entry(1, 1700, 10 * 60, now)
        fresh = queue_entry(2, 2100, 0, now)
        self.assertFalse(queue_entries_compatible(veteran, fresh, now))

        fresh.joined_at = now - timedelta(minutes=10)
        self.assertTrue(queue_entries_compatible(veteran, fresh, now))

        outside_cap = queue_entry(3, 2201, 60 * 60, now)
        self.assertFalse(queue_entries_compatible(veteran, outside_cap, now))

    def test_real_pairings_choose_expected_division_and_pool_rules(self):
        cases = (
            (1500, 1500, 0, "GOLD I", 9, 1),
            (1700, 1800, 0, "GOLD III", 9, 1),
            (1600, 1925, 7.5 * 60, "GOLD III", 9, 1),
            (1700, 2100, 10 * 60, "GOLD V", 9, 1),
            (2000, 2500, 10 * 60, "PLATINUM III", 11, 2),
        )
        now = utcnow()
        for left, right, waited, pool_rank, best_of, bans in cases:
            with self.subTest(left=left, right=right, waited=waited):
                self.assertTrue(
                    queue_entries_compatible(
                        queue_entry(1, left, waited, now), queue_entry(2, right, waited, now), now
                    )
                )
                self.assertEqual(average_rank([left, right]), pool_rank)
                rule = rule_for(pool_rank)
                self.assertEqual((rule.best_of, rule.bans_per_team), (best_of, bans))

    async def test_real_upsets_use_new_rating_formula(self):
        cases = (
            # lower MMR, higher MMR, pool rank, games played, expected deltas
            (1500, 1500, "GOLD I", 0, (33, -31)),
            (1700, 1800, "GOLD III", 20, (21, -20)),
            (1600, 1925, "GOLD III", 20, (31, -28)),
            (1700, 2100, "GOLD V", 20, (34, -27)),
            (2000, 2500, "PLATINUM III", 20, (31, -24)),
        )
        for lower, higher, pool_rank, games, expected_deltas in cases:
            with self.subTest(lower=lower, higher=higher, pool_rank=pool_rank):
                rows = {
                    1: SomsaiRating(user_id=1, format="1v1", rating=lower, games=games),
                    2: SomsaiRating(user_id=2, format="1v1", rating=higher, games=games),
                }

                async def rating(_session, user_id, _ruleset_id, _variant_id, _format):
                    return rows[user_id]

                match = SimpleNamespace(
                    ruleset_id=0,
                    variant_id=0,
                    format="1v1",
                    state={"history": [], "pool_rank": pool_rank},
                )
                with patch("app.service.somsai_rating_service.ensure_rating", side_effect=rating):
                    changes = await settle_ratings(FakeSession(), match, [[1], [2]], winner=0)

                self.assertEqual([change["delta"] for change in changes], list(expected_deltas))
                self.assertEqual(rank_from_rating(changes[0]["before"]), rank_from_rating(lower))
                self.assertEqual(rank_from_rating(changes[1]["before"]), rank_from_rating(higher))


if __name__ == "__main__":
    unittest.main()
