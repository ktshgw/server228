"""Ranked catalogue previews and transactional preset changes."""

from typing import Any

from app.database import Beatmap, Beatmapset, MatchmakingMapPreset, MatchmakingPool
from app.models.ranked_admin import RankedPresetSpec
from app.service.matchmaking_service import MINIMUM_RANKED_BEATMAPS, minimum_ranked_beatmaps, ranked_beatmaps
from app.service.ranked_catalogue_service import catalogue_sync_status

from fastapi import HTTPException
from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


def preset_payload(preset: MatchmakingMapPreset) -> dict[str, Any]:
    return preset.model_dump()


async def preview_preset(session: AsyncSession, spec: RankedPresetSpec) -> dict[str, Any]:
    preset = MatchmakingMapPreset(**spec.model_dump())
    maps = await ranked_beatmaps(session, spec.ruleset_id, spec.variant_id, preset=preset)
    ids = [item["beatmap_id"] for item in maps]
    rows = (
        (
            await session.exec(
                select(Beatmap, Beatmapset)
                .options(lazyload("*"))
                .join(Beatmapset, col(Beatmap.beatmapset_id) == col(Beatmapset.id))
                .where(col(Beatmap.id).in_(ids[:100]))
                .order_by(col(Beatmap.difficulty_rating), col(Beatmap.id))
            )
        ).all()
        if ids
        else []
    )
    eligible_ids = set(ids)
    return {
        "count": len(ids),
        "minimum": MINIMUM_RANKED_BEATMAPS,
        "excluded_ids": [item for item in (spec.beatmap_ids or []) if item not in eligible_ids],
        "items": [
            {
                "id": beatmap.id,
                "version": beatmap.version,
                "stars": beatmap.difficulty_rating,
                "length": beatmap.hit_length,
                "artist": beatmapset.artist,
                "title": beatmapset.title,
                "beatmapset_id": beatmap.beatmapset_id,
            }
            for beatmap, beatmapset in rows
        ],
    }


async def ranked_catalogue(session: AsyncSession) -> dict[str, Any]:
    presets = (await session.exec(select(MatchmakingMapPreset).order_by(col(MatchmakingMapPreset.id)))).all()
    pools = (
        await session.exec(
            select(MatchmakingPool)
            .where(MatchmakingPool.type == "ranked_play", col(MatchmakingPool.ranked).is_(True))
            .order_by(col(MatchmakingPool.id))
        )
    ).all()
    items = []
    for pool in pools:
        count = len(await ranked_beatmaps(session, pool.ruleset_id, pool.variant_id, pool.id))
        items.append(
            {
                "id": pool.id,
                "name": pool.name,
                "ruleset_id": pool.ruleset_id,
                "variant_id": pool.variant_id,
                "active": pool.active,
                "preset_id": pool.beatmap_preset_id,
                "beatmap_count": count,
                "lobby_size": pool.lobby_size,
                "minimum": minimum_ranked_beatmaps(pool),
            }
        )
    return {
        "pools": items,
        "presets": [preset_payload(item) for item in presets],
        "minimum": MINIMUM_RANKED_BEATMAPS,
        "catalogue_sync": await catalogue_sync_status(),
    }


async def save_preset(
    session: AsyncSession, spec: RankedPresetSpec, preset_id: int | None, expected_revision: int | None
) -> tuple[dict[str, Any] | None, MatchmakingMapPreset]:
    data = {name: getattr(spec, name) for name in RankedPresetSpec.model_fields}
    before = None
    if preset_id is None:
        if expected_revision is not None:
            raise HTTPException(422, "A new preset has no revision")
        preset = MatchmakingMapPreset(**data)
    else:
        preset = (
            await session.exec(
                select(MatchmakingMapPreset)
                .where(MatchmakingMapPreset.id == preset_id)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if preset is None:
            raise HTTPException(404, "Ranked preset not found")
        if preset.revision != expected_revision:
            raise HTTPException(409, "Ranked preset changed; refresh and try again")
        before = preset_payload(preset)
        assigned = (
            await session.exec(select(MatchmakingPool).where(MatchmakingPool.beatmap_preset_id == preset_id))
        ).all()
        if assigned:
            if any((pool.ruleset_id, pool.variant_id) != (spec.ruleset_id, spec.variant_id) for pool in assigned):
                raise HTTPException(409, "An active preset cannot change ruleset")
            await require_playable_preset(
                session, RankedPresetSpec(**data), minimum=max(minimum_ranked_beatmaps(pool) for pool in assigned)
            )
        for name, value in data.items():
            setattr(preset, name, value)
        preset.revision += 1
    session.add(preset)
    await session.flush()
    return before, preset


async def require_playable_preset(
    session: AsyncSession, spec: RankedPresetSpec, *, minimum: int = MINIMUM_RANKED_BEATMAPS
) -> None:
    maps = await ranked_beatmaps(
        session, spec.ruleset_id, spec.variant_id, preset=MatchmakingMapPreset(**spec.model_dump())
    )
    if len(maps) < minimum:
        raise HTTPException(409, f"A Ranked preset needs at least {minimum} eligible beatmaps")


async def assign_preset(
    session: AsyncSession, pool_id: int, preset_id: int | None, expected_preset_id: int | None
) -> tuple[dict[str, Any], dict[str, Any]]:
    preset = None
    if preset_id is not None:
        preset = (
            await session.exec(
                select(MatchmakingMapPreset)
                .where(MatchmakingMapPreset.id == preset_id)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if preset is None:
            raise HTTPException(404, "Ranked preset not found")
    pool = (
        await session.exec(
            select(MatchmakingPool)
            .options(lazyload("*"))
            .where(MatchmakingPool.id == pool_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if pool is None or pool.type != "ranked_play" or not pool.ranked:
        raise HTTPException(404, "Ranked pool not found")
    if pool.beatmap_preset_id != expected_preset_id:
        raise HTTPException(409, "Ranked preset changed; refresh and try again")
    if preset is not None:
        if (pool.ruleset_id, pool.variant_id) != (preset.ruleset_id, preset.variant_id):
            raise HTTPException(422, "Preset ruleset does not match the pool")
        await require_playable_preset(
            session,
            RankedPresetSpec(**{key: getattr(preset, key) for key in RankedPresetSpec.model_fields}),
            minimum=minimum_ranked_beatmaps(pool),
        )
    elif len(await ranked_beatmaps(session, pool.ruleset_id, pool.variant_id)) < minimum_ranked_beatmaps(pool):
        raise HTTPException(409, f"A Ranked preset needs at least {minimum_ranked_beatmaps(pool)} eligible beatmaps")
    before = {"pool_id": pool.id, "preset_id": pool.beatmap_preset_id}
    pool.beatmap_preset_id = preset_id
    session.add(pool)
    await session.flush()
    return before, {"pool_id": pool.id, "preset_id": preset_id}
