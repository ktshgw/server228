"""SOMSAI map warehouse administration with staff/CSRF/audit controls."""

import asyncio
import json
from typing import Any

from app.database import SomsaiMap, SomsaiPool
from app.dependencies.database import Database, with_db
from app.dependencies.fetcher import get_fetcher
from app.features.somsai.models.somsai_admin import (
    SomsaiDelete,
    SomsaiImportRequest,
    SomsaiImportWrite,
    SomsaiMapDelete,
    SomsaiMapWrite,
    SomsaiPoolSpec,
    SomsaiPoolWrite,
    SomsaiWarehouseImport,
)
from app.features.somsai.services.somsai_collector_service import (
    collector_preview,
    fetch_collector_source,
    preview_collector,
)
from app.features.somsai.services.somsai_pool_service import (
    delete_pool,
    pool_payload,
    prepare_missing_metadata,
    preview_pool,
    save_pool,
)
from app.features.somsai.services.somsai_rank_pool import RANKS
from app.features.somsai.services.somsai_warehouse_service import (
    REFRESH_STATE,
    SLOT_PATTERN,
    begin_refresh,
    map_payload,
    parse_beatmap_id,
    refresh_all_maps,
    save_map,
)
from app.router.private.admin_panel import AdminSession, _audit, _require_capability, _require_csrf
from app.router.private.router import router

from fastapi import HTTPException, Request, Response
from sqlalchemy import Float, cast, or_
from sqlmodel import col, func, select

_refresh_task: asyncio.Task | None = None
_import_task: asyncio.Task | None = None
IMPORT_STATE = {
    "running": False, "done": 0, "total": 0, "imported": 0,
    "failed": 0, "error": None, "error_details": [], "details_truncated": 0,
}


def _import_error_text(exc: Exception) -> str:
    detail = exc.detail if isinstance(exc, HTTPException) else None
    if isinstance(detail, (dict, list)):
        text = json.dumps(detail, ensure_ascii=False)
    else:
        text = str(detail or exc).strip() or type(exc).__name__
    cause = exc.__cause__ or exc.__context__
    if cause is not None:
        cause_text = str(cause).strip()
        if cause_text and cause_text not in text:
            text = f"{text} ({type(cause).__name__}: {cause_text})"
    return text[:1000]


def _add_import_detail(message: str) -> None:
    details = IMPORT_STATE["error_details"]
    if len(details) < 250:
        details.append(message)
    else:
        IMPORT_STATE["details_truncated"] += 1


async def _import_warehouse(url: str, selected_rounds: list[str]) -> None:
    IMPORT_STATE.update(
        running=True, done=0, total=0, imported=0, failed=0,
        error=None, error_details=[], details_truncated=0,
    )
    try:
        data, provenance = await fetch_collector_source(url)
        if provenance["source_kind"] == "collector_tournament":
            available = [str(entry.get("round", "")) for entry in data.get("rounds", [])]
            rounds = available if "__all__" in selected_rounds else selected_rounds
            if any(name not in available for name in rounds):
                raise ValueError("Выбранный раунд отсутствует в маппуле")
        else:
            rounds = [None]
        jobs = []
        preflight_failed = 0
        for round_name in rounds:
            try:
                preview = collector_preview(
                    data, provenance, SomsaiImportRequest(url=url, round=round_name, category="NM")
                )
                preflight_failed += int(preview.get("skipped", 0))
                round_label = round_name or "collection"
                for warning in preview.get("warnings", []):
                    _add_import_detail(f"{round_label}: {warning}")
                jobs.extend((round_name, slot) for slot in preview["slots"])
            except Exception as exc:
                # A malformed stage must not cancel the remaining stages when
                # importing an entire tournament.
                preflight_failed += 1
                _add_import_detail(f"{round_name or 'collection'}: {_import_error_text(exc)}")
        IMPORT_STATE.update(total=len(jobs) + preflight_failed, failed=preflight_failed, done=preflight_failed)
        for round_name, slot in jobs:
            try:
                async with with_db() as db:
                    await save_map(
                        db, slot["id"], int(slot["beatmap_id"]),
                        source_kind=provenance["source_kind"], source_url=provenance["source_url"],
                        source_round=round_name,
                    )
                    await db.commit()
                IMPORT_STATE["imported"] += 1
            except Exception as exc:
                IMPORT_STATE["failed"] += 1
                _add_import_detail(
                    f"{round_name or 'collection'} · {slot['id']} · #{slot['beatmap_id']}: {_import_error_text(exc)}"
                )
            finally:
                IMPORT_STATE["done"] += 1
    except Exception as exc:
        IMPORT_STATE["error"] = _import_error_text(exc)
    finally:
        IMPORT_STATE["running"] = False

@router.get("/admin-panel/somsai/maps", include_in_schema=False)
async def list_somsai_maps(
    context: AdminSession,
    session: Database,
    slot: str = "NM1",
    page: int = 1,
    search: str = "",
    rank: str = "",
    source: str = "",
    stars_min: float | None = None,
    stars_max: float | None = None,
    bpm_min: float | None = None,
    bpm_max: float | None = None,
    length_min: float | None = None,
    length_max: float | None = None,
    cs_min: float | None = None,
    cs_max: float | None = None,
    ar_min: float | None = None,
    ar_max: float | None = None,
    od_min: float | None = None,
    od_max: float | None = None,
    hp_min: float | None = None,
    hp_max: float | None = None,
):
    _require_capability(context, "administrator")
    page = max(1, page)
    query = select(SomsaiMap).where(SomsaiMap.slot == slot)
    search = search.strip()[:100]
    if search:
        pattern = f"%{search}%"
        text_filters: list[Any] = [
            col(SomsaiMap.artist).ilike(pattern),
            col(SomsaiMap.title).ilike(pattern),
            col(SomsaiMap.version).ilike(pattern),
        ]
        if search.isdigit():
            text_filters.extend((col(SomsaiMap.beatmap_id) == int(search), col(SomsaiMap.beatmapset_id) == int(search)))
        query = query.where(or_(*text_filters))
    if rank:
        if rank == "__not_used__":
            query = query.where(func.json_length(SomsaiMap.eligible_ranks) == 0)
        else:
            if rank not in RANKS:
                raise HTTPException(422, "Unknown SOMSAI rank")
            query = query.where(func.json_contains(SomsaiMap.eligible_ranks, json.dumps(rank)) == 1)
    if source:
        if source not in {"manual", "collector_tournament", "collector_collection"}:
            raise HTTPException(422, "Unknown map source")
        query = query.where(SomsaiMap.source_kind == source)
    numeric_ranges = {
        "stars": (stars_min, stars_max), "bpm": (bpm_min, bpm_max), "length": (length_min, length_max),
        "cs": (cs_min, cs_max), "ar": (ar_min, ar_max), "od": (od_min, od_max), "hp": (hp_min, hp_max),
    }
    for key, (minimum, maximum) in numeric_ranges.items():
        value = cast(func.json_unquote(func.json_extract(SomsaiMap.stats, f"$.{key}")), Float)
        if minimum is not None:
            query = query.where(value >= minimum)
        if maximum is not None:
            query = query.where(value <= maximum)
    total = (await session.exec(select(func.count()).select_from(query.subquery()))).one()
    rows = (
        await session.exec(query.order_by(col(SomsaiMap.id).desc()).offset((page - 1) * 48).limit(48))
    ).all()
    return {
        "slot": slot,
        "page": page,
        "pages": max(1, (total + 47) // 48),
        "total": total,
        "maps": [map_payload(row) for row in rows],
    }


@router.get("/admin-panel/somsai/maps/{map_id}/cover", include_in_schema=False)
async def somsai_map_cover(map_id: int, context: AdminSession, session: Database):
    """Serve warehouse covers through the authenticated same-origin API."""
    _require_capability(context, "administrator")
    row = await session.get(SomsaiMap, map_id)
    if row is None:
        raise HTTPException(404, "Map not found")
    fallback_base = f"https://assets.ppy.sh/beatmaps/{row.beatmapset_id}/covers"
    candidates = [row.cover_url, f"{fallback_base}/cover.jpg", f"{fallback_base}/card.jpg"]
    fetcher = await get_fetcher()
    client = await fetcher._get_client()
    for url in dict.fromkeys(value for value in candidates if value):
        if not str(url).startswith("https://assets.ppy.sh/"):
            continue
        try:
            upstream = await client.get(str(url))
            upstream.raise_for_status()
            content_type = upstream.headers.get("content-type", "image/jpeg").split(";", 1)[0]
            if not content_type.startswith("image/") or not upstream.content:
                continue
            return Response(
                content=upstream.content,
                media_type=content_type,
                headers={"Cache-Control": "private, max-age=86400, stale-while-revalidate=604800"},
            )
        except Exception:  # noqa: S112 - try the next trusted cover candidate
            continue
    raise HTTPException(404, "Cover is unavailable")


@router.post("/admin-panel/somsai/maps", include_in_schema=False)
async def add_somsai_map(payload: SomsaiMapWrite, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    beatmap_id = parse_beatmap_id(payload.url)
    try:
        before, row = await save_map(
            session,
            payload.slot,
            beatmap_id,
            ruleset_id=payload.ruleset_id,
            variant_id=payload.variant_id,
            source_url=f"https://osu.ppy.sh/beatmaps/{beatmap_id}",
        )
        after = map_payload(row)
        await _audit(
            session, request, context.user, action="somsai.map.update" if before else "somsai.map.create",
            target_type="somsai_map", target_id=int(row.id or 0), reason=payload.reason,
            before=before, after=after,
        )
        await session.commit()
        return after
    except Exception:
        await session.rollback()
        raise


@router.delete("/admin-panel/somsai/maps/{map_id}", include_in_schema=False)
async def remove_somsai_map(
    map_id: int, payload: SomsaiMapDelete, request: Request, context: AdminSession, session: Database
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    if REFRESH_STATE["running"]:
        raise HTTPException(409, "SOMSAI warehouse refresh is already running")
    row = await session.get(SomsaiMap, map_id)
    if row is None:
        raise HTTPException(404, "Карта не найдена")
    before = map_payload(row)
    await session.delete(row)
    await _audit(
        session, request, context.user, action="somsai.map.delete", target_type="somsai_map",
        target_id=map_id, reason=payload.reason, before=before, after={"deleted": True},
    )
    await session.commit()
    return {"deleted": True}


@router.post("/admin-panel/somsai/warehouse/import", include_in_schema=False)
async def import_somsai_warehouse(
    payload: SomsaiWarehouseImport, request: Request, context: AdminSession, session: Database
):
    global _import_task
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    if IMPORT_STATE["running"]:
        return dict(IMPORT_STATE)
    IMPORT_STATE.update(
        running=True, done=0, total=0, imported=0, failed=0,
        error=None, error_details=[], details_truncated=0,
    )
    _import_task = asyncio.create_task(_import_warehouse(payload.url, payload.rounds))
    await _audit(
        session, request, context.user, action="somsai.warehouse.import", target_type="somsai_warehouse",
        target_id=payload.url, reason=payload.reason, before=None, after={"queued": True, "rounds": payload.rounds},
    )
    await session.commit()
    return dict(IMPORT_STATE)


@router.get("/admin-panel/somsai/warehouse/import", include_in_schema=False)
async def somsai_import_status(context: AdminSession):
    _require_capability(context, "administrator")
    return dict(IMPORT_STATE)


async def _start_refresh(
    request: Request,
    context: AdminSession,
    session: Database,
    *,
    category: str | None = None,
    slot: str | None = None,
    map_id: int | None = None,
) -> dict:
    global _refresh_task
    scope = "map" if map_id is not None else "slot" if slot is not None else "category" if category is not None else "all"
    if begin_refresh(scope=scope, category=category, slot=slot, map_id=map_id):
        _refresh_task = asyncio.create_task(
            refresh_all_maps(prepared=True, category=category, slot=slot, map_id=map_id)
        )
        target_id = str(map_id) if map_id is not None else slot or category or "all"
        await _audit(
            session,
            request,
            context.user,
            action=f"somsai.warehouse.refresh.{scope}",
            target_type="somsai_warehouse",
            target_id=target_id,
            reason="no reason",
            before=None,
            after={"queued": True, "scope": scope},
        )
        await session.commit()
    return dict(REFRESH_STATE)


@router.post("/admin-panel/somsai/warehouse/refresh", include_in_schema=False)
async def refresh_somsai_warehouse(request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    return await _start_refresh(request, context, session)


@router.post("/admin-panel/somsai/warehouse/refresh/category/{category}", include_in_schema=False)
async def refresh_somsai_category(category: str, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    category = category.upper()
    if category not in {"NM", "HD", "HR", "DT", "FM", "TB"}:
        raise HTTPException(422, "Unknown SOMSAI category")
    return await _start_refresh(request, context, session, category=category)


@router.post("/admin-panel/somsai/warehouse/refresh/slot/{slot}", include_in_schema=False)
async def refresh_somsai_slot(slot: str, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    slot = slot.upper()
    if SLOT_PATTERN.fullmatch(slot) is None:
        raise HTTPException(422, "Unknown SOMSAI slot")
    return await _start_refresh(request, context, session, slot=slot)


@router.post("/admin-panel/somsai/maps/{map_id}/refresh", include_in_schema=False)
async def refresh_somsai_map(map_id: int, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    if await session.get(SomsaiMap, map_id) is None:
        raise HTTPException(404, "Map not found")
    return await _start_refresh(request, context, session, map_id=map_id)


@router.get("/admin-panel/somsai/warehouse/refresh", include_in_schema=False)
async def somsai_refresh_status(context: AdminSession):
    _require_capability(context, "administrator")
    return dict(REFRESH_STATE)


@router.get("/admin-panel/somsai/pools", include_in_schema=False)
async def list_somsai_pools(context: AdminSession, session: Database):
    _require_capability(context, "administrator")
    return {
        "pools": [
            pool_payload(pool) for pool in (await session.exec(select(SomsaiPool).order_by(col(SomsaiPool.id)))).all()
        ]
    }


@router.post("/admin-panel/somsai/preview", include_in_schema=False)
async def preview_somsai_pool(payload: SomsaiPoolSpec, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    return await preview_pool(session, payload)


@router.post("/admin-panel/somsai/import/preview", include_in_schema=False)
async def preview_somsai_import(payload: SomsaiImportRequest, request: Request, context: AdminSession):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    return await preview_collector(payload)


async def _write(
    pool_id: int | None,
    payload: SomsaiPoolWrite,
    request: Request,
    context: AdminSession,
    session: Database,
    imported: SomsaiImportWrite | None = None,
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    try:
        provenance = None
        spec = SomsaiPoolSpec.model_validate(payload.model_dump(exclude={"reason", "expected_revision"}))
        if imported:
            provenance = await preview_collector(imported)
            if not provenance["slots"]:
                raise HTTPException(422, "Сначала выберите раунд с картами")
            source_slots = [slot for slot in provenance["slots"] if slot.get("checksum")]
            missing_checksums = len(provenance["slots"]) - len(source_slots)
            if missing_checksums:
                provenance["warnings"] = [
                    *provenance.get("warnings", []),
                    f"Пропущено карт без контрольной суммы: {missing_checksums}",
                ]
                provenance["slots"] = source_slots
            if not source_slots:
                raise HTTPException(422, "В выбранной стадии не осталось карт, доступных для импорта")
            # Refetch the public source on save; browser-supplied provenance is never trusted.
            spec = SomsaiPoolSpec.model_validate({**spec.model_dump(), "slots": source_slots, "active": False})
        metadata = None
        if spec.active or imported:
            metadata = await prepare_missing_metadata(session, spec, await get_fetcher())
            failed_ids = set(metadata.pop("failed_ids", []))
            if failed_ids:
                remaining = [slot for slot in spec.slots if slot.beatmap_id not in failed_ids]
                if not remaining:
                    raise HTTPException(422, "Не удалось получить ни одной карты выбранной стадии")
                spec = SomsaiPoolSpec.model_validate({**spec.model_dump(), "slots": remaining})
                if provenance:
                    provenance["slots"] = [
                        slot for slot in provenance["slots"] if slot["beatmap_id"] not in failed_ids
                    ]
                    provenance["warnings"] = [
                        *provenance.get("warnings", []),
                        f"Пропущено недоступных карт: {len(failed_ids)}",
                    ]
        before, pool = await save_pool(
            session,
            spec,
            pool_id,
            payload.expected_revision,
            metadata,
            provenance,
            append=bool(imported and imported.append),
        )
        after = pool_payload(pool)
        await _audit(
            session,
            request,
            context.user,
            action="somsai.pool.update" if before else "somsai.pool.create",
            target_type="somsai_pool",
            target_id=int(pool.id or 0),
            reason=payload.reason,
            before=before,
            after=after,
        )
        await session.commit()
        return after
    except Exception:
        await session.rollback()
        raise


@router.post("/admin-panel/somsai/pools", include_in_schema=False)
async def create_somsai_pool(payload: SomsaiPoolWrite, request: Request, context: AdminSession, session: Database):
    return await _write(None, payload, request, context, session)


@router.patch("/admin-panel/somsai/pools/{pool_id}", include_in_schema=False)
async def update_somsai_pool(
    pool_id: int, payload: SomsaiPoolWrite, request: Request, context: AdminSession, session: Database
):
    return await _write(pool_id, payload, request, context, session)


@router.post("/admin-panel/somsai/import", include_in_schema=False)
async def create_somsai_import(payload: SomsaiImportWrite, request: Request, context: AdminSession, session: Database):
    return await _write(None, payload.pool, request, context, session, payload)


@router.post("/admin-panel/somsai/pools/{pool_id}/import", include_in_schema=False)
async def update_somsai_import(
    pool_id: int, payload: SomsaiImportWrite, request: Request, context: AdminSession, session: Database
):
    return await _write(pool_id, payload.pool, request, context, session, payload)


@router.delete("/admin-panel/somsai/pools/{pool_id}", include_in_schema=False)
async def remove_somsai_pool(
    pool_id: int, payload: SomsaiDelete, request: Request, context: AdminSession, session: Database
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    try:
        before = await delete_pool(session, pool_id, payload.expected_revision)
        await _audit(
            session,
            request,
            context.user,
            action="somsai.pool.delete",
            target_type="somsai_pool",
            target_id=pool_id,
            reason=payload.reason,
            before=before,
            after={"deleted": True},
        )
        await session.commit()
        return {"deleted": True}
    except Exception:
        await session.rollback()
        raise
