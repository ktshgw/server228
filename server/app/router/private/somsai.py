"""Native SOMSAI API. OAuth identity is authoritative for every action."""

from app.database.somsai import SomsaiMatch
from app.dependencies.database import Redis
from app.dependencies.fetcher import Fetcher
from app.dependencies.user import ClientUser
from app.models.somsai import SomsaiAction
from app.service.somsai_bot_service import bot_personas, prepare_draft_features
from app.service.somsai_match_service import match_payload, members_of
from app.service.somsai_party_service import reject, somsai_transaction
from app.service.somsai_service import handle_action, public_state

from .router import router

from fastapi import BackgroundTasks
from pydantic import BaseModel, Field


@router.get("/somsai/leaderboard", include_in_schema=False)
async def somsai_leaderboard(
    current_user: ClientUser, ruleset_id: int = 0, variant_id: int = 0, format: str = "1v1", page: int = 1
):
    from app.service.somsai_lobby_service import leaderboard

    async with somsai_transaction() as session:
        return await leaderboard(session, current_user.id, ruleset_id, variant_id, format, page)


@router.get("/somsai/social", include_in_schema=False)
async def somsai_social(current_user: ClientUser):
    from app.service.somsai_party_service import party_snapshot

    async with somsai_transaction() as session:
        return await party_snapshot(session, current_user.id, public=True)


class PartyChatMessage(BaseModel):
    content: str = Field(min_length=1, max_length=500)


@router.get("/somsai/party/chat", include_in_schema=False)
async def somsai_party_chat(current_user: ClientUser, redis: Redis):
    from app.service.somsai_lobby_service import party_chat

    async with somsai_transaction() as session:
        return await party_chat(session, redis, current_user.id)


@router.post("/somsai/party/chat", include_in_schema=False)
async def somsai_party_message(payload: PartyChatMessage, current_user: ClientUser, redis: Redis):
    from app.service.somsai_lobby_service import party_chat

    async with somsai_transaction() as session:
        return await party_chat(session, redis, current_user.id, payload.content)


@router.get("/somsai/state", include_in_schema=False)
async def somsai_state(
    current_user: ClientUser,
    fetcher: Fetcher,
    redis: Redis,
    background_tasks: BackgroundTasks,
    ruleset_id: int = 0,
    variant_id: int = 0,
):
    async with somsai_transaction() as session:
        result = await public_state(session, current_user.id, ruleset_id, variant_id)
    match = result.get("match")
    if match and match.get("pool_selected") and match["stage"] not in {"ended", "cancelled"}:
        missing = [s for s in match["slots"] if not s.get("display_stats")]
        if missing and await redis.set(f"somsai:pool-stats:{match['id']}", "1", nx=True, ex=30):
            background_tasks.add_task(prepare_draft_features, fetcher, redis, match["id"], ruleset_id, missing)
    return result


@router.post("/somsai/actions", include_in_schema=False)
async def somsai_action(
    payload: SomsaiAction, current_user: ClientUser, fetcher: Fetcher, redis: Redis, background_tasks: BackgroundTasks
):
    personas = (
        await bot_personas(fetcher, redis, payload.ruleset_id, payload.bot_level, int(payload.format[0]))
        if payload.action == "custom_create" and payload.with_bots
        else None
    )
    async with somsai_transaction() as session:
        await handle_action(session, current_user.id, payload, bot_profiles=personas)
        result = await public_state(session, current_user.id, payload.ruleset_id, payload.variant_id)
    if personas and result.get("match"):
        match = result["match"]
        background_tasks.add_task(
            prepare_draft_features, fetcher, redis, match["id"], payload.ruleset_id, match["slots"]
        )
    return result


@router.get("/somsai/matches/{match_id}", include_in_schema=False)
async def somsai_match(match_id: int, current_user: ClientUser):
    async with somsai_transaction() as session:
        match = await session.get(SomsaiMatch, match_id)
        if match is None or current_user.id not in members_of(match):
            reject("Матч недоступен.", 404)
        return await match_payload(session, match)
