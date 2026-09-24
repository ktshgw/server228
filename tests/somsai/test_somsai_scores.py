"""SOMSAI score admission in isolated SQL, including native route boundaries."""

# The project uses unittest rather than pytest.
# ruff: noqa: PT027
from copy import deepcopy
from functools import partial
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import Playlist, User
from app.features.somsai.database.somsai import SomsaiMatch
from app.models.mods import APIMod, init_mods
from app.models.room import RoomCategory
from app.models.score import GameMode, Rank, SoloScoreSubmissionInfo
from app.router.v2.score import _submit_playlist_score_once, create_playlist_score
from app.features.somsai.services.somsai_score_service import (
    validate_somsai_score_submission as submit_guard,
    validate_somsai_score_token,
)
from tests.test_ranked_elo_admin import AsyncTestSession

from fastapi import BackgroundTasks, HTTPException
from sqlalchemy.dialects import mysql
from sqlmodel import Session, SQLModel, create_engine
from sqlmodel.ext.asyncio.session import AsyncSession

validate_somsai_score_submission = partial(submit_guard, ruleset_id=0)


class SomsaiScoreTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        init_mods()
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(self.engine, tables=[SQLModel.metadata.tables[SomsaiMatch.__tablename__]])
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        self.match = SomsaiMatch(
            id=1,
            name="Score admission test",
            format="2v2",
            owner_id=7,
            pool_id=1,
            room_id=42,
            stage="playing",
            state={
                "teams": [[7, 9], [8, 10]],
                "current_slot": "DT1",
                "playlist_item_id": 1,
                "slots": [
                    {"id": "DT1", "beatmap_id": 100, "category": "DT", "mods": [{"acronym": "DT"}]},
                    {"id": "NM1", "beatmap_id": 101, "category": "NM", "mods": []},
                    {"id": "FM1", "beatmap_id": 102, "category": "FM", "mods": []},
                    {"id": "TB", "beatmap_id": 103, "category": "TB", "mods": []},
                    {"id": "HD1", "beatmap_id": 104, "category": "HD", "mods": [{"acronym": "HD"}]},
                ],
            },
        )
        self.db.add(self.match)
        self.db.commit()

    def item(self, beatmap_id: int = 100) -> Playlist:
        # Legacy rooms created before NoFail was removed must still finish with their issued mods.
        return Playlist(
            id=1, room_id=42, beatmap_id=beatmap_id, ruleset_id=0, owner_id=1, required_mods=[{"acronym": "NF"}]
        )

    async def test_new_rounds_accept_dt_and_freemods_without_nofail(self) -> None:
        for beatmap_id, mods in (
            (100, [{"acronym": "DT"}]),
            (101, []),
            (102, [{"acronym": "HD"}, {"acronym": "HR"}]),
            (103, []),
        ):
            item = self.item(beatmap_id)
            item.required_mods = []
            await validate_somsai_score_submission(self.session, 42, item, 7, mods)
            with self.assertRaises(HTTPException):
                await validate_somsai_score_submission(self.session, 42, item, 7, [{"acronym": "NF"}, *mods])

    async def test_only_playing_current_map_issues_roster_tokens(self) -> None:
        for user_id in (7, 8, 9, 10):
            await validate_somsai_score_token(self.session, 42, 1, user_id)
        for stage in ("waiting", "banning", "picking", "ready", "results", "ended", "cancelled"):
            with self.subTest(stage=stage):
                self.match.stage = stage
                self.db.commit()
                with self.assertRaises(HTTPException) as error:
                    await validate_somsai_score_token(self.session, 42, 1, 7)
                assert error.exception.status_code == 409

    async def test_token_rejects_outsider_and_wrong_playlist(self) -> None:
        for playlist_id, user_id, expected in ((1, 999, 403), (0, 7, 409), (2, 7, 409)):
            with self.subTest(playlist_id=playlist_id, user_id=user_id):
                with self.assertRaises(HTTPException) as error:
                    await validate_somsai_score_token(self.session, 42, playlist_id, user_id)
                assert error.exception.status_code == expected

    async def test_non_somsai_room_is_untouched(self) -> None:
        await validate_somsai_score_token(self.session, 99, 0, 999)
        await validate_somsai_score_submission(self.session, 99, self.item(), 999, [{"acronym": "AT"}])

    async def test_submission_cannot_change_the_token_ruleset(self) -> None:
        with self.assertRaises(HTTPException) as error:
            await validate_somsai_score_submission(
                self.session, 42, self.item(), 7, [{"acronym": "NF"}, {"acronym": "DT"}], ruleset_id=1
            )
        assert error.exception.status_code == 422

    async def test_snapshot_reads_never_add_lock_order_or_joins(self) -> None:
        await validate_somsai_score_token(self.session, 42, 1, 7)
        await validate_somsai_score_submission(self.session, 42, self.item(), 7, [{"acronym": "NF"}, {"acronym": "DT"}])
        for query in self.session.statements:
            sql = str(query.compile(dialect=mysql.dialect())).upper()
            assert "FOR UPDATE" not in sql
            assert "JOIN" not in sql
            assert "SOMSAI_LOCK" not in sql
            assert "SOMSAI_MATCHES" in sql

    async def test_native_default_dt_settings_and_pitch_are_accepted(self) -> None:
        cases: list[dict[str, bool | float | str | int]] = [
            {},
            {"speed_change": 1.5},
            {"speed_change": 1.5, "adjust_pitch": False},
            {"adjust_pitch": True},
        ]
        for settings in cases:
            with self.subTest(settings=settings):
                await validate_somsai_score_submission(
                    self.session,
                    42,
                    self.item(),
                    7,
                    [{"acronym": "NF"}, {"acronym": "DT", "settings": settings}],
                )

    async def test_required_and_allowed_mods_are_enforced(self) -> None:
        invalid: list[list[APIMod]] = [
            [{"acronym": "DT"}],
            [{"acronym": "NF"}],
            [{"acronym": "NF"}, {"acronym": "DT"}, {"acronym": "HD"}],
            [{"acronym": "NF"}, {"acronym": "DT"}, {"acronym": "AT"}],
            [{"acronym": "NF"}, {"acronym": "DT"}, {"acronym": "DA"}],
            [{"acronym": "NF"}, {"acronym": "DT"}, {"acronym": "DT"}],
            [{"acronym": "NF"}, {"acronym": "DT", "settings": {"speed_change": 1.3}}],
            [{"acronym": "NF"}, {"acronym": "DT", "settings": {"speed_change": True}}],
        ]
        for mods in invalid:
            with self.subTest(mods=mods), self.assertRaises(HTTPException):
                await validate_somsai_score_submission(self.session, 42, self.item(), 7, mods)

    async def test_freemod_and_tiebreaker_allow_only_hd_hr_with_nf(self) -> None:
        choices: list[list[APIMod]] = [
            [],
            [{"acronym": "HD"}],
            [{"acronym": "HR"}],
            [{"acronym": "HD"}, {"acronym": "HR"}],
        ]
        for beatmap_id in (102, 103):
            for mods in choices:
                await validate_somsai_score_submission(
                    self.session, 42, self.item(beatmap_id), 7, [{"acronym": "NF"}, *mods]
                )
            with self.assertRaises(HTTPException):
                await validate_somsai_score_submission(
                    self.session, 42, self.item(beatmap_id), 7, [{"acronym": "NF"}, {"acronym": "DT"}]
                )

    async def test_hidden_default_is_accepted_but_visibility_override_is_rejected(self) -> None:
        await validate_somsai_score_submission(
            self.session,
            42,
            self.item(104),
            7,
            [{"acronym": "NF"}, {"acronym": "HD", "settings": {"only_fade_approach_circles": False}}],
        )
        with self.assertRaises(HTTPException):
            await validate_somsai_score_submission(
                self.session,
                42,
                self.item(104),
                7,
                [{"acronym": "NF"}, {"acronym": "HD", "settings": {"only_fade_approach_circles": True}}],
            )

    async def test_late_previous_map_score_is_saved_after_match_ends(self) -> None:
        state = deepcopy(self.match.state)
        state.update(playlist_item_id=2, current_slot="NM1", settled=True)
        self.match.state = state
        self.match.stage = "ended"
        self.db.commit()
        await validate_somsai_score_submission(
            self.session, 42, self.item(100), 7, [{"acronym": "NF"}, {"acronym": "DT"}]
        )
        assert self.match.stage == "ended"
        assert self.match.state == state
        with self.assertRaises(HTTPException):
            await validate_somsai_score_submission(self.session, 42, self.item(999), 7, [{"acronym": "NF"}])


class SomsaiScoreRouteTests(unittest.IsolatedAsyncioTestCase):
    async def test_token_guard_runs_before_issuance(self) -> None:
        session = Mock(spec=AsyncSession)
        session.get = AsyncMock(return_value=SimpleNamespace(category=RoomCategory.REALTIME, ends_at=None))
        session.exec = AsyncMock(return_value=SimpleNamespace(first=lambda: SimpleNamespace(id=1)))
        verification: Any = SimpleNamespace(
            validate_client_version=AsyncMock(return_value=SimpleNamespace(version="test"))
        )
        user = cast(User, SimpleNamespace(id=7, is_restricted=AsyncMock(return_value=False)))
        with (
            patch.object(GameMode, "check_ruleset_version", return_value=True),
            patch(
                "app.router.v2.score.validate_somsai_score_token", AsyncMock(side_effect=HTTPException(403, "guard"))
            ) as guard,
            self.assertRaises(HTTPException),
        ):
            await create_playlist_score(session, Mock(), Mock(), 42, 1, verification, 100, "a" * 32, 0, user)
        guard.assert_awaited_once_with(session, 42, 1, 7)
        session.add.assert_not_called()

    async def test_submission_guard_runs_before_persisting_score(self) -> None:
        init_mods()
        room = SimpleNamespace(category=RoomCategory.REALTIME)
        item = SimpleNamespace(id=1)
        token = SimpleNamespace(user_id=7, playlist_item_id=1, room_id=42)
        session = Mock(spec=AsyncSession)
        session.exec = AsyncMock(
            side_effect=[SimpleNamespace(first=lambda value=value: value) for value in (room, item, token)]
        )
        user = cast(User, SimpleNamespace(id=7, is_restricted=AsyncMock(return_value=False)))
        info = SoloScoreSubmissionInfo(
            rank=Rank.A, total_score=1000, total_score_without_mods=1000, accuracy=1, ruleset_id=0
        )
        with (
            patch(
                "app.router.v2.score.validate_somsai_score_submission",
                AsyncMock(side_effect=HTTPException(422, "guard")),
            ) as guard,
            patch("app.router.v2.score.submit_score", AsyncMock()) as save,
            self.assertRaises(HTTPException),
        ):
            await _submit_playlist_score_once(
                Mock(spec=BackgroundTasks), session, 42, 1, 123, info, user, Mock(), Mock()
            )
        guard.assert_awaited_once_with(session, 42, item, 7, info.mods, ruleset_id=0)
        save.assert_not_awaited()
