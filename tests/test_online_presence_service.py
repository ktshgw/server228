import asyncio
from typing import Any
import unittest

from app.service.online_presence_service import (
    ONLINE_PRESENCE_KEY_PREFIX,
    _user_id_from_presence_key,
    count_online_users,
    get_online_user_ids,
    refresh_online_presence,
)


class FakeRedis:
    def __init__(self, keys: list[Any]):
        self.keys = keys
        self.calls: list[tuple[str, int]] = []

    async def scan_iter(self, *, match: str, count: int):
        self.calls.append((match, count))
        for key in self.keys:
            yield key


class FakeExpiringRedis:
    def __init__(self, result: bool = True):
        self.result = result
        self.calls: list[tuple[str, int, bool]] = []

    async def expire(self, key: str, ttl: int, *, xx: bool = False) -> bool:
        self.calls.append((key, ttl, xx))
        return self.result


class OnlinePresenceParsingTests(unittest.TestCase):
    def test_accepts_only_positive_decimal_metadata_ids(self) -> None:
        assert _user_id_from_presence_key("metadata:online:42") == 42
        assert _user_id_from_presence_key(b"metadata:online:7") == 7
        assert _user_id_from_presence_key("metadata:online:0") is None
        assert _user_id_from_presence_key("metadata:online:-1") is None
        assert _user_id_from_presence_key("metadata:online:1.5") is None
        assert _user_id_from_presence_key("other:online:42") is None
        assert _user_id_from_presence_key(b"metadata:online:\xff") is None
        assert _user_id_from_presence_key(42) is None


class OnlinePresenceServiceTests(unittest.TestCase):
    def test_scans_without_keys_and_returns_sorted_unique_ids(self) -> None:
        redis = FakeRedis(
            [
                "metadata:online:10",
                b"metadata:online:2",
                "metadata:online:10",
                "metadata:online:broken",
            ]
        )

        result = asyncio.run(get_online_user_ids(redis, scan_count=25))  # type: ignore[arg-type]

        assert result == [2, 10]
        assert redis.calls == [(f"{ONLINE_PRESENCE_KEY_PREFIX}*", 25)]

    def test_count_uses_distinct_live_ids(self) -> None:
        redis = FakeRedis(["metadata:online:3", "metadata:online:3", "metadata:online:9"])

        assert asyncio.run(count_online_users(redis)) == 2  # type: ignore[arg-type]

    def test_refresh_extends_only_an_existing_spectator_key(self) -> None:
        redis = FakeExpiringRedis()

        refreshed = asyncio.run(refresh_online_presence(redis, 42, ttl_seconds=90))  # type: ignore[arg-type]

        assert refreshed is True
        assert redis.calls == [("metadata:online:42", 90, True)]

    def test_refresh_rejects_invalid_user_without_touching_redis(self) -> None:
        redis = FakeExpiringRedis()

        refreshed = asyncio.run(refresh_online_presence(redis, 0))  # type: ignore[arg-type]

        assert refreshed is False
        assert redis.calls == []
