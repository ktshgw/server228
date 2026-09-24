"""Discover the official FA catalogue without relying on users' map requests.

Search returns complete native difficulty lists. New sets can be inserted in a
single transaction; revisions of existing charts go through the normal updater
so score invalidation and local rank reviews cannot be bypassed.
"""

import asyncio
from datetime import UTC, datetime, timedelta
import json
from typing import Any, cast

from app.database import Beatmap, Beatmapset, BeatmapsetDict
from app.database.beatmap import BeatmapDict
from app.database.beatmapset import BeatmapAvailability
from app.dependencies.database import get_redis, with_db
from app.dependencies.fetcher import get_fetcher
from app.log import service_logger
from app.models.beatmap import BeatmapRankStatus
from app.service.beatmapset_update_service import (
    _comparable_timestamp,
    get_beatmapset_update_service,
)

from httpx import HTTPStatusError
from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

logger = service_logger("RankedCatalogue")
STATE_KEY = "ranked:official-catalogue:v1:state"
SEEN_KEY = "ranked:official-catalogue:v1:seen"
LOCK_KEY = "ranked:official-catalogue:v1:lock"
SEARCH_URL = "https://osu.ppy.sh/api/v2/beatmapsets/search"
SEARCH_PARAMS = {"s": "ranked", "c": "featured_artists", "sort": "ranked_desc", "nsfw": "true"}


async def catalogue_sync_status() -> dict[str, Any]:
    data = await get_redis().get(STATE_KEY)
    state = json.loads(data) if data else {}
    return {key: state.get(key) for key in ("status", "processed", "total", "last_completed", "error")}


async def import_search_page(session: AsyncSession, sets: list[dict[str, Any]]) -> list[int]:
    """Import new maps and harmless metadata; return sets requiring revision processing."""
    refresh = []
    for raw in sorted(sets, key=lambda item: item["id"]):
        if raw.get("track_id") is None or raw.get("ranked") not in (1, 2):
            raise ValueError("Official search did not honour the Featured Artist / Ranked filter")
        source = dict(raw)
        source["availability"] = BeatmapAvailability.model_validate(raw.get("availability") or {})
        parent = await Beatmapset.from_resp_no_save(cast(BeatmapsetDict, source))
        maps = [
            await Beatmap.from_resp_no_save(session, cast(BeatmapDict, item))
            for item in raw["beatmaps"]
            if not item.get("convert", False)
        ]
        if not maps or len({item.id for item in maps}) != len(maps):
            raise ValueError("Official search returned an empty or duplicate difficulty list")
        if any(item.beatmapset_id != parent.id or not item.checksum for item in maps):
            raise ValueError("Official search returned invalid difficulty metadata")
        existing = (
            await session.exec(
                select(Beatmapset).options(lazyload("*")).where(Beatmapset.id == parent.id).with_for_update()
            )
        ).first()
        if existing is None:
            session.add(parent)
            await session.flush()
            session.add_all(maps)
            await session.flush()
            continue
        if _comparable_timestamp(existing.last_updated) > _comparable_timestamp(parent.last_updated):
            continue
        saved = {
            item.id: item
            for item in (
                await session.exec(
                    select(Beatmap)
                    .options(lazyload("*"))
                    .where(Beatmap.beatmapset_id == parent.id)
                    .order_by(col(Beatmap.id))
                    .with_for_update()
                )
            ).all()
        }
        if (
            existing.beatmap_status != parent.beatmap_status
            or {item.id for item in maps} != {item.id for item in saved.values() if item.deleted_at is None}
            or any(
                item.id not in saved
                or saved[item.id].checksum != item.checksum
                or saved[item.id].beatmap_status != item.beatmap_status
                or saved[item.id].deleted_at != item.deleted_at
                for item in maps
            )
        ):
            refresh.append(parent.id)
            continue
        # Search omits descriptions and other expanded fields. Preserve those,
        # plus all local data; refresh only the fields used by Ranked selection.
        existing.track_id = parent.track_id
        existing.download_disabled = parent.download_disabled
        existing.availability_info = parent.availability_info
        for item in maps:
            for field in ("difficulty_rating", "total_length", "hit_length", "cs"):
                setattr(saved[item.id], field, getattr(item, field))
    await session.flush()
    return refresh


async def sync_official_catalogue(*, force: bool = False) -> None:
    """Refresh daily, checkpoint every page and keep the usable catalogue on failure."""
    redis = get_redis()
    lock = redis.lock(LOCK_KEY, timeout=7200, blocking_timeout=0, thread_local=False)
    if not await lock.acquire(blocking=False):
        return
    state: dict[str, Any] = {}
    try:
        state = json.loads(await redis.get(STATE_KEY) or "{}")
        last_completed = state.get("last_completed")
        if (
            not force
            and state.get("status") == "complete"
            and last_completed
            and datetime.now(UTC) - datetime.fromisoformat(last_completed) < timedelta(days=1)
        ):
            return
        if state.get("status") in (None, "complete"):
            state = {"processed": 0, "cursor": None, "last_completed": last_completed}
            await redis.delete(SEEN_KEY)
        state.update(status="running", error=None)
        await redis.set(STATE_KEY, json.dumps(state))
        fetcher = await get_fetcher()
        updater = get_beatmapset_update_service()
        for _ in range(2000):
            params = dict(SEARCH_PARAMS)
            if state.get("cursor"):
                params["cursor_string"] = state["cursor"]
            async with asyncio.timeout(120):
                page = await fetcher.request_api(SEARCH_URL, params=params, timeout=30)
            sets = page["beatmapsets"]
            cursor = page.get("cursor_string")
            if cursor and (cursor == state.get("cursor") or not sets):
                raise ValueError("Official search pagination did not advance")
            if not sets and not state["processed"]:
                raise ValueError("Official search returned an empty catalogue")
            async with with_db() as session:
                refresh = await import_search_page(session, sets)
                await session.commit()
            for set_id in refresh:
                await updater.refresh_beatmapset(set_id)
            if sets:
                await redis.sadd(SEEN_KEY, *(str(item["id"]) for item in sets))
            state.update(processed=await redis.scard(SEEN_KEY), total=page.get("total"), cursor=cursor)
            if not cursor:
                if state["processed"] < (state["total"] or 0):
                    raise ValueError("Official search stopped before the complete catalogue was read")
                # Recheck sets removed from the search as well (unranked,
                # delisted FA tracks, deleted sets); do not retain stale members.
                seen = {int(value) for value in await redis.smembers(SEEN_KEY)}
                async with with_db() as session:
                    known = (
                        await session.exec(
                            select(Beatmapset.id).where(
                                col(Beatmapset.track_id).is_not(None),
                                col(Beatmapset.beatmap_status).in_(
                                    (BeatmapRankStatus.RANKED, BeatmapRankStatus.APPROVED)
                                ),
                            )
                        )
                    ).all()
                for set_id in sorted(set(known) - seen):
                    try:
                        await updater.refresh_beatmapset(set_id)
                    except HTTPStatusError as exc:
                        if exc.response.status_code != 404:
                            raise
                        async with with_db() as session:
                            missing = await session.get(Beatmapset, set_id)
                            if missing is not None:
                                missing.download_disabled = True
                                await session.commit()
                state.update(status="complete", last_completed=datetime.now(UTC).isoformat())
            await redis.set(STATE_KEY, json.dumps(state))
            await lock.extend(7200, replace_ttl=True)
            logger.info("Official catalogue: {}/{} sets ({})", state["processed"], state["total"], state["status"])
            if state["status"] == "complete":
                return
            await asyncio.sleep(1)
        raise ValueError("Official catalogue pagination exceeded its safety limit")
    except Exception as exc:
        # Log only the exception type in the public status, never OAuth details.
        state.update(status="error", error=type(exc).__name__)
        await redis.set(STATE_KEY, json.dumps(state))
        logger.exception("Official catalogue update failed; next run will resume its last page")
        raise
    finally:
        if await lock.owned():
            await lock.release()
