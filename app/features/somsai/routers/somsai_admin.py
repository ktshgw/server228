"""SOMSAI map warehouse administration with staff/CSRF/audit controls."""

import asyncio

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
from app.features.somsai.services.somsai_collector_service import collector_preview, fetch_collector_source, preview_collector
from app.features.somsai.services.somsai_pool_service import delete_pool, pool_payload, prepare_missing_metadata, preview_pool, save_pool
from app.features.somsai.services.somsai_warehouse_service import (
    REFRESH_STATE,
    map_payload,
    parse_beatmap_id,
    refresh_all_maps,
    save_map,
)

from app.router.private.admin_panel import AdminSession, _audit, _require_capability, _require_csrf
from app.router.private.router import router

from fastapi import HTTPException, Request
from sqlmodel import col, func, select

_refresh_task: asyncio.Task | None = None
_import_task: asyncio.Task | None = None
IMPORT_STATE = {
    "running": False, "done": 0, "total": 0, "imported": 0,
    "failed": 0, "error": None,
}


async def _import_warehouse(url: str, selected_round: str) -> None:
    IMPORT_STATE.update(running=True, done=0, total=0, imported=0, failed=0, error=None)
    try:
        data, provenance = await fetch_collector_source(url)
        if provenance["source_kind"] == "collector_tournament":
            available = [str(entry.get("round", "")) for entry in data.get("rounds", [])]
            rounds = available if selected_round == "__all__" else [selected_round]
            if any(name not in available for name in rounds):
                raise ValueError("Выбранный раунд отсутствует в маппуле")
        else:
            rounds = [None]
        jobs = []
        for round_name in rounds:
            preview = collector_preview(
                data, provenance, SomsaiImportRequest(url=url, round=round_name, category="NM")
            )
            jobs.extend((round_name, slot) for slot in preview["slots"])
        IMPORT_STATE["total"] = len(jobs)
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
            except Exception:
                IMPORT_STATE["failed"] += 1
            finally:
                IMPORT_STATE["done"] += 1
    except Exception as exc:
        IMPORT_STATE["error"] = str(exc)
    finally:
        IMPORT_STATE["running"] = False


@router.get("/admin-panel/somsai/maps", include_in_schema=False)
async def list_somsai_maps(context: AdminSession, session: Database, slot: str = "NM1", page: int = 1):
    _require_capability(context, "administrator")
    page = max(1, page)
    query = select(SomsaiMap).where(SomsaiMap.slot == slot)
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
    IMPORT_STATE.update(running=True, done=0, total=0, imported=0, failed=0, error=None)
    _import_task = asyncio.create_task(_import_warehouse(payload.url, payload.round))
    await _audit(
        session, request, context.user, action="somsai.warehouse.import", target_type="somsai_warehouse",
        target_id=payload.url, reason=payload.reason, before=None, after={"queued": True, "round": payload.round},
    )
    await session.commit()
    return dict(IMPORT_STATE)


@router.get("/admin-panel/somsai/warehouse/import", include_in_schema=False)
async def somsai_import_status(context: AdminSession):
    _require_capability(context, "administrator")
    return dict(IMPORT_STATE)


@router.post("/admin-panel/somsai/warehouse/refresh", include_in_schema=False)
async def refresh_somsai_warehouse(request: Request, context: AdminSession, session: Database):
    global _refresh_task
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    if not REFRESH_STATE["running"]:
        _refresh_task = asyncio.create_task(refresh_all_maps())
        await _audit(
            session, request, context.user, action="somsai.warehouse.refresh", target_type="somsai_warehouse",
            target_id="all", reason="no reason", before=None, after={"queued": True},
        )
        await session.commit()
    return dict(REFRESH_STATE)


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
            if any(not slot["checksum"] for slot in provenance["slots"]):
                raise HTTPException(422, "В источнике нет контрольных сумм некоторых карт; добавьте эти карты вручную")
            # Refetch the public source on save; browser-supplied provenance is never trusted.
            spec = SomsaiPoolSpec.model_validate({**spec.model_dump(), "slots": provenance["slots"], "active": False})
        metadata = None
        if spec.active or imported:
            metadata = await prepare_missing_metadata(session, spec, await get_fetcher())
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
