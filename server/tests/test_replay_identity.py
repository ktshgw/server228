from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import User
from app.models.score import GameMode
from app.router.v2.score import download_score_replay
from app.service.score_import_service import (
    ServerReplayScoreMetadata,
    _embedded_osr_score_metadata,
    _osr_replay_data_end,
    parse_uploaded_osr,
)
from app.storage import StorageService
from main import get_user_avatar_root
from tests.test_score_import_service import osr_file

from fastapi.responses import FileResponse, RedirectResponse
from sqlmodel.ext.asyncio.session import AsyncSession


class DownloadedReplayIdentityTests(unittest.IsolatedAsyncioTestCase):
    async def test_client_score_download_embeds_local_identity_without_import_provenance(self) -> None:
        owner = SimpleNamespace(id=1_500_000_001, username="mindblock")
        score = SimpleNamespace(
            id=321,
            user_id=owner.id,
            user=owner,
            has_replay=True,
            replay_filename="replays/client.osr",
            gamemode=GameMode.OSU,
            ended_at=datetime(2026, 9, 5, tzinfo=UTC),
            beatmap_id=123,
        )
        metadata = ServerReplayScoreMetadata(
            online_score_id=score.id,
            user_id=owner.id,
            client_version="2026.804.2",
            rank="A",
            mods=[{"acronym": "DT", "settings": {"speed_change": 1.3}}],
            statistics={"great": 300},
            maximum_statistics={"great": 308},
            total_score_without_mods=765432,
        )
        original = osr_file(username=owner.username)
        db = Mock(spec=AsyncSession)
        db.exec = AsyncMock(return_value=SimpleNamespace(first=lambda: score))
        db.get = AsyncMock(
            return_value=SimpleNamespace(version="Insane", beatmapset=SimpleNamespace(artist="Artist", title="Title"))
        )
        storage = Mock(spec=StorageService)
        storage.is_exists = AsyncMock(return_value=True)
        storage.read_file = AsyncMock(return_value=original)

        with (
            patch.object(ServerReplayScoreMetadata, "from_score", return_value=metadata),
            patch("app.router.v2.score.hub.emit"),
        ):
            response = await download_score_replay(score.id, db, cast(Any, owner), storage)

        content = bytes(response.body)
        embedded = _embedded_osr_score_metadata(content)
        assert embedded["user_id"] == owner.id
        assert embedded["online_id"] == score.id
        assert embedded["mods"] == metadata.mods
        assert parse_uploaded_osr(content).header.player_name == owner.username
        # Only the format version and appended score metadata change: no frames,
        # hit counts, score, life bar, or timestamp may be rewritten.
        assert content[5 : _osr_replay_data_end(content)] == original[5 : _osr_replay_data_end(original)]
        db.exec.assert_awaited_once()
        storage.write_file.assert_not_called()


class ClientAvatarEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_default_avatar_uses_the_existing_site_asset(self) -> None:
        for avatar_url in (
            "",
            "https://lazer.g0v0.top/default.jpg",
            "https://lazer-data.g0v0.top/default.jpg",
        ):
            with self.subTest(avatar_url=avatar_url):
                user = SimpleNamespace(
                    avatar_url=avatar_url, is_active=True, is_restricted=AsyncMock(return_value=False)
                )
                db = Mock(spec=AsyncSession)
                db.get = AsyncMock(return_value=user)
                response = await get_user_avatar_root(1_500_000_001, db)
                assert isinstance(response, FileResponse)
                assert Path(response.path).name == "soms-default-avatar.png"
                assert response.media_type == "image/png"
                db.get.assert_awaited_once_with(User, 1_500_000_001)

    async def test_uploaded_avatar_is_preserved(self) -> None:
        url = "http://127.0.0.1:8000/files/avatars/user-hash.png"
        user = SimpleNamespace(avatar_url=url, is_active=True, is_restricted=AsyncMock(return_value=False))
        db = Mock(spec=AsyncSession)
        db.get = AsyncMock(return_value=user)
        response = await get_user_avatar_root(1_500_000_001, db)
        assert isinstance(response, RedirectResponse)
        assert response.headers["location"] == url


if __name__ == "__main__":
    unittest.main()
