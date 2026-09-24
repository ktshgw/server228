from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from app.database import Beatmapset
from app.models.score import GameMode, Rank
from app.router.private.web_site import get_web_beatmap_scores, get_web_beatmapset


class _ReadOnlySession:
    def __init__(self) -> None:
        self.lookups: list[tuple[object, int]] = []

    async def get(self, model, identifier):
        self.lookups.append((model, identifier))
        return None

    async def exec(self, *_args, **_kwargs):
        raise AssertionError("A missing local beatmapset must not trigger a database write or query")

    async def commit(self) -> None:
        raise AssertionError("Viewing beatmap details must never commit")


class _DetailFetcher:
    async def get_beatmapset(self, beatmapset_id: int):
        return {
            "id": beatmapset_id,
            "artist": "Artist",
            "title": "Title",
            "creator": "Mapper",
            "status": "ranked",
            "ranked": 1,
            "bpm": 180.0,
            "play_count": 10,
            "favourite_count": 2,
            "covers": {"cover": "https://assets.ppy.sh/example.jpg"},
            "preview_url": "https://b.ppy.sh/preview/123.mp3",
            "description": {"description": "Description"},
            "source": "",
            "tags": "test",
            "beatmaps": [
                {
                    "id": 456,
                    "version": "Hard",
                    "mode": "osu",
                    "difficulty_rating": 4.2,
                    "status": "ranked",
                    "ranked": 1,
                }
            ],
        }


class WebSiteBeatmapDetailTests(unittest.IsolatedAsyncioTestCase):
    def test_upstream_description_is_flattened_into_a_text_only_sink(self) -> None:
        app_source = (Path(__file__).resolve().parents[1] / "static" / "site" / "app.js").read_text(encoding="utf-8")

        assert 'new DOMParser().parseFromString(String(source), "text/html")' in app_source
        assert '"SCRIPT", "STYLE", "SVG", "TEMPLATE"' in app_source
        assert '$("#beatmap-detail-description").textContent = description' in app_source

    async def test_upstream_detail_view_does_not_materialise_database_rows(self) -> None:
        session = _ReadOnlySession()

        response = await get_web_beatmapset(
            beatmapset_id=123,
            session=session,  # type: ignore[arg-type]
            fetcher=_DetailFetcher(),  # type: ignore[arg-type]
        )

        assert response["id"] == 123
        assert response["beatmaps"][0]["id"] == 456
        assert response["local_policy"] is None
        assert session.lookups == [(Beatmapset, 123)]

    async def test_local_leaderboard_exposes_download_only_for_stored_replay(self) -> None:
        user = SimpleNamespace(
            id=7,
            server_id=1,
            username="player",
            avatar_url=None,
            country_code="RU",
            is_online=False,
            is_owner=False,
            is_admin=False,
            is_gmt=False,
            is_bng=False,
            is_qat=False,
            is_supporter=False,
            join_date=None,
            last_visit=None,
            soms_playmode=GameMode.OSU,
            location=None,
            interests=None,
            occupation=None,
            discord=None,
            website=None,
            page={},
        )
        score = SimpleNamespace(
            id=99,
            pp=321.0,
            total_score=1_000_000,
            accuracy=0.99,
            max_combo=999,
            rank=Rank.S,
            mods=[],
            ended_at=None,
            has_replay=True,
            ranked=True,
            passed=True,
            n300=99,
            n100=1,
            n50=0,
            nmiss=0,
            ngeki=0,
            nkatu=0,
        )
        session = SimpleNamespace(
            info={"negative_pp_score_counts": {}},
            exec=AsyncMock(
                side_effect=[
                    SimpleNamespace(all=lambda: []),
                ]
            ),
        )

        board = {"rows": [(score, user, 1)], "top": (score, user, 1), "personal": None, "total": 1, "mode": "osu"}
        with (
            patch("app.service.web_beatmap_leaderboard_service.leaderboard_page", AsyncMock(return_value=board)),
            patch("app.router.private.web_site._optional_web_user", AsyncMock(return_value=None)),
        ):
            response = await get_web_beatmap_scores(123, session, None, None)  # type: ignore[arg-type]

        replay = response["items"][0]["score"]
        assert replay["has_replay"] is True
        assert replay["replay_url"] == "/api/v2/scores/99/download"
        assert response["top_score"]["score"]["statistics"]["great"] == 99


if __name__ == "__main__":
    unittest.main()
