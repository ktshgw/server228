"""Exercise the HTTP contract actually sent by spectator's SharedInteropRoom."""

from typing import Any
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.const import BANCHOBOT_ID
from app.database import Room
from app.dependencies.database import get_db
from app.models.room import MatchType, QueueMode
from app.router import lio

from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient


class LioRoomCreationContractTests(unittest.IsolatedAsyncioTestCase):
    async def create_through_http(self, payload: dict[str, Any]) -> tuple[Room, AsyncMock]:
        def assign_database_id(room: Room) -> None:
            room.id = 42

        session = Mock(
            add=Mock(),
            commit=AsyncMock(),
            refresh=AsyncMock(side_effect=assign_database_id),
            get=AsyncMock(return_value=None),
        )
        application = FastAPI()
        application.include_router(lio.router)
        application.dependency_overrides[get_db] = lambda: session
        # Isolate persistence and chat delivery, but keep the real HTTP route,
        # payload selection, enum parser and Room construction in the test.
        with (
            patch.object(lio, "_validate_user_exists", AsyncMock()),
            patch.object(lio, "_ensure_room_chat_channel", AsyncMock()),
            patch.object(lio, "_add_playlist_items", AsyncMock()) as add_playlist,
        ):
            async with AsyncClient(transport=ASGITransport(app=application), base_url="http://test") as client:
                response = await client.post("/_lio/multiplayer/rooms", json=payload)
            assert response.status_code == 200, response.text
            assert response.json() == 42
        session.add.assert_called_once()
        return session.add.call_args.args[0], add_playlist

    async def test_shared_interop_canonical_ranked_type_reaches_database(self) -> None:
        room, add_playlist = await self.create_through_http(
            {
                "name": "SOMS! Ranked test",
                "user_id": BANCHOBOT_ID,
                "type": "ranked_play",
                "queue_mode": "host_only",
                "initial_playlist": [],
                "tournament_mode": True,
            }
        )
        assert room.type == MatchType.RANKED_PLAY
        assert room.queue_mode == QueueMode.HOST_ONLY
        assert room.host_id == BANCHOBOT_ID
        add_playlist.assert_not_awaited()

    async def test_canonical_type_takes_precedence_over_legacy_alias(self) -> None:
        room, _ = await self.create_through_http(
            {"user_id": BANCHOBOT_ID, "type": "ranked_play", "match_type": "Matchmaking"}
        )
        assert room.type == MatchType.RANKED_PLAY

    async def test_legacy_ranked_alias_is_still_supported(self) -> None:
        room, _ = await self.create_through_http({"user_id": BANCHOBOT_ID, "match_type": "RankedPlay"})
        assert room.type == MatchType.RANKED_PLAY

    async def test_other_canonical_types_are_preserved(self) -> None:
        for serialized, expected in (
            ("matchmaking", MatchType.MATCHMAKING),
            ("head_to_head", MatchType.HEAD_TO_HEAD),
            ("team_versus", MatchType.TEAM_VERSUS),
        ):
            with self.subTest(type=serialized):
                room, add_playlist = await self.create_through_http({"user_id": BANCHOBOT_ID, "type": serialized})
                assert room.type == expected
                if expected == MatchType.MATCHMAKING:
                    add_playlist.assert_not_awaited()
                else:
                    add_playlist.assert_awaited_once()

    async def test_omitted_type_preserves_bot_and_player_defaults(self) -> None:
        for owner, expected in ((BANCHOBOT_ID, MatchType.MATCHMAKING), (1234, MatchType.HEAD_TO_HEAD)):
            with self.subTest(owner=owner):
                room, _ = await self.create_through_http({"user_id": owner})
                assert room.type == expected
