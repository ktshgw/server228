"""Lobby read models use real isolated SQL and check private-party boundaries."""

# ruff: noqa: PT009, PT027
import json
import unittest
from unittest.mock import AsyncMock

from app.features.somsai.database.somsai import SomsaiPartyInvite
from app.features.somsai.services.somsai_lobby_service import leaderboard, party_chat
from app.features.somsai.services.somsai_party_service import party_action
from app.features.somsai.services.somsai_rating_service import ensure_rating
from app.features.somsai.services.somsai_service import public_state

from fastapi import HTTPException
from sqlmodel import select
import test_somsai_core as core


class SomsaiLobbyTests(unittest.IsolatedAsyncioTestCase):
    setUp = core.SomsaiCoreTests.setUp

    async def test_pinned_self_and_order_are_format_specific(self):
        for uid, value in ((10, 1500), (11, 2900), (12, 1500), (2, 5000)):
            row = await ensure_rating(self.session, uid, 0, 0, "1v1")
            row.rating = value
            self.db.add(row)
        other = await ensure_rating(self.session, 10, 0, 0, "2v2")
        other.rating = 2100
        self.db.add(other)
        self.db.flush()
        result = await leaderboard(self.session, 10, 0, 0, "1v1", 1)
        self.assertEqual([e["user"]["id"] for e in result["items"]], [11, 10, 12])
        self.assertEqual(result["self"]["rank"], 2)
        self.assertEqual(result["self"]["rating"], 1500)
        other = await leaderboard(self.session, 10, 0, 0, "2v2", 1)
        self.assertEqual(other["self"]["rating"], 2100)
        empty = await leaderboard(self.session, 10, 0, 0, "1v1", 2)
        self.assertEqual(empty["items"], [])
        self.assertEqual(empty["self"]["rank"], 2)

    async def test_member_ratings_and_private_chat_access(self):
        await party_action(self.session, 10, "party_invite", target_id=11)
        self.db.flush()
        invite = self.db.exec(select(SomsaiPartyInvite)).one()
        await party_action(self.session, 11, "party_accept", invitation_id=invite.id)
        self.db.flush()
        state = await public_state(self.session, 10, 0, 0)
        self.assertEqual(len(state["party"]["members"]), 2)
        self.assertEqual(set(state["party"]["members"][1]["ratings"]), {"1v1", "2v2"})
        redis = AsyncMock()
        redis.lrange.return_value = [json.dumps({"message_id": 1, "content": "private"})]
        member = await party_chat(self.session, redis, 11)
        self.assertEqual(member["messages"][0]["content"], "private")
        redis.reset_mock()
        outsider = await party_chat(self.session, redis, 12)
        self.assertEqual(outsider["messages"], [])
        redis.lrange.assert_not_called()
        with self.assertRaises(HTTPException):
            await party_chat(self.session, redis, 12, "cannot post")
        await party_action(self.session, 11, "party_leave")
        self.assertEqual((await party_chat(self.session, redis, 11))["messages"], [])

    async def test_chat_rejects_empty_long_and_flood_messages(self):
        await party_action(self.session, 10, "party_create")
        self.db.flush()
        redis = AsyncMock()
        for message in ("  ", "a" * 501):
            with self.assertRaises(HTTPException) as failure:
                await party_chat(self.session, redis, 10, message)
            self.assertEqual(failure.exception.status_code, 422)
        redis.set.return_value = False
        with self.assertRaises(HTTPException) as failure:
            await party_chat(self.session, redis, 10, "hello")
        self.assertEqual(failure.exception.status_code, 429)
