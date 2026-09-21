from app.database import User
from app.database.marathon import Marathon
from app.dependencies.database import Database, Redis
from app.dependencies.user import ClientUser
from app.models.marathon import CreateMarathon, StartMarathon, SubmitMarathonScore
from app.service.marathon_service import (
    create_marathon,
    marathon_leaderboard,
    marathon_payload,
    start_marathon,
    submit_marathon_score,
    visible_marathon,
)

from .router import router

from fastapi import HTTPException, Query
from sqlmodel import col, select


@router.get("/marathons", include_in_schema=False)
async def list_marathons(
    session: Database, user: ClientUser, ruleset_id: int = Query(0, ge=0, le=3), page: int = Query(1, ge=1, le=10000)
):
    rows = (
        await session.exec(
            select(Marathon, User)
            .join(User, User.id == Marathon.owner_id)
            .where(
                Marathon.ruleset_id == ruleset_id,
                col(Marathon.deleted).is_(False),
                col(User.is_active).is_(True),
                ~User.is_restricted_query(col(User.id)),
            )
            .order_by(col(Marathon.id).desc())
            .offset((page - 1) * 30)
            .limit(31)
        )
    ).all()
    return {"items": [marathon_payload(item, owner) for item, owner in rows[:30]], "has_more": len(rows) > 30}


@router.post("/marathons", include_in_schema=False)
async def save_marathon(payload: CreateMarathon, session: Database, user: ClientUser):
    return await create_marathon(session, user, payload)


@router.get("/marathons/{marathon_id}/scores", include_in_schema=False)
async def get_marathon_scores(marathon_id: int, session: Database, user: ClientUser):
    return await marathon_leaderboard(session, marathon_id)


@router.post("/marathons/{marathon_id}/start", include_in_schema=False)
async def begin_marathon(marathon_id: int, payload: StartMarathon, session: Database, redis: Redis, user: ClientUser):
    return await start_marathon(session, redis, user, marathon_id, payload)


@router.post("/marathons/{marathon_id}/scores", include_in_schema=False)
async def save_marathon_score(
    marathon_id: int, payload: SubmitMarathonScore, session: Database, redis: Redis, user: ClientUser
):
    return await submit_marathon_score(session, redis, user, marathon_id, payload)


@router.delete("/marathons/{marathon_id}", include_in_schema=False)
async def delete_marathon(marathon_id: int, session: Database, user: ClientUser):
    marathon = await visible_marathon(session, marathon_id)
    if marathon.owner_id != user.id:
        raise HTTPException(403, "Удалить марафон может только создатель")
    marathon.deleted = True
    session.add(marathon)
    await session.commit()
    return {"deleted": True}
