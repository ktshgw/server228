"""Spectator transport authenticated with the existing signed-URL protocol."""

from copy import deepcopy

from app.dependencies.database import Database
from app.dependencies.fetcher import Fetcher
from app.features.somsai.database.somsai import SomsaiMatch
from app.features.somsai.dependencies import require_somsai_interop
from app.features.somsai.models.somsai import PartyReserveRequest, SomsaiBotResults, SomsaiRoomEvent
from app.features.somsai.services.somsai_match_service import FINAL_STAGES, match_action, match_payload, room_event
from app.features.somsai.services.somsai_party_service import (
    activity,
    claim_native_room,
    native_reserve,
    party_snapshot,
    reject,
    release_native_room,
    release_reservation,
    renew_reservation,
    somsai_transaction,
)

from fastapi import APIRouter, Depends
from sqlmodel import select

router = APIRouter(prefix="/_lio", include_in_schema=False, dependencies=[Depends(require_somsai_interop)])


@router.get("/somsai/rooms/{room_id}/bot-beatmap/{item_id}")
async def internal_bot_beatmap(room_id: int, item_id: int, session: Database, fetcher: Fetcher):
    match = (await session.exec(select(SomsaiMatch).where(SomsaiMatch.room_id == room_id))).first()
    if (
        not match
        or not match.state.get("bots")
        or match.stage not in {"ready", "playing"}
        or match.state["playlist_item_id"] != item_id
    ):
        reject("Раунд ботов недоступен.", 409)
    slot = next(slot for slot in match.state["slots"] if slot["id"] == match.state["current_slot"])
    # Exact reviewed revision, using the same cache/fallbacks as player downloads.
    raw = await fetcher.get_beatmap_raw(slot["beatmap_id"], slot["checksum"])
    return {"raw": raw, "checksum": slot["checksum"]}


@router.post("/somsai/rooms/{room_id}/bot-results")
async def internal_bot_results(room_id: int, payload: SomsaiBotResults):
    async with somsai_transaction() as session:
        match = (await session.exec(select(SomsaiMatch).where(SomsaiMatch.room_id == room_id))).first()
        if (
            not match
            or match.ranked
            or match.stage not in {"playing", "results"}
            or match.state["playlist_item_id"] != payload.playlist_item_id
        ):
            reject("Раунд ботов уже изменился.", 409)
        ids = {bot["user_id"] for bot in match.state.get("bots", [])}
        if {score.user_id for score in payload.scores} != ids or len(payload.scores) != len(ids):
            reject("Состав ботов не совпадает.", 422)
        scores = {str(score.user_id): score.model_dump() for score in payload.scores}
        previous = match.state.get("bot_scores", {})
        if previous and previous != scores:
            reject("Результаты раунда уже сохранены.", 409)
        state = deepcopy(match.state)
        state["bot_scores"] = scores
        match.state = state
        session.add(match)
        return await match_payload(session, match, internal=True)


@router.post("/somsai/native-rooms/{room_id}/users/{user_id}")
async def internal_native_claim(room_id: int, user_id: int):
    async with somsai_transaction() as session:
        user_activity = await activity(session, user_id)
        if user_activity.reservation_id:
            match = await session.get(SomsaiMatch, user_activity.match_id) if user_activity.match_id else None
            if match is not None and match.stage not in FINAL_STAGES and match.room_id != room_id:
                # Creating or joining another native lobby is an explicit leave
                # from SOMSAI. Settle that departure instead of returning a raw
                # SignalR 409 after the new room was already inserted.
                await match_action(session, match, user_id, "leave_match")
            elif match is None or match.stage in FINAL_STAGES:
                await release_reservation(session, user_activity.reservation_id)
        await claim_native_room(session, user_id, room_id)
        return {"ok": True}


@router.delete("/somsai/native-rooms/{room_id}/users/{user_id}")
async def internal_native_release(room_id: int, user_id: int):
    async with somsai_transaction() as session:
        await release_native_room(session, user_id, room_id)
        return {"ok": True}


@router.get("/parties/{user_id}")
async def internal_party(user_id: int):
    async with somsai_transaction() as session:
        return await party_snapshot(session, user_id)


@router.post("/parties/{user_id}/reserve")
async def internal_reserve(user_id: int, payload: PartyReserveRequest):
    async with somsai_transaction() as session:
        return await native_reserve(session, user_id, payload.pool_id, str(payload.request_id))


@router.post("/party-reservations/{reservation_id}/renew")
async def internal_renew(reservation_id: str):
    async with somsai_transaction() as session:
        await renew_reservation(session, reservation_id)
        return {"ok": True}


@router.delete("/party-reservations/{reservation_id}")
async def internal_release(reservation_id: str):
    async with somsai_transaction() as session:
        await release_reservation(session, reservation_id)
        return {"ok": True}


@router.get("/somsai/rooms/{room_id}")
async def internal_somsai_room(room_id: int):
    async with somsai_transaction() as session:
        match = (await session.exec(select(SomsaiMatch).where(SomsaiMatch.room_id == room_id))).first()
        return await match_payload(session, match, internal=True) if match else {"managed": False}


@router.post("/somsai/rooms/{room_id}/events")
async def internal_somsai_event(room_id: int, payload: SomsaiRoomEvent):
    async with somsai_transaction() as session:
        return await room_event(
            session, room_id, payload.event, payload.playlist_item_id, payload.connected, ready=payload.ready,
            available=payload.available,
        )
