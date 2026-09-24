import asyncio
from contextlib import asynccontextmanager
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database import Score, ScoreImport
from app.dependencies.staff import StaffRole, has_staff_role
from app.models.beatmap import BeatmapRankStatus
from app.models.mods import init_mods
from app.models.score import GameMode
from app.router.private.admin_panel import (
    ProfileClearRequest,
    RankingMutationRequest,
    ScoreImportPreviewRequest,
    ScoreImportRequest,
    UserUpdateRequest,
    _can_access_admin_panel,
    _finalise_import_replay,
    _public_user,
    _role_flags,
    _session_digest,
)

from pydantic import ValidationError


def assert_raises(expected: type[BaseException], action: Any) -> None:
    try:
        action()
    except expected:
        return
    raise AssertionError(f"Expected {expected.__name__} to be raised")


def staff(**overrides: Any) -> Any:
    values = {
        "id": 42,
        "server_id": 7,
        "username": "tester",
        "email": "private@example.invalid",
        "country_code": "XX",
        "is_active": True,
        "is_online": False,
        "is_supporter": False,
        "is_bot": False,
        "is_owner": False,
        "is_admin": False,
        "is_gmt": False,
        "is_qat": False,
        "is_bng": False,
        "join_date": None,
        "last_visit": None,
        "pw_bcrypt": "must-never-leak",
    }
    values.update(overrides)
    return SimpleNamespace(**values)


class AdminPanelRoleTests(unittest.TestCase):
    def test_owner_inherits_every_capability(self) -> None:
        assert all(_role_flags(staff(is_owner=True)).values())

    def test_specialist_roles_stay_separate(self) -> None:
        ranker = _role_flags(staff(is_bng=True))
        moderator = _role_flags(staff(is_gmt=True))

        assert ranker["ranker"]
        assert not ranker["administrator"]
        assert not ranker["moderator"]
        assert moderator["moderator"]
        assert not moderator["ranker"]

    def test_only_administrators_and_owner_can_enter_standalone_panel(self) -> None:
        assert _can_access_admin_panel(staff(is_owner=True))
        assert _can_access_admin_panel(staff(is_admin=True))
        assert not _can_access_admin_panel(staff(is_bng=True))
        assert not _can_access_admin_panel(staff(is_qat=True))
        assert not _can_access_admin_panel(staff(is_gmt=True))

    def test_native_beatmap_moderation_is_limited_to_bng_and_administrators(self) -> None:
        assert has_staff_role(staff(is_owner=True), StaffRole.BEATMAP_MODERATOR)
        assert has_staff_role(staff(is_admin=True), StaffRole.BEATMAP_MODERATOR)
        assert has_staff_role(staff(is_bng=True), StaffRole.BEATMAP_MODERATOR)
        assert not has_staff_role(staff(is_qat=True), StaffRole.BEATMAP_MODERATOR)
        assert not has_staff_role(staff(is_gmt=True), StaffRole.BEATMAP_MODERATOR)

    def test_public_user_never_exposes_password_hash(self) -> None:
        payload = _public_user(cast(Any, staff(is_owner=True)), include_email=True)

        assert payload["email"] == "private@example.invalid"
        assert "pw_bcrypt" not in payload
        assert payload["server_id"] == 7


class AdminPanelRequestTests(unittest.TestCase):
    def test_country_code_is_normalised(self) -> None:
        request = UserUpdateRequest.model_validate(
            {"username": " renamed ", "country_code": " ru ", "reason": "correct profile"}
        )

        assert request.username == "renamed"
        assert request.country_code == "RU"

    def test_role_flags_do_not_coerce_strings_to_booleans(self) -> None:
        assert_raises(
            ValidationError,
            lambda: UserUpdateRequest.model_validate({"is_admin": "false", "reason": "invalid flag"}),
        )

    def test_score_import_contract_is_strict_and_trims_source(self) -> None:
        request = ScoreImportRequest.model_validate(
            {
                "source": " https://osu.ppy.sh/scores/123 ",
                "ruleset": "osu",
                "target_user_id": 42,
                "include_replay": False,
                "allow_unverified_revision": False,
                "reason": "account migration",
            }
        )

        assert request.source == "https://osu.ppy.sh/scores/123"
        assert request.target_user_id == 42
        assert request.include_replay is False
        assert request.allow_unverified_revision is False
        assert_raises(
            ValidationError,
            lambda: ScoreImportRequest.model_validate(
                {
                    "source": "123",
                    "target_user_id": 42,
                    "include_replay": "false",
                    "reason": "invalid flag",
                }
            ),
        )
        assert_raises(
            ValidationError,
            lambda: ScoreImportPreviewRequest.model_validate({"source": " ", "unknown": True}),
        )
        assert_raises(
            ValidationError,
            lambda: ScoreImportPreviewRequest.model_validate({"source": "123", "allow_unverified_revision": "false"}),
        )

    def test_profile_clear_requires_strict_confirmation_contract(self) -> None:
        request = ProfileClearRequest.model_validate(
            {
                "confirmation": " tester ",
                "reset_public_profile": True,
                "reason": "requested reset",
            }
        )

        assert request.confirmation == "tester"
        assert_raises(
            ValidationError,
            lambda: ProfileClearRequest.model_validate(
                {
                    "confirmation": "tester",
                    "reset_public_profile": "true",
                    "reason": "requested reset",
                }
            ),
        )

    def test_ranking_only_accepts_ranked_or_loved(self) -> None:
        ranked = RankingMutationRequest.model_validate(
            {"action": "rank", "beatmapset_id": 123, "status": 1, "reason": "reviewed"}
        )
        loved = RankingMutationRequest.model_validate(
            {"action": "rank", "beatmapset_id": 123, "status": 4, "reason": "reviewed"}
        )
        local_unrank = RankingMutationRequest.model_validate(
            {"action": "unrank", "beatmapset_id": 123, "reason": "disable local leaderboard"}
        )
        inherited = RankingMutationRequest.model_validate(
            {"action": "inherit", "beatmapset_id": 123, "reason": "restore inherited status"}
        )

        assert ranked.status == BeatmapRankStatus.RANKED
        assert loved.status == BeatmapRankStatus.LOVED
        assert local_unrank.action == "unrank"
        assert inherited.action == "inherit"
        assert_raises(
            ValidationError,
            lambda: RankingMutationRequest.model_validate(
                {"action": "rank", "beatmapset_id": 123, "status": 3, "reason": "invalid"}
            ),
        )
        assert_raises(
            ValidationError,
            lambda: RankingMutationRequest.model_validate(
                {"action": "rank", "beatmapset_id": 123, "status": True, "reason": "invalid"}
            ),
        )

    def test_session_token_is_only_stored_as_digest(self) -> None:
        opaque_value = "test-session-token"
        digest = _session_digest(opaque_value)

        assert opaque_value not in digest
        assert len(digest) == 64


class ReplayFinalizationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        init_mods()
        self.score = SimpleNamespace(
            id=55,
            user_id=7,
            beatmap_id=123,
            gamemode=GameMode.OSU,
            mods=[{"acronym": "DT"}],
            total_score=1_000_000,
            passed=True,
            leaderboard_eligible=True,
            room_id=None,
            playlist_item_id=None,
            has_replay=False,
            replay_filename="replays/55.osr",
        )
        self.previous = SimpleNamespace(
            **{
                **vars(self.score),
                "id": 54,
                "total_score": 900_000,
                "has_replay": True,
                "replay_filename": "replays/54.osr",
            }
        )
        self.provenance = SimpleNamespace(score_id=55, replay_imported=False)
        self.files = {self.previous.replay_filename: b"previous replay"}
        self.events: list[str] = []

        async def get(model: Any, _id: int, **_kwargs: Any) -> Any:
            return self.score if model is Score else self.provenance if model is ScoreImport else None

        async def commit() -> None:
            self.events.append("commit")

        self.session = SimpleNamespace(
            get=AsyncMock(side_effect=get),
            exec=AsyncMock(return_value=SimpleNamespace(all=lambda: [self.previous, self.score])),
            rollback=AsyncMock(),
            commit=AsyncMock(side_effect=commit),
            add=Mock(),
        )

        @asynccontextmanager
        async def fake_with_db():
            yield self.session

        db_patch = patch("app.router.private.admin_panel.with_db", fake_with_db)
        db_patch.start()
        self.addCleanup(db_patch.stop)

        async def write_file(path: str, content: bytes, *_args: Any) -> None:
            self.events.append("write")
            self.files[path] = content

        async def delete_file(path: str) -> None:
            self.events.append("delete")
            self.files.pop(path, None)

        self.storage = SimpleNamespace(
            is_exists=AsyncMock(side_effect=lambda path: path in self.files),
            write_file=AsyncMock(side_effect=write_file),
            read_file=AsyncMock(side_effect=lambda path: self.files[path]),
            delete_file=AsyncMock(side_effect=delete_file),
        )

    async def finalise(self) -> str:
        return await _finalise_import_replay(
            cast(Any, self.storage),
            score_id=55,
            provenance_id=6,
            replay_path=self.score.replay_filename,
            replay_content=b"new replay",
        )

    async def test_better_import_replaces_client_replay_after_commit(self) -> None:
        assert await self.finalise() == "stored"
        assert self.files == {self.score.replay_filename: b"new replay"}
        assert self.score.has_replay is True
        assert self.previous.has_replay is False
        assert self.provenance.replay_imported is True
        assert self.events == ["write", "commit", "delete"]

    async def test_lower_import_keeps_client_replay(self) -> None:
        self.score.total_score = 800_000
        assert await self.finalise() == "not_retained"
        assert self.previous.has_replay is True
        assert self.score.has_replay is False
        assert self.provenance.replay_imported is False
        self.storage.write_file.assert_not_awaited()
        self.storage.delete_file.assert_not_awaited()
        self.session.commit.assert_not_awaited()

    async def test_equal_score_keeps_earlier_result_even_without_replay(self) -> None:
        self.score.total_score = self.previous.total_score
        self.previous.has_replay = False
        self.files.clear()
        assert await self.finalise() == "not_retained"
        assert self.files == {}
        self.storage.write_file.assert_not_awaited()

    async def test_default_speed_settings_compete_with_implicit_dt(self) -> None:
        self.score.mods = [{"acronym": "DT", "settings": {"speed_change": 1.5}}]
        assert await self.finalise() == "stored"
        assert self.previous.has_replay is False
        assert self.previous.replay_filename not in self.files

    async def test_custom_mod_settings_and_modes_retain_separate_replays(self) -> None:
        cases = [
            ([{"acronym": "DT"}], [{"acronym": "DT", "settings": {"speed_change": 1.3}}], GameMode.OSU),
            (
                [{"acronym": "DA", "settings": {"approach_rate": 9}}],
                [{"acronym": "DA", "settings": {"approach_rate": 10}}],
                GameMode.OSU,
            ),
            ([{"acronym": "DT"}], [{"acronym": "DT"}], GameMode.TAIKO),
        ]
        for previous_mods, imported_mods, mode in cases:
            with self.subTest(mods=imported_mods, mode=mode):
                self.files.pop(self.score.replay_filename, None)
                self.score.has_replay = False
                self.score.total_score = 800_000
                self.previous.mods = previous_mods
                self.score.mods = imported_mods
                self.score.gamemode = mode
                assert await self.finalise() == "stored"
                assert self.previous.has_replay is True
                assert self.previous.replay_filename in self.files
        self.storage.delete_file.assert_not_awaited()

    async def test_ineligible_import_never_stores_replay(self) -> None:
        for attribute, value in [
            ("passed", False),
            ("leaderboard_eligible", False),
            ("room_id", 1),
            ("playlist_item_id", 1),
        ]:
            with self.subTest(attribute=attribute):
                original = getattr(self.score, attribute)
                setattr(self.score, attribute, value)
                assert await self.finalise() == "not_retained"
                setattr(self.score, attribute, original)
        self.storage.write_file.assert_not_awaited()
        self.storage.delete_file.assert_not_awaited()
        self.session.commit.assert_not_awaited()

    async def test_failed_commit_preserves_old_file_and_cleans_new_file(self) -> None:
        self.session.commit.side_effect = RuntimeError("commit failed")
        with patch(
            "app.router.private.admin_panel._import_replay_metadata_is_committed", AsyncMock(return_value=False)
        ):
            assert await self.finalise() == "unavailable"
        assert self.files == {self.previous.replay_filename: b"previous replay"}
        self.storage.delete_file.assert_awaited_once_with(self.score.replay_filename)

    async def test_ambiguous_commit_does_not_delete_published_replay(self) -> None:
        self.session.commit.side_effect = RuntimeError("connection lost after commit")
        with patch("app.router.private.admin_panel._import_replay_metadata_is_committed", AsyncMock(return_value=True)):
            assert await self.finalise() == "stored"
        assert self.files[self.score.replay_filename] == b"new replay"
        self.storage.delete_file.assert_not_awaited()

    async def test_concurrent_upload_is_rechecked_under_score_locks(self) -> None:
        self.storage.is_exists.side_effect = [False, True]
        assert await self.finalise() == "unavailable"
        self.storage.write_file.assert_not_awaited()
        self.storage.delete_file.assert_not_awaited()

    async def test_cancellation_cleans_only_the_replay_written_by_this_import(self) -> None:
        files: dict[str, bytes] = {}

        async def write_file(path: str, content: bytes, *_args: Any) -> None:
            files[path] = content
            raise asyncio.CancelledError

        async def read_file(path: str) -> bytes:
            return files[path]

        async def delete_file(path: str) -> None:
            files.pop(path, None)

        storage = SimpleNamespace(
            is_exists=AsyncMock(return_value=False),
            write_file=write_file,
            read_file=read_file,
            delete_file=delete_file,
        )

        try:
            await _finalise_import_replay(
                cast(Any, storage),
                score_id=55,
                provenance_id=6,
                replay_path="replays/55.osr",
                replay_content=b"owned replay",
            )
        except asyncio.CancelledError:
            pass
        else:
            raise AssertionError("Replay finalization must propagate cancellation")

        assert files == {}

    async def test_existing_replay_path_is_never_overwritten_or_deleted(self) -> None:
        storage = SimpleNamespace(
            is_exists=AsyncMock(return_value=True),
            write_file=AsyncMock(),
            read_file=AsyncMock(),
            delete_file=AsyncMock(),
        )

        result = await _finalise_import_replay(
            cast(Any, storage),
            score_id=55,
            provenance_id=6,
            replay_path="replays/55.osr",
            replay_content=b"new replay",
        )

        assert result == "unavailable"
        storage.write_file.assert_not_awaited()
        storage.read_file.assert_not_awaited()
        storage.delete_file.assert_not_awaited()


if __name__ == "__main__":
    unittest.main()
