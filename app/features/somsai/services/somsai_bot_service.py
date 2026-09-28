"""Server-owned custom opponents with independent match-scoped skillsets."""

import asyncio
import json
import random
import secrets
import time

from app.database import User, UserStatistics
from app.features.somsai.services.somsai_bot_skill import division_for, roll_ai_profile
from app.features.somsai.services.somsai_bot_tech import technical_features
from app.log import log
from app.models.score import GameMode
from app.models.user import Page


async def _upstream_budget(redis, purpose="profile"):
    # Shared across rooms/workers. Exhausting the enrichment budget falls back
    # to cached data/rank rather than stalling room creation or flooding Bancho.
    key = f"somsai:bot-upstream-budget:{int(time.time()) // 60}"
    if purpose == "profile":
        profile_requests = await redis.incr(key + ":profiles")
        if profile_requests == 1:
            await redis.expire(key + ":profiles", 65)
        if profile_requests > 32:
            raise TimeoutError("Profile budget exhausted; reserve requests for the selected pool")
    requests = await redis.incr(key)
    if requests == 1:
        await redis.expire(key, 65)
    if purpose != "identity" and requests > 44:
        raise TimeoutError("Reserve the last requests for opponent identities")
    if requests > 48:
        raise TimeoutError("Bot profile enrichment budget exhausted")


async def _official_request(fetcher, redis, url: str, **kwargs):
    await _upstream_budget(redis, "identity" if "/rankings/" in url or "/users/mrekk/" in url else "profile")
    return await fetcher.request_api(url, **kwargs)


async def _chart_raw(fetcher, redis, map_id, checksum, purpose="profile"):
    key = f"beatmap:{map_id}:{checksum}:raw" if checksum else f"beatmap:{map_id}:raw"
    cached = await redis.get(key)
    if cached:
        return cached.decode("utf-8") if isinstance(cached, bytes) else cached
    await _upstream_budget(redis, purpose)
    async with asyncio.timeout(3):
        raw = await fetcher.get_beatmap_raw(map_id, checksum)
    if not isinstance(raw, str) or len(raw) > 4_000_000 or "[HitObjects]" not in raw:
        raise ValueError("Invalid beatmap data")
    await redis.set(key, raw, ex=86400)
    return raw


async def _technical(fetcher, redis, ruleset_id, map_id, checksum, limit):
    if ruleset_id != 0:
        return {}
    key = f"somsai:bot-tech:v1:{map_id}:{checksum}"
    try:
        cached = await redis.get(key)
        if cached:
            return json.loads(cached)
        async with limit:
            raw = await _chart_raw(fetcher, redis, map_id, checksum)
        result = technical_features(raw)
        await redis.set(key, json.dumps(result), ex=1209600 if checksum else 86400)
        return result
    except Exception:
        return {}


async def bot_personas(
    fetcher,
    redis,
    ruleset_id: int,
    level: str,
    count: int = 1,
    *,
    target_mmr: int | None = None,
    target_rank: str | None = None,
) -> list[dict]:
    """Choose visual identities, then independently roll a new match skillset."""
    _ = level  # Kept in the wire signature for older clients.
    division = division_for(rating=target_mmr, rank=target_rank)
    values = await _persona_candidates(fetcher, redis, ruleset_id, division)
    selected = random.sample(values, min(count, len(values)))
    while len(selected) < max(1, count):
        selected.append(random.choice(values))
    # roll_ai_profile is intentionally called here, once per bot and new match.
    # Nothing from the chosen public account is passed into the roll.
    result = [
        {**user, "ai_profile": roll_ai_profile(rating=target_mmr, rank=target_rank)}
        for user in selected[: max(1, count)]
    ]
    # Every freshly rolled bot team must satisfy the same FM contract as players.
    # One member is pure HD (EZ+HD is also legal for humans), and another carries HR.
    if len(result) > 1:
        preferences = [bot["ai_profile"].get("freemod_preference") or "" for bot in result]
        pure_hd = next((i for i, value in enumerate(preferences) if "HD" in value and "HR" not in value), None)
        if pure_hd is None:
            pure_hd = 0
            result[pure_hd]["ai_profile"]["freemod_preference"] = "HD"
        hr = next((i for i, value in enumerate(preferences) if i != pure_hd and "HR" in value), None)
        if hr is None:
            hr = next(i for i in range(len(result)) if i != pure_hd)
            result[hr]["ai_profile"]["freemod_preference"] = "HR"
    return result


async def _persona_candidates(fetcher, redis, ruleset_id: int, division) -> list[dict]:
    """Find a real profile in the division for presentation only."""
    mode = ("osu", "taiko", "fruits", "mania")[ruleset_id]
    lower, upper = division.rank_low, division.rank_high
    direct = upper <= 10_000
    page = random.randint((lower - 1) // 50 + 1, (upper - 1) // 50 + 1) if direct else None
    cache_suffix = str(page) if page else str(random.randint(1, 8))
    key = f"somsai:bot-personas:v4:{mode}:{division.name}:{cache_suffix}"
    cached = await redis.get(key)
    if cached and await redis.get(key + ":fresh"):
        return json.loads(cached)
    candidates = []
    try:
        async with asyncio.timeout(10):
            for _ in range(4):
                if direct:
                    params = {"cursor[page]": str(page)}
                else:
                    # Country rankings expose global ranks past the public
                    # global leaderboard cutoff. Returned global ranks are
                    # always validated against the requested division.
                    params = {
                        "country": random.choice(("FI", "NZ", "IE", "EE", "HR", "LT", "LV", "IS")),
                        "cursor[page]": str(random.randint(1, 120)),
                    }
                data = await _official_request(
                    fetcher,
                    redis,
                    f"https://osu.ppy.sh/api/v2/rankings/{mode}/performance",
                    params=params,
                    timeout=4,
                )
                candidates.extend(
                    {**row["user"], "global_rank": row.get("global_rank")}
                    for row in data.get("ranking", [])
                    if lower <= (row.get("global_rank") or 0) <= upper and row.get("user")
                )
                if len(candidates) >= 4:
                    break
    except Exception as exc:
        log("SomsaiBots").warning("Official bot identities unavailable: {}", type(exc).__name__)
    unique = {
        int(user["id"]): {
            "official_id": int(user["id"]),
            "username": str(user["username"])[:20],
            "avatar_url": f"https://a.ppy.sh/{int(user['id'])}",
            "country_code": user.get("country_code", "XX"),
            "global_rank": user.get("global_rank") or (user.get("statistics") or {}).get("global_rank"),
        }
        for user in candidates
        if user.get("id") and user.get("username") and not user.get("is_deleted") and not user.get("is_restricted")
    }
    values = list(unique.values())
    if not values and cached:
        values = json.loads(cached)
    if not values:
        values = [
            {
                "username": f"{division.name.title()} Player",
                "country_code": "XX",
                "official_id": None,
                "avatar_url": "/site/soms-default-avatar.png",
                "global_rank": None,
            }
        ]
    await redis.set(key, json.dumps(values), ex=43_200 if unique else 60)
    await redis.set(key + ":fresh", "1", ex=600 if unique else 60)
    return values


async def prepare_draft_features(fetcher, redis, match_id: int, ruleset_id: int, slots: list[dict]) -> None:
    """Populate exact modded map styles after creating the room, without holding its lock."""
    from copy import deepcopy

    from app.calculating import get_calculator
    from app.features.somsai.database.somsai import SomsaiMatch
    from app.features.somsai.services.somsai_match_service import FINAL_STAGES
    from app.features.somsai.services.somsai_party_service import somsai_transaction

    attributes = {}
    displays = {}
    limit = asyncio.Semaphore(4)

    async def load(slot):
        mods = [] if slot.get("category") in {"FM", "TB"} else slot.get("mods") or []
        key = (
            f"somsai:bot-draft:v4:{ruleset_id}:{slot['beatmap_id']}:{slot.get('checksum')}:"
            f"{json.dumps(mods, sort_keys=True)}"
        )
        try:
            cached = await redis.get(key)
            if cached:
                data = json.loads(cached)
                attributes[slot["id"]] = data["attributes"]
                displays[slot["id"]] = data["display"]
                return
            else:
                async with limit:
                    raw = await _chart_raw(fetcher, redis, slot["beatmap_id"], slot.get("checksum"), "draft")
                    result = await get_calculator().calculate_difficulty(raw, mods, GameMode.from_int(ruleset_id))
                attributes[slot["id"]] = result.model_dump()
                if ruleset_id == 0:
                    attributes[slot["id"]]["technical"] = technical_features(raw)
                from app.features.somsai.services.somsai_map_stats import display_stats

                displays[slot["id"]] = display_stats(raw, slot, attributes[slot["id"]], ruleset_id)
                await redis.set(
                    key, json.dumps({"attributes": attributes[slot["id"]], "display": displays[slot["id"]]}), ex=86400
                )
        except Exception:
            return

    try:
        async with asyncio.timeout(12):
            await asyncio.gather(*(load(slot) for slot in slots[:64]))
    except TimeoutError:
        pass
    if not attributes:
        return
    async with somsai_transaction() as session:
        match = await session.get(SomsaiMatch, match_id)
        if match is None or match.stage in FINAL_STAGES:
            return
        state = deepcopy(match.state)
        for slot in state["slots"]:
            if slot["id"] in attributes and any(
                s["id"] == slot["id"] and s["beatmap_id"] == slot["beatmap_id"] for s in slots
            ):
                slot["bot_attributes"] = attributes[slot["id"]]
                slot["display_stats"] = displays.get(slot["id"])
        # Draft metadata must not invalidate an already displayed action revision.
        match.state = state
        session.add(match)


async def create_bot_team(session, count: int, level: str, personas: list[dict]) -> list[dict]:
    choices = random.sample(personas, min(count, len(personas)))
    result = []
    for index in range(count):
        persona = choices[index % len(choices)]
        suffix = secrets.token_hex(4)
        name = f"{persona['username'][:15]} [bot {suffix}]"
        official = persona.get("official_id")
        url = f"https://osu.ppy.sh/users/{official}" if official else None
        description = "SOMSAI server bot for custom matches. Not a real Bancho player."
        if url:
            description += f" Player template: {url}"
        user = User(
            username=name,
            email=f"somsai-bot-{suffix}@soms.invalid",
            is_bot=True,
            pw_bcrypt="!",
            avatar_url=persona["avatar_url"],
            country_code=persona.get("country_code") or "XX",
            website=url,
            page=Page(raw=description, html=description),
        )
        session.add(user)
        await session.flush()
        for mode in (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
            session.add(UserStatistics(user_id=user.id, mode=mode, is_ranked=False))
        result.append({**persona, "user_id": user.id, "level": level, "seed": secrets.randbelow(2**30)})
    return result
