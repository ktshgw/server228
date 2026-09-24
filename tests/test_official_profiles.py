# unittest is the repository's standalone test runner.
# ruff: noqa: PT027
import json
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock

from app.service.official_profile_service import get_official_resource, validate_request

from fastapi import HTTPException
from httpx import HTTPStatusError, Request, Response


class OfficialProfileTests(unittest.IsolatedAsyncioTestCase):
    def test_supported_native_resources_and_username_escaping(self):
        for suffix in (
            "",
            "/",
            "/osu",
            "/taiko",
            "/fruits",
            "/mania",
            "/scores/best",
            "/scores/pinned",
            "/beatmapsets/ranked",
            "/beatmapsets/guest",
            "/beatmapsets/most_played",
            "/recent_activity",
            "/kudosu",
        ):
            with self.subTest(suffix=suffix):
                validate_request("users/42" + suffix, {"limit": "5", "offset": "0"})
        assert validate_request("users/Mapper Name/", {"key": "username"})[0] == "users/Mapper%20Name"

    def test_only_public_profile_reads_are_allowed(self):
        for target in (
            "me",
            "users/me",
            "users/../osu",
            "users/2/friends",
            "users/2/tokens",
            "users/2/scores/123/download",
            "https://evil.invalid",
            "users/%2f/",
            "users/2\\evil",
            "users/2/../../oauth/token",
        ):
            with self.subTest(target=target), self.assertRaises(HTTPException):
                validate_request(target, {})
        for query in ({"access_token": "x"}, {"mode": "other"}, {"limit": "101"}, {"offset": "-1"}, {"key": "secret"}):
            with self.subTest(query=query), self.assertRaises(HTTPException):
                validate_request("users/42", query)

    async def test_cache_and_upstream_identity_are_separate_from_soms(self):
        data = {"id": 42, "username": "Official mapper", "avatar_url": "https://a.ppy.sh/42"}
        fetcher = SimpleNamespace(request_api=AsyncMock(return_value=data))
        redis = SimpleNamespace(get=AsyncMock(return_value=None), set=AsyncMock())
        result = await get_official_resource(fetcher, redis, "users/42/osu", {})
        assert result == data
        fetcher.request_api.assert_awaited_once_with(
            "https://osu.ppy.sh/api/v2/users/42/osu", params={}, headers={"x-api-version": "20260804"}, timeout=12
        )
        assert redis.set.await_args.kwargs["ex"] == 300
        redis.get.return_value = json.dumps(data)
        assert await get_official_resource(fetcher, redis, "users/42/osu", {}) == data
        assert fetcher.request_api.await_count == 1

    async def test_errors_are_bounded_and_do_not_disclose_credentials(self):
        redis = SimpleNamespace(get=AsyncMock(return_value=None), set=AsyncMock())
        for error, expected in (
            (TimeoutError(), 503),
            (HTTPStatusError("secret", request=Request("GET", "https://osu.ppy.sh"), response=Response(404)), 404),
            (HTTPStatusError("secret", request=Request("GET", "https://osu.ppy.sh"), response=Response(401)), 503),
        ):
            fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=error))
            with self.subTest(error=error), self.assertRaises(HTTPException) as caught:
                await get_official_resource(fetcher, redis, "users/42", {})
            assert caught.exception.status_code == expected
            assert "secret" not in caught.exception.detail
        redis.set.assert_not_awaited()

    async def test_invalid_target_never_reaches_upstream(self):
        fetcher = SimpleNamespace(request_api=AsyncMock())
        redis = SimpleNamespace(get=AsyncMock(), set=AsyncMock())
        with self.assertRaises(HTTPException):
            await get_official_resource(fetcher, redis, "users/2/friends", {})
        fetcher.request_api.assert_not_awaited()
        redis.get.assert_not_awaited()
