"""Official discovery, safe metadata import and pagination recovery."""

# ruff: noqa: PT027
from contextlib import asynccontextmanager
from copy import deepcopy
import json
from types import SimpleNamespace
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch

from app.database import Beatmap, Beatmapset
from app.database.failtime import FailTime
from app.models.beatmap import BeatmapRankStatus
from app.service.ranked_catalogue_service import (
    SEARCH_PARAMS,
    STATE_KEY,
    import_search_page,
    sync_official_catalogue,
)
from tests.test_ranked_elo_admin import AsyncTestSession

from sqlmodel import Session, SQLModel, create_engine, select


def snapshot(set_id=1):
    return {
        "id": set_id,
        "track_id": 42,
        "ranked": 1,
        "artist": "Test",
        "artist_unicode": "Test",
        "title": "FA",
        "title_unicode": "FA",
        "creator": "Mapper",
        "user_id": 7,
        "preview_url": "",
        "video": False,
        "covers": None,
        "availability": {"download_disabled": False},
        "last_updated": "2026-01-01T00:00:00Z",
        "submitted_date": "2025-01-01T00:00:00Z",
        "beatmaps": [
            {
                "id": set_id * 10,
                "beatmapset_id": set_id,
                "user_id": 7,
                "mode": "osu",
                "ranked": 1,
                "version": "Test",
                "difficulty_rating": 4.5,
                "total_length": 130,
                "hit_length": 120,
                "checksum": "a" * 32,
                "last_updated": "2026-01-01T00:00:00Z",
                "cs": 4,
                "url": "https://osu.ppy.sh/beatmaps/10",
                "convert": False,
            }
        ],
    }


class CatalogueImportTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(
            self.engine, tables=[SQLModel.metadata.tables[m.__tablename__] for m in (Beatmapset, Beatmap, FailTime)]
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.session.add_all = self.db.add_all
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)

    async def test_unknown_fa_maps_are_imported_without_any_player_or_score(self):
        assert await import_search_page(self.session, [snapshot()]) == []
        await self.session.commit()
        assert self.db.get(Beatmapset, 1).track_id == 42
        assert self.db.get(Beatmap, 10).difficulty_rating == 4.5
        assert await import_search_page(self.session, [snapshot()]) == []
        await self.session.commit()
        assert len(self.db.exec(select(Beatmap)).all()) == 1

    async def test_refresh_preserves_description_and_routes_revision_changes_to_updater(self):
        await import_search_page(self.session, [snapshot()])
        await self.session.commit()
        parent = self.db.get(Beatmapset, 1)
        parent.description = {"bbcode": "Keep this"}
        await self.session.commit()
        changed = snapshot()
        changed["beatmaps"][0]["difficulty_rating"] = 5
        assert await import_search_page(self.session, [changed]) == []
        await self.session.commit()
        assert self.db.get(Beatmap, 10).difficulty_rating == 5
        assert parent.description == {"bbcode": "Keep this"}
        changed["beatmaps"][0]["checksum"] = "b" * 32
        assert await import_search_page(self.session, [changed]) == [1]
        assert self.db.get(Beatmap, 10).checksum == "a" * 32
        changed = snapshot()
        changed["beatmaps"][0]["ranked"] = 0
        assert await import_search_page(self.session, [changed]) == [1]
        assert self.db.get(Beatmap, 10).beatmap_status == BeatmapRankStatus.RANKED

    async def test_missing_difficulty_requires_revision_processing_and_old_snapshot_is_ignored(self):
        await import_search_page(self.session, [snapshot()])
        await self.session.commit()
        raw = snapshot()
        new_map = deepcopy(raw["beatmaps"][0])
        new_map["id"] = 11
        raw["beatmaps"].append(new_map)
        assert await import_search_page(self.session, [raw]) == [1]
        assert self.db.get(Beatmap, 11) is None
        raw["last_updated"] = "2025-12-01T00:00:00Z"
        assert await import_search_page(self.session, [raw]) == []

    async def test_bad_source_is_rejected_instead_of_importing_the_entire_ranked_catalogue(self):
        for change in ({"track_id": None}, {"ranked": 4}, {"beatmaps": []}):
            raw = {**snapshot(), **change}
            with self.assertRaises(ValueError):
                await import_search_page(self.session, [raw])
            await self.session.rollback()


class DiscoveryTests(unittest.IsolatedAsyncioTestCase):
    async def test_pages_resume_after_failure_and_daily_run_is_a_noop(self):
        state = {}
        seen = set()
        lock = SimpleNamespace(
            acquire=AsyncMock(return_value=True),
            extend=AsyncMock(),
            owned=AsyncMock(return_value=True),
            release=AsyncMock(),
        )

        async def get(key):
            return state.get(key)

        async def set_value(key, value):
            state[key] = value

        async def sadd(_key, *values):
            seen.update(values)

        redis = SimpleNamespace(
            get=get,
            set=set_value,
            delete=AsyncMock(side_effect=lambda _key: seen.clear()),
            sadd=sadd,
            scard=AsyncMock(side_effect=lambda _key: len(seen)),
            smembers=AsyncMock(side_effect=lambda _key: seen.copy()),
            lock=lambda *_args, **_kwargs: lock,
        )
        session = SimpleNamespace(commit=AsyncMock(), exec=AsyncMock(return_value=SimpleNamespace(all=lambda: [])))

        @asynccontextmanager
        async def database():
            yield session

        first = {"beatmapsets": [snapshot()], "total": 2, "cursor_string": "next"}
        last = {"beatmapsets": [snapshot(2)], "total": 2, "cursor_string": None}
        fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=[first, TimeoutError(), last]))
        module = "app.service.ranked_catalogue_service."
        with (
            patch(module + "get_redis", return_value=redis),
            patch(module + "get_fetcher", AsyncMock(return_value=fetcher)),
            patch(module + "get_beatmapset_update_service", return_value=SimpleNamespace()),
            patch(module + "with_db", database),
            patch(module + "import_search_page", AsyncMock(return_value=[])) as importer,
            patch(module + "asyncio.sleep", AsyncMock()),
        ):
            with self.assertRaises(TimeoutError):
                await sync_official_catalogue()
            assert json.loads(state[STATE_KEY])["cursor"] == "next"
            await sync_official_catalogue()
            assert fetcher.request_api.call_args.kwargs["params"] == {**SEARCH_PARAMS, "cursor_string": "next"}
            result = json.loads(state[STATE_KEY])
            assert result["status"] == "complete"
            assert result["processed"] == 2
            await sync_official_catalogue()
            assert fetcher.request_api.await_count == 3
            assert importer.await_count == 2
