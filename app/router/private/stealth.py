from typing import Literal

from app.dependencies.database import Redis
from app.dependencies.fetcher import Fetcher
from app.dependencies.user import ClientUser
from app.service.stealth_service import nearby_players

from .router import router

from fastapi import HTTPException, Query


@router.get("/stealth/nearby")
async def stealth_nearby(
    current_user: ClientUser,
    fetcher: Fetcher,
    redis: Redis,
    mode: Literal["osu", "taiko", "fruits", "mania"] = "osu",
    pp: float = Query(ge=0, le=1000000, allow_inf_nan=False),
    country: str | None = Query(default=None, pattern="^[A-Za-z]{2}$"),
):
    if not await redis.set(f"stealth:request:{current_user.id}", "1", ex=2, nx=True):
        raise HTTPException(429, "Подождите немного перед следующим подбором.")
    return await nearby_players(fetcher, redis, mode, pp, country)
