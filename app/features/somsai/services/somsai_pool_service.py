"""Tournament catalogue edits and canonical, revision-checked match snapshots."""

from copy import deepcopy
import math
from typing import Any

from app.database import Beatmap, Beatmapset, SomsaiPool
from app.features.somsai.models.somsai_admin import CATEGORY_MODS, SomsaiPoolSpec, SomsaiSlotSpec
from app.fetcher import Fetcher

from fastapi import HTTPException
from sqlalchemy.orm import lazyload
from sqlmodel import col, delete, select
from sqlmodel.ext.asyncio.session import AsyncSession


def pool_skill_mmr(slots: list[dict]) -> int | None:
    """Catalogue estimate, not a measured player rating or source rank.

    Inverse of SOMSAI's established star-to-MMR scale. Recomputed from canonical
    map difficulty on match creation, so stale import metadata cannot pick maps.
    """
    stars = [
        slot["difficulty_rating"]
        for slot in slots
        if isinstance(slot.get("difficulty_rating"), (int, float))
        and math.isfinite(slot["difficulty_rating"])
        and slot["difficulty_rating"] > 0
    ]
    return round(max(0, min(5000, 1000 + 220 * (sum(stars) / len(stars) - 3.5)))) if stars else None


def pool_payload(pool: SomsaiPool) -> dict[str, Any]:
    data = pool.model_dump()
    stars = [
        slot["difficulty_rating"] for slot in pool.slots if isinstance(slot.get("difficulty_rating"), (int, float))
    ]
    data["average_stars"] = round(sum(stars) / len(stars), 2) if stars else None
    data["estimated_mmr"] = pool_skill_mmr(pool.slots)
    return data


def _spec(pool: SomsaiPool) -> SomsaiPoolSpec:
    data = {key: value for key, value in pool.model_dump().items() if key in SomsaiPoolSpec.model_fields}
    data["slots"] = [
        {key: value for key, value in slot.items() if key in SomsaiSlotSpec.model_fields} for slot in pool.slots
    ]
    return SomsaiPoolSpec.model_validate(data)


async def _canonical_slots(session: AsyncSession, spec: SomsaiPoolSpec) -> tuple[list[dict[str, Any]], list[str]]:
    ids = [slot.beatmap_id for slot in spec.slots]
    rows = (
        (
            await session.exec(
                select(Beatmap, Beatmapset)
                .join(Beatmapset, col(Beatmapset.id) == col(Beatmap.beatmapset_id))
                .where(col(Beatmap.id).in_(ids))
                .options(lazyload("*"))
            )
        ).all()
        if ids
        else []
    )
    maps = {beatmap.id: (beatmap, beatmapset) for beatmap, beatmapset in rows}
    slots: list[dict[str, Any]] = []
    errors = []
    for slot in spec.slots:
        item = slot.model_dump()
        item.update(category=slot.category, mods=deepcopy(CATEGORY_MODS[slot.category]), label=slot.id)
        entry = maps.get(slot.beatmap_id)
        if entry is None:
            errors.append(f"{slot.id}: карта {slot.beatmap_id} отсутствует в локальном каталоге")
        else:
            beatmap, beatmapset = entry
            if int(beatmap.mode) != spec.ruleset_id or (spec.ruleset_id == 3 and beatmap.cs != spec.variant_id):
                errors.append(f"{slot.id}: режим карты не соответствует пулу")
            if beatmap.deleted_at or not beatmap.checksum:
                errors.append(f"{slot.id}: карта удалена или не имеет контрольной суммы")
            elif beatmapset.download_disabled:
                errors.append(f"{slot.id}: скачивание карты недоступно")
            elif slot.checksum and slot.checksum != beatmap.checksum:
                errors.append(f"{slot.id}: контрольная сумма источника отличается от локальной карты")
            item.update(
                checksum=slot.checksum or beatmap.checksum,
                beatmapset_id=beatmap.beatmapset_id,
                name=f"{beatmapset.artist} - {beatmapset.title} [{beatmap.version}]",
                difficulty_rating=beatmap.difficulty_rating,
                bpm=beatmap.bpm,
                ar=beatmap.ar,
                cs=beatmap.cs,
                od=beatmap.accuracy,
                hp=beatmap.drain,
                total_length=beatmap.total_length,
                artist=beatmapset.artist,
                title=beatmapset.title,
                version=beatmap.version,
                hit_length=beatmap.hit_length,
                count_circles=beatmap.count_circles,
                count_sliders=beatmap.count_sliders,
                count_spinners=beatmap.count_spinners,
            )
        slots.append(item)
    return slots, errors


def _format_errors(spec: SomsaiPoolSpec) -> list[str]:
    errors = []
    if sum(slot.id == "TB" for slot in spec.slots) != 1:
        errors.append("Для матча нужен ровно один TB")
    required = spec.best_of - 1 + 2 * spec.bans_per_team
    if sum(slot.id != "TB" for slot in spec.slots) < required:
        errors.append(
            f"Для BO{spec.best_of} и {spec.bans_per_team} бана с каждой стороны нужно {required} обычных карт + TB"
        )
    return errors


async def preview_pool(session: AsyncSession, spec: SomsaiPoolSpec) -> dict[str, Any]:
    slots, errors = await _canonical_slots(session, spec)
    errors.extend(_format_errors(spec))
    return {"slots": slots, "errors": errors, "playable": not errors}


async def get_playable_pool_slots(session: AsyncSession, pool: SomsaiPool) -> list[dict[str, Any]]:
    result = await preview_pool(session, _spec(pool))
    if result["errors"]:
        raise HTTPException(409, {"message": "Турнирный пул сейчас недоступен", "errors": result["errors"]})
    return result["slots"]


async def prepare_missing_metadata(
    session: AsyncSession, spec: SomsaiPoolSpec, fetcher: Fetcher
) -> dict[str, list[Any]]:
    """Read upstream before taking any pool edit lock. No archive/raw map downloads."""
    ids = {slot.beatmap_id for slot in spec.slots}
    existing = set((await session.exec(select(Beatmap.id).where(col(Beatmap.id).in_(ids)))).all()) if ids else set()
    pending_maps = []
    pending_sets: dict[int, Any] = {}
    for beatmap_id in sorted(ids - existing):
        try:
            response = await fetcher.get_beatmap(beatmap_id)
            if response.get("id") != beatmap_id:
                raise ValueError("beatmap identity mismatch")
            beatmap = await Beatmap.from_resp_no_save(session, response)
            pending_maps.append(beatmap)
            set_id = beatmap.beatmapset_id
            if set_id not in pending_sets and await session.get(Beatmapset, set_id) is None:
                set_response = await fetcher.get_beatmapset(set_id)
                if set_response.get("id") != set_id:
                    raise ValueError("beatmapset identity mismatch")
                pending_sets[set_id] = await Beatmapset.from_resp_no_save(set_response)
        except Exception as exc:
            raise HTTPException(422, f"Не удалось получить метаданные карты {beatmap_id}; пул не изменён") from exc
    return {"beatmaps": pending_maps, "beatmapsets": list(pending_sets.values())}


async def save_pool(
    session: AsyncSession,
    spec: SomsaiPoolSpec,
    pool_id: int | None,
    expected_revision: int | None,
    metadata: dict[str, list[Any]] | None = None,
    provenance: dict[str, Any] | None = None,
    append: bool = False,
) -> tuple[dict[str, Any] | None, SomsaiPool]:
    pool = None
    if pool_id is not None:
        pool = (
            await session.exec(
                select(SomsaiPool)
                .where(SomsaiPool.id == pool_id)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if pool is None:
            raise HTTPException(404, "Турнирный пул не найден")
        if expected_revision != pool.revision:
            raise HTTPException(409, "Пул уже изменён другим администратором. Откройте его заново")
    elif expected_revision is not None or append:
        raise HTTPException(422, "Для нового пула версия и пополнение не применяются")
    before = pool_payload(pool) if pool else None
    if append and pool:
        old = _spec(pool).slots
        counts = {
            category: max(
                [int(slot.id[2:]) for slot in old if slot.category == category and category != "TB"], default=0
            )
            for category in CATEGORY_MODS
        }
        additions = []
        for slot in spec.slots:
            counts[slot.category] += 1
            additions.append(
                slot.model_copy(
                    update={"id": "TB" if slot.category == "TB" else f"{slot.category}{counts[slot.category]}"}
                )
            )
        try:
            spec = SomsaiPoolSpec.model_validate({**spec.model_dump(), "slots": [*old, *additions]})
        except ValueError as exc:
            raise HTTPException(422, "Пополнение содержит повтор карты, TB или больше 64 слотов") from exc
    for model, key in ((Beatmapset, "beatmapsets"), (Beatmap, "beatmaps")):
        for item in (metadata or {}).get(key, []):
            if await session.get(model, item.id) is None:
                session.add(item)
        await session.flush()
    result = await preview_pool(session, spec)
    if spec.active and result["errors"]:
        raise HTTPException(422, {"message": "Пул нельзя включить", "errors": result["errors"]})
    pool = pool or SomsaiPool(name=spec.name)
    for key, value in spec.model_dump(exclude={"slots"}).items():
        setattr(pool, key, value)
    pool.slots = result["slots"]
    if provenance:
        source = {
            key: provenance[key]
            for key in ("source_kind", "source_id", "source_round", "source_url", "source_metadata")
        }
        if append:
            history = list(pool.source_metadata.get("imports", []))[-19:]
            history.append(source)
            pool.source_metadata = {**pool.source_metadata, "imports": history}
        else:
            for key, value in source.items():
                setattr(pool, key, value)
    pool.revision = (pool.revision + 1) if before else 1
    session.add(pool)
    await session.flush()
    return before, pool


async def delete_pool(session: AsyncSession, pool_id: int, revision: int) -> dict[str, Any]:
    pool = (
        await session.exec(
            select(SomsaiPool)
            .where(SomsaiPool.id == pool_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if pool is None:
        raise HTTPException(404, "Турнирный пул не найден")
    if pool.revision != revision:
        raise HTTPException(409, "Пул уже изменён другим администратором")
    before = pool_payload(pool)
    # Core stores pool_id as historical provenance, without a destructive FK cascade.
    await session.exec(delete(SomsaiPool).where(col(SomsaiPool.id) == pool_id))
    return before
