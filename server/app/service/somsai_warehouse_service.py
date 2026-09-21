"""Official-api backed storage for reusable SOMSAI maps."""

from __future__ import annotations

import asyncio
from datetime import UTC, datetime
import re
from typing import Any
from urllib.parse import urlsplit

from app.database import Beatmap, Beatmapset, SomsaiMap
from app.dependencies.database import with_db
from app.dependencies.fetcher import get_fetcher
from app.helpers import utcnow
from app.models.somsai_admin import CATEGORY_MODS
from app.service.somsai_map_stats import display_stats
from app.service.somsai_rank_pool import eligibility_label, eligible_ranks

from fastapi import HTTPException
from sqlalchemy import func
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

SLOT_PATTERN = re.compile(r"^(?:NM[1-6]|HD[1-3]|HR[1-3]|DT[1-4]|FM[1-3]|TB)$")
REFRESH_STATE: dict[str, Any] = {
    "running": False,
    "done": 0,
    "total": 0,
    "failed": 0,
    "last_id": 0,
    "started_at": None,
    "completed_at": None,
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


async def refresh_all_maps() -> None:
    if REFRESH_STATE["running"]:
        return
    REFRESH_STATE.update(
        running=True,
        done=0,
        total=0,
        failed=0,
        last_id=0,
        started_at=datetime.now(UTC).isoformat(),
        completed_at=None,
    )
    try:
        async with with_db() as session:
            upper_id = int((await session.exec(select(func.max(SomsaiMap.id)))).one() or 0)
            REFRESH_STATE["total"] = (
                await session.exec(select(func.count()).select_from(SomsaiMap).where(col(SomsaiMap.id) <= upper_id))
            ).one()
        cursor = 0
        while cursor < upper_id:
            async with with_db() as session:
                ids = [
                    int(value)
                    for value in (
                        await session.exec(
                            select(SomsaiMap.id)
                            .where(col(SomsaiMap.id) > cursor, col(SomsaiMap.id) <= upper_id)
                            .order_by(col(SomsaiMap.id))
                            .limit(500)
                        )
                    ).all()
                    if value is not None
                ]
            if not ids:
                break
            for row_id in ids:
                refreshed = False
                for attempt in range(3):
                    try:
                        async with with_db() as session:
                            row = await session.get(SomsaiMap, row_id)
                            if row is not None:
                                data = await canonical_map(
                                    row.slot, row.beatmap_id, row.ruleset_id, row.variant_id, session=session
                                )
                                for key, value in data.items():
                                    setattr(row, key, value)
                                session.add(row)
                                await session.commit()
                        refreshed = True
                        break
                    except Exception:
                        await asyncio.sleep(0.5 * (2**attempt))
                if not refreshed:
                    REFRESH_STATE["failed"] += 1
                cursor = row_id
                REFRESH_STATE["last_id"] = row_id
                REFRESH_STATE["done"] += 1
                # One in-flight Bancho request at a time; retry/backoff handles
                # transient failures without skipping the remainder of the DB.
                await asyncio.sleep(0.12)
    finally:
        REFRESH_STATE["running"] = False
        REFRESH_STATE["completed_at"] = datetime.now(UTC).isoformat()
