"""Official Ranked Play catalogue rules and optional operator presets."""

from typing import Any

from app.database import Beatmap, Beatmapset, MatchmakingMapPreset, MatchmakingPool
from app.models.beatmap import BeatmapRankStatus
from app.models.score import GameMode
from app.service.beatmap_ranking_service import get_effective_beatmap_policies

from sqlalchemy.dialects.mysql import insert
from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

# Ranked Play deals five distinct cards per participant.
MINIMUM_RANKED_BEATMAPS = 10
RANKED_QUEUES = ((0, 0), (1, 0), (2, 0), (3, 4), (3, 7))
OFFICIAL_MIN_HIT_LENGTH = 60
OFFICIAL_MAX_HIT_LENGTH = 300


def minimum_ranked_beatmaps(pool: MatchmakingPool) -> int:
    return max(MINIMUM_RANKED_BEATMAPS, pool.lobby_size * 5)


async def ranked_beatmaps(
    session: AsyncSession,
    ruleset_id: int,
    variant: int = 0,
    pool_id: int | None = None,
    *,
    preset: MatchmakingMapPreset | None = None,
    limit: int | None = None,
) -> list[dict[str, Any]]:
    if ruleset_id not in range(4) or (ruleset_id == 3 and variant not in (4, 7)):
        return []
    if pool_id is not None:
        pool = await session.get(MatchmakingPool, pool_id)
        if pool is None or (pool.ruleset_id, pool.variant_id) != (ruleset_id, variant):
            return []
        if pool.beatmap_preset_id is not None:
            preset = await session.get(MatchmakingMapPreset, pool.beatmap_preset_id)
            if preset is None:
                return []
    if preset is not None and (preset.ruleset_id, preset.variant_id) != (ruleset_id, variant):
        return []
    statement = (
        select(Beatmap)
        .join(Beatmapset, col(Beatmap.beatmapset_id) == col(Beatmapset.id))
        .options(lazyload("*"))
        .where(
            Beatmap.mode == GameMode.from_int(ruleset_id),
            col(Beatmap.deleted_at).is_(None),
            col(Beatmapset.download_disabled).is_(False),
            col(Beatmap.hit_length).between(
                preset.min_length if preset else OFFICIAL_MIN_HIT_LENGTH,
                preset.max_length if preset else OFFICIAL_MAX_HIT_LENGTH,
            ),
            col(Beatmap.checksum).is_not(None),
        )
        .order_by(col(Beatmap.id))
    )
    if ruleset_id == 3:
        statement = statement.where(Beatmap.cs == variant)
    if preset is None:
        # Mirrors ppy/osu-server-spectator's global pool query. The scheduled
        # catalogue import discovers FA maps independently of client requests.
        statement = statement.where(
            col(Beatmapset.track_id).is_not(None),
            col(Beatmap.beatmap_status).in_((BeatmapRankStatus.RANKED, BeatmapRankStatus.APPROVED)),
        )
    if preset:
        statement = statement.where(col(Beatmap.difficulty_rating).between(preset.min_stars, preset.max_stars))
        if preset.beatmap_ids is not None:
            statement = statement.where(col(Beatmap.id).in_(preset.beatmap_ids))
    result = []
    last_id = None
    # Availability checks need only enough cards for the hands, not the full
    # catalogue. Apply policy before counting; continue past excluded pages.
    batch_size = max(64, (limit or 0) * 2)
    while True:
        page = statement
        if limit is not None:
            page = page.limit(batch_size)
            if last_id is not None:
                page = page.where(Beatmap.id > last_id)
        beatmaps = list((await session.exec(page)).all())
        policies = await get_effective_beatmap_policies(session, beatmaps)
        for beatmap in beatmaps:
            if not policies[beatmap.id].pp_enabled or not policies[beatmap.id].leaderboard_enabled:
                continue
            result.append(
                {
                    "beatmap_id": beatmap.id,
                    "beatmapset_id": beatmap.beatmapset_id,
                    "checksum": beatmap.checksum,
                    "approved": int(policies[beatmap.id].status),
                    "difficulty_rating": beatmap.difficulty_rating,
                    "total_length": beatmap.total_length,
                    "playmode": ruleset_id,
                    "osu_file_version": 14,
                }
            )
            if limit is not None and len(result) >= limit:
                return result
        if limit is None or len(beatmaps) < batch_size:
            return result
        last_id = beatmaps[-1].id


async def ranked_pool_available(session: AsyncSession, pool: MatchmakingPool) -> bool:
    if not pool.active:
        return False
    required = minimum_ranked_beatmaps(pool)
    return len(await ranked_beatmaps(session, pool.ruleset_id, pool.variant_id, pool.id, limit=required)) >= required


async def ranked_pools(session: AsyncSession) -> list[dict[str, Any]]:
    """Seed default queues once, preserving operator edits thereafter."""
    for ruleset_id, variant in RANKED_QUEUES:
        for name, lobby_size in (("SOMS!", 2),):
            statement = insert(MatchmakingPool).values(
                ruleset_id=ruleset_id,
                variant_id=variant,
                name=name,
                type="ranked_play",
                ranked=True,
                active=True,
                lobby_size=lobby_size,
                rating_search_radius=150,
                rating_search_radius_max=9999,
                rating_search_radius_exp=15,
                use_dmr=False,
            )
            await session.execute(statement.on_duplicate_key_update(id=MatchmakingPool.id))
    await session.commit()
    pools = list((await session.exec(select(MatchmakingPool).where(col(MatchmakingPool.active).is_(True)))).all())
    result = []
    for pool in pools:
        if pool.type == "ranked_play" and pool.lobby_size == 2:
            result.append(pool.model_dump())
    return result
