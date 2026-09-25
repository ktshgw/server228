"""Build a frozen match pool from the reusable SOMSAI map warehouse."""

from __future__ import annotations

import secrets

from app.database import SomsaiMap
from app.features.somsai.services.somsai_party_service import reject
from app.features.somsai.services.somsai_rank_pool import normalise_rank, rank_from_rating, rule_for

from sqlmodel import select
from sqlmodel.ext.asyncio.session import AsyncSession


def _slot_payload(row: SomsaiMap) -> dict:
    stats = row.stats
    return {
        "id": row.slot, "label": row.slot, "category": row.category,
        "beatmap_id": row.beatmap_id, "beatmapset_id": row.beatmapset_id, "checksum": row.checksum,
        "name": f"{row.artist} - {row.title} [{row.version}]", "artist": row.artist,
        "title": row.title, "version": row.version, "mods": row.mods,
        "difficulty_rating": stats.get("stars", 0), "bpm": stats.get("bpm", 0),
        "cs": stats.get("cs", 0), "ar": stats.get("ar", 0), "od": stats.get("od", 0),
        "hp": stats.get("hp", 0), "total_length": stats.get("length", 0),
        "display_stats": stats, "stats_are_modded": True,
        "cover_url": row.cover_url, "warehouse_map_id": row.id,
        "eligible_ranks": row.eligible_ranks,
    }


async def mixed_pool(
    session: AsyncSession,
    ruleset_id: int,
    variant_id: int,
    rating: float | None = None,
    *,
    rank: str | None = None,
) -> dict:
    target_rank = normalise_rank(rank) if rank else rank_from_rating(rating or 1000)
    rule = rule_for(target_rank)
    options: dict[str, list[SomsaiMap]] = {}
    for slot in rule.slots:
        rows = list((await session.exec(select(SomsaiMap).where(
            SomsaiMap.slot == slot, SomsaiMap.ruleset_id == ruleset_id, SomsaiMap.variant_id == variant_id,
        ))).all())
        options[slot] = [row for row in rows if target_rank in row.eligible_ranks]
    missing = [slot for slot, rows in options.items() if not rows]
    if missing:
        reject(f"В хранилище нет подходящих карт для {target_rank}: {', '.join(missing)}")

    rng = secrets.SystemRandom()
    candidates = {slot: list(rows) for slot, rows in options.items()}
    for rows in candidates.values():
        rng.shuffle(rows)
    owners: dict[int, str] = {}

    def assign(slot: str, visited: set[int]) -> bool:
        for row in candidates[slot]:
            if row.beatmap_id in visited:
                continue
            visited.add(row.beatmap_id)
            if row.beatmap_id not in owners or assign(owners[row.beatmap_id], visited):
                owners[row.beatmap_id] = slot
                return True
        return False

    for slot in sorted(candidates, key=lambda item: len(candidates[item])):
        if not assign(slot, set()):
            reject(f"Недостаточно разных карт, чтобы собрать {target_rank} без повторов")
    selected = {
        slot: rng.choice([row for row in options[slot] if row.beatmap_id == map_id])
        for map_id, slot in owners.items()
    }
    slots = [_slot_payload(selected[slot]) for slot in rule.slots]
    return {
        "id": 0, "revision": 1, "name": f"SOMSAI {target_rank}", "pool_rank": target_rank,
        "best_of": rule.best_of, "bans_per_team": rule.bans_per_team, "source_url": None,
        "slots": slots, "selection_kind": "warehouse", "mmr_window": None, "source_pools": [],
        "average_stars": round(sum(float(slot["difficulty_rating"]) for slot in slots) / len(slots), 2),
    }
