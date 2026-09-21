"""Ranked profile queries against an isolated SQL database, never the server DB."""

from datetime import datetime, timedelta
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch

from app.database import MatchmakingPool, MatchmakingUserStats, User
from app.database.user_account_history import UserAccountHistory, UserAccountHistoryType
from app.dependencies.database import get_db, json_serializer
from app.helpers import utcnow
from app.models.score import GameMode
from app.router.private.web_site import get_web_user
from app.service.ranked_profile_service import ranked_profile_payload

from fastapi import FastAPI
from httpx import ASGITransport, AsyncClient
from sqlalchemy import event
from sqlalchemy.dialects import mysql
from sqlmodel import Session, SQLModel, create_engine


class AsyncSqlSession:
    def __init__(self, session: Session):
        self.session = session
        self.statements: list[Any] = []

    async def exec(self, statement: Any) -> Any:
        self.statements.append(statement)
        return self.session.exec(statement)


class RankedProfileTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self) -> None:
        self.engine = create_engine("sqlite://", json_serializer=json_serializer)

        # Adapt only MySQL's TIMESTAMPADD syntax; ranking and visibility filters
        # execute as SQL, including real restriction-history rows.
        @event.listens_for(self.engine, "connect")
        def configure(dbapi: Any, _record: Any) -> None:
            dbapi.create_function(
                "timestampadd",
                3,
                lambda _unit, seconds, stamp: (datetime.fromisoformat(stamp) + timedelta(seconds=seconds)).strftime(
                    "%Y-%m-%d %H:%M:%S"
                ),
            )

        @event.listens_for(self.engine, "before_cursor_execute", retval=True)
        def adapt(_connection: Any, _cursor: Any, statement: str, parameters: Any, _context: Any, _many: Any):
            return statement.replace("timestampadd(SECOND,", "timestampadd('SECOND',"), parameters

        SQLModel.metadata.create_all(
            self.engine,
            tables=[
                SQLModel.metadata.tables[model.__tablename__]
                for model in (User, UserAccountHistory, MatchmakingPool, MatchmakingUserStats)
            ],
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncSqlSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        for user_id in range(10, 19):
            self.db.add(
                User(
                    id=user_id,
                    username=f"fixture{user_id}",
                    email=f"fixture{user_id}@example.invalid",
                    pw_bcrypt="fixture-disabled-login",
                    is_bot=user_id == 15,
                    is_active=user_id != 16,
                )
            )
        self.db.add_all(
            [
                MatchmakingPool(id=1, name="osu", type="ranked_play", ranked=True),
                MatchmakingPool(id=2, name="mania 4K", ruleset_id=3, variant_id=4, type="ranked_play", ranked=True),
                MatchmakingPool(id=3, name="mania 7K", ruleset_id=3, variant_id=7, type="ranked_play", ranked=True),
                MatchmakingPool(id=4, name="osu second", type="ranked_play", ranked=True),
                MatchmakingPool(
                    id=5, name="inactive taiko", ruleset_id=1, type="ranked_play", ranked=True, active=False
                ),
                MatchmakingPool(id=6, name="quick catch", ruleset_id=2, type="quick_play", ranked=True),
                MatchmakingPool(id=7, name="unranked catch", ruleset_id=2, type="ranked_play", ranked=False),
                UserAccountHistory(
                    id=None, user_id=17, type=UserAccountHistoryType.RESTRICTION, permanent=True, length=0
                ),
            ]
        )
        self.add_rating(10, 1, 1699.6)
        self.add_rating(11, 1, 1700.1)
        self.add_rating(12, 1, 1699.6)
        self.add_rating(13, 1, 9999, contests=0)
        self.add_rating(15, 1, 3000)
        self.add_rating(16, 1, 3000)
        self.add_rating(17, 1, 3000)
        self.add_rating(10, 2, 1400)
        self.add_rating(10, 3, 2100)
        self.add_rating(11, 3, 2200)
        self.add_rating(10, 4, 9000)
        self.db.commit()

    def add_rating(self, user: int, pool: int, mmr: float, contests: int = 3) -> None:
        self.db.add(
            MatchmakingUserStats(
                user_id=user,
                pool_id=pool,
                elo_data={"approximate_posterior": {"mu": mmr, "sig": 90}, "contest_count": contests},
            )
        )

    async def test_mmr_is_the_unrounded_rating_and_world_place_uses_the_same_pool(self) -> None:
        payload = await ranked_profile_payload(self.session, 10, GameMode.OSU)
        assert payload == {"pool_id": 1, "variant_id": 0, "mmr": 1699.6, "global_rank": 2}
        tied = await ranked_profile_payload(self.session, 12, GameMode.OSU)
        assert tied["global_rank"] == 3

    async def test_mania_variants_have_independent_ratings_and_world_places(self) -> None:
        four = await ranked_profile_payload(self.session, 10, GameMode.MANIA, 4)
        seven = await ranked_profile_payload(self.session, 10, GameMode.MANIA, 7)
        assert (four["pool_id"], four["mmr"], four["global_rank"]) == (2, 1400, 1)
        assert (seven["pool_id"], seven["mmr"], seven["global_rank"]) == (3, 2100, 2)

    async def test_unplayed_missing_and_nonpublic_users_have_no_earned_rating(self) -> None:
        for user_id in (13, 14, 15, 16, 17):
            payload = await ranked_profile_payload(self.session, user_id, GameMode.OSU)
            assert payload["mmr"] is None
            assert payload["global_rank"] is None

    async def test_unrelated_and_inactive_queues_are_not_ranked_profile_pools(self) -> None:
        for mode in (GameMode.TAIKO, GameMode.FRUITS):
            payload = await ranked_profile_payload(self.session, 10, mode)
            assert payload["pool_id"] is None
            assert payload["mmr"] is None

    async def test_temporary_restrictions_expire_without_changing_other_users_places(self) -> None:
        self.add_rating(18, 1, 2500)
        restriction = UserAccountHistory(
            id=None,
            user_id=18,
            type=UserAccountHistoryType.RESTRICTION,
            length=3600,
            timestamp=utcnow() - timedelta(minutes=5),
        )
        self.db.add(restriction)
        self.db.commit()
        assert (await ranked_profile_payload(self.session, 10, GameMode.OSU))["global_rank"] == 2
        restriction.timestamp = utcnow() - timedelta(hours=2)
        self.db.commit()
        assert (await ranked_profile_payload(self.session, 10, GameMode.OSU))["global_rank"] == 3

    async def test_mysql_query_ranks_numeric_json_values_before_filtering_the_profile(self) -> None:
        await ranked_profile_payload(self.session, 10, GameMode.OSU)
        sql = str(self.session.statements[-1].compile(dialect=mysql.dialect()))
        assert "row_number() OVER" in sql
        assert "JSON_EXTRACT" in sql
        assert "timestampadd(SECOND" in sql
        assert "user_account_history" in sql

    async def test_profile_api_includes_ranked_data_for_requested_mania_variant(self) -> None:
        user = self.db.get(User, 10)
        with (
            patch("app.router.private.web_site._find_public_user", AsyncMock(return_value=user)),
            patch("app.router.private.web_site._statistics_payload", AsyncMock(return_value={"global_rank": None})),
            patch("app.router.private.web_site._profile_extras_payload", AsyncMock(return_value={})),
            patch("app.router.private.web_site._web_follower_count", AsyncMock(return_value=0)),
        ):
            payload = await get_web_user("10", self.session, mode="mania", ranked_variant=7)
        assert payload["ranked"] == {"pool_id": 3, "variant_id": 7, "mmr": 2100, "global_rank": 2}

    async def test_http_query_coerces_key_count_and_rejects_unsupported_variants(self) -> None:
        app = FastAPI()
        app.add_api_route("/users/{identifier}", get_web_user, methods=["GET"])
        app.dependency_overrides[get_db] = lambda: self.session
        with (
            patch("app.router.private.web_site._find_public_user", AsyncMock(return_value=self.db.get(User, 10))),
            patch("app.router.private.web_site._statistics_payload", AsyncMock(return_value={"global_rank": None})),
            patch("app.router.private.web_site._profile_extras_payload", AsyncMock(return_value={})),
            patch("app.router.private.web_site._web_follower_count", AsyncMock(return_value=0)),
        ):
            async with AsyncClient(transport=ASGITransport(app=app), base_url="http://test") as client:
                for variant, rating in ((4, 1400), (7, 2100)):
                    response = await client.get(f"/users/10?mode=mania&ranked_variant={variant}")
                    assert response.status_code == 200, response.text
                    assert response.json()["ranked"]["mmr"] == rating
                for variant in ("5", "abc"):
                    response = await client.get(f"/users/10?mode=mania&ranked_variant={variant}")
                    assert response.status_code == 422
