"""Public, per-pool Ranked MMR and world placement for website profiles."""

from typing import Any

from app.database import MatchmakingPool, MatchmakingUserStats, User
from app.models.score import GameMode
from app.service.ranked_elo_admin_service import current_elo

from sqlalchemy.orm import lazyload
from sqlmodel import col, func, select
from sqlmodel.ext.asyncio.session import AsyncSession


async def ranked_profile_payload(
    session: AsyncSession, user_id: int, mode: GameMode, mania_variant: int = 4
) -> dict[str, Any]:
    """Use one active queue; do not combine different pools or mania key counts.

    Spectator creates Elo state before the first completed match. Its default
    1500 is not an earned rating, so placement begins at contest_count > 0.
    Equal ratings use the same stable user-ID tie break as the site's PP rank.
    """
    variant = mania_variant if mode == GameMode.MANIA else 0
    if variant not in ({4, 7} if mode == GameMode.MANIA else {0}):
        raise ValueError("Ranked mania variant must be 4 or 7")
    ruleset_id = (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA).index(mode)
    pool = (
        await session.exec(
            select(MatchmakingPool)
            .where(
                MatchmakingPool.ruleset_id == ruleset_id,
                MatchmakingPool.variant_id == variant,
                MatchmakingPool.type == "ranked_play",
                col(MatchmakingPool.ranked).is_(True),
                col(MatchmakingPool.active).is_(True),
            )
            .order_by(col(MatchmakingPool.id))
            .limit(1)
        )
    ).first()
    result: dict[str, Any] = {
        "pool_id": pool.id if pool else None,
        "variant_id": variant,
        "mmr": None,
        "global_rank": None,
    }
    if pool is None:
        return result

    elo = col(MatchmakingUserStats.elo_data)
    rating = func.coalesce(elo["approximate_posterior"]["mu"].as_float(), 1500.0)
    ranking = (
        select(
            MatchmakingUserStats.id,
            func.row_number().over(order_by=(rating.desc(), col(MatchmakingUserStats.user_id))).label("global_rank"),
        )
        .join(User, col(User.id) == col(MatchmakingUserStats.user_id))
        .where(
            MatchmakingUserStats.pool_id == pool.id,
            elo["contest_count"].as_integer() > 0,
            col(User.is_active).is_(True),
            col(User.is_bot).is_(False),
            ~User.is_restricted_query(col(User.id)),
        )
        .subquery()
    )
    row = (
        await session.exec(
            select(MatchmakingUserStats, ranking.c.global_rank)
            .join(ranking, ranking.c.id == col(MatchmakingUserStats.id))
            .options(lazyload("*"))
            .where(MatchmakingUserStats.user_id == user_id)
        )
    ).first()
    if row is not None:
        stats, position = row
        result["mmr"] = current_elo(stats)
        result["global_rank"] = int(position)
    return result
