"""Server-owned custom opponents with bounded official score histories."""

import asyncio
import json
import random
import secrets
import time

from app.database import User, UserStatistics
from app.log import log
from app.models.score import GameMode
from app.models.user import Page
from app.features.somsai.services.somsai_bot_skill import history_candidates, mod_names, normalize_level, score_sample, skill_profile
from app.features.somsai.services.somsai_bot_tech import technical_features

LEVELS = ("easy", "medium", "hard", "top1000", "mrekk")
NAMES = ("Bubble Rookie", "Coral Swimmer", "Reef Hunter", "Deep Sea Ace", "mrekk")
RANKS = ((100000, 999999), (10000, 99999), (1000, 9999), (1, 999), (1, 1))


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


async def bot_personas(fetcher, redis, ruleset_id: int, level: str, count: int = 1) -> list[dict]:
    level = normalize_level(level)
    values = await _persona_candidates(fetcher, redis, ruleset_id, level)
    selected = random.sample(values, min(max(1, count), len(values)))
    # Only chosen opponents are profiled; never fetch scores for a whole leaderboard.
    limit = asyncio.Semaphore(4)
    profiles = await asyncio.gather(*(_load_skill(fetcher, redis, ruleset_id, level, user, limit) for user in selected))
    return [{**user, "skill_profile": profile} for user, profile in zip(selected, profiles)]


async def _persona_candidates(fetcher, redis, ruleset_id: int, level: str) -> list[dict]:
    """Bounded upstream lookup, outside the SOMSAI transaction/roster lock."""
    mode = ("osu", "taiko", "fruits", "mania")[ruleset_id]
    # Cache each global page separately. A tier-wide cache used to pin every
    # room to the same 50 players for twelve hours (or to a failed lookup).
    top_page = (random.randint(1, 999) - 1) // 50 + 1 if level == "top1000" else None
    key = f"somsai:bot-personas:v3:{mode}:{level}" + (f":{top_page}" if top_page else "")
    cached = await redis.get(key)
    if cached and await redis.get(key + ":fresh"):
        return json.loads(cached)
    index = LEVELS.index(level)
    candidates = []
    try:
        async with asyncio.timeout(15):
            if level == "mrekk":
                data = await _official_request(
                    fetcher, redis, "https://osu.ppy.sh/api/v2/users/mrekk/osu", params={"key": "username"}, timeout=5
                )
                candidates = [data]
            else:
                lower, upper = RANKS[index]
                # Country lists expose GLOBAL ranks past the global endpoint's
                # 10,000 cutoff. Validate that returned players fit this tier.
                for _ in range(3):
                    if lower >= 10000:
                        params = {
                            "country": random.choice(("FI", "NZ", "IE", "EE", "HR")),
                            "cursor[page]": str(random.randint(8, 60) if index == 0 else random.randint(2, 12)),
                        }
                    else:
                        params = {"cursor[page]": str(top_page or (random.randint(lower, upper) - 1) // 50 + 1)}
                    data = await _official_request(
                        fetcher,
                        redis,
                        f"https://osu.ppy.sh/api/v2/rankings/{mode}/performance",
                        params=params,
                        timeout=5,
                    )
                    candidates.extend(
                        {**row["user"], "global_rank": row.get("global_rank"), "pp": row.get("pp")}
                        for row in data.get("ranking", [])
                        if lower <= (row.get("global_rank") or 0) <= upper and row.get("user")
                    )
                    if len(candidates) >= 4:
                        break
    except Exception as exc:
        # An upstream outage must not prevent a local custom from being created.
        log("SomsaiBots").warning("Official bot profiles unavailable: {}", type(exc).__name__)
    unique = {
        int(u["id"]): {
            "official_id": int(u["id"]),
            "username": str(u["username"])[:20],
            "avatar_url": f"https://a.ppy.sh/{int(u['id'])}",
            "country_code": u.get("country_code", "XX"),
            "global_rank": u.get("global_rank") or (u.get("statistics") or {}).get("global_rank"),
        }
        for u in candidates
        if u.get("id") and u.get("username") and not u.get("is_deleted") and not u.get("is_restricted")
    }
    values = list(unique.values())
    if not values and cached:
        values = json.loads(cached)
    if not values and top_page:
        # Keep real identities during short upstream outages or rate limiting.
        # Never cache a generic placeholder over a leaderboard page.
        pages = await asyncio.gather(
            *(redis.get(f"somsai:bot-personas:v3:{mode}:{level}:{page}") for page in range(1, 21))
        )
        values = [user for page in pages if page for user in json.loads(page) if user.get("official_id")]
    if not values:
        values = [
            {
                "username": NAMES[index],
                "country_code": "XX",
                "official_id": None,
                "avatar_url": "/site/soms-default-avatar.png",
                "global_rank": None,
            }
        ]
    if unique or not top_page:
        await redis.set(key, json.dumps(values), ex=43200 if unique else 60)
    # The roster is chosen for each new room. Refresh ranking pages every ten
    # minutes; the longer-lived copy is only an outage/rate-limit fallback.
    await redis.set(key + ":fresh", "1", ex=600 if unique else 60)
    return values


async def _load_skill(fetcher, redis, ruleset_id: int, level: str, user: dict, limit: asyncio.Semaphore) -> dict:
    fallback = skill_profile(user.get("global_rank"), level, [])
    official_id = user.get("official_id")
    if not official_id or level == "mrekk":
        return fallback
    mode = ("osu", "taiko", "fruits", "mania")[ruleset_id]
    key = f"somsai:bot-skill:v2:{mode}:{official_id}"
    cached = await redis.get(key)
    if cached:
        # Rank can change before the longer-lived score history expires.
        return skill_profile(user.get("global_rank"), level, json.loads(cached).get("samples", []))
    samples = []
    try:
        async with asyncio.timeout(12):

            async def history(kind: str):
                try:
                    async with limit:
                        return await _official_request(
                            fetcher,
                            redis,
                            f"https://osu.ppy.sh/api/v2/users/{official_id}/scores/{kind}",
                            params={"mode": mode, "limit": 100 if kind == "best" else 16, "include_fails": 1},
                            timeout=4,
                        )
                except Exception:
                    return []

            best, recent = await asyncio.gather(history("best"), history("recent"))
            unique = history_candidates(list(best), list(recent))

            async def analyse(score: dict):
                mods = score.get("mods") or []
                attributes = None
                # Aim/speed attributes distinguish e.g. aim-DT from stream-DT.
                if (score.get("beatmap") or {}).get("id"):
                    beatmap = score.get("beatmap") or {}
                    map_id = beatmap.get("id")
                    if not map_id:
                        return
                    mod_key = json.dumps(mods, sort_keys=True, separators=(",", ":"))
                    attr_key = f"somsai:bot-attrs:v1:{ruleset_id}:{map_id}:{mod_key}"
                    attr_cached = await redis.get(attr_key)
                    if attr_cached:
                        attributes = json.loads(attr_cached)
                    else:
                        try:
                            async with limit:
                                result = await _official_request(
                                    fetcher,
                                    redis,
                                    f"https://osu.ppy.sh/api/v2/beatmaps/{map_id}/attributes",
                                    method="POST",
                                    json={"ruleset_id": ruleset_id, "mods": mods},
                                    timeout=3,
                                )
                            attributes = result["attributes"]
                            await redis.set(attr_key, json.dumps(attributes), ex=86400)
                        except Exception:
                            if any(m in mod_names(mods) for m in ("DT", "HT", "HR", "EZ", "FL")):
                                return  # Never treat a DT score's base stars as its played difficulty.
                if (score.get("beatmap") or {}).get("id"):
                    chart = score["beatmap"]
                    tech = await _technical(fetcher, redis, ruleset_id, chart["id"], chart.get("checksum"), limit)
                    if tech:
                        attributes = {**(attributes or {}), "technical": tech}
                sample = score_sample(score, attributes)
                if sample is not None:
                    samples.append(sample)

            work_limit = asyncio.Semaphore(2)

            async def analyse_bounded(score):
                async with work_limit:
                    await analyse(score)

            await asyncio.gather(*(analyse_bounded(score) for score in unique))
    except Exception as exc:
        log("SomsaiBots").warning("Bot skill history incomplete: {}", type(exc).__name__)
    # Deterministic snapshot despite asynchronous request completion order.
    samples.sort(key=lambda s: (s["stars"], str(s["mods"]), s["ability"]))
    profile = skill_profile(user.get("global_rank"), level, samples)
    await redis.set(key, json.dumps(profile), ex=21600 if samples else 60)
    return profile


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
    level = normalize_level(level)
    choices = random.sample(personas, min(count, len(personas)))
    result = []
    for index in range(count):
        persona = choices[index % len(choices)]
        suffix = secrets.token_hex(4)
        name = f"{persona['username'][:15]} [bot {suffix}]"
        official = persona.get("official_id")
        url = f"https://osu.ppy.sh/users/{official}" if official else None
        description = "Серверный бот SOMSAI для кастомных матчей. Не настоящий игрок Bancho."
        if url:
            description += f" Образ игрока: {url}"
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
