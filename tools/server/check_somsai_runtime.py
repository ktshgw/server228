"""Exercise the installed SOMSAI schema and full BO7 in a rolled-back transaction.

Run with the app's environment after migrations and pool seeding. No fixture
scores, rooms, parties or ratings are committed. SQL auto-increment gaps are
expected. Existing matches/searches are left alone; two idle accounts are used.
"""

import asyncio
import json

from app.log import logger

logger.remove()

from app.database import Playlist, Room, Score, User
from app.features.somsai.database.somsai import SomsaiActivity, SomsaiMatch, SomsaiNativeRoom, SomsaiRating, SomsaiReservation
from app.dependencies.database import engine, with_db
from app.helpers import utcnow
from app.models.score import GameMode, Rank
from app.features.somsai.services.somsai_match_service import (
    create_match,
    draft_action,
    find_slot,
    match_action,
    match_payload,
    room_event,
    settle_round,
)
from app.features.somsai.services.somsai_party_service import active_user, aware, lock_somsai, reserve_members
from app.features.somsai.services.somsai_service import public_state

from fastapi import HTTPException
from sqlalchemy.orm import lazyload
from sqlmodel import col, func, select


async def check() -> None:
    tracked = (Score, Room, SomsaiMatch, SomsaiRating, SomsaiReservation)
    async with with_db() as session:
        await lock_somsai(session)
        before = {
            model.__tablename__: (await session.exec(select(func.count()).select_from(model))).one()
            for model in tracked
        }
        users = (
            await session.exec(select(User.id).where(col(User.is_active).is_(True), col(User.is_bot).is_(False)))
        ).all()
        idle = []
        for uid in users:
            try:
                await active_user(session, uid)
            except HTTPException:
                continue
            activity = await session.get(SomsaiActivity, uid)
            native = await session.get(SomsaiNativeRoom, uid)
            if activity and activity.reservation_id:
                continue
            if native and aware(native.expires_at) > utcnow():
                continue
            idle.append(uid)
            if len(idle) == 2:
                break
        if len(idle) < 2:
            raise RuntimeError("Need two idle eligible accounts; no running match was changed")
        reservations = [await reserve_members(session, uid, [uid], "somsai:check", None) for uid in idle]
        match = await create_match(session, [[idle[0]], [idle[1]]], "1v1", 0, 0, [row.id for row in reservations])
        assert match.room_id is not None
        payload = await match_payload(session, match, internal=True)
        assert payload["managed"]
        assert payload["roster"] == [[idle[0]], [idle[1]]]
        assert (await public_state(session, idle[0], 0, 0))["match"]["id"] == match.id
        for uid in idle:
            await match_action(session, match, uid, "ready")
        assert match.stage == "banning"
        assert match.state["selection_kind"] == "mixed"
        assert match.state["pool_selected"] and not match.state["pool_candidates"]
        assert len({slot["beatmap_id"] for slot in match.state["slots"]}) == len(match.state["slots"])
        assert all(slot["source_pool_id"] and slot["source_revision"] for slot in match.state["slots"])
        wins_needed = match.state["best_of"] // 2 + 1
        while match.stage == "banning":
            captain = match.state["captains"][match.state["turn_team"]]
            slot = next(slot for slot in match.state["slots"] if slot["status"] == "available")
            await draft_action(session, match, captain, "ban", slot["id"])
        played = 0
        while match.stage != "ended":
            assert match.stage == "picking"
            captain = match.state["captains"][match.state["turn_team"]]
            slot = next(slot for slot in match.state["slots"] if slot["status"] == "available")
            await draft_action(session, match, captain, "pick", slot["id"])
            for uid in idle:
                await match_action(session, match, uid, "ready")
            item_id = match.state["playlist_item_id"]
            await room_event(session, match.room_id, "started", item_id, idle, ready=idle)
            assert match.stage == "playing"
            item = (
                await session.exec(
                    select(Playlist)
                    .options(lazyload("*"))
                    .where(Playlist.room_id == match.room_id, Playlist.id == item_id)
                )
            ).one()
            selected = find_slot(match.state, match.state["current_slot"])
            for index, uid in enumerate(idle):
                session.add(
                    Score(
                        beatmap_id=item.beatmap_id,
                        user_id=uid,
                        room_id=match.room_id,
                        playlist_item_id=item_id,
                        rank=Rank.A,
                        type="solo_score",
                        accuracy=0.95,
                        ended_at=utcnow(),
                        started_at=utcnow(),
                        has_replay=False,
                        max_combo=100,
                        passed=True,
                        total_score=900000 - index * 100000,
                        mods=item.required_mods,
                        gamemode=GameMode.OSU,
                        map_md5=selected["checksum"],
                        n300=100,
                        n100=0,
                        n50=0,
                        nmiss=0,
                        ngeki=0,
                        nkatu=0,
                        processed=True,
                        preserve=False,
                    )
                )
            await session.flush()
            await room_event(session, match.room_id, "completed", item_id, idle, ready=[])
            assert await settle_round(session, match)
            await session.flush()
            await session.refresh(match)
            played += 1
            assert played <= wins_needed
        assert match.state["wins"] == [wins_needed, 0]
        assert len(match.state["rating_changes"]) == 2
        deltas = [row["delta"] for row in match.state["rating_changes"]]
        assert deltas[0] > 0 > deltas[1]
        # Duplicate transport completion must not award a second match or rating.
        revision = match.revision
        await room_event(session, match.room_id, "completed", match.state["playlist_item_id"], idle, ready=[])
        assert match.revision == revision
        await session.rollback()
        after = {
            model.__tablename__: (await session.exec(select(func.count()).select_from(model))).one()
            for model in tracked
        }
        assert before == after, (before, after)
        print(
            json.dumps(
                {
                    "mysql_rounds": played,
                    "mixed_pool": "verified",
                    "source_tournaments": len(match.state["source_pools"]),
                    "pool_voting": False,
                    "score_submissions": played * 2,
                    "roster": "verified",
                    "elo_once": True,
                    "rollback_verified": True,
                }
            )
        )
    await engine.dispose()


if __name__ == "__main__":
    asyncio.run(check())
