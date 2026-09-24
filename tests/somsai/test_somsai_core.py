"""Authoritative SOMSAI flow on real isolated SQL, with HTTP signature boundaries."""

# ruff: noqa: PT009, PT027
from contextlib import asynccontextmanager
from copy import deepcopy
from datetime import datetime, timedelta
import hashlib
import hmac
import time
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch
from uuid import uuid4

from app.database import (
    AdminAuditEvent,
    Beatmap,
    Beatmapset,
    BestScore,
    ChatChannel,
    MatchmakingPool,
    Playlist,
    Relationship,
    RelationshipType,
    Room,
    Score,
    User,
    UserStatistics,
)
from app.database.room_participated_user import RoomParticipatedUser
from app.features.somsai.database.somsai import (
    SomsaiLock,
    SomsaiMatch,
    SomsaiNativeRoom,
    SomsaiPartyInvite,
    SomsaiQueue,
    SomsaiRating,
    SomsaiReservation,
)
from app.database.user_account_history import UserAccountHistory
from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus
from app.models.score import GameMode, Rank
from app.features.somsai.models.somsai_admin import SomsaiPoolSpec
from app.router import lio, somsai_lio
from app.features.somsai.routers.somsai_lio import router as interop_router
from app.features.somsai.services.somsai_match_service import (
    finish_match,
    match_action,
    members_of,
    room_event,
    settle_round,
    tick_match,
)
from app.features.somsai.services.somsai_party_service import (
    _legacy_native_reserve as native_reserve,
    activity,
    aware,
    claim_native_room,
    expire_reservations,
    get_party,
    party_action,
    release_native_room,
    release_reservation,
    renew_reservation,
)
from app.features.somsai.services.somsai_pool_service import save_pool
from app.features.somsai.services.somsai_rating_service import ensure_rating, initial_rating
from app.features.somsai.services.somsai_service import create_custom, join_custom, join_queue, leave_queue, public_state

from fastapi import FastAPI, HTTPException
import httpx
from sqlalchemy import BigInteger, Integer, MetaData, event
from sqlalchemy.orm import lazyload
from sqlmodel import Session, SQLModel, create_engine, select


class AsyncSql:
    def __init__(self, db):
        self.db = db
        self.info = db.info

    async def exec(self, statement):
        return self.db.exec(statement)

    async def execute(self, statement):
        return self.db.execute(statement)

    async def get(self, model, key):
        return self.db.get(model, key, options=[lazyload("*")])

    def add(self, item):
        self.db.add(item)

    async def flush(self):
        self.db.flush()

    async def refresh(self, item):
        self.db.refresh(item)

    async def delete(self, item):
        self.db.delete(item)

    async def commit(self):
        self.db.commit()

    async def rollback(self):
        self.db.rollback()


class SomsaiCoreTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.engine = create_engine("sqlite://")

        @event.listens_for(self.engine, "connect")
        def connect(connection, _record):
            connection.execute("PRAGMA foreign_keys=ON")
            connection.create_function(
                "timestampadd",
                3,
                lambda _unit, seconds, stamp: (datetime.fromisoformat(stamp) + timedelta(seconds=seconds)).strftime(
                    "%Y-%m-%d %H:%M:%S"
                ),
            )

        @event.listens_for(self.engine, "before_cursor_execute", retval=True)
        def adapt(_connection, _cursor, statement, parameters, _context, _many):
            return statement.replace("timestampadd(SECOND,", "timestampadd('SECOND',"), parameters

        metadata = MetaData()
        for table in SQLModel.metadata.tables.values():
            table.to_metadata(metadata)
        wanted = {
            model.__tablename__
            for model in (
                AdminAuditEvent,
                User,
                UserStatistics,
                UserAccountHistory,
                Beatmap,
                Beatmapset,
                BestScore,
                Room,
                RoomParticipatedUser,
                ChatChannel,
                Playlist,
                Score,
                Relationship,
                MatchmakingPool,
            )
        }
        wanted.update(name for name in metadata.tables if name.startswith("somsai_"))
        wanted.update({"negative_pp_rules", "beatmap_mapper_credits"})
        while True:
            expanded = wanted | {fk.column.table.name for name in wanted for fk in metadata.tables[name].foreign_keys}
            if expanded == wanted:
                break
            wanted = expanded
        for name in wanted:
            for column in metadata.tables[name].columns:
                if column.primary_key and isinstance(column.type, BigInteger):
                    column.type = Integer()
        metadata.create_all(self.engine, tables=[metadata.tables[name] for name in wanted])
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncSql(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        for uid in (2, *range(10, 18)):
            self.db.add(
                User(
                    id=uid,
                    username=f"fixture{uid}",
                    email=f"{uid}@example.invalid",
                    pw_bcrypt="disabled",
                    is_bot=uid == 2,
                    is_active=True,
                )
            )
        self.db.flush()
        for uid in range(10, 18):
            self.db.add(UserStatistics(user_id=uid, mode=GameMode.OSU, pp=100))
        self.db.add(SomsaiLock(id=1))
        self.db.add_all(
            [
                MatchmakingPool(id=1, name="Ranked1", type="ranked_play", lobby_size=2),
                MatchmakingPool(id=2, name="Ranked2", type="ranked_play", lobby_size=4),
            ]
        )
        now = utcnow()
        self.db.add(
            Beatmapset(
                id=100,
                artist="Test",
                artist_unicode="Test",
                creator="test",
                title="Tournament",
                title_unicode="Tournament",
                user_id=2,
                preview_url="",
                video=False,
                covers=None,
                last_updated=now,
                submitted_date=now,
                beatmap_status=BeatmapRankStatus.GRAVEYARD,
            )
        )
        self.db.flush()
        self.db.add_all(
            [
                Beatmap(
                    id=i,
                    beatmapset_id=100,
                    user_id=2,
                    mode=GameMode.OSU,
                    version=f"Map {i}",
                    difficulty_rating=5,
                    url=f"https://osu.ppy.sh/beatmaps/{i}",
                    total_length=120,
                    hit_length=100,
                    checksum=f"{i:032x}",
                    beatmap_status=BeatmapRankStatus.GRAVEYARD,
                    last_updated=now,
                    cs=4,
                )
                for i in range(101, 114)
            ]
        )
        self.db.commit()

    async def asyncSetUp(self):
        slots = [{"id": f"NM{i - 100}", "beatmap_id": i} for i in range(101, 113)] + [{"id": "TB", "beatmap_id": 113}]
        _, self.pool = await save_pool(
            self.session, SomsaiPoolSpec(name="Fixture pool", slots=slots, active=True), None, None
        )
        # Equal stored SOMSAI ratings allow immediate pairing while still exercising the real seed separately.
        for uid in range(10, 18):
            for kind in ("1v1", "2v2"):
                self.db.add(SomsaiRating(user_id=uid, format=kind, rating=1500))
        self.db.commit()

    async def make_match(self):
        await join_queue(self.session, 10, "1v1", 0, 0)
        await join_queue(self.session, 11, "1v1", 0, 0)
        return self.db.exec(select(SomsaiMatch)).one()

    async def add_alternative_pool(self):
        slots = [{"id": f"NM{i - 100}", "beatmap_id": i} for i in range(101, 113)] + [{"id": "TB", "beatmap_id": 113}]
        _, pool = await save_pool(
            self.session, SomsaiPoolSpec(name="Alternative tournament", slots=slots, active=True, best_of=9), None, None
        )
        return pool

    async def test_mixed_pool_skips_voting_and_freezes_all_sources(self):
        alternative = await self.add_alternative_pool()
        match = await self.make_match()
        before = deepcopy(match.state["slots"])
        for uid in members_of(match):
            await match_action(self.session, match, uid, "ready")
        self.assertEqual(match.stage, "banning")
        self.assertEqual(match.state["pool_candidates"], [])
        self.assertTrue(match.state["pool_selected"])
        self.assertEqual(len(match.state["source_pools"]), 2)
        self.assertEqual(len({slot["beatmap_id"] for slot in before}), len(before))
        self.assertTrue(all(slot["source_revision"] == 1 for slot in before))
        alternative.name = "Changed by admin during draft"
        alternative.slots = []
        self.db.add(alternative)
        self.db.commit()
        self.db.expire_all()
        match = self.db.get(SomsaiMatch, match.id)
        self.assertEqual(match.state["slots"], before)
        with self.assertRaises(HTTPException):
            await match_action(self.session, match, 10, "pool_vote", pool_id=alternative.id)

    async def test_automatic_pool_uses_all_eligible_tournaments_without_old_cap(self):
        for _ in range(30):
            await self.add_alternative_pool()
        match = await self.make_match()
        self.assertEqual(len(match.state["source_pools"]), 31)
        self.assertEqual(match.state["selection_kind"], "mixed")

    async def test_custom_target_mmr_is_not_a_rating_change(self):
        await create_custom(self.session, 10, "1v1", 0, 0, None, "MMR test", target_mmr=1250)
        match = self.db.exec(select(SomsaiMatch)).one()
        self.assertEqual(match.state["match_rating"], 1250)
        self.assertFalse(match.ranked)
        self.assertEqual((await ensure_rating(self.session, 10, 0, 0, "2v2")).rating, 1500)
        response = await public_state(self.session, 11, 0, 0)
        self.assertEqual(response["customs"][0]["target_mmr"], 1250)

    async def test_custom_waiting_for_roster_gets_fresh_server_connection_grace(self):
        await create_custom(self.session, 10, "1v1", 0, 0, None, "Wait for friend")
        match = self.db.exec(select(SomsaiMatch)).one()
        match.state = {**match.state, "server_seen_at": (utcnow() - timedelta(minutes=10)).isoformat()}
        await join_custom(self.session, 11, match.id, 1)
        await match_action(self.session, match, 10, "custom_start")
        await tick_match(self.session, match)
        self.assertEqual(match.stage, "banning")

    async def test_party_invitation_by_exact_username_preserves_privacy(self):
        await party_action(self.session, 10, "party_invite", target_username="  @FiXtUrE11  ")
        invite = self.db.exec(select(SomsaiPartyInvite)).one()
        self.assertEqual(invite.target_id, 11)
        with self.assertRaises(HTTPException):
            await party_action(self.session, 10, "party_invite", target_username="fixture")
        self.db.add(Relationship(user_id=12, target_id=10, type=RelationshipType.BLOCK))
        self.db.flush()
        with self.assertRaises(HTTPException):
            await party_action(self.session, 10, "party_invite", target_username="fixture12")

    async def ready_first_map(self, match):
        for uid in members_of(match):
            await match_action(self.session, match, uid, "ready")
        for slot in ("NM1", "NM2"):
            await match_action(
                self.session, match, match.state["captains"][match.state["turn_team"]], "ban", slot_id=slot
            )
        await match_action(
            self.session, match, match.state["captains"][match.state["turn_team"]], "pick", slot_id="NM3"
        )
        for uid in members_of(match):
            await match_action(self.session, match, uid, "ready")

    def add_score(self, match, uid, total, mode=GameMode.OSU):
        slot = next(slot for slot in match.state["slots"] if slot["id"] == match.state["current_slot"])
        now = utcnow()
        score = Score(
            beatmap_id=slot["beatmap_id"],
            user_id=uid,
            rank=Rank.A,
            type="solo_score",
            accuracy=0.99,
            ended_at=now,
            started_at=now - timedelta(seconds=120),
            has_replay=False,
            max_combo=100,
            passed=True,
            total_score=total,
            mods=[{"acronym": "NF"}],
            room_id=match.room_id,
            playlist_item_id=match.state["playlist_item_id"],
            gamemode=mode,
            map_md5=slot["checksum"],
            n300=100,
            n100=1,
            n50=0,
            nmiss=0,
            ngeki=0,
            nkatu=0,
        )
        self.db.add(score)
        self.db.flush()
        return score

    async def test_uuid_retry_is_idempotent_identity_checked_and_expiry_never_revives(self):
        request_id = str(uuid4())
        first = await native_reserve(self.session, 10, 1, request_id)
        again = await native_reserve(self.session, 10, 1, request_id)
        self.assertEqual(first, again)
        self.assertEqual(first["reservation_id"], request_id)
        self.assertEqual(len(self.db.exec(select(SomsaiReservation)).all()), 1)
        for user, pool in ((11, 1), (10, 2)):
            with self.assertRaises(HTTPException):
                await native_reserve(self.session, user, pool, request_id)
        row = self.db.get(SomsaiReservation, request_id)
        row.expires_at = utcnow() - timedelta(seconds=1)
        with self.assertRaises(HTTPException):
            await renew_reservation(self.session, request_id)
        await expire_reservations(self.session)
        self.assertIsNone((await activity(self.session, 10)).reservation_id)
        with self.assertRaises(HTTPException):
            await native_reserve(self.session, 10, 1, request_id)
        fresh = await native_reserve(self.session, 10, 1, str(uuid4()))
        self.assertNotEqual(fresh["reservation_id"], request_id)

    async def test_released_uuid_cannot_be_reused_and_old_release_keeps_new_lease(self):
        old = str(uuid4())
        await native_reserve(self.session, 10, 1, old)
        await release_reservation(self.session, old)
        with self.assertRaises(HTTPException):
            await native_reserve(self.session, 10, 1, old)
        new = str(uuid4())
        await native_reserve(self.session, 10, 1, new)
        await release_reservation(self.session, old)
        self.assertEqual((await activity(self.session, 10)).reservation_id, new)

    async def test_native_room_claim_and_queue_exclude_each_other_without_stale_room_history(self):
        await claim_native_room(self.session, 10, 777)
        with self.assertRaises(HTTPException):
            await native_reserve(self.session, 10, 1, str(uuid4()))
        await release_native_room(self.session, 10, 888)
        self.assertIsNotNone(self.db.get(SomsaiNativeRoom, 10))
        self.db.get(SomsaiNativeRoom, 10).expires_at = utcnow() - timedelta(seconds=1)
        await native_reserve(self.session, 10, 1, str(uuid4()))
        with self.assertRaises(HTTPException):
            await claim_native_room(self.session, 10, 777)

    async def test_native_room_claim_renews_somsai_room_and_new_lobby_leaves_match(self):
        match = await self.make_match()

        # Spectator periodically renews the native room backing SOMSAI. The
        # match reservation must not conflict with its own room.
        await claim_native_room(self.session, 10, match.room_id)
        self.assertEqual(self.db.get(SomsaiNativeRoom, 10).room_id, match.room_id)
        await release_native_room(self.session, 10, match.room_id)

        @asynccontextmanager
        async def transaction():
            yield self.session

        # Creating a normal lobby is an explicit departure from the active
        # SOMSAI match and must not bubble a raw 409 through SignalR CreateRoom.
        with patch("app.features.somsai.routers.somsai_lio.somsai_transaction", transaction):
            await somsai_lio.internal_native_claim(999, 10)

        self.assertEqual(match.stage, "cancelled")
        self.assertIsNone((await activity(self.session, 10)).reservation_id)
        self.assertEqual(self.db.get(SomsaiNativeRoom, 10).room_id, 999)

    async def test_invite_accept_rechecks_block_and_inviter_identity(self):
        await party_action(self.session, 10, "party_invite", target_id=11)
        invite = self.db.exec(select(SomsaiPartyInvite)).one()
        block = Relationship(user_id=11, target_id=10, type=RelationshipType.BLOCK)
        self.db.add(block)
        self.db.flush()
        with self.assertRaises(HTTPException):
            await party_action(self.session, 11, "party_accept", invitation_id=invite.id)
        self.assertIsNone(await get_party(self.session, 11))
        self.db.delete(block)
        await party_action(self.session, 11, "party_accept", invitation_id=invite.id)
        party = await get_party(self.session, 10)
        self.assertEqual(party.members, [10, 11])
        with self.assertRaises(HTTPException):
            await native_reserve(self.session, 11, 2, str(uuid4()))
        reserved = await native_reserve(self.session, 10, 2, str(uuid4()))
        self.assertEqual(reserved["members"], [10, 11])

    async def test_party_stays_together_in_2v2_and_queue_close_race_cancels_without_rating(self):
        await party_action(self.session, 10, "party_invite", target_id=11)
        invite = self.db.exec(select(SomsaiPartyInvite)).one()
        await party_action(self.session, 11, "party_accept", invitation_id=invite.id)
        await join_queue(self.session, 10, "2v2", 0, 0)
        await join_queue(self.session, 12, "2v2", 0, 0)
        await join_queue(self.session, 13, "2v2", 0, 0)
        match = self.db.exec(select(SomsaiMatch)).one()
        self.assertIn([10, 11], match.state["teams"])
        self.assertEqual(len(self.db.exec(select(SomsaiQueue)).all()), 0)
        await leave_queue(self.session, 11)
        self.assertEqual(match.stage, "cancelled")
        self.assertTrue(all(rating.games == 0 for rating in self.db.exec(select(SomsaiRating)).all()))

    async def test_custom_team_change_keeps_one_reservation_and_survives_session_reload(self):
        await create_custom(self.session, 10, "2v2", 0, 0, None, "Custom fixture")
        match = self.db.exec(select(SomsaiMatch)).one()
        reservation = (await activity(self.session, 10)).reservation_id
        await join_custom(self.session, 10, match.id, 1)
        await join_custom(self.session, 10, match.id, 1)
        self.assertEqual(match.state["teams"], [[], [10]])
        self.assertEqual((await activity(self.session, 10)).reservation_id, reservation)
        self.assertEqual(len(self.db.exec(select(SomsaiReservation)).all()), 1)
        self.db.commit()
        match_id = match.id
        self.db.expunge_all()
        self.assertEqual(self.db.get(SomsaiMatch, match_id).state["teams"], [[], [10]])

    async def test_ready_draft_permissions_stale_revision_and_tiebreaker(self):
        match = await self.make_match()
        with self.assertRaises(HTTPException):
            await match_action(self.session, match, 12, "ready")
        await self.ready_first_map(match)
        self.assertEqual(match.stage, "ready")
        with self.assertRaises(HTTPException):
            await match_action(self.session, match, 10, "ready", expected_revision=1)
        self.assertEqual([slot["status"] for slot in match.state["slots"][:3]], ["banned", "banned", "picked"])
        self.assertEqual({slot["selected_by_team"] for slot in match.state["slots"][:2]}, {0, 1})
        self.assertIn(match.state["slots"][2]["selected_by_team"], (0, 1))
        self.assertEqual(next(s for s in match.state["slots"] if s["id"] == "TB")["status"], "tiebreaker")
        self.assertTrue((await self.session.get(Room, match.room_id)).tournament_mode)

    async def test_pick_uses_current_server_revision_after_mapper_update(self):
        match = await self.make_match()
        updated = await self.session.get(Beatmap, 103)
        updated.checksum = "f" * 32
        self.db.add(updated)
        self.db.commit()
        await self.ready_first_map(match)
        selected = next(slot for slot in match.state["slots"] if slot["id"] == "NM3")
        self.assertEqual(selected["checksum"], "f" * 32)
        self.assertEqual(match.stage, "ready")

    async def test_started_completed_retries_are_idempotent_and_abort_skips_only_round(self):
        match = await self.make_match()
        await self.ready_first_map(match)
        item = match.state["playlist_item_id"]
        first = await room_event(self.session, match.room_id, "started", item, [10, 11], ready=[10, 11])
        again = await room_event(self.session, match.room_id, "started", item, [10, 11], ready=[10, 11])
        self.assertEqual(first["revision"], again["revision"])
        self.assertEqual(match.stage, "playing")
        await room_event(self.session, match.room_id, "aborted", item, [10, 11])
        await room_event(self.session, match.room_id, "aborted", item, [10, 11])
        self.assertEqual(match.stage, "picking")
        self.assertEqual(match.state["wins"], [0, 0])
        self.assertEqual(len(match.state["history"]), 1)
        self.assertTrue(match.state["history"][0]["forfeit"])
        self.assertEqual(match.state.get("rating_changes", []), [])

    async def test_end_is_exactly_once_and_room_closes_without_losing_history(self):
        match = await self.make_match()
        await finish_match(self.session, match, 0)
        before = deepcopy(match.state["rating_changes"])
        await finish_match(self.session, match, 1)
        self.assertEqual(match.state["rating_changes"], before)
        self.assertEqual(sum(row.games for row in self.db.exec(select(SomsaiRating)).all()), 2)
        self.assertTrue(aware((await self.session.get(Room, match.room_id)).ends_at) <= utcnow())
        self.assertIsNone((await activity(self.session, 10)).reservation_id)
        self.assertEqual((await activity(self.session, 10)).match_id, match.id)

    async def test_seed_is_once_per_mode_format_and_bounded(self):
        self.assertEqual(initial_rating(None, 0), 1000)
        self.assertEqual(initial_rating(1, 100), 2000)
        self.assertEqual(initial_rating(100, 100), 1000)
        seeded = await ensure_rating(self.session, 10, 1, 0, "1v1")
        self.assertEqual(seeded.initial_rating, 1000)
        seeded.rating = 1200
        self.assertEqual((await ensure_rating(self.session, 10, 1, 0, "1v1")).rating, 1200)

    async def test_expired_queue_does_not_revive_on_late_poll(self):
        await join_queue(self.session, 10, "1v1", 0, 0)
        self.db.exec(select(SomsaiQueue)).one().expires_at = utcnow() - timedelta(seconds=1)
        result = await public_state(self.session, 10, 0, 0)
        self.assertIsNone(result["queue"])
        self.assertIsNone((await activity(self.session, 10)).reservation_id)

    async def test_public_state_returns_twenty_compact_recent_matches(self):
        now = utcnow()
        for index in range(25):
            local_team = index % 2
            teams = [[10], [11]] if local_team == 0 else [[11], [10]]
            winner = local_team if index % 3 else 1 - local_team
            self.db.add(
                SomsaiMatch(
                    name=f"History {index}",
                    format="1v1",
                    owner_id=10,
                    pool_id=self.pool.id,
                    stage="ended",
                    ended_at=now + timedelta(seconds=index),
                    state={
                        "teams": teams,
                        "wins": [4, 2],
                        "winner_team_id": winner,
                        "rating_changes": [{"user_id": 10, "delta": index - 12}],
                    },
                )
            )
        self.db.commit()

        result = await public_state(self.session, 10, 0, 0)
        recent = result["recent_matches"]
        self.assertEqual(len(recent), 20)
        self.assertEqual(recent[0]["rating_delta"], 12)
        self.assertEqual(recent[0]["opponents"], ["fixture11"])
        self.assertEqual(recent[0]["local_team_id"], 0)
        self.assertIn(recent[0]["outcome"], {"win", "loss"})

    async def test_future_ended_tournament_room_accepts_join_and_survives_empty_disconnect(self):
        from app.dependencies.database import get_db

        match = await self.make_match()
        app = FastAPI()
        app.include_router(lio.router)
        app.dependency_overrides[get_db] = lambda: self.session
        with (
            patch.object(lio.server, "join_room_channel", AsyncMock()),
            patch.object(lio.server, "leave_room_channel", AsyncMock()),
        ):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                path = f"/_lio/multiplayer/rooms/{match.room_id}/users/10"
                response = await client.put(path, json={"password": match.password})
                self.assertEqual(response.status_code, 200, response.text)
                self.assertEqual(self.db.exec(select(RoomParticipatedUser)).one().left_at, None)
                response = await client.delete(path)
                self.assertEqual(response.status_code, 200, response.text)
                self.assertFalse(response.json()["room_ended"])
        self.assertIsNotNone(self.db.exec(select(RoomParticipatedUser)).one().left_at)
        room = await self.session.get(Room, match.room_id)
        self.assertGreater(aware(room.ends_at), utcnow())
        room.tournament_mode = False
        self.assertTrue(await lio._end_room_if_empty(self.session, room.id))
        self.assertLessEqual(aware(room.ends_at), utcnow())

    async def test_ranked_interop_cannot_create_room_or_get_playable_catalogue(self):
        before = len(self.db.exec(select(Room)).all())
        with self.assertRaises(HTTPException) as error:
            await lio._create_room(self.session, {"user_id": 10, "type": "RankedPlay"})
        self.assertEqual(error.exception.status_code, 403)
        self.assertEqual(len(self.db.exec(select(Room)).all()), before)
        self.assertFalse(await lio.get_matchmaking_pool_availability(self.session, 1))
        self.assertEqual(await lio.get_matchmaking_beatmaps(self.session, 0), [])

    async def test_signed_http_cannot_reserve_disabled_ranked_even_with_valid_signature(self):
        @asynccontextmanager
        async def transaction():
            yield self.session
            await self.session.commit()

        app = FastAPI()
        app.include_router(interop_router)
        path = f"/_lio/parties/10/reserve?timestamp={int(time.time())}"
        signature = hmac.new(b"fixture-secret", f"http://test{path}".encode(), hashlib.sha1).hexdigest()
        request_id = str(uuid4())
        with (
            patch("app.features.somsai.dependencies.settings.shared_interop_secret", "fixture-secret"),
            patch("app.features.somsai.routers.somsai_lio.somsai_transaction", transaction),
        ):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                for _ in range(2):
                    response = await client.post(
                        path, json={"request_id": request_id, "pool_id": 1}, headers={"X-LIO-Signature": signature}
                    )
                    self.assertEqual(response.status_code, 403, response.text)
                response = await client.post(
                    path, json={"request_id": "invalid", "pool_id": 1}, headers={"X-LIO-Signature": signature}
                )
                self.assertEqual(response.status_code, 422)
        self.assertEqual(len(self.db.exec(select(SomsaiReservation)).all()), 0)

    async def test_round_filters_mode_roster_and_duplicate_attempts_then_replays_tie(self):
        match = await self.make_match()
        await self.ready_first_map(match)
        item = match.state["playlist_item_id"]
        await room_event(self.session, match.room_id, "started", item, [10, 11], ready=[10, 11])
        self.add_score(match, 10, 999999, GameMode.TAIKO)
        self.add_score(match, 12, 999999)
        good = self.add_score(match, 10, 500000)
        self.add_score(match, 10, 999999)
        await room_event(self.session, match.room_id, "completed", item, [10, 11])
        self.assertFalse(await settle_round(self.session, match))
        self.add_score(match, 11, 500000)
        self.assertTrue(await settle_round(self.session, match))
        self.assertEqual(match.state["wins"], [0, 0])
        self.assertEqual(match.state["history"][0]["players"][0]["score_id"], good.id)
        self.assertEqual(match.state["history"][0]["team_scores"], [500000, 500000])
        self.assertEqual(match.state["playlist_item_id"], item + 1)
        self.assertEqual(match.stage, "ready")

    async def test_full_bo7_reaches_tb_and_persists_one_rating_update_after_restart(self):
        match = await self.make_match()
        await self.ready_first_map(match)
        for round_index, winner in enumerate((0, 1, 0, 1, 0, 1, 0)):
            if match.stage == "picking":
                slot = next(slot["id"] for slot in match.state["slots"] if slot["status"] == "available")
                await match_action(
                    self.session, match, match.state["captains"][match.state["turn_team"]], "pick", slot_id=slot
                )
            if round_index == 6:
                self.assertEqual(match.state["current_slot"], "TB")
            for uid in (10, 11):
                await match_action(self.session, match, uid, "ready")
            item = match.state["playlist_item_id"]
            await room_event(self.session, match.room_id, "started", item, [10, 11], ready=[10, 11])
            for team, uid in enumerate((10, 11)):
                self.add_score(match, uid, 600000 if winner == team else 400000)
            await room_event(self.session, match.room_id, "completed", item, [10, 11])
            self.assertTrue(await settle_round(self.session, match))
            match_id = match.id
            self.db.commit()
            self.db.expunge_all()
            match = self.db.get(SomsaiMatch, match_id)
        self.assertEqual(match.stage, "ended")
        self.assertEqual(match.state["wins"], [4, 3])
        self.assertEqual(len(match.state["history"]), 7)
        self.assertEqual(len(match.state["rating_changes"]), 2)
        self.assertEqual(sum(row.games for row in self.db.exec(select(SomsaiRating)).all()), 2)
        self.assertFalse(await settle_round(self.session, match))
        self.assertEqual(sum(row.games for row in self.db.exec(select(SomsaiRating)).all()), 2)


class InteropSecurityTests(unittest.IsolatedAsyncioTestCase):
    async def test_missing_forged_and_expired_signatures_rejected_before_db(self):
        app = FastAPI()
        app.include_router(interop_router)
        with patch("app.features.somsai.dependencies.settings.shared_interop_secret", "fixture-secret"):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                for timestamp, secret in ((int(time.time()), "wrong"), (1, "fixture-secret")):
                    path = f"/_lio/parties/10?timestamp={timestamp}"
                    signature = hmac.new(secret.encode(), f"http://test{path}".encode(), hashlib.sha1).hexdigest()
                    response = await client.get(path, headers={"X-LIO-Signature": signature})
                    self.assertEqual(response.status_code, 403)
                self.assertEqual((await client.get("/_lio/parties/10")).status_code, 403)
