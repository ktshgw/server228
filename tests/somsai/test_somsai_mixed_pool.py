"""Pool diversity, canonical skill estimates and duplicate-free slot matching."""

# ruff: noqa: PT009, PT027
from copy import deepcopy
import unittest

from app.database import Beatmap, SomsaiPool
from app.features.somsai.models.somsai_admin import SomsaiPoolSpec
from app.features.somsai.services.somsai_mixed_pool_service import assemble_slots, mixed_pool
from app.features.somsai.services.somsai_pool_service import pool_skill_mmr, save_pool
from tests import test_somsai_core as core

from fastapi import HTTPException


class MixedPoolTests(unittest.IsolatedAsyncioTestCase):
    setUp = core.SomsaiCoreTests.setUp
    asyncSetUp = core.SomsaiCoreTests.asyncSetUp

    async def add_pool(self, stars, offset):
        slots = []
        for original in self.pool.slots:
            row = await self.session.get(Beatmap, original["beatmap_id"])
            data = row.model_dump()
            data.update(id=row.id + offset, difficulty_rating=stars, checksum=f"{row.id + offset:032x}")
            self.db.add(Beatmap(**data))
            slots.append({"id": original["id"], "beatmap_id": row.id + offset})
        self.db.flush()
        _, result = await save_pool(
            self.session, SomsaiPoolSpec(name=f"{stars} stars", slots=slots, active=True), None, None
        )
        return result

    async def test_skill_band_uses_canonical_maps_and_preserves_admin_bounds(self):
        far = await self.add_pool(9, 1000)
        near = await self.add_pool(5.5, 2000)
        # Stale import values must not make the difficult tournament eligible.
        far.slots = [{**slot, "difficulty_rating": 5} for slot in far.slots]
        result = await mixed_pool(self.session, 0, 0, 1500)
        self.assertEqual({pool["id"] for pool in result["source_pools"]}, {self.pool.id, near.id})
        self.assertEqual(result["mmr_window"], 250)
        self.assertTrue(all(slot["source_pool_id"] != far.id for slot in result["slots"]))
        self.pool.rating_max = near.rating_max = far.rating_max = 1000
        with self.assertRaises(HTTPException):
            await mixed_pool(self.session, 0, 0, 1500)

    async def test_small_catalogue_widens_estimate_window(self):
        result = await mixed_pool(self.session, 0, 0, 3000)
        self.assertGreater(result["mmr_window"], 250)
        self.assertEqual(len(result["slots"]), len(self.pool.slots))
        self.assertEqual(pool_skill_mmr(self.pool.slots), 1330)

    async def test_random_slots_draw_from_both_sources_and_never_duplicate(self):
        other = await self.add_pool(5, 1000)
        sources = [(self.pool, self.pool.slots), (other, other.slots)]
        seen = {slot["id"]: set() for slot in self.pool.slots}
        originals = deepcopy(self.pool.slots)
        for _ in range(100):
            slots = assemble_slots(sources, self.pool)
            self.assertEqual(len({slot["beatmap_id"] for slot in slots}), len(slots))
            for slot in slots:
                seen[slot["id"]].add(slot["source_pool_id"])
                source = self.pool if slot["source_pool_id"] == self.pool.id else other
                self.assertIn((slot["id"], slot["beatmap_id"]), [(s["id"], s["beatmap_id"]) for s in source.slots])
        self.assertTrue(all(len(ids) == 2 for ids in seen.values()))
        self.assertEqual(self.pool.slots, originals)

    def test_augmenting_assignment_preserves_scarce_slots(self):
        slots = [{"id": "NM1", "beatmap_id": 1}, {"id": "NM2", "beatmap_id": 2}, {"id": "TB", "beatmap_id": 3}]
        first = SomsaiPool(id=1, name="first", slots=slots)
        second = SomsaiPool(id=2, name="second", slots=[{"id": "NM1", "beatmap_id": 2}, {"id": "NM2", "beatmap_id": 1}])
        for _ in range(20):
            result = assemble_slots([(first, first.slots), (second, second.slots)], first)
            self.assertEqual({slot["beatmap_id"] for slot in result}, {1, 2, 3})
