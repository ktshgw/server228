"""Cached, public osu! profile reads for the client's separate official overlay."""

import asyncio
import hashlib
import json
import re
from urllib.parse import quote

from app.fetcher import Fetcher
from app.fetcher._base import TokenAuthError

from fastapi import HTTPException
from httpx import HTTPError, HTTPStatusError
from redis.asyncio import Redis

_slots = asyncio.Semaphore(4)
_suffix = re.compile(
    r"(?:osu|taiko|fruits|mania|recent_activity|kudosu|"
    r"scores/(?:best|firsts|recent|pinned)|"
    r"beatmapsets/(?:ranked|loved|pending|graveyard|guest|nominated|favourite|most_played))?"
)


def validate_request(target: str, query: dict[str, str]) -> tuple[str, dict[str, str]]:
    parts = target.rstrip("/").split("/")
    if len(parts) < 2 or parts[0] != "users" or not 1 <= len(parts[1]) <= 50:
        raise HTTPException(404, "Unknown official profile resource")
    if any(c in parts[1] for c in "\\?#%") or parts[1] in {".", "..", "me"}:
        raise HTTPException(400, "Invalid official user")
    suffix = "/".join(parts[2:])
    if not _suffix.fullmatch(suffix):
        raise HTTPException(404, "Unknown official profile resource")
    if suffix not in {"", "osu", "taiko", "fruits", "mania"} and not parts[1].isdigit():
        raise HTTPException(400, "A user ID is required")
    allowed = {"key", "mode", "limit", "offset", "include_fails", "legacy_only"}
    if query.keys() - allowed:
        raise HTTPException(400, "Unsupported official profile query")
    for key, value in query.items():
        if key in {"limit", "offset"}:
            maximum = 100 if key == "limit" else 10000
            if not value.isdigit() or not 0 <= int(value) <= maximum:
                raise HTTPException(400, "Invalid pagination")
        elif key == "key" and value not in {"id", "username"}:
            raise HTTPException(400, "Invalid lookup type")
        elif key == "mode" and value not in {"osu", "taiko", "fruits", "mania"}:
            raise HTTPException(400, "Invalid ruleset")
        elif key in {"include_fails", "legacy_only"} and value not in {"0", "1", "true", "false"}:
            raise HTTPException(400, "Invalid flag")
    path = "users/" + quote(parts[1], safe="") + ("/" + suffix if suffix else "")
    return path, query


async def get_official_resource(fetcher: Fetcher, redis: Redis, target: str, query: dict[str, str]):
    path, params = validate_request(target, query)
    digest = hashlib.sha256(json.dumps([path, params], sort_keys=True).encode()).hexdigest()
    cache_key = f"official-profile:20260804:{digest}"
    cached = await redis.get(cache_key)
    if cached:
        return json.loads(cached)
    try:
        async with asyncio.timeout(18), _slots:
            # Recheck after waiting for a slot: most profile requests are shared by viewers.
            cached = await redis.get(cache_key)
            if cached:
                return json.loads(cached)
            data = await fetcher.request_api(
                f"https://osu.ppy.sh/api/v2/{path}",
                params=params,
                headers={"x-api-version": "20260804"},
                timeout=12,
            )
    except HTTPStatusError as exc:
        code = 404 if exc.response.status_code == 404 else 503
        raise HTTPException(code, "Official profile unavailable") from None
    except (HTTPError, TimeoutError, TokenAuthError):
        raise HTTPException(503, "Official profile unavailable") from None
    await redis.set(cache_key, json.dumps(data, ensure_ascii=False), ex=300)
    return data
