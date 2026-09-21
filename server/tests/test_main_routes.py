from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from main import open_beatmapset, open_user_profile

from fastapi import HTTPException


class BrowserCompatibilityRouteTests(unittest.IsolatedAsyncioTestCase):
    async def test_bot_profile_link_opens_only_its_official_profile(self) -> None:
        bot = SimpleNamespace(is_bot=True, is_active=True, website="https://osu.ppy.sh/users/7562902")
        session = SimpleNamespace(get=AsyncMock(return_value=bot))
        with patch("main.resolve_human_user", new=AsyncMock(return_value=None)):
            response = await open_user_profile(999, session=session)
            assert response.headers["location"] == bot.website
            bot.website = "https://example.com/"
            caught = None
            try:
                await open_user_profile(999, session=session)
            except HTTPException as exc:
                caught = exc
            assert caught is not None
            assert caught.status_code == 404

    async def test_game_profile_link_redirects_to_site_hash_route(self) -> None:
        user = SimpleNamespace(server_id=1, id=1500000000, is_active=True, is_restricted=AsyncMock(return_value=False))
        with patch("main.resolve_human_user", new=AsyncMock(return_value=user)):
            response = await open_user_profile(1500000000, session=object())  # type: ignore[arg-type]

        assert response.status_code == 302
        assert response.headers["location"] == "/site/#profile/1"

    async def test_invalid_profile_id_is_not_redirected(self) -> None:
        caught: HTTPException | None = None
        try:
            await open_user_profile(0, session=object())  # type: ignore[arg-type]
        except HTTPException as exc:
            caught = exc

        assert caught is not None
        assert caught.status_code == 404

    async def test_game_beatmap_link_uses_fragment_aware_bridge(self) -> None:
        response = await open_beatmapset(1177270)
        body = bytes(response.body).decode()

        assert response.status_code == 200
        assert response.headers["cache-control"] == "no-store"
        assert 'name="soms-beatmapset-id" content="1177270"' in body
        assert 'http-equiv="refresh" content="1;url=/site/#beatmap/1177270"' in body
        assert 'src="/site/legacy-beatmap-bridge.js"' in body
        assert 'href="/site/#beatmap/1177270"' in body
        assert "window.location" not in body

    async def test_invalid_beatmapset_id_is_not_bridged(self) -> None:
        caught: HTTPException | None = None
        try:
            await open_beatmapset(0)
        except HTTPException as exc:
            caught = exc

        assert caught is not None
        assert caught.status_code == 404


if __name__ == "__main__":
    unittest.main()
