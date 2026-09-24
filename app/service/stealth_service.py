"""Public official identities and PP/rank samples; never writes a user's identity or statistics."""

import asyncio
import json
import math
import random
import re

from app.fetcher import Fetcher
from app.fetcher._base import TokenAuthError

from fastapi import HTTPException
from httpx import HTTPError
from redis.asyncio import Redis

_slots = asyncio.Semaphore(2)
_countries = ("FI", "NO", "IE", "NZ", "AT", "EE", "LV", "SI", "HR", "IS")


def candidates_and_rank(rows: list[dict], pp: float, *, exact: bool = False) -> dict:
    # Country lists include the real GLOBAL rank, even below the global top-10,000 cutoff.
    unique = {}
    for row in rows:
        user = row.get("user") or {}
        value, rank = row.get("pp"), row.get("global_rank")
        country = str(user.get("country_code") or (user.get("country") or {}).get("code") or "").upper()
        country_rank = row.get("country_rank")
        if (
            user.get("id", 0) > 1
            and user.get("username")
            and not user.get("is_deleted")
            and not user.get("is_restricted")
            and isinstance(value, (int, float))
            and math.isfinite(value)
            and isinstance(rank, int)
            and rank > 0
        ):
            unique[user["id"]] = {
                "id": user["id"],
                "username": user["username"],
                "avatar_url": user.get("avatar_url") or f"https://a.ppy.sh/{user['id']}",
                "pp": value,
                "global_rank": rank,
                "country_code": country if re.fullmatch("[A-Z]{2}", country) else None,
                "country_rank": country_rank if isinstance(country_rank, int) and country_rank > 0 else None,
            }
    samples = sorted(unique.values(), key=lambda x: x["pp"])
    if not samples:
        raise HTTPException(503, "Не удалось получить игроков официального рейтинга.")
    nearest = min(samples, key=lambda x: abs(x["pp"] - pp))
    # Prefer the same rounded PP, otherwise a small, explicit neighbourhood.
    candidates = [s for s in samples if round(s["pp"]) == round(pp)]
    if len(candidates) < 2:
        radius = max(25, pp * 0.015, abs(nearest["pp"] - pp))
        candidates = sorted((s for s in samples if abs(s["pp"] - pp) <= radius), key=lambda s: abs(s["pp"] - pp))[:30]
    below = next((s for s in reversed(samples) if s["pp"] <= pp), None)
    above = next((s for s in samples if s["pp"] >= pp), None)
    rank = nearest["global_rank"]
    if exact:
        higher = [s["global_rank"] for s in samples if s["pp"] > pp]
        rank = max(higher, default=0) + 1
    elif below and above and above["pp"] > below["pp"]:
        fraction = (pp - below["pp"]) / (above["pp"] - below["pp"])
        rank = round(
            math.exp(math.log(below["global_rank"]) * (1 - fraction) + math.log(above["global_rank"]) * fraction)
        )
    return {"candidates": candidates, "global_rank": max(1, rank), "estimated": not exact, "pp": pp}


async def nearby_players(fetcher: Fetcher, redis: Redis, mode: str, pp: float, country: str | None = None) -> dict:
    if mode not in {"osu", "taiko", "fruits", "mania"} or not math.isfinite(pp) or not 0 <= pp <= 1000000:
        raise HTTPException(400, "Некорректный режим или PP.")
    if country is not None:
        country = country.upper()
        if not re.fullmatch("[A-Z]{2}", country):
            raise HTTPException(400, "Некорректный код страны.")

    async def page(country: str, number: int) -> dict:
        key = f"stealth:ranking:v1:{mode}:{country}:{number}"
        if cached := await redis.get(key):
            return json.loads(cached)
        params = {"cursor[page]": str(number)}
        if country:
            params["country"] = country
        data = await fetcher.request_api(
            f"https://osu.ppy.sh/api/v2/rankings/{mode}/performance", params=params, timeout=5
        )
        await redis.set(key, json.dumps(data), ex=1200)
        return data

    async def search(country: str) -> list[dict]:
        def ranked_rows(data: dict, number: int) -> list[dict]:
            # Never mutate the cached response. A country page's position also
            # supplies the rank when the upstream omits country_rank.
            return [
                {**row, "country_rank": row.get("country_rank") or (number - 1) * 50 + index + 1} if country else row
                for index, row in enumerate(data.get("ranking", []))
            ]

        first = await page(country, 1)
        rows = ranked_rows(first, 1)
        low, high = 1, max(1, min(200, math.ceil(first.get("total", 0) / 50)))
        while low <= high:
            mid = (low + high) // 2
            data = first if mid == 1 else await page(country, mid)
            batch = ranked_rows(data, mid)
            rows.extend(batch)
            if not batch or pp >= (batch[0].get("pp") or 0):
                high = mid - 1
            elif pp < (batch[-1].get("pp") or 0):
                low = mid + 1
            else:
                break
        return rows

    try:
        async with asyncio.timeout(50), _slots:
            # The official global endpoint clamps all pages to 200. Do not repeatedly
            # request nonexistent pages for a typical 4/5/6-digit account.
            last = (await page("", 200)).get("ranking", [])
            country_samples = await search(country) if country else None
            if last and pp >= (last[-1].get("pp") or 0):
                result = candidates_and_rank(await search(""), pp, exact=True)
            elif country_samples is not None:
                result = candidates_and_rank(country_samples, pp)
            else:
                countries = random.sample(_countries, 2)
                samples = []
                for sample_country in countries:
                    samples.extend(await search(sample_country))
                result = candidates_and_rank(samples, pp)
            if country_samples is not None:
                higher = [r["country_rank"] for r in country_samples if (r.get("pp") or 0) > pp]
                at_or_below = [r["country_rank"] for r in country_samples if (r.get("pp") or 0) <= pp]
                result["country_code"] = country
                result["country_rank"] = min(at_or_below) if at_or_below else max(higher, default=0) + 1
                result["country_rank_estimated"] = result["country_rank"] > 10000
            return result
    except (HTTPError, TimeoutError, TokenAuthError):
        raise HTTPException(503, "Официальный рейтинг временно недоступен. Попробуйте Reroll stealth позже.") from None
