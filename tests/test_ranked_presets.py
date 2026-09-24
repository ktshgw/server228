"""Exercise preset persistence and catalogue filters in an isolated SQL database."""

# unittest is the repository's runner; pytest is not installed.
# ruff: noqa: PT027
from types import SimpleNamespace
from typing import Any
import unittest
from unittest.mock import patch

from app.database import Beatmap, Beatmapset, MatchmakingMapPreset, MatchmakingPool
from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus
from app.models.ranked_admin import RankedPresetSpec
from app.models.score import GameMode
from app.service.matchmaking_service import ranked_beatmaps, ranked_pool_available
from app.service.ranked_preset_service import assign_preset, preview_preset, save_preset
from tests.test_ranked_elo_admin import AsyncTestSession

from fastapi import HTTPException
from pydantic import ValidationError
from sqlalchemy.orm import lazyload
from sqlmodel import Session, SQLModel, create_engine


class RankedPresetTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(
            self.engine,
            tables=[
                SQLModel.metadata.tables[model.__tablename__]
                for model in (MatchmakingMapPreset, MatchmakingPool, Beatmapset, Beatmap)
            ],
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        now = utcnow()
        self.db.add(
            Beatmapset(
                id=1,
                artist="Test",
                artist_unicode="Test",
                creator="test",
                title="Preset fixture",
                title_unicode="Preset fixture",
                user_id=2,
                preview_url="",
                video=False,
                covers=None,
                last_updated=now,
                submitted_date=now,
                beatmap_status=BeatmapRankStatus.RANKED,
                download_disabled=False,
                track_id=1,
            )
        )
        for index in range(1, 15):
            self.db.add(
                Beatmap(
                    id=index,
                    beatmapset_id=1,
                    user_id=2,
                    mode=GameMode.OSU,
                    version=f"Test {index}",
                    difficulty_rating=4 + index / 10,
                    url=f"https://example.invalid/{index}",
                    total_length=120,
                    hit_length=100,
                    checksum=f"{index:032x}",
                    beatmap_status=BeatmapRankStatus.RANKED,
                    last_updated=now,
                    cs=4,
                )
            )
        self.db.add_all(
            [
                MatchmakingPool(id=1, name="SOMS", type="ranked_play", ranked=True, lobby_size=2),
                MatchmakingPool(id=2, name="Second osu pool", type="ranked_play", ranked=True, lobby_size=2),
                MatchmakingPool(
                    id=3, name="Mania", ruleset_id=3, variant_id=4, type="ranked_play", ranked=True, lobby_size=2
                ),
            ]
        )
        self.db.commit()

        async def effective_policy(_session, maps):
            return {
                item.id: SimpleNamespace(
                    status=BeatmapRankStatus.RANKED, leaderboard_enabled=item.id != 14, pp_enabled=item.id != 14
                )
                for item in maps
            }

        policy_patch = patch("app.service.matchmaking_service.get_effective_beatmap_policies", effective_policy)
        policy_patch.start()
        self.addCleanup(policy_patch.stop)

    def spec(self, **changes: Any) -> RankedPresetSpec:
        return RankedPresetSpec.model_validate({"name": "Test preset", "ruleset_id": 0, **changes})

    async def test_preview_filters_stars_and_reports_unranked_and_unknown_ids(self):
        result = await preview_preset(self.session, self.spec(max_stars=5, beatmap_ids=[1, 9, 11, 14, 999]))
        assert result["count"] == 2
        assert result["excluded_ids"] == [11, 14, 999]
        assert [item["id"] for item in result["items"]] == [1, 9]
        assert result["items"][0]["beatmapset_id"] == 1

    async def test_length_filter_and_default_catalogue(self):
        assert len(await ranked_beatmaps(self.session, 0)) == 13
        result = await preview_preset(self.session, self.spec(min_length=101))
        assert result["count"] == 0

    async def test_availability_reads_only_enough_eligible_maps(self):
        observed = []

        async def policy(_session, maps):
            observed.extend(item.id for item in maps)
            return {
                item.id: SimpleNamespace(status=BeatmapRankStatus.RANKED, leaderboard_enabled=True, pp_enabled=True)
                for item in maps
            }

        template = self.db.get(Beatmap, 1, options=[lazyload("*")]).model_dump()
        for index in range(15, 215):
            self.db.add(Beatmap(**(template | {"id": index, "checksum": f"{index:032x}"})))
        self.db.commit()
        with patch("app.service.matchmaking_service.get_effective_beatmap_policies", policy):
            assert await ranked_pool_available(self.session, self.db.get(MatchmakingPool, 1))
        assert len(observed) == 64, "Queue entry must not materialise the entire catalogue"

    async def test_limited_lookup_pages_past_maps_disabled_by_policy(self):
        template = self.db.get(Beatmap, 1, options=[lazyload("*")]).model_dump()
        for index in range(15, 80):
            self.db.add(Beatmap(**(template | {"id": index, "checksum": f"{index:032x}"})))
        self.db.commit()

        async def policy(_session, maps):
            return {
                item.id: SimpleNamespace(
                    status=BeatmapRankStatus.RANKED, leaderboard_enabled=item.id > 64, pp_enabled=item.id > 64
                )
                for item in maps
            }

        with patch("app.service.matchmaking_service.get_effective_beatmap_policies", policy):
            maps = await ranked_beatmaps(self.session, 0, pool_id=1, limit=10)
            assert [item["beatmap_id"] for item in maps] == list(range(65, 75))
            team_pool = MatchmakingPool(id=4, name="Teams", type="ranked_play", ranked=True, lobby_size=4)
            self.db.add(team_pool)
            self.db.commit()
            assert not await ranked_pool_available(self.session, team_pool)

    async def test_official_default_requires_fa_and_upstream_rank_but_custom_preset_does_not(self):
        beatmap = self.db.get(Beatmap, 1, options=[lazyload("*")])
        assert beatmap is not None
        beatmap.beatmap_status = BeatmapRankStatus.PENDING
        await self.session.commit()
        assert len(await ranked_beatmaps(self.session, 0)) == 12
        assert (await preview_preset(self.session, self.spec()))["count"] == 13
        parent = self.db.get(Beatmapset, 1)
        assert parent is not None
        parent.track_id = None
        await self.session.commit()
        assert await ranked_beatmaps(self.session, 0) == []
        assert (await preview_preset(self.session, self.spec()))["count"] == 13
        with self.assertRaises(HTTPException):
            await assign_preset(self.session, 1, None, None)

    async def test_official_length_limits_are_inclusive_one_to_five_minutes(self):
        for index, length in enumerate((59, 60, 240, 241, 300, 301), start=1):
            beatmap = self.db.get(Beatmap, index, options=[lazyload("*")])
            assert beatmap is not None
            beatmap.hit_length = length
        await self.session.commit()
        eligible = {item["beatmap_id"] for item in await ranked_beatmaps(self.session, 0)}
        assert eligible.intersection(range(1, 7)) == {2, 3, 4, 5}

    async def test_assignment_affects_only_selected_pool_and_preserves_default(self):
        _, preset = await save_preset(self.session, self.spec(beatmap_ids=list(range(1, 11))), None, None)
        assert preset.id is not None
        await assign_preset(self.session, 1, preset.id, None)
        await self.session.commit()
        assert len(await ranked_beatmaps(self.session, 0, pool_id=1)) == 10
        assert len(await ranked_beatmaps(self.session, 0, pool_id=2)) == 13
        assert await ranked_beatmaps(self.session, 3, 4, pool_id=1) == []
        await assign_preset(self.session, 1, None, preset.id)
        await self.session.commit()
        assert len(await ranked_beatmaps(self.session, 0, pool_id=1)) == 13

    async def test_small_draft_saves_but_cannot_replace_playable_pool(self):
        _, draft = await save_preset(self.session, self.spec(max_stars=4.2), None, None)
        assert draft.id is not None
        await self.session.commit()
        with self.assertRaises(HTTPException) as error:
            await assign_preset(self.session, 1, draft.id, None)
        assert error.exception.status_code == 409
        await self.session.rollback()
        assert len(await ranked_beatmaps(self.session, 0, pool_id=1)) == 13

    async def test_team_assignment_requires_four_distinct_hands(self):
        self.db.add(MatchmakingPool(id=4, name="Teams", type="ranked_play", ranked=True, lobby_size=4))
        _, preset = await save_preset(self.session, self.spec(), None, None)
        await self.session.commit()
        # Thirteen eligible maps support the existing duel queue, but cannot
        # deal twenty different cards to four team players.
        await assign_preset(self.session, 1, preset.id, None)
        with self.assertRaises(HTTPException) as error:
            await assign_preset(self.session, 4, preset.id, None)
        assert "20" in error.exception.detail
        with self.assertRaises(HTTPException):
            await assign_preset(self.session, 4, None, None)

    async def test_team_preset_edit_cannot_shrink_to_duel_only_size(self):
        _, preset = await save_preset(self.session, self.spec(), None, None)
        assert preset.id is not None
        self.db.add(
            MatchmakingPool(
                id=4, name="Teams", type="ranked_play", ranked=True, lobby_size=4, beatmap_preset_id=preset.id
            )
        )
        await self.session.commit()
        with self.assertRaises(HTTPException) as error:
            await save_preset(self.session, self.spec(), preset.id, 1)
        assert "20" in error.exception.detail

    async def test_active_preset_cannot_be_edited_to_an_empty_catalogue(self):
        _, preset = await save_preset(self.session, self.spec(), None, None)
        assert preset.id is not None
        preset_id = preset.id
        await assign_preset(self.session, 1, preset_id, None)
        await self.session.commit()
        with self.assertRaises(HTTPException) as error:
            await save_preset(self.session, self.spec(max_stars=1), preset_id, 1)
        assert error.exception.status_code == 409
        await self.session.rollback()
        assert len(await ranked_beatmaps(self.session, 0, pool_id=1)) == 13

    async def test_wrong_mode_cannot_be_assigned(self):
        _, preset = await save_preset(self.session, self.spec(), None, None)
        with self.assertRaises(HTTPException) as error:
            await assign_preset(self.session, 3, preset.id, None)
        assert error.exception.status_code == 422

    async def test_stale_preset_update_and_stale_pool_assignment_rejected(self):
        _, preset = await save_preset(self.session, self.spec(), None, None)
        preset_id = preset.id
        before, updated = await save_preset(self.session, self.spec(name="Changed preset"), preset_id, 1)
        assert before is not None
        assert before["name"] == "Test preset"
        assert updated.revision == 2
        await self.session.commit()
        with self.assertRaises(HTTPException) as error:
            await save_preset(self.session, self.spec(), preset_id, 1)
        assert error.exception.status_code == 409
        await self.session.rollback()
        await assign_preset(self.session, 1, preset_id, None)
        await self.session.commit()
        with self.assertRaises(HTTPException) as error:
            await assign_preset(self.session, 1, None, None)
        assert error.exception.status_code == 409

    def test_validation_rejects_bad_ranges_and_deduplicates_manual_maps(self):
        assert self.spec(beatmap_ids=[1, 1, 2]).beatmap_ids == [1, 2]
        invalid = [
            {"min_stars": 7, "max_stars": 4},
            {"min_length": 300, "max_length": 120},
            {"ruleset_id": 3, "variant_id": 0},
            {"variant_id": 4},
            {"beatmap_ids": []},
            {"beatmap_ids": [True]},
            {"max_stars": float("nan")},
        ]
        for value in invalid:
            with self.subTest(value=value), self.assertRaises(ValidationError):
                self.spec(**value)
