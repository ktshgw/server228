"""Seed ONLY the disposable SOMS ranked integration database and create a real LIO room.

Run inside the friends-private-osu-app image on the isolated Docker network
``soms-ranked-review``. The exact production-shaped rooms/playlist DDL is read
from the first argument; no production connection or user data is used.
"""

import asyncio
import json
import os
from pathlib import Path
import sys


def require_isolated_database() -> None:
    expected = {
        "SOMS_MATCHMAKING_DB_TEST": "1",
        "MYSQL_HOST": "soms-ranked-review-db",
        "MYSQL_DATABASE": "soms_matchmaking_integration",
        "MYSQL_USER": "soms_review",
    }
    for name, value in expected.items():
        if os.environ.get(name) != value:
            raise RuntimeError(f"Refusing integration seed: {name} must equal {value!r}")
    if os.environ.get("MYSQL_PORT", "3306") != "3306":
        raise RuntimeError("Refusing unexpected MySQL port")


async def main() -> None:
    require_isolated_database()

    from app.log import logger

    logger.remove()

    from app.config import settings
    from app.database import Beatmap, Beatmapset, MatchmakingPool, User
    from app.dependencies.database import engine
    from app.helpers import utcnow
    from app.models.beatmap import BeatmapRankStatus
    from app.models.room import MatchType
    from app.models.score import GameMode
    from app.router.lio import _create_room
    from app.service.matchmaking_service import ranked_beatmaps

    from sqlalchemy import text
    from sqlmodel import SQLModel
    from sqlmodel.ext.asyncio.session import AsyncSession

    if (
        settings.mysql_host != "soms-ranked-review-db"
        or settings.mysql_database != "soms_matchmaking_integration"
        or engine.url.host != "soms-ranked-review-db"
        or engine.url.database != "soms_matchmaking_integration"
    ):
        raise RuntimeError("Settings/engine do not point at the disposable integration database")

    def parent_closure(names: set[str]):
        tables = set()

        def add(name: str) -> None:
            table = SQLModel.metadata.tables[name]
            if table in tables:
                return
            tables.add(table)
            for foreign_key in table.foreign_keys:
                add(foreign_key.column.table.name)

        for name in names:
            add(name)
        return list(tables)

    parent_tables = parent_closure({"lazer_users", "beatmapsets", "beatmaps", "matchmaking_pools"})
    final_tables = parent_closure(
        {
            "rooms",
            "room_playlists",
            "failtime",
            "room_participated_users",
            "chat_channels",
            "beatmap_ranking_policies",
            "beatmapset_ranking_policies",
            "beatmap_ranking_events",
            "matchmaking_user_stats",
            "matchmaking_pool_beatmaps",
            "matchmaking_room_events",
            "matchmaking_user_elo_history",
        }
    )
    async with engine.begin() as connection:
        if await connection.scalar(text("SELECT DATABASE()")) != "soms_matchmaking_integration":
            raise RuntimeError("Connection database guard failed")
        await connection.run_sync(lambda sync: SQLModel.metadata.create_all(sync, tables=parent_tables))
        schema = await asyncio.to_thread(Path(sys.argv[1]).read_text, encoding="utf-8-sig")
        statements = [part.strip() for part in schema.split(";") if part.strip()]
        if len(statements) != 2 or any(
            not statement.startswith(("CREATE TABLE `rooms`", "CREATE TABLE `room_playlists`"))
            for statement in statements
        ):
            raise RuntimeError("Expected exact rooms + room_playlists CREATE TABLE statements")
        for statement in statements:
            await connection.execute(text(statement.replace("CREATE TABLE `", "CREATE TABLE IF NOT EXISTS `", 1)))
        await connection.run_sync(lambda sync: SQLModel.metadata.create_all(sync, tables=final_tables))

    async with AsyncSession(engine, expire_on_commit=False) as session:
        for user_id in (2, 1234, 2345):
            username = f"ranked-integration-{user_id}"
            existing = await session.get(User, user_id)
            if existing is not None:
                if existing.username != username:
                    raise RuntimeError("Refusing to modify a non-fixture user")
                continue
            session.add(
                User(
                    id=user_id,
                    username=username,
                    email=f"{username}@example.invalid",
                    pw_bcrypt="integration-fixture-login-disabled",
                    is_bot=user_id == 2,
                )
            )
        await session.commit()
        now = utcnow()
        beatmapset_id = 81000
        if await session.get(Beatmapset, beatmapset_id) is None:
            session.add(
                Beatmapset(
                    id=beatmapset_id,
                    artist="Integration",
                    artist_unicode="Integration",
                    creator="ranked-integration-2",
                    title="Ranked integration fixture",
                    title_unicode="Ranked integration fixture",
                    user_id=2,
                    preview_url="",
                    video=False,
                    covers=None,
                    last_updated=now,
                    submitted_date=now,
                    beatmap_status=BeatmapRankStatus.RANKED,
                    download_disabled=False,
                )
            )
            await session.commit()
        for index in range(12):
            beatmap_id = 82000 + index
            if await session.get(Beatmap, beatmap_id) is None:
                session.add(
                    Beatmap(
                        id=beatmap_id,
                        beatmapset_id=beatmapset_id,
                        user_id=2,
                        mode=GameMode.OSU,
                        version=f"Integration {index}",
                        difficulty_rating=4 + index / 10,
                        url=f"https://example.invalid/beatmaps/{beatmap_id}",
                        total_length=120,
                        hit_length=100,
                        checksum=f"{beatmap_id:032x}",
                        beatmap_status=BeatmapRankStatus.RANKED,
                        last_updated=now,
                        cs=4,
                    )
                )
        if await session.get(MatchmakingPool, 1) is None:
            session.add(
                MatchmakingPool(
                    id=1,
                    ruleset_id=0,
                    variant_id=0,
                    name="SOMS! integration",
                    type="ranked_play",
                    ranked=True,
                    active=True,
                    lobby_size=2,
                    rating_search_radius=150,
                    use_dmr=False,
                )
            )
        await session.commit()
        eligible = await ranked_beatmaps(session, 0)
        if len(eligible) < 10:
            raise RuntimeError(f"Real ranking eligibility returned only {len(eligible)} maps")
        # Real function, real AsyncSession, real User lookup, real enum binding.
        room, host_id = await _create_room(
            session,
            {
                "name": "SOMS ranked MySQL integration",
                "user_id": 2,
                "type": "ranked_play",
                "queue_mode": "host_only",
            },
        )
        stored_type = await session.scalar(text("SELECT type FROM rooms WHERE id=:id"), {"id": room.id})
        if room.type != MatchType.RANKED_PLAY or stored_type != "RANKED_PLAY" or host_id != 2:
            raise RuntimeError("Canonical LIO ranked_play request was not persisted as RankedPlay")
        print(
            json.dumps(
                {
                    "database": settings.mysql_database,
                    "room_id": room.id,
                    "host_id": host_id,
                    "pool_id": 1,
                    "player_ids": [1234, 2345],
                    "eligible_maps": len(eligible),
                    "stored_room_type": stored_type,
                }
            )
        )
    await engine.dispose()


if __name__ == "__main__":
    asyncio.run(main())
