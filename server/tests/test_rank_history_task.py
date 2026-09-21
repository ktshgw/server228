import asyncio
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from datetime import UTC, date, datetime
from typing import Any, cast
import unittest
from unittest.mock import patch

from app.database.rank_history import RankHistory, RankTop
from app.database.statistics import UserStatistics
from app.database.user import User
from app.database.user_account_history import UserAccountHistory, UserAccountHistoryType
from app.models.score import GameMode
from app.tasks.calculate_all_user_rank import calculate_user_rank

from sqlmodel import Session, col, create_engine, exists, select
from sqlmodel.ext.asyncio.session import AsyncSession


class AsyncSessionAdapter:
    def __init__(self, session: Session):
        self.session = session

    async def exec(self, statement: Any):
        return self.session.exec(statement)

    async def execute(self, statement: Any):
        return self.session.execute(statement)

    def add(self, instance: Any) -> None:
        self.session.add(instance)

    async def commit(self) -> None:
        self.session.commit()


class RankHistoryTaskTests(unittest.TestCase):
    def test_daily_snapshot_uses_public_population_and_target_date(self) -> None:
        engine = create_engine("sqlite://")
        try:
            User.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            UserStatistics.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            UserAccountHistory.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            RankHistory.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            RankTop.__table__.create(engine)  # pyright: ignore[reportAttributeAccessIssue]
            with Session(engine) as session:
                session.add_all(
                    [
                        User(id=1, username="one", email="one@example.invalid", pw_bcrypt="x"),
                        User(id=2, username="two", email="two@example.invalid", pw_bcrypt="x"),
                        User(id=3, username="hidden", email="hidden@example.invalid", pw_bcrypt="x"),
                    ]
                )
                session.add_all(
                    [
                        UserStatistics(user_id=1, mode=GameMode.OSU, pp=200),
                        UserStatistics(user_id=2, mode=GameMode.OSU, pp=100),
                        UserStatistics(user_id=3, mode=GameMode.OSU, pp=300),
                    ]
                )
                session.add(
                    UserAccountHistory(
                        id=1,
                        user_id=3,
                        type=UserAccountHistoryType.RESTRICTION,
                        description="hidden from public ranking",
                        length=0,
                        permanent=True,
                    )
                )
                snapshot_date = date(2026, 9, 3)
                session.add_all(
                    [
                        RankHistory(id=1, user_id=1, mode=GameMode.OSU, rank=99, date=snapshot_date),
                        RankHistory(id=2, user_id=2, mode=GameMode.OSU, rank=99, date=snapshot_date),
                        RankHistory(id=3, user_id=3, mode=GameMode.OSU, rank=1, date=snapshot_date),
                        RankTop(id=1, user_id=1, mode=GameMode.OSU, rank=99, date=date(2026, 9, 1)),
                        RankTop(id=2, user_id=2, mode=GameMode.OSU, rank=99, date=date(2026, 9, 1)),
                    ]
                )
                session.commit()

                adapter = AsyncSessionAdapter(session)

                @asynccontextmanager
                async def fake_with_db() -> AsyncIterator[AsyncSession]:
                    yield cast(AsyncSession, adapter)

                def restricted_query(user_id: Any):
                    return exists().where(
                        col(UserAccountHistory.user_id) == user_id,
                        col(UserAccountHistory.type) == UserAccountHistoryType.RESTRICTION,
                        col(UserAccountHistory.permanent).is_(True),
                    )

                with (
                    patch("app.tasks.calculate_all_user_rank.with_db", fake_with_db),
                    patch("app.tasks.calculate_all_user_rank.GameMode", (GameMode.OSU,)),
                    patch("app.tasks.calculate_all_user_rank.utcnow", return_value=datetime(2026, 9, 4, tzinfo=UTC)),
                    patch.object(User, "is_restricted_query", side_effect=restricted_query),
                ):
                    asyncio.run(calculate_user_rank(is_today=False))

                history = session.exec(
                    select(RankHistory).order_by(col(RankHistory.user_id), col(RankHistory.date))
                ).all()
                rank_tops = session.exec(select(RankTop).order_by(col(RankTop.user_id))).all()

            assert [(row.user_id, row.rank, row.date) for row in history] == [
                (1, 1, snapshot_date),
                (2, 2, snapshot_date),
            ]
            assert [(row.user_id, row.rank, row.date) for row in rank_tops] == [
                (1, 1, snapshot_date),
                (2, 2, snapshot_date),
            ]
        finally:
            engine.dispose()
