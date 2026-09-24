import base64
import json
import unittest
from unittest.mock import patch

from app.database import SearchBeatmapsetsResp
from app.router.private.web_site import _cursor_for_search_cache, search_web_beatmapsets


class _CacheService:
    def __init__(self) -> None:
        self.lookups: list[tuple[str, str]] = []
        self.writes: list[tuple[str, str]] = []

    async def get_search_from_cache(self, query_hash: str, cursor_hash: str):
        self.lookups.append((query_hash, cursor_hash))
        return None

    async def cache_search_result(self, query_hash: str, cursor_hash: str, _result: dict) -> None:
        self.writes.append((query_hash, cursor_hash))


class _Fetcher:
    def __init__(self) -> None:
        self.cursor_string: str | None = None
        self.cache_cursor: dict[str, int | float] = {}

    async def search_beatmapset(self, query, cursor, _redis) -> SearchBeatmapsetsResp:
        self.cursor_string = query.cursor_string
        self.cache_cursor = cursor.copy()
        return SearchBeatmapsetsResp(beatmapsets=[], total=0, cursor_string="next_cursor")


class WebSiteBeatmapPaginationTests(unittest.IsolatedAsyncioTestCase):
    def test_numeric_cursor_reuses_the_fetcher_cache_shape(self) -> None:
        cursor = {"_id": 42, "_score": 9.5}
        encoded = base64.b64encode(json.dumps(cursor).encode()).decode()

        assert _cursor_for_search_cache(encoded) == cursor
        assert _cursor_for_search_cache("opaque_cursor") != {}
        assert _cursor_for_search_cache(None) == {}

    async def test_cursor_is_forwarded_and_separates_cached_pages(self) -> None:
        cache = _CacheService()
        fetcher = _Fetcher()
        overlay_first_page: list[bool] = []

        async def passthrough_overlay(_session, _query, result, *, current_user, first_page):
            assert current_user is None
            overlay_first_page.append(first_page)
            return result

        with (
            patch(
                "app.router.private.web_site.get_beatmapset_cache_service",
                return_value=cache,
            ),
            patch(
                "app.router.private.web_site.overlay_local_ranked_beatmapsets",
                side_effect=passthrough_overlay,
            ),
        ):
            response = await search_web_beatmapsets(
                session=object(),  # type: ignore[arg-type]
                redis=object(),  # type: ignore[arg-type]
                fetcher=fetcher,  # type: ignore[arg-type]
                q="artist",
                mode="osu",
                status_filter="leaderboard",
                sort="relevance_desc",
                cursor_string="opaque_cursor_2",
            )

        assert fetcher.cursor_string == "opaque_cursor_2"
        assert fetcher.cache_cursor
        assert cache.lookups == cache.writes
        assert overlay_first_page == [False]
        assert response["cursor_string"] == "next_cursor"


if __name__ == "__main__":
    unittest.main()
