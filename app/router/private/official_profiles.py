from app.dependencies.database import Redis
from app.dependencies.fetcher import Fetcher
from app.service.official_profile_service import get_official_resource

from .router import router

from fastapi import Request


@router.get("/official-profiles/{target:path}")
async def official_profile(target: str, request: Request, fetcher: Fetcher, redis: Redis):
    return await get_official_resource(fetcher, redis, target, dict(request.query_params))
