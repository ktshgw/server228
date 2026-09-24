from collections.abc import Awaitable
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import Beatmap, Beatmapset, BeatmapsetModel, User
from app.database.beatmapset import BeatmapAvailability
from app.models.error import RequestError
from app.router.v2.beatmapset import download_beatmapset, get_beatmapset, lookup_beatmapset
from app.service.beatmap_download_service import BeatmapDownloadService

from fastapi import HTTPException
from httpx import HTTPStatusError


async def assert_raises_async[ErrorT: BaseException](expected: type[ErrorT], awaitable: Awaitable[Any]) -> ErrorT:
    try:
        await awaitable
    except expected as exc:
        return exc
    raise AssertionError(f"Expected {expected.__name__} to be raised")


class BeatmapsetSharedCacheTests(unittest.IsolatedAsyncioTestCase):
    async def test_authenticated_detail_does_not_read_or_write_shared_cache(self) -> None:
        cache_service = Mock()
        cache_service.get_beatmapset_from_cache = AsyncMock(return_value={"id": 999})
        cache_service.cache_beatmapset = AsyncMock()
        db = Mock()
        db.refresh = AsyncMock()
        current_user = SimpleNamespace(id=1)
        transformed = {"id": 123, "has_favourited": True}

        with (
            patch.object(Beatmapset, "get_or_fetch", new=AsyncMock(return_value=Mock())) as get_or_fetch,
            patch.object(BeatmapsetModel, "transform", new=AsyncMock(return_value=transformed)) as transform,
        ):
            result = await get_beatmapset(
                db=db,
                request=Mock(),
                beatmapset_id=123,
                current_user=current_user,
                fetcher=Mock(),
                cache_service=cache_service,
            )

        assert result == transformed
        cache_service.get_beatmapset_from_cache.assert_not_awaited()
        cache_service.cache_beatmapset.assert_not_awaited()
        get_or_fetch.assert_awaited_once()
        transform.assert_awaited_once()

    async def test_authenticated_lookup_does_not_read_or_write_shared_cache(self) -> None:
        cache_service = Mock()
        cache_service.get_beatmap_lookup_from_cache = AsyncMock(return_value={"id": 999})
        cache_service.cache_beatmap_lookup = AsyncMock()
        beatmap = SimpleNamespace(beatmapset=Mock())
        transformed = {"id": 123, "has_favourited": True}

        with (
            patch.object(Beatmap, "get_or_fetch", new=AsyncMock(return_value=beatmap)) as get_or_fetch,
            patch.object(BeatmapsetModel, "transform", new=AsyncMock(return_value=transformed)) as transform,
        ):
            result = await lookup_beatmapset(
                db=Mock(),
                request=Mock(),
                beatmap_id=456,
                current_user=SimpleNamespace(id=1),
                fetcher=Mock(),
                cache_service=cache_service,
            )

        assert result == transformed
        cache_service.get_beatmap_lookup_from_cache.assert_not_awaited()
        cache_service.cache_beatmap_lookup.assert_not_awaited()
        get_or_fetch.assert_awaited_once()
        transform.assert_awaited_once()

    async def test_anonymous_detail_can_use_shared_cache(self) -> None:
        cached = {"id": 123, "has_favourited": False}
        cache_service = Mock()
        cache_service.get_beatmapset_from_cache = AsyncMock(return_value=cached)

        with patch.object(Beatmapset, "get_or_fetch", new=AsyncMock()) as get_or_fetch:
            result = await get_beatmapset(
                db=Mock(),
                request=Mock(),
                beatmapset_id=123,
                current_user=None,
                fetcher=Mock(),
                cache_service=cache_service,
            )

        assert result == cached
        cache_service.get_beatmapset_from_cache.assert_awaited_once_with(123)
        get_or_fetch.assert_not_awaited()


class BeatmapsetDownloadTests(unittest.IsolatedAsyncioTestCase):
    async def test_reliable_international_mirrors_precede_catboy(self) -> None:
        service = BeatmapDownloadService()
        try:
            ordered_names = [
                endpoint.name for endpoint in sorted(service.international_endpoints, key=lambda e: e.priority)
            ]
            assert ordered_names == [
                "Nerinyan",
                "OsuDirect",
                "Catboy",
            ]
            assert service.get_download_url(1532723, no_video=True, is_china=False).startswith(
                "https://api.nerinyan.moe/d/1532723"
            )
        finally:
            await service.http_client.aclose()

    async def test_default_download_keeps_video_and_verifies_availability(self) -> None:
        fetcher = Mock()
        fetcher.get_beatmapset = AsyncMock(return_value={"availability": BeatmapAvailability(download_disabled=False)})
        download_service = Mock()
        download_service.get_download_url.return_value = "https://mirror.example/d/123"
        geoip_helper = Mock()
        geoip_helper.lookup.return_value = {"country_iso": "US"}

        with patch("app.router.v2.beatmapset.get_geoip_helper", return_value=geoip_helper):
            response = await download_beatmapset(
                client_ip="203.0.113.1",
                beatmapset_id=123,
                current_user=cast(User, SimpleNamespace(country_code="US")),
                download_service=download_service,
                fetcher=fetcher,
            )

        assert response.headers["location"] == "https://mirror.example/d/123"
        fetcher.get_beatmapset.assert_awaited_once_with(123)
        download_service.get_download_url.assert_called_once_with(beatmapset_id=123, no_video=False, is_china=False)

    async def test_download_disabled_is_rejected_before_mirror_selection(self) -> None:
        fetcher = Mock()
        fetcher.get_beatmapset = AsyncMock(return_value={"availability": BeatmapAvailability(download_disabled=True)})
        download_service = Mock()

        raised = await assert_raises_async(
            HTTPException,
            download_beatmapset(
                client_ip="203.0.113.1",
                beatmapset_id=123,
                current_user=cast(User, SimpleNamespace(country_code="US")),
                download_service=download_service,
                fetcher=fetcher,
            ),
        )

        assert raised.status_code == 403
        download_service.get_download_url.assert_not_called()

    async def test_unknown_availability_fails_closed(self) -> None:
        fetcher = Mock()
        fetcher.get_beatmapset = AsyncMock(return_value={})
        download_service = Mock()

        raised = await assert_raises_async(
            HTTPException,
            download_beatmapset(
                client_ip="203.0.113.1",
                beatmapset_id=123,
                current_user=cast(User, SimpleNamespace(country_code="US")),
                download_service=download_service,
                fetcher=fetcher,
            ),
        )

        assert raised.status_code == 503
        download_service.get_download_url.assert_not_called()

    async def test_official_not_found_preserves_not_found_response(self) -> None:
        fetcher = Mock()
        response = Mock(status_code=404)
        fetcher.get_beatmapset = AsyncMock(side_effect=HTTPStatusError("not found", request=Mock(), response=response))

        raised = await assert_raises_async(
            RequestError,
            download_beatmapset(
                client_ip="203.0.113.1",
                beatmapset_id=123,
                current_user=cast(User, SimpleNamespace(country_code="US")),
                download_service=Mock(),
                fetcher=fetcher,
            ),
        )

        assert raised.status_code == 404


if __name__ == "__main__":
    unittest.main()
