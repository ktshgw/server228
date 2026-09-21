"""Supporter inbox and notification preferences (website session + CSRF)."""

from datetime import UTC

from app.database.beatmap import Beatmap
from app.database.soms_activity import SomsActivity
from app.database.user_preference import UserPreference
from app.dependencies.database import Database
from app.service.soms_activity_service import notification_preferences

from .router import router
from .web_site import WEB_API_PREFIX, WebSession, _require_csrf

from fastapi import HTTPException, Request, Response
from pydantic import BaseModel
from sqlmodel import col, func, select


class NotificationPreferences(BaseModel):
    rank_lost: bool = True
    friend_added: bool = True
    friend_removed: bool = True


@router.get(f"{WEB_API_PREFIX}/notifications")
async def inbox(context: WebSession, session: Database, response: Response, before: int | None = None):
    response.headers["Cache-Control"] = "private, no-store"
    preferences = await notification_preferences(session, context.user.id)
    if not context.user.is_supporter:
        return {"supporter": False, "items": [], "unread": 0, "preferences": preferences}
    conditions = [SomsActivity.recipient_id == context.user.id]
    unread = (
        await session.exec(
            select(func.count()).select_from(SomsActivity).where(*conditions, col(SomsActivity.is_read).is_(False))
        )
    ).one()
    if before is not None:
        conditions.append(col(SomsActivity.id) < before)
    items = (
        await session.exec(select(SomsActivity).where(*conditions).order_by(col(SomsActivity.id).desc()).limit(30))
    ).all()
    legacy_beatmap_ids = {
        int(row.payload["beatmap_id"])
        for row in items
        if row.kind == "rank_lost" and row.payload.get("beatmap_id") and not row.payload.get("beatmapset_id")
    }
    beatmapsets = (
        dict(
            (
                await session.exec(
                    select(Beatmap.id, Beatmap.beatmapset_id).where(col(Beatmap.id).in_(legacy_beatmap_ids))
                )
            ).all()
        )
        if legacy_beatmap_ids
        else {}
    )

    def notification_data(row: SomsActivity) -> dict:
        data = dict(row.payload)
        if row.kind == "rank_lost" and not data.get("beatmapset_id"):
            data["beatmapset_id"] = beatmapsets.get(int(data.get("beatmap_id") or 0))
        return data

    return {
        "supporter": True,
        "unread": unread,
        "preferences": preferences,
        "items": [
            {
                "id": row.id,
                "kind": row.kind,
                "data": notification_data(row),
                "created_at": row.created_at.replace(tzinfo=UTC),
                "is_read": row.is_read,
            }
            for row in items
        ],
    }


@router.put(f"{WEB_API_PREFIX}/notifications/preferences")
async def save_preferences(payload: NotificationPreferences, request: Request, context: WebSession, session: Database):
    _require_csrf(request, context)
    preference = await session.get(UserPreference, context.user.id)
    if preference is None:
        preference = UserPreference(user_id=context.user.id)
    preference.extra = {**(preference.extra or {}), "soms_notifications": payload.model_dump()}
    session.add(preference)
    await session.commit()
    return payload


@router.put(f"{WEB_API_PREFIX}/notifications/read")
async def mark_read(request: Request, context: WebSession, session: Database, through: int):
    _require_csrf(request, context)
    if not context.user.is_supporter:
        raise HTTPException(403, "Уведомления доступны с supporter")
    from sqlalchemy import update

    await session.exec(
        update(SomsActivity)
        .where(
            SomsActivity.recipient_id == context.user.id,
            col(SomsActivity.id) <= through,
            col(SomsActivity.is_read).is_(False),
        )
        .values(is_read=True)
    )
    await session.commit()
    return {"ok": True}


@router.put(f"{WEB_API_PREFIX}/notifications/{{notification_id}}/read")
async def read_one(notification_id: int, request: Request, context: WebSession, session: Database):
    _require_csrf(request, context)
    row = await session.get(SomsActivity, notification_id)
    if not context.user.is_supporter or row is None or row.recipient_id != context.user.id:
        raise HTTPException(404, "Уведомление не найдено")
    row.is_read = True
    session.add(row)
    await session.commit()
    return {"ok": True}
