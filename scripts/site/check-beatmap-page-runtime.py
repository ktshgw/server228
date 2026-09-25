"""Read-only verification against the running server's MySQL."""

import asyncio
from types import SimpleNamespace

from app.log import logger

logger.remove()

from app.database import TotalScoreBestScore, User
from app.dependencies.database import engine
from app.models.mods import init_mods
from app.service.web_beatmap_community_service import comment_page
from app.service.web_beatmap_leaderboard_service import leaderboard_page

from sqlalchemy import text
from sqlmodel import select
from sqlmodel.ext.asyncio.session import AsyncSession


async def main():
    init_mods()
    async with AsyncSession(engine) as session:
        version = (await session.execute(text("SELECT version_num FROM alembic_version"))).one()[0]
        sample = (await session.exec(select(TotalScoreBestScore).limit(1))).first()
        assert sample is not None
        user = await session.get(User, sample.user_id)
        assert user is not None
        viewer = SimpleNamespace(id=user.id, country_code=user.country_code, is_supporter=True)
        checks = []
        for scope, mods in [("global", None), ("country", None), ("friends", None), ("global", "NM"), ("global", "DT")]:
            result = await leaderboard_page(session, sample.beatmap_id, sample.gamemode, 1, 50, viewer, scope, mods)
            assert len({row[1].id for row in result["rows"]}) == len(result["rows"])
            assert result["total"] >= len(result["rows"])
            checks.append({"scope": scope, "mods": mods, "total": result["total"]})
        await comment_page(session, 110870, viewer)
        print({"migration": version, "mysql_boards": checks, "comments": "OK"})
        await session.rollback()
    await engine.dispose()


asyncio.run(main())
