"""Read models for the native SOMSAI browser and private party chat."""

import json

from app.database import User
from app.features.somsai.database.somsai import SomsaiRating
from app.features.somsai.services.somsai_party_service import get_party, reject, user_payload
from app.features.somsai.services.somsai_rating_service import ensure_rating, rating_payload
from app.features.somsai.services.somsai_service import validate_mode
from app.helpers import utcnow

from sqlmodel import col, select


async def leaderboard(session, user_id: int, ruleset_id: int, variant_id: int, format: str, page: int) -> dict:
    validate_mode(ruleset_id, variant_id)
    if format not in {"1v1", "2v2"} or page < 1 or page > 10000:
        reject("Неизвестный формат или страница.", 422)
    own = await ensure_rating(session, user_id, ruleset_id, variant_id, format)
    filters = [
        SomsaiRating.ruleset_id == ruleset_id,
        SomsaiRating.variant_id == variant_id,
        SomsaiRating.format == format,
        col(User.is_active).is_(True),
        col(User.is_bot).is_(False),
        ~User.is_restricted_query(col(User.id)),
    ]
    rows = (
        await session.exec(
            select(SomsaiRating)
            .join(User, col(User.id) == col(SomsaiRating.user_id))
            .where(*filters)
            .order_by(col(SomsaiRating.rating).desc(), col(SomsaiRating.user_id))
            .offset((page - 1) * 50)
            .limit(51)
        )
    ).all()

    async def entry(row, rank):
        return {
            "user": await user_payload(session, row.user_id),
            "rating": row.rating,
            "rank": rank,
            "wins": row.wins,
            "losses": row.losses,
        }

    own_rank = await rating_payload(session, own)
    return {
        "items": [await entry(row, (page - 1) * 50 + i + 1) for i, row in enumerate(rows[:50])],
        "self": await entry(own, own_rank["rank"]),
        "has_more": len(rows) > 50,
        "page": page,
    }


async def party_chat(session, redis, user_id: int, message: str | None = None) -> dict:
    party = await get_party(session, user_id)
    if party is None:
        if message is not None:
            reject("Сначала пригласите друга в пати.", 409)
        return {"party_id": None, "messages": []}
    key = f"somsai:party-chat:{party.id}"
    if message is not None:
        message = message.strip()
        if not message or len(message) > 500:
            reject("Сообщение должно содержать от 1 до 500 символов.", 422)
        if not await redis.set(f"{key}:throttle:{user_id}", "1", nx=True, ex=1):
            reject("Слишком много сообщений. Подождите секунду.", 429)
        sender = await user_payload(session, user_id)
        message_id = await redis.incr(f"{key}:sequence")
        value = {
            "message_id": message_id,
            "channel_id": party.id,
            "sender": sender,
            "content": message,
            "timestamp": utcnow().isoformat(),
        }
        async with redis.pipeline(transaction=True) as pipe:
            pipe.rpush(key, json.dumps(value))
            pipe.ltrim(key, -100, -1)
            pipe.expire(key, 604800)
            pipe.expire(f"{key}:sequence", 604800)
            await pipe.execute()
    return {"party_id": party.id, "messages": [json.loads(value) for value in await redis.lrange(key, 0, -1)]}
