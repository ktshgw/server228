from pathlib import Path
import runpy
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database.score import Score
from app.models.score import GameMode, Rank
from app.router.private.web_site import (
    WebScorePinReorderRequest,
    _score_payloads,
    pin_web_score,
    reorder_web_score_pin,
    unpin_web_score,
)
from app.router.v2.score import (
    pin_score as pin_lazer_score,
    reorder_score_pin as reorder_lazer_score_pin,
    unpin_score as unpin_lazer_score,
)

from fastapi import HTTPException
from sqlalchemy.dialects import mysql

ROOT = Path(__file__).resolve().parents[1]
MIGRATION = ROOT / "migrations/versions/2026-09-04_c6e8d0a4f125_harden_score_pin_order.py"


class ExpireOnCommitRecord(SimpleNamespace):
    """Small ORM stand-in that rejects reads after a simulated commit."""

    def __init__(self, *, expire_fields: set[str], **values: object) -> None:
        super().__init__(**values)
        self._expire_fields = frozenset(expire_fields)
        self._expired = False

    def expire(self) -> None:
        self._expired = True

    def __getattribute__(self, name: str):
        state = object.__getattribute__(self, "__dict__")
        if state.get("_expired", False) and name in state.get("_expire_fields", ()):
            raise AssertionError(f"expired ORM attribute read after commit: {name}")
        return super().__getattribute__(name)


class WebScorePinPersistenceTests(unittest.TestCase):
    def test_score_model_persists_nonnegative_pin_order_with_database_default(self) -> None:
        column = Score.__table__.c.pinned_order  # pyright: ignore[reportAttributeAccessIssue]
        assert column.nullable is False
        assert column.server_default is not None
        assert str(column.server_default.arg) == "0"
        assert {
            constraint.name
            for constraint in Score.__table__.constraints  # pyright: ignore[reportAttributeAccessIssue]
        } >= {"ck_scores_pinned_order_nonnegative"}

    def test_pin_hardening_migration_extends_head_and_repairs_before_constraint(self) -> None:
        migration = runpy.run_path(str(MIGRATION))
        fake_op = Mock()
        migration["upgrade"].__globals__["op"] = fake_op
        migration["upgrade"].__globals__["_check_constraint_exists"] = Mock(return_value=False)

        migration["upgrade"]()

        assert migration["down_revision"] == "b5d1f7a4c902"
        calls = fake_op.method_calls
        execute_indexes = [index for index, item in enumerate(calls) if item[0] == "execute"]
        constraint_index = next(index for index, item in enumerate(calls) if item[0] == "create_check_constraint")
        assert execute_indexes[-1] < constraint_index
        assert str(calls[execute_indexes[0]].args[0]) == migration["PIN_ORDER_REPAIR_SQL"]
        assert str(calls[execute_indexes[1]].args[0]).strip() == migration["PIN_ORDER_COMPACT_SQL"].strip()
        alter = next(item for item in calls if item[0] == "alter_column")
        assert str(alter.kwargs["server_default"]) == "0"

    def test_pin_hardening_migration_resumes_after_mysql_check_ddl_was_applied(self) -> None:
        migration = runpy.run_path(str(MIGRATION))
        fake_op = Mock()
        migration["upgrade"].__globals__["op"] = fake_op
        migration["upgrade"].__globals__["_check_constraint_exists"] = Mock(return_value=True)

        migration["upgrade"]()

        fake_op.create_check_constraint.assert_not_called()
        assert fake_op.execute.call_count == 2
        fake_op.alter_column.assert_called_once()


class WebScorePinEndpointTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def _score(score_id: int, order: int = 0) -> SimpleNamespace:
        return SimpleNamespace(
            id=score_id,
            user_id=7,
            processed=True,
            passed=True,
            gamemode=GameMode.OSU,
            pinned_order=order,
        )

    @staticmethod
    def _context(*, expires: bool = False) -> SimpleNamespace:
        values = {"id": 7, "is_restricted": AsyncMock(return_value=False)}
        user = ExpireOnCommitRecord(expire_fields={"id"}, **values) if expires else SimpleNamespace(**values)
        return SimpleNamespace(user=user)

    @staticmethod
    def _session(*results: object, expire_on_commit: tuple[ExpireOnCommitRecord, ...] = ()) -> SimpleNamespace:
        async def commit() -> None:
            for record in expire_on_commit:
                record.expire()

        return SimpleNamespace(
            exec=AsyncMock(side_effect=results),
            add=Mock(),
            commit=AsyncMock(side_effect=commit),
        )

    @classmethod
    def _expiring_score(cls, score_id: int, order: int = 0) -> ExpireOnCommitRecord:
        return ExpireOnCommitRecord(
            expire_fields={"id", "gamemode", "pinned_order"},
            **vars(cls._score(score_id, order)),
        )

    async def test_owner_can_pin_and_unpin_a_processed_pass(self) -> None:
        score = self._score(10)
        pin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: score),
            SimpleNamespace(all=lambda: []),
        )
        cache = SimpleNamespace(invalidate_user_scores_cache=AsyncMock())
        with patch("app.router.private.web_site._require_csrf"):
            pinned = await pin_web_score(
                10,
                SimpleNamespace(),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                pin_session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert pinned == {"score_id": 10, "pinned": True, "pinned_order": 1}
        pin_session.commit.assert_awaited_once()

        unpin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: score),
            SimpleNamespace(all=lambda: [score]),
        )
        with patch("app.router.private.web_site._require_csrf"):
            unpinned = await unpin_web_score(
                10,
                SimpleNamespace(),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                unpin_session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert unpinned == {"score_id": 10, "pinned": False, "pinned_order": None}
        assert score.pinned_order == 0
        unpin_session.commit.assert_awaited_once()

    async def test_pin_rejects_failed_or_unprocessed_scores(self) -> None:
        score = self._score(10)
        score.passed = False
        session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: score),
            SimpleNamespace(all=lambda: []),
        )
        with (
            patch("app.router.private.web_site._require_csrf"),
            self.assertRaisesRegex(HTTPException, "422"),  # noqa: PT027
        ):
            await pin_web_score(
                10,
                SimpleNamespace(),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                SimpleNamespace(invalidate_user_scores_cache=AsyncMock()),  # type: ignore[arg-type]
            )
        session.commit.assert_not_awaited()

    async def test_reorder_returns_compact_persisted_order(self) -> None:
        first, second, third = self._score(10, 1), self._score(20, 2), self._score(30, 3)
        session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: third),
            SimpleNamespace(all=lambda: [first, second, third]),
        )
        with patch("app.router.private.web_site._require_csrf"):
            result = await reorder_web_score_pin(
                30,
                WebScorePinReorderRequest(before_score_id=20),
                SimpleNamespace(),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                SimpleNamespace(invalidate_user_scores_cache=AsyncMock()),  # type: ignore[arg-type]
            )

        assert result["ordered_score_ids"] == [10, 30, 20]
        assert [first.pinned_order, second.pinned_order, third.pinned_order] == [1, 3, 2]
        session.commit.assert_awaited_once()

    async def test_pin_mutations_only_use_captured_scalars_after_expire_on_commit(self) -> None:
        cache = SimpleNamespace(invalidate_user_scores_cache=AsyncMock())

        pin_score = self._expiring_score(10)
        pin_context = self._context(expires=True)
        pin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: pin_score),
            SimpleNamespace(all=lambda: []),
            expire_on_commit=(pin_context.user, pin_score),
        )
        with patch("app.router.private.web_site._require_csrf"):
            pinned = await pin_web_score(
                10,
                SimpleNamespace(),  # type: ignore[arg-type]
                pin_context,  # type: ignore[arg-type]
                pin_session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )
        assert pinned == {"score_id": 10, "pinned": True, "pinned_order": 1}

        unpin_score = self._expiring_score(20, 1)
        unpin_context = self._context(expires=True)
        unpin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: unpin_score),
            SimpleNamespace(all=lambda: [unpin_score]),
            expire_on_commit=(unpin_context.user, unpin_score),
        )
        with patch("app.router.private.web_site._require_csrf"):
            unpinned = await unpin_web_score(
                20,
                SimpleNamespace(),  # type: ignore[arg-type]
                unpin_context,  # type: ignore[arg-type]
                unpin_session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )
        assert unpinned == {"score_id": 20, "pinned": False, "pinned_order": None}

        first = self._expiring_score(30, 1)
        second = self._expiring_score(40, 2)
        reorder_context = self._context(expires=True)
        reorder_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: second),
            SimpleNamespace(all=lambda: [first, second]),
            expire_on_commit=(reorder_context.user, first, second),
        )
        with patch("app.router.private.web_site._require_csrf"):
            reordered = await reorder_web_score_pin(
                40,
                WebScorePinReorderRequest(before_score_id=30),
                SimpleNamespace(),  # type: ignore[arg-type]
                reorder_context,  # type: ignore[arg-type]
                reorder_session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )
        assert reordered == {
            "score_id": 40,
            "pinned": True,
            "pinned_order": 1,
            "ordered_score_ids": [40, 30],
        }

    async def test_duplicate_max_orders_are_compacted_before_a_new_pin(self) -> None:
        first = self._score(10, 1)
        second = self._score(20, 2)
        duplicate_max = self._score(30, 2)
        new_score = self._score(40)
        session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: new_score),
            SimpleNamespace(all=lambda: [first, second, duplicate_max]),
        )
        cache = SimpleNamespace(invalidate_user_scores_cache=AsyncMock())

        with patch("app.router.private.web_site._require_csrf"):
            result = await pin_web_score(
                40,
                SimpleNamespace(),  # type: ignore[arg-type]
                self._context(),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert [first.pinned_order, second.pinned_order, duplicate_max.pinned_order, new_score.pinned_order] == [
            1,
            2,
            3,
            4,
        ]
        assert result["pinned_order"] == 4
        owner_lock = session.exec.await_args_list[0].args[0]
        owner_sql = str(owner_lock.compile(dialect=mysql.dialect()))
        assert "FROM lazer_users" in owner_sql
        assert "FOR UPDATE" in owner_sql

    async def test_lazer_pin_mutations_share_locking_and_compaction(self) -> None:
        cache = SimpleNamespace(invalidate_user_scores_cache=AsyncMock())
        current_user = SimpleNamespace(id=7)

        first, duplicate, new_score = self._score(10, 2), self._score(20, 2), self._score(30)
        pin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: new_score),
            SimpleNamespace(all=lambda: [first, duplicate]),
        )
        await pin_lazer_score(pin_session, current_user, cache, 30)  # type: ignore[arg-type]
        assert [first.pinned_order, duplicate.pinned_order, new_score.pinned_order] == [1, 2, 3]

        unpin_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: duplicate),
            SimpleNamespace(all=lambda: [first, duplicate, new_score]),
        )
        await unpin_lazer_score(unpin_session, cache, 20, current_user)  # type: ignore[arg-type]
        assert [first.pinned_order, duplicate.pinned_order, new_score.pinned_order] == [1, 0, 2]

        reorder_session = self._session(
            SimpleNamespace(first=lambda: 7),
            SimpleNamespace(first=lambda: new_score),
            SimpleNamespace(all=lambda: [first, new_score]),
        )
        await reorder_lazer_score_pin(
            reorder_session,  # type: ignore[arg-type]
            cache,  # type: ignore[arg-type]
            current_user,  # type: ignore[arg-type]
            30,
            before_score_id=10,
        )
        assert [first.pinned_order, new_score.pinned_order] == [2, 1]

    async def test_score_payload_links_exact_difficulty_and_existing_replay_route(self) -> None:
        beatmapset = SimpleNamespace(id=1177270, title="Song", artist="Artist", creator="Mapper", covers={})
        beatmap = SimpleNamespace(
            id=2455380,
            beatmapset_id=1177270,
            version="Insane",
            difficulty_rating=5.2,
            mode=GameMode.OSU,
            max_combo=1000,
            beatmapset=beatmapset,
            deleted_at=None,
            beatmap_status=1,
        )
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
            g0v0_playmode=GameMode.OSU,
            location=None,
            interests=None,
            occupation=None,
            discord=None,
            website=None,
            page={},
        )
        score = SimpleNamespace(
            id=99,
            beatmap_id=beatmap.id,
            pinned_order=2,
            user=user,
            beatmap=beatmap,
            gamemode=GameMode.OSU,
            rank=Rank.S,
            accuracy=0.99,
            pp=321.0,
            total_score=1_000_000,
            max_combo=999,
            n300=900,
            n100=10,
            n50=1,
            nmiss=0,
            ngeki=0,
            nkatu=0,
            mods=[{"acronym": "DT", "settings": {"speed_change": 1.2, "adjust_pitch": True}}],
            passed=True,
            ended_at=None,
            has_replay=True,
            processed=True,
            ranked=True,
            leaderboard_eligible=True,
        )
        session = SimpleNamespace(info={}, exec=AsyncMock(return_value=SimpleNamespace(all=lambda: [])))

        payload = (await _score_payloads(session, [score]))[0]  # type: ignore[arg-type]

        assert payload["beatmap_url"] == "/site/#beatmap/1177270/2455380"
        assert payload["replay_url"] == "/api/v2/scores/99/download"
        assert payload["is_pinned"] is True
        assert payload["pinned_order"] == 2
        assert payload["user"]["server_id"] == 1
        assert payload["user"]["username"] == "player"
        assert payload["user"]["avatar_url"] == "/site/soms-default-avatar.png"
        assert payload["mods"] == [{"acronym": "DT", "settings": {"speed_change": 1.2, "adjust_pitch": True}}]


if __name__ == "__main__":
    unittest.main()
