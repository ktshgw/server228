"""Immutable playlists with scores that cannot affect normal rankings."""

import json
import time
from uuid import uuid4

from app.database import User
from app.database.marathon import Marathon, MarathonScore
from app.models.marathon import CreateMarathon, StartMarathon, SubmitMarathonScore
from app.models.mods import API_MODS, get_speed_rate

from fastapi import HTTPException
from sqlalchemy.exc import IntegrityError
from sqlmodel import col, func, select


async def visible_marathon(session, marathon_id: int) -> Marathon:
    marathon = await session.get(Marathon, marathon_id)
    if marathon is None or marathon.deleted:
        raise HTTPException(404, "Марафон не найден")
    return marathon


def marathon_payload(marathon: Marathon, owner: User) -> dict:
    return {
        "id": marathon.id,
        "name": marathon.name,
        "ruleset_id": marathon.ruleset_id,
        "compiler_version": marathon.compiler_version,
        "segments": marathon.segments,
        "duration_ms": marathon.duration_ms,
        "owner_id": owner.id,
        "owner_name": owner.username,
    }


async def create_marathon(session, user: User, payload: CreateMarathon) -> dict:
    if await user.is_restricted(session):
        raise HTTPException(403, "Недоступно для ограниченного аккаунта")
    # Serialize the quota check for simultaneous creates from the same account.
    await session.exec(select(User.id).where(User.id == user.id).with_for_update())
    count = (
        await session.exec(
            select(func.count())
            .select_from(Marathon)
            .where(Marathon.owner_id == user.id, col(Marathon.deleted).is_(False))
        )
    ).one()
    if count >= 100:
        raise HTTPException(422, "Можно сохранить до 100 марафонов. Удалите ненужные подборки.")
    marathon = Marathon(
        owner_id=user.id,
        name=payload.name,
        ruleset_id=payload.ruleset_id,
        segments=[segment.model_dump() for segment in payload.segments],
        # Each song has 1.5 seconds of lead-in/out; adjacent tails crossfade for 1s.
        duration_ms=sum(segment.end_ms - segment.start_ms + 3000 for segment in payload.segments)
        - (len(payload.segments) - 1) * 1000,
    )
    session.add(marathon)
    await session.flush()
    result = marathon_payload(marathon, user)
    await session.commit()
    return result


async def start_marathon(session, redis, user: User, marathon_id: int, payload: StartMarathon) -> dict:
    marathon = await visible_marathon(session, marathon_id)
    if await user.is_restricted(session):
        raise HTTPException(403, "Недоступно для ограниченного аккаунта")
    if any(mod["acronym"] not in API_MODS.get(marathon.ruleset_id, {}) for mod in payload.mods):
        raise HTTPException(422, "Неизвестный мод")
    if len(json.dumps(payload.mods)) > 8000:
        raise HTTPException(422, "Слишком много настроек модов")
    # Assisted/autoplay runs remain playable locally, but have no public result.
    assisted = any(mod["acronym"] in {"AT", "CN", "AP", "RX"} for mod in payload.mods)
    attempt_id = str(uuid4())
    rate = max(0.1, min(10.0, get_speed_rate(payload.mods)))
    await redis.set(
        f"marathon:attempt:{attempt_id}",
        json.dumps(
            {
                "user_id": user.id,
                "marathon_id": marathon_id,
                "mods": payload.mods,
                "started_at": time.time(),
                "minimum_seconds": marathon.duration_ms / 1000 / rate * 0.7,
                "assisted": assisted,
            }
        ),
        ex=24 * 60 * 60,
    )
    return {"attempt_id": attempt_id}


async def submit_marathon_score(session, redis, user: User, marathon_id: int, payload: SubmitMarathonScore) -> dict:
    await visible_marathon(session, marathon_id)
    if await user.is_restricted(session):
        raise HTTPException(403, "Недоступно для ограниченного аккаунта")
    attempt_id = str(payload.attempt_id)
    existing = (await session.exec(select(MarathonScore).where(MarathonScore.attempt_id == attempt_id))).first()
    if existing is not None:
        if existing.user_id != user.id or existing.marathon_id != marathon_id:
            raise HTTPException(404, "Попытка не найдена")
        return {"saved": True, "id": existing.id}
    raw = await redis.get(f"marathon:attempt:{attempt_id}")
    if not raw:
        raise HTTPException(422, "Срок этой попытки истёк")
    attempt = json.loads(raw)
    if attempt["user_id"] != user.id or attempt["marathon_id"] != marathon_id:
        raise HTTPException(404, "Попытка не найдена")
    if attempt["assisted"]:
        return {"saved": False, "reason": "assisted"}
    if time.time() - attempt["started_at"] < attempt["minimum_seconds"]:
        raise HTTPException(422, "Марафон ещё не завершён")
    score = MarathonScore(
        marathon_id=marathon_id,
        user_id=user.id,
        attempt_id=attempt_id,
        total_score=payload.total_score,
        accuracy=payload.accuracy,
        max_combo=payload.max_combo,
        mods=attempt["mods"],
    )
    session.add(score)
    try:
        await session.flush()
        score_id = score.id
        await session.commit()
    except IntegrityError:
        await session.rollback()
        # A concurrent retry of the same completed attempt must not duplicate it.
        existing = (await session.exec(select(MarathonScore.id).where(MarathonScore.attempt_id == attempt_id))).first()
        if existing is None:
            raise
        score_id = existing
    await redis.delete(f"marathon:attempt:{attempt_id}")
    return {"saved": True, "id": score_id}


async def marathon_leaderboard(session, marathon_id: int) -> dict:
    await visible_marathon(session, marathon_id)
    # One best result per player; names/avatars follow the current server profile.
    ranked = (
        select(
            MarathonScore.id,
            func.row_number()
            .over(
                partition_by=MarathonScore.user_id,
                order_by=(col(MarathonScore.total_score).desc(), col(MarathonScore.accuracy).desc(), MarathonScore.id),
            )
            .label("position"),
        )
        .where(MarathonScore.marathon_id == marathon_id)
        .subquery()
    )
    rows = (
        await session.exec(
            select(MarathonScore, User)
            .join(User, User.id == MarathonScore.user_id)
            .where(
                col(MarathonScore.id).in_(select(ranked.c.id).where(ranked.c.position == 1)),
                col(User.is_active).is_(True),
                col(User.is_bot).is_(False),
                ~User.is_restricted_query(col(User.id)),
            )
            .order_by(col(MarathonScore.total_score).desc(), col(MarathonScore.accuracy).desc(), MarathonScore.id)
            .limit(100)
        )
    ).all()
    return {
        "items": [
            {
                "user_id": user.id,
                "username": user.username,
                "avatar_url": user.avatar_url,
                "country_code": user.country_code,
                "total_score": score.total_score,
                "accuracy": score.accuracy,
                "max_combo": score.max_combo,
                "mods": score.mods,
            }
            for score, user in rows
        ]
    }
