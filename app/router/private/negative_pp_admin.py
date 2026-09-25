"""Administrator/owner-only, audited management of negative PP rules."""

from app.database import Beatmap, Beatmapset, NegativePPRule, UserStatistics
from app.dependencies.database import Database
from app.dependencies.fetcher import get_fetcher
from app.models.negative_pp import NegativePPDelete, NegativePPTarget, NegativePPWrite
from app.service.beatmap_ranking_reconciliation_service import invalidate_score_reconciliation_caches
from app.service.negative_pp_service import prepare_mapper_rule, recalculate, resolve_target, rule_payload, target_maps

from .admin_panel import AdminSession, _audit, _require_capability, _require_csrf
from .router import router

from fastapi import HTTPException, Request
from httpx import HTTPStatusError
from sqlalchemy.exc import IntegrityError
from sqlmodel import col, select


@router.get("/admin-panel/negative-pp", include_in_schema=False)
async def list_negative_pp(context: AdminSession, session: Database):
    _require_capability(context, "administrator")
    return {
        "items": [
            rule_payload(rule)
            for rule in (await session.exec(select(NegativePPRule).order_by(col(NegativePPRule.id).desc()))).all()
        ]
    }


async def _resolve(session, payload):
    try:
        return await resolve_target(session, await get_fetcher(), payload.kind, payload.reference)
    except HTTPStatusError as exc:
        raise HTTPException(422, "Не удалось найти цель в osu! API; проверьте ID или ссылку") from exc


@router.post("/admin-panel/negative-pp/preview", include_in_schema=False)
async def preview_negative_pp(payload: NegativePPTarget, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    target_id, label = await _resolve(session, payload)
    return {"kind": payload.kind, "target_id": target_id, "label": label}


async def _lock_targets(session, kind, target_id):
    # Submissions and upstream updates lock the parent before PP aggregates.
    set_ids = select(Beatmap.beatmapset_id).where(col(Beatmap.id).in_(target_maps(kind, target_id)))
    condition = Beatmapset.id == target_id if kind == "beatmapset" else col(Beatmapset.id).in_(set_ids)
    await session.exec(select(Beatmapset.id).where(condition).order_by(col(Beatmapset.id)).with_for_update())
    # A rule can affect any member's weighted best list, including while they
    # submit another map. Serialize policy writes behind aggregate writers,
    # before inserting/deleting rules (whose current reads take shared locks).
    await session.exec(
        select(UserStatistics.user_id)
        .order_by(col(UserStatistics.user_id), col(UserStatistics.mode))
        .with_for_update()
    )
    return list((await session.exec(target_maps(kind, target_id).with_for_update())).all())


@router.post("/admin-panel/negative-pp", include_in_schema=False)
async def add_negative_pp(payload: NegativePPWrite, request: Request, context: AdminSession, session: Database):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    actor_id = context.user.id
    target_id, label = await _resolve(session, payload)
    if payload.kind == "mapper":
        await prepare_mapper_rule(session, await get_fetcher())
    try:
        maps = await _lock_targets(session, payload.kind, target_id)
        rule = NegativePPRule(
            kind=payload.kind, target_id=target_id, label=label, reason=payload.reason, created_by=actor_id
        )
        session.add(rule)
        await session.flush()
        result = await recalculate(session, maps)
        after = rule_payload(rule)
        await _audit(
            session,
            request,
            context.user,
            action="negative_pp.add",
            target_type=payload.kind,
            target_id=target_id,
            reason=payload.reason,
            before=None,
            after=after,
        )
        await session.commit()
    except IntegrityError as exc:
        await session.rollback()
        raise HTTPException(409, "Эта цель уже находится в списке") from exc
    except Exception:
        await session.rollback()
        raise
    await invalidate_score_reconciliation_caches(result)
    return {"rule": after, "beatmaps": len(maps), "user_modes": len(result.affected_user_modes)}


@router.delete("/admin-panel/negative-pp/{rule_id}", include_in_schema=False)
async def remove_negative_pp(
    rule_id: int, payload: NegativePPDelete, request: Request, context: AdminSession, session: Database
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    try:
        rule = await session.get(NegativePPRule, rule_id)
        if rule is None:
            raise HTTPException(404, "Правило уже удалено")
        maps = await _lock_targets(session, rule.kind, rule.target_id)
        before = rule_payload(rule)
        await session.delete(rule)
        await session.flush()
        result = await recalculate(session, maps)
        await _audit(
            session,
            request,
            context.user,
            action="negative_pp.remove",
            target_type=rule.kind,
            target_id=rule.target_id,
            reason=payload.reason,
            before=before,
            after={"deleted": True},
        )
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    await invalidate_score_reconciliation_caches(result)
    return {"deleted": True, "beatmaps": len(maps), "user_modes": len(result.affected_user_modes)}
