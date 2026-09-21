"""Real MySQL penalty/restore check; all rule and PP changes are rolled back."""

import asyncio
import sys

from app.log import logger

logger.remove()

from app.database import Beatmap, BestScore, NegativePPRule, UserStatistics
from app.database.score import calculate_user_pp, get_user_best_pp
from app.dependencies.database import with_db
from app.dependencies.fetcher import get_fetcher
from app.service.negative_pp_service import negative_score_users, recalculate

from sqlmodel import col, func, select


async def run():
    async with with_db() as session:
        if "--sync-owners" in sys.argv:
            from app.service.negative_pp_service import prepare_mapper_rule

            await prepare_mapper_rule(session, await get_fetcher())
            print("Authoritative owners of previously scored maps synced; no penalty rules added.")
            return
        if "--probe" in sys.argv:
            scored_sets = (
                await session.exec(
                    select(func.count(func.distinct(Beatmap.beatmapset_id))).join(
                        BestScore, BestScore.beatmap_id == Beatmap.id
                    )
                )
            ).one()
            set_id = (
                await session.exec(
                    select(Beatmap.beatmapset_id).join(BestScore, BestScore.beatmap_id == Beatmap.id).limit(1)
                )
            ).one()
            data = await (await get_fetcher()).request_api(f"https://osu.ppy.sh/api/v2/beatmapsets/{set_id}")
            from app.fetcher.beatmapset import adapter

            parsed = adapter.validate_python(data)
            sample = await Beatmap.from_resp_no_save(session, parsed["beatmaps"][0])
            assert sample.owners_known
            assert sample.mapper_credits
            print(
                {
                    "scored_sets": scored_sets,
                    "sample_set": set_id,
                    "owners_present": all("owners" in bm for bm in data["beatmaps"]),
                    "difficulties": len(data["beatmaps"]),
                }
            )
            return
        row = (
            await session.exec(select(BestScore).where(BestScore.pp > 0).order_by(col(BestScore.pp).desc()).limit(1))
        ).one()
        user_id, mode, beatmap_id = row.user_id, row.gamemode, row.beatmap_id
        before, _ = await calculate_user_pp(session, user_id, mode)
        bests = await get_user_best_pp(session, user_id, mode)
        index = next(i for i, score in enumerate(bests) if score.beatmap_id == beatmap_id)
        rule = NegativePPRule(
            kind="beatmap",
            target_id=beatmap_id,
            label="Rollback runtime check",
            reason="Temporary transactional verification",
            created_by=user_id,
        )
        try:
            session.add(rule)
            await session.flush()
            result = await recalculate(session, [beatmap_id])
            statistics = (
                await session.exec(
                    select(UserStatistics).where(UserStatistics.user_id == user_id, UserStatistics.mode == mode)
                )
            ).one()
            expected = before - 2 * row.pp * 0.95**index
            assert abs(statistics.pp - expected) < 0.1, (statistics.pp, expected)
            assert user_id in await negative_score_users(session)
            await session.delete(rule)
            await session.flush()
            await recalculate(session, [beatmap_id])
            assert abs(statistics.pp - before) < 0.1
            print(
                {
                    "mysql_weighted_subtraction": "passed",
                    "restore": "passed",
                    "affected_user_modes": len(result.affected_user_modes),
                }
            )
        finally:
            await session.rollback()
            print("Transaction rolled back; no penalty rules saved.")


if __name__ == "__main__":
    asyncio.run(run())
