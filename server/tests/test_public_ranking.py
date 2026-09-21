import asyncio
from typing import Any, cast
import unittest
from unittest.mock import patch

from app.database.best_scores import BestScore
from app.database.rank_history import RankHistory
from app.database.statistics import UserStatistics, get_rank, public_ranking_conditions
from app.database.user import User
from app.database.user_account_history import UserAccountHistory, UserAccountHistoryType
from app.models.score import GameMode
from app.service.ranking_cache_service import RankingCacheService

from sqlalchemy import func
from sqlmodel import Session, col, create_engine, exists, select
from sqlmodel.ext.asyncio.session import AsyncSession


class AsyncSessionAdapter:
    def __init__(self, session: Session):
        self.session = session

    async def exec(self, statement: Any):
        return self.session.exec(statement)

    def add(self, instance: Any) -> None:
        self.session.add(instance)


class PublicRankingEligibilityTests(unittest.TestCase):
    def test_hidden_users_do_not_consume_global_rank_positions(self) -> None:
        engine = create_engine("sqlite://")
        try:
            User.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            UserStatistics.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            UserAccountHistory.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            RankHistory.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            BestScore.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            with Session(engine) as session:
                users = [
                    User(id=1, username="visible-one", email="one@example.invalid", pw_bcrypt="x"),
                    User(id=2, username="visible-two", email="two@example.invalid", pw_bcrypt="x"),
                    User(id=3, username="restricted", email="three@example.invalid", pw_bcrypt="x"),
                    User(id=4, username="inactive", email="four@example.invalid", pw_bcrypt="x", is_active=False),
                    User(id=5, username="bot", email="five@example.invalid", pw_bcrypt="x", is_bot=True),
                ]
                session.add_all(users)
                session.add_all(
                    [
                        UserStatistics(user_id=1, mode=GameMode.OSU, pp=200),
                        UserStatistics(user_id=2, mode=GameMode.OSU, pp=100),
                        UserStatistics(user_id=3, mode=GameMode.OSU, pp=300),
                        UserStatistics(user_id=4, mode=GameMode.OSU, pp=400),
                        UserStatistics(user_id=5, mode=GameMode.OSU, pp=500),
                        UserStatistics(user_id=1, mode=GameMode.TAIKO, pp=900),
                    ]
                )
                session.add(
                    UserAccountHistory(
                        id=None,
                        user_id=3,
                        type=UserAccountHistoryType.RESTRICTION,
                        description="test restriction",
                        length=0,
                        permanent=True,
                    )
                )
                session.commit()

                def restricted_query(user_id):
                    return exists().where(
                        col(UserAccountHistory.user_id) == user_id,
                        col(UserAccountHistory.type) == UserAccountHistoryType.RESTRICTION,
                        col(UserAccountHistory.permanent).is_(True),
                    )

                with patch.object(User, "is_restricted_query", side_effect=restricted_query):
                    ranked = (
                        select(
                            UserStatistics.user_id,
                            func.row_number()
                            .over(order_by=(col(UserStatistics.pp).desc(), col(UserStatistics.user_id).asc()))
                            .label("rank"),
                        )
                        .where(*public_ranking_conditions(GameMode.OSU))
                        .subquery()
                    )
                    rows = session.exec(select(ranked.c.user_id, ranked.c.rank).order_by(ranked.c.rank)).all()
                    second_user = session.exec(
                        select(UserStatistics).where(
                            col(UserStatistics.user_id) == 2,
                            col(UserStatistics.mode) == GameMode.OSU,
                        )
                    ).one()
                    global_rank = asyncio.run(get_rank(cast(AsyncSession, AsyncSessionAdapter(session)), second_user))

            assert [tuple(row) for row in rows] == [(1, 1), (2, 2)]
            assert global_rank == 2
        finally:
            engine.dispose()

    def test_user_ranking_cache_namespace_is_versioned(self) -> None:
        service = RankingCacheService(redis=None)  # type: ignore[arg-type]

        assert service._get_cache_key(GameMode.OSU, "performance") == "ranking:v2:osu:performance:page:1"
        assert service._get_stats_cache_key(GameMode.OSU, "performance") == "ranking:stats:v2:osu:performance"


if __name__ == "__main__":
    unittest.main()
