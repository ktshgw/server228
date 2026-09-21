"""Retire historic Difficulty Adjust PP without changing gameplay history."""

from app.config import settings
from app.database import Beatmap, Beatmapset, BestScore, Score, UserStatistics
from app.database.score import calculate_user_pp
from app.models.mods import mods_can_get_pp
from app.models.score import GameMode
from app.service.beatmap_ranking_service import get_effective_beatmap_policies

from sqlalchemy.orm import lazyload
from sqlmodel import col, func, select
from sqlmodel.ext.asyncio.session import AsyncSession


def has_difficulty_adjust(mods: object) -> bool:
    return isinstance(mods, list) and any(
        isinstance(mod, dict) and str(mod.get("acronym", "")).upper() == "DA" for mod in mods
    )


def replacement_is_eligible(score: Score, *, map_pp_enabled: bool, checksum: str) -> bool:
    return (
        score.processed
        and score.passed
        and score.ranked
        and map_pp_enabled
        and score.map_md5 == checksum
        and score.pp > 0
        and not has_difficulty_adjust(score.mods)
        and mods_can_get_pp(int(score.gamemode), score.mods)
    )


async def retire_difficulty_adjust_pp(session: AsyncSession) -> dict:
    """Stage the repair; caller commits (or rolls back) and invalidates caches.

    Lock order matches score processing: mapsets, user statistics, scores,
    maps, policies. Rebuild only map/mode bests with a historic DA play. All
    play counts, score totals, medals and replay records stay untouched.
    Repeating the repair is safe, including after a cache refresh failure.
    """

    da_rows = (
        await session.exec(
            select(Score.id, Score.user_id, Score.gamemode, Score.beatmap_id).where(
                func.json_contains(Score.mods, '{"acronym":"DA"}') == 1
            )
        )
    ).all()
    if not da_rows:
        return {"scores": [], "profiles": []}

    map_ids = sorted({row[3] for row in da_rows})
    user_modes = sorted(
        {(row[1], GameMode(row[2])) for row in da_rows}, key=lambda row: (row[0], int(row[1]), row[1].value)
    )
    mapset_ids = (
        await session.exec(select(Beatmap.beatmapset_id).where(col(Beatmap.id).in_(map_ids)).distinct())
    ).all()
    await session.exec(
        select(Beatmapset)
        .options(lazyload("*"))
        .where(col(Beatmapset.id).in_(mapset_ids))
        .order_by(col(Beatmapset.id))
        .with_for_update()
    )
    stats = {}
    for user_id, mode in user_modes:
        row = (
            await session.exec(
                select(UserStatistics)
                .where(UserStatistics.user_id == user_id, UserStatistics.mode == mode)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if row is not None:
            stats[(user_id, mode)] = row

    scores = (
        await session.exec(
            select(Score)
            .options(lazyload("*"))
            .where(col(Score.beatmap_id).in_(map_ids), col(Score.user_id).in_({row[1] for row in da_rows}))
            .order_by(col(Score.id))
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).all()
    beatmaps = (
        await session.exec(
            select(Beatmap)
            .options(lazyload("*"))
            .where(col(Beatmap.id).in_(map_ids))
            .order_by(col(Beatmap.id))
            .with_for_update()
        )
    ).all()
    policies = await get_effective_beatmap_policies(session, beatmaps, for_update=True)
    checksums = {beatmap.id: beatmap.checksum for beatmap in beatmaps}
    changed_scores = []
    for score in scores:
        if has_difficulty_adjust(score.mods):
            changed_scores.append({"id": score.id, "previous_pp": score.pp})
            score.pp = 0
            session.add(score)

    keys = {(row[1], GameMode(row[2]), row[3]) for row in da_rows}
    replacements: dict[tuple[int, GameMode, int], Score] = {}
    for score in scores:
        key = (score.user_id, score.gamemode, score.beatmap_id)
        policy = policies.get(score.beatmap_id)
        if (
            key not in keys
            or policy is None
            or not replacement_is_eligible(
                score,
                map_pp_enabled=policy.pp_enabled or settings.enable_all_beatmap_pp,
                checksum=checksums[score.beatmap_id],
            )
        ):
            continue
        previous = replacements.get(key)
        if previous is None or score.pp > previous.pp:
            replacements[key] = score

    for user_id, mode, map_id in sorted(keys, key=lambda row: (row[0], row[1].value, row[2])):
        old_bests = (
            await session.exec(
                select(BestScore)
                .where(BestScore.user_id == user_id, BestScore.gamemode == mode, BestScore.beatmap_id == map_id)
                .with_for_update()
            )
        ).all()
        for best in old_bests:
            await session.delete(best)
        await session.flush()
        replacement = replacements.get((user_id, mode, map_id))
        if replacement is not None:
            session.add(
                BestScore(
                    user_id=user_id,
                    gamemode=mode,
                    beatmap_id=map_id,
                    score_id=replacement.id,
                    pp=replacement.pp,
                    acc=replacement.accuracy,
                )
            )
    await session.flush()

    profiles = []
    for (user_id, mode), statistics in stats.items():
        previous_pp = statistics.pp
        statistics.pp, statistics.hit_accuracy = await calculate_user_pp(session, user_id, mode, for_update=True)
        session.add(statistics)
        profiles.append({"user_id": user_id, "mode": mode.value, "previous_pp": previous_pp, "pp": statistics.pp})
    await session.flush()
    return {"scores": changed_scores, "profiles": profiles}
