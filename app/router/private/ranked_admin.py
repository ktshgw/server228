"""Ranked preset editor using the existing admin session, CSRF and audit trail."""

from app.dependencies.database import Database
from app.models.ranked_admin import RankedPresetAssignment, RankedPresetSpec, RankedPresetWrite
from app.service.ranked_preset_service import (
    assign_preset,
    preset_payload,
    preview_preset,
    ranked_catalogue,
    save_preset,
)

from .admin_panel import AdminSession, _audit, _require_capability, _require_csrf
from .router import router

from fastapi import Request


@router.get("/admin-panel/ranked", include_in_schema=False)
async def get_ranked_catalogue(context: AdminSession, session: Database):
    _require_capability(context, "administrator")
    return await ranked_catalogue(session)


@router.post("/admin-panel/ranked/preview", include_in_schema=False)
async def preview_ranked_preset(payload: RankedPresetSpec, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    return await preview_preset(session, payload)


async def write_preset(
    preset_id: int | None, payload: RankedPresetWrite, request: Request, context: AdminSession, session: Database
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    try:
        before, preset = await save_preset(session, payload, preset_id, payload.expected_revision)
        after = preset_payload(preset)
        await _audit(
            session,
            request,
            context.user,
            action="ranked.preset.update" if before else "ranked.preset.create",
            target_type="ranked_preset",
            target_id=after["id"],
            reason=payload.reason,
            before=before,
            after=after,
        )
        await session.commit()
        return after
    except Exception:
        await session.rollback()
        raise


@router.post("/admin-panel/ranked/presets", include_in_schema=False)
async def create_ranked_preset(payload: RankedPresetWrite, request: Request, context: AdminSession, session: Database):
    return await write_preset(None, payload, request, context, session)


@router.patch("/admin-panel/ranked/presets/{preset_id}", include_in_schema=False)
async def update_ranked_preset(
    preset_id: int, payload: RankedPresetWrite, request: Request, context: AdminSession, session: Database
):
    return await write_preset(preset_id, payload, request, context, session)


@router.patch("/admin-panel/ranked/pools/{pool_id}", include_in_schema=False)
async def set_ranked_preset(
    pool_id: int, payload: RankedPresetAssignment, request: Request, context: AdminSession, session: Database
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    try:
        before, after = await assign_preset(session, pool_id, payload.preset_id, payload.expected_preset_id)
        await _audit(
            session,
            request,
            context.user,
            action="ranked.pool.preset",
            target_type="ranked_pool",
            target_id=pool_id,
            reason=payload.reason,
            before=before,
            after=after,
        )
        await session.commit()
        return after
    except Exception:
        await session.rollback()
        raise
