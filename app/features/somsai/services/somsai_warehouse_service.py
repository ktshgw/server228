"""Official-api backed storage for reusable SOMSAI maps."""

from __future__ import annotations

import asyncio
from datetime import UTC, datetime
import importlib
import re
from typing import Any
from urllib.parse import urlsplit

from app.database import Beatmap, Beatmapset, SomsaiMap
from app.dependencies.database import with_db
from app.dependencies.fetcher import get_fetcher
from app.features.somsai.models.somsai_admin import CATEGORY_MODS
from app.features.somsai.services.somsai_map_stats import display_stats
from app.features.somsai.services.somsai_rank_pool import eligibility_label, eligible_ranks
from app.helpers import utcnow

from fastapi import HTTPException
from httpx import AsyncClient, HTTPStatusError, Timeout, TimeoutException
from sqlalchemy import func
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

SLOT_PATTERN = re.compile(r"^(?:NM[1-6]|HD[1-3]|HR[1-3]|DT[1-4]|FM[1-3]|TB)$")
REFRESH_STATE: dict[str, Any] = {
    "running": False,
    "done": 0,
    "total": 0,
    "failed": 0,
    "changed": 0,
    "unchanged": 0,
    "last_id": 0,
    "started_at": None,
    "completed_at": None,
    "error_details": [],
    "details_truncated": 0,
    "scope": "all",
    "category": None,
    "slot": None,
    "map_id": None,
}


def parse_beatmap_id(value: str) -> int:
    value = value.strip()
    if value.isdigit() and int(value) > 0:
        return int(value)
    parsed = urlsplit(value)
    if parsed.scheme != "https" or parsed.netloc not in {"osu.ppy.sh", "www.osu.ppy.sh"}:
        raise HTTPException(422, "Укажите ссылку на сложность с официального сайта osu!")
    direct = re.fullmatch(r"/beatmaps/([1-9][0-9]*)/?", parsed.path)
    if direct and not parsed.query and not parsed.fragment:
        return int(direct.group(1))
    beatmapset = re.fullmatch(r"/beatmapsets/[1-9][0-9]*/?", parsed.path)
    fragment = re.fullmatch(r"(?:osu|taiko|fruits|mania)/([1-9][0-9]*)", parsed.fragment)
    if beatmapset and fragment and not parsed.query:
        return int(fragment.group(1))
    raise HTTPException(422, "Ссылка должна вести на конкретную сложность beatmap")


def map_payload(row: SomsaiMap) -> dict[str, Any]:
    return {
        **row.model_dump(),
        "name": f"{row.artist} — {row.title} [{row.version}]",
        "beatmap_url": f"https://osu.ppy.sh/beatmaps/{row.beatmap_id}",
        "cover_url": row.cover_url or f"https://assets.ppy.sh/beatmaps/{row.beatmapset_id}/covers/cover.jpg",
    }


async def canonical_map(
    slot: str,
    beatmap_id: int,
    ruleset_id: int = 0,
    variant_id: int = 0,
    *,
    session: AsyncSession | None = None,
) -> dict[str, Any]:
    if not SLOT_PATTERN.fullmatch(slot):
        raise HTTPException(422, "Неизвестный слот SOMSAI")
    fetcher = await get_fetcher()
    try:
        beatmap = await fetcher.get_beatmap(beatmap_id)
        if int(beatmap["id"]) != beatmap_id:
            raise ValueError("beatmap identity mismatch")
        actual_ruleset = int(beatmap.get("mode_int", 0))
        if actual_ruleset != ruleset_id:
            raise HTTPException(422, "Режим карты не совпадает с режимом хранилища")
        checksum = beatmap.get("checksum")
        raw = await fetcher.get_beatmap_raw(beatmap_id, checksum)
        category = slot[:2]
        calculation_mods = [dict(mod) for mod in CATEGORY_MODS[category]]
        gameplay_mods = [] if category == "FM" else calculation_mods
        attribute_response = await fetcher.request_api(
            f"https://osu.ppy.sh/api/v2/beatmaps/{beatmap_id}/attributes",
            method="POST",
            json={"ruleset_id": ruleset_id, "mods": calculation_mods},
        )
        attributes = attribute_response["attributes"]
        source = {
            "category": category,
            "mods": calculation_mods,
            "bpm": beatmap.get("bpm", 0),
            "cs": beatmap.get("cs", 0),
            "od": beatmap.get("accuracy", 0),
            "ar": beatmap.get("ar", 0),
            "hp": beatmap.get("drain", 0),
            "total_length": beatmap.get("total_length", 0),
            "hit_length": beatmap.get("hit_length", 0),
        }
        stats = {
            key: round(value, 2) if isinstance(value, float) else value
            for key, value in display_stats(raw, source, attributes, ruleset_id).items()
        }
        ranks = eligible_ranks(slot, float(stats["stars"]))
        beatmapset = beatmap.get("beatmapset") or {}
        if session is not None:
            set_id = int(beatmap.get("beatmapset_id") or beatmapset.get("id") or 0)
            full_set = await fetcher.get_beatmapset(set_id)
            await session.merge(await Beatmapset.from_resp_no_save(full_set))
            await session.flush()
            await session.merge(await Beatmap.from_resp_no_save(session, beatmap))
            await session.flush()
        covers = beatmapset.get("covers") or {}
        return {
            "slot": slot,
            "category": category,
            "beatmap_id": beatmap_id,
            "beatmapset_id": int(beatmap.get("beatmapset_id") or beatmapset.get("id") or 0),
            "ruleset_id": ruleset_id,
            "variant_id": variant_id,
            "checksum": checksum,
            "artist": str(beatmapset.get("artist") or "")[:255],
            "title": str(beatmapset.get("title") or "")[:255],
            "version": str(beatmap.get("version") or "")[:255],
            "cover_url": covers.get("cover@2x") or covers.get("cover") or covers.get("card@2x") or covers.get("card"),
            "mods": gameplay_mods,
            "stats": stats,
            "eligible_ranks": ranks,
            "eligibility_label": eligibility_label(ranks),
            "refreshed_at": utcnow(),
        }
    except HTTPException:
        raise
    except Exception as exc:
        raise HTTPException(422, f"Не удалось получить карту {beatmap_id} и её сложность с osu!") from exc


async def save_map(
    session: AsyncSession,
    slot: str,
    beatmap_id: int,
    *,
    ruleset_id: int = 0,
    variant_id: int = 0,
    source_kind: str = "manual",
    source_url: str | None = None,
    source_round: str | None = None,
) -> tuple[dict[str, Any] | None, SomsaiMap]:
    data = await canonical_map(slot, beatmap_id, ruleset_id, variant_id, session=session)
    row = (
        await session.exec(select(SomsaiMap).where(SomsaiMap.slot == slot, SomsaiMap.beatmap_id == beatmap_id))
    ).first()
    before = map_payload(row) if row else None
    row = row or SomsaiMap(**data)
    for key, value in data.items():
        setattr(row, key, value)
    row.source_kind = source_kind
    row.source_url = source_url
    row.source_round = source_round
    session.add(row)
    await session.flush()
    return before, row


def begin_refresh(
    *,
    scope: str = "all",
    category: str | None = None,
    slot: str | None = None,
    map_id: int | None = None,
) -> bool:
    """Atomically reserve one process-wide refresh job before returning to the event loop."""
    if REFRESH_STATE["running"]:
        return False
    REFRESH_STATE.update(
        running=True,
        done=0,
        total=0,
        failed=0,
        changed=0,
        unchanged=0,
        last_id=0,
        started_at=datetime.now(UTC).isoformat(),
        completed_at=None,
        error_details=[],
        details_truncated=0,
        scope=scope,
        category=category,
        slot=slot,
        map_id=map_id,
    )
    return True


def reload_somsai_calculators() -> None:
    """Reload editable calculation rules before an operator-triggered refresh."""

    from app.features.somsai.services import somsai_map_stats, somsai_rank_pool

    importlib.invalidate_caches()
    importlib.reload(somsai_map_stats)
    importlib.reload(somsai_rank_pool)
    global display_stats, eligible_ranks, eligibility_label
    display_stats = somsai_map_stats.display_stats
    eligible_ranks = somsai_rank_pool.eligible_ranks
    eligibility_label = somsai_rank_pool.eligibility_label


async def _refresh_api_request(client: AsyncClient, method: str, url: str, **kwargs: Any) -> dict[str, Any]:
    """Use a refresh-only client so a slow bulk job cannot stall website requests."""

    fetcher = await get_fetcher()
    for attempt in range(2):
        await fetcher.ensure_valid_access_token()
        response = await client.request(method, url, headers=fetcher.header, **kwargs)
        if response.status_code == 401 and attempt == 0:
            fetcher.token_expiry = 0
            continue
        response.raise_for_status()
        return response.json()
    raise RuntimeError("osu! API authentication failed")


async def _refresh_canonical_map(
    client: AsyncClient,
    slot: str,
    beatmap_id: int,
    ruleset_id: int,
    variant_id: int,
) -> dict[str, Any]:
    """Fetch only metadata and difficulty attributes needed by a warehouse row."""

    beatmap = await _refresh_api_request(client, "GET", f"https://osu.ppy.sh/api/v2/beatmaps/{beatmap_id}")
    if int(beatmap["id"]) != beatmap_id:
        raise ValueError("beatmap identity mismatch")
    if int(beatmap.get("mode_int", 0)) != ruleset_id:
        raise ValueError("beatmap ruleset no longer matches the warehouse row")

    category = slot[:2]
    calculation_mods = [dict(mod) for mod in CATEGORY_MODS[category]]
    gameplay_mods = [] if category == "FM" else calculation_mods
    attribute_response = await _refresh_api_request(
        client,
        "POST",
        f"https://osu.ppy.sh/api/v2/beatmaps/{beatmap_id}/attributes",
        json={"ruleset_id": ruleset_id, "mods": calculation_mods},
    )
    source = {
        "category": category,
        "mods": calculation_mods,
        "bpm": beatmap.get("bpm", 0),
        "cs": beatmap.get("cs", 0),
        "od": beatmap.get("accuracy", 0),
        "ar": beatmap.get("ar", 0),
        "hp": beatmap.get("drain", 0),
        "total_length": beatmap.get("total_length", 0),
        "hit_length": beatmap.get("hit_length", 0),
    }
    stats = {
        key: round(value, 2) if isinstance(value, float) else value
        for key, value in display_stats("", source, attribute_response["attributes"], ruleset_id).items()
    }
    ranks = eligible_ranks(slot, float(stats["stars"]))
    beatmapset = beatmap.get("beatmapset") or {}
    covers = beatmapset.get("covers") or {}
    return {
        "slot": slot,
        "category": category,
        "beatmap_id": beatmap_id,
        "beatmapset_id": int(beatmap.get("beatmapset_id") or beatmapset.get("id") or 0),
        "ruleset_id": ruleset_id,
        "variant_id": variant_id,
        "checksum": beatmap.get("checksum"),
        "artist": str(beatmapset.get("artist") or "")[:255],
        "title": str(beatmapset.get("title") or "")[:255],
        "version": str(beatmap.get("version") or "")[:255],
        "cover_url": covers.get("cover@2x") or covers.get("cover") or covers.get("card@2x") or covers.get("card"),
        "mods": gameplay_mods,
        "stats": stats,
        "eligible_ranks": ranks,
        "eligibility_label": eligibility_label(ranks),
        "refreshed_at": utcnow(),
    }


async def _refresh_one_map(row_id: int, client: AsyncClient) -> bool:
    """Recalculate one row without occupying the website's shared API client."""

    for attempt in range(2):
        try:
            async with with_db() as session:
                source = await session.get(SomsaiMap, row_id)
                if source is None:
                    return False
                identity = (source.slot, source.beatmap_id, source.ruleset_id, source.variant_id)

            data = await _refresh_canonical_map(client, *identity)

            async with with_db() as session:
                row = await session.get(SomsaiMap, row_id)
                if row is None:
                    return False
                before = {key: value for key, value in map_payload(row).items() if key != "refreshed_at"}
                for key, value in data.items():
                    setattr(row, key, value)
                session.add(row)
                await session.flush()
                after = {key: value for key, value in map_payload(row).items() if key != "refreshed_at"}
                await session.commit()
                REFRESH_STATE["changed" if before != after else "unchanged"] += 1
                return True
        except Exception as exc:
            if attempt == 0 and not isinstance(exc, TimeoutException):
                await asyncio.sleep(0.35)
                continue
            details = REFRESH_STATE["error_details"]
            if isinstance(exc, HTTPStatusError):
                description = f"HTTP {exc.response.status_code} from osu! API"
            else:
                description = str(exc).strip() or type(exc).__name__
            message = f"Map #{row_id}: {type(exc).__name__}: {description}"[:1000]
            if len(details) < 100:
                details.append(message)
            else:
                REFRESH_STATE["details_truncated"] += 1
    return False


async def refresh_all_maps(
    *,
    prepared: bool = False,
    category: str | None = None,
    slot: str | None = None,
    map_id: int | None = None,
) -> None:
    scope = "map" if map_id is not None else "slot" if slot is not None else "category" if category is not None else "all"
    if not prepared and not begin_refresh(scope=scope, category=category, slot=slot, map_id=map_id):
        return
    try:
        reload_somsai_calculators()
        filters = []
        if category is not None:
            filters.append(SomsaiMap.category == category)
        if slot is not None:
            filters.append(SomsaiMap.slot == slot)
        if map_id is not None:
            filters.append(SomsaiMap.id == map_id)
        async with with_db() as session:
            upper_query = select(func.max(SomsaiMap.id))
            count_query = select(func.count()).select_from(SomsaiMap)
            if filters:
                upper_query = upper_query.where(*filters)
                count_query = count_query.where(*filters)
            upper_id = int((await session.exec(upper_query)).one() or 0)
            REFRESH_STATE["total"] = int((await session.exec(count_query)).one() or 0)
        cursor = 0
        while cursor < upper_id:
            async with with_db() as session:
                id_query = (
                    select(SomsaiMap.id)
                    .where(col(SomsaiMap.id) > cursor, col(SomsaiMap.id) <= upper_id, *filters)
                    .order_by(col(SomsaiMap.id))
                    .limit(500)
                )
                ids = [int(value) for value in (await session.exec(id_query)).all() if value is not None]
            if not ids:
                break
            timeout = Timeout(8.0, connect=4.0)
            async with AsyncClient(timeout=timeout, follow_redirects=True) as client:
                for row_id in ids:
                    refreshed = await _refresh_one_map(row_id, client)
                    if not refreshed:
                        REFRESH_STATE["failed"] += 1
                    REFRESH_STATE["done"] += 1
                    REFRESH_STATE["last_id"] = row_id
                    cursor = row_id
                    await asyncio.sleep(0.12)
    finally:
        REFRESH_STATE["running"] = False
        REFRESH_STATE["completed_at"] = datetime.now(UTC).isoformat()
