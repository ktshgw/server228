"""Edit the same per-pool Elo data that spectator reads for Ranked Play."""

from copy import deepcopy
import hashlib
import json
import math
from typing import Any

from app.database import MatchmakingPool, MatchmakingUserStats, Room, RoomParticipatedUser
from app.helpers import utcnow
from app.models.room import MatchType

from sqlalchemy import or_
from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

MAX_ADMIN_ELO = 10_000


class RankedEloNotFoundError(ValueError):
    pass


class RankedEloConflictError(ValueError):
    pass


def elo_version(stats: MatchmakingUserStats | None) -> str:
    # Compare the full rating state, not the rounded value displayed by lazer.
    # This catches a completed match or another administrator's edit.
    snapshot = None if stats is None else {"id": stats.id, "elo_data": stats.elo_data}
    return hashlib.sha256(json.dumps(snapshot, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def current_elo(stats: MatchmakingUserStats | None) -> float | None:
    if stats is None:
        return None
    rating = (stats.elo_data or {}).get("approximate_posterior") or {}
    value = float(rating.get("mu", 1500))
    if not math.isfinite(value):
        raise ValueError("Stored Ranked Elo is not finite")
    return value


def elo_payload(pool: MatchmakingPool, stats: MatchmakingUserStats | None) -> dict[str, Any]:
    return {
        "pool_id": pool.id,
        "pool_name": pool.name,
        "ruleset_id": pool.ruleset_id,
        "variant_id": pool.variant_id,
        "active": pool.active,
        "elo": current_elo(stats),
        "contest_count": (stats.elo_data or {}).get("contest_count", 0) if stats else 0,
        "version": elo_version(stats),
    }


async def list_ranked_elo(session: AsyncSession, user_id: int) -> list[dict[str, Any]]:
    rows = await session.exec(
        select(MatchmakingPool, MatchmakingUserStats)
        .outerjoin(
            MatchmakingUserStats,
            (col(MatchmakingUserStats.pool_id) == col(MatchmakingPool.id))
            & (col(MatchmakingUserStats.user_id) == user_id),
        )
        .options(lazyload("*"))
        .where(MatchmakingPool.type == "ranked_play", col(MatchmakingPool.ranked).is_(True))
        .order_by(col(MatchmakingPool.ruleset_id), col(MatchmakingPool.variant_id), col(MatchmakingPool.id))
    )
    return [elo_payload(pool, stats) for pool, stats in rows.all()]


async def change_ranked_elo(
    session: AsyncSession, user_id: int, pool_id: int, elo: int, expected_version: str
) -> tuple[dict[str, Any], dict[str, Any]]:
    if type(elo) is not int or not 0 <= elo <= MAX_ADMIN_ELO:
        raise ValueError(f"Elo must be an integer between 0 and {MAX_ADMIN_ELO}")
    pool = await session.get(MatchmakingPool, pool_id)
    if pool is None or pool.type != "ranked_play" or not pool.ranked:
        raise RankedEloNotFoundError("Ranked pool not found")

    # A room's final rating calculation reads and writes its own snapshot.
    # Do not allow manual edits while that calculation can still be pending.
    active_room = (
        await session.exec(
            select(RoomParticipatedUser.room_id)
            .join(Room, col(Room.id) == col(RoomParticipatedUser.room_id))
            .where(
                RoomParticipatedUser.user_id == user_id,
                col(RoomParticipatedUser.left_at).is_(None),
                Room.type == MatchType.RANKED_PLAY,
                or_(col(Room.ends_at).is_(None), col(Room.ends_at) > utcnow()),
            )
            .limit(1)
        )
    ).first()
    if active_room is not None:
        raise RankedEloConflictError("Leave the active Ranked room before changing Elo")

    stats = (
        await session.exec(
            select(MatchmakingUserStats)
            .options(lazyload("*"))
            .where(MatchmakingUserStats.user_id == user_id, MatchmakingUserStats.pool_id == pool_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if elo_version(stats) != expected_version:
        raise RankedEloConflictError("Ranked Elo changed; refresh the player and try again")
    before = elo_payload(pool, stats)
    if stats is not None and current_elo(stats) == elo:
        raise RankedEloConflictError("This Ranked Elo is already set")

    if stats is None:
        stats = MatchmakingUserStats(user_id=user_id, pool_id=pool_id)
        data: dict[str, Any] = {
            "initial_rating": {"mu": elo, "sig": 150},
            "contest_count": 0,
            "approximate_posterior": {"mu": elo, "sig": 150},
        }
    else:
        data = deepcopy(stats.elo_data or {})
        data.setdefault("initial_rating", {"mu": 1500, "sig": 150})
        data.setdefault("contest_count", 0)
        rating = dict(data.get("approximate_posterior") or {"sig": 150})
        rating["mu"] = elo
        data["approximate_posterior"] = rating
    stats.elo_data = data
    stats.updated_at = utcnow()
    session.add(stats)
    await session.flush()
    return before, elo_payload(pool, stats)
