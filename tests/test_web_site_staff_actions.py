from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch
from urllib.parse import urlsplit

from app.config import settings
from app.database import AdminAuditEvent, Score, User
from app.models.beatmap import BeatmapRankStatus
from app.models.score import GameMode
from app.router.private.web_site import (
    WebBeatmapModerationRequest,
    WebContext,
    delete_web_score,
    moderate_web_beatmapset,
)
from app.service.admin_score_service import ScoreDeletionResult

from fastapi import HTTPException, Request


class _MutationContext:
    def __init__(self, session: object) -> None:
        self.session = session

    async def __aenter__(self) -> object:
        return self.session

    async def __aexit__(self, *_args: object) -> None:
        return None


def _csrf_value() -> str:
    return "test-" + "csrf-token"


def _request(method: str = "POST") -> Request:
    parsed = urlsplit(str(settings.server_url))
    origin = f"{parsed.scheme}://{parsed.netloc}"
    return Request(
        {
            "type": "http",
            "method": method,
            "path": "/",
            "headers": [(b"origin", origin.encode()), (b"x-csrf-token", _csrf_value().encode())],
            "client": ("127.0.0.1", 1234),
        }
    )


def _context(*, admin: bool = False, bng: bool = False) -> WebContext:
    user = SimpleNamespace(
        id=9,
        username="operator",
        is_owner=False,
        is_admin=admin,
        is_bng=bng,
        is_restricted=AsyncMock(return_value=False),
    )
    return WebContext(user=user, session=SimpleNamespace(csrf_token=_csrf_value()))  # type: ignore[arg-type]


class WebSiteBeatmapModerationTests(unittest.IsolatedAsyncioTestCase):
    async def test_bng_can_love_a_set_with_leaderboard_but_without_pp(self) -> None:
        request_session = SimpleNamespace()
        mutation_session = SimpleNamespace()
        with (
            patch("app.router.private.web_site._refresh_web_ranking_target", new_callable=AsyncMock) as refresh,
            patch("app.router.private.web_site.with_db", return_value=_MutationContext(mutation_session)),
            patch("app.router.private.web_site.apply_local_rank", new_callable=AsyncMock) as apply_rank,
        ):
            result = await moderate_web_beatmapset(
                123,
                WebBeatmapModerationRequest(action="love"),
                _request(),
                _context(bng=True),
                request_session,  # type: ignore[arg-type]
            )

        refresh.assert_awaited_once_with(123)
        apply_rank.assert_awaited_once_with(
            mutation_session,
            actor_user_id=9,
            beatmapset_id=123,
            status=BeatmapRankStatus.LOVED,
            leaderboard_enabled=True,
            pp_enabled=False,
            replace_difficulty_overrides=True,
            reason="Website love action by operator",
        )
        assert result["status"] == BeatmapRankStatus.LOVED
        assert result["leaderboard_enabled"] is True
        assert result["pp_enabled"] is False

    async def test_non_bng_specialist_cannot_moderate_beatmaps(self) -> None:
        context = _context()
        context.user.is_qat = True  # type: ignore[attr-defined]

        caught: HTTPException | None = None
        try:
            await moderate_web_beatmapset(
                123,
                WebBeatmapModerationRequest(action="rank"),
                _request(),
                context,
                SimpleNamespace(),  # type: ignore[arg-type]
            )
        except HTTPException as exc:
            caught = exc

        assert caught is not None
        assert caught.status_code == 403


class WebSiteScoreDeletionTests(unittest.IsolatedAsyncioTestCase):
    async def test_administrator_deletion_uses_full_score_cleanup_and_removes_replay(self) -> None:
        score = SimpleNamespace(id=77, user_id=15)
        target = SimpleNamespace(id=15)
        request_session = SimpleNamespace(
            get=AsyncMock(side_effect=[score, target]),
            add=Mock(),
            commit=AsyncMock(),
        )
        storage = SimpleNamespace(delete_file=AsyncMock())
        cache_service = SimpleNamespace()
        redis = SimpleNamespace()
        deletion = ScoreDeletionResult(
            score_id=77,
            ruleset=GameMode.OSU,
            replay_paths=("replays/77.osr",),
            score_data=SimpleNamespace(),  # type: ignore[arg-type]
        )

        with (
            patch("app.router.private.web_site.delete_user_score", new=AsyncMock(return_value=deletion)) as remove,
            patch(
                "app.router.private.web_site._invalidate_deleted_score_caches",
                new=AsyncMock(),
            ) as invalidate,
            patch("app.router.private.web_site.ScoreDeletedEvent", return_value=SimpleNamespace()) as event,
            patch("app.router.private.web_site.hub.emit") as emit,
        ):
            result = await delete_web_score(
                77,
                _request("DELETE"),
                _context(admin=True),
                request_session,  # type: ignore[arg-type]
                redis,  # type: ignore[arg-type]
                storage,  # type: ignore[arg-type]
                cache_service,  # type: ignore[arg-type]
            )

        request_session.get.assert_any_await(Score, 77)
        request_session.get.assert_any_await(User, 15)
        remove.assert_awaited_once_with(request_session, redis, target, 77)
        audit = request_session.add.call_args.args[0]
        assert isinstance(audit, AdminAuditEvent)
        assert audit.actor_user_id == 9
        assert audit.target_id == "77"
        request_session.commit.assert_awaited_once()
        storage.delete_file.assert_awaited_once_with("replays/77.osr")
        invalidate.assert_awaited_once_with(cache_service, redis, 15)
        event.assert_called_once_with(score=deletion.score_data)
        emit.assert_called_once()
        assert result == {"deleted_score_id": 77}

    async def test_bng_cannot_delete_scores(self) -> None:
        request_session = SimpleNamespace(get=AsyncMock())

        caught: HTTPException | None = None
        try:
            await delete_web_score(
                77,
                _request("DELETE"),
                _context(bng=True),
                request_session,  # type: ignore[arg-type]
                SimpleNamespace(),  # type: ignore[arg-type]
                SimpleNamespace(),  # type: ignore[arg-type]
                SimpleNamespace(),  # type: ignore[arg-type]
            )
        except HTTPException as exc:
            caught = exc

        assert caught is not None
        assert caught.status_code == 403
        request_session.get.assert_not_awaited()


if __name__ == "__main__":
    unittest.main()
