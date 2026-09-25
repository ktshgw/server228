"""Transparent SOMSAI Elo policy; independent from PP and native Ranked Elo.

RomAI publishes its tournament workflow, but not its rating coefficients. The
following is SOMSAI's policy, not a claim to reproduce RomAI's private formula.
"""

from itertools import pairwise
import math

from app.database import User, UserStatistics
from app.database.statistics import public_ranking_conditions
from app.features.somsai.database.somsai import SomsaiRating
from app.helpers import utcnow
from app.models.score import GameMode

from sqlmodel import col, func, select
from sqlmodel.ext.asyncio.session import AsyncSession

_K_ANCHORS = [(0, 40), (600, 40), (1600, 32), (2300, 24), (2800, 16), (3500, 10), (5000, 10)]


def k_factor(rating: float, games: int) -> float:
    if games < 10:
        return 64.0
    r = max(0.0, min(5000.0, rating))
    for (r0, k0), (r1, k1) in pairwise(_K_ANCHORS):
        if r0 <= r <= r1:
            t = (r - r0) / (r1 - r0) if r1 != r0 else 0
            return k0 + t * (k1 - k0)
    return _K_ANCHORS[-1][1]


def initial_rating(rank: int | None, population: int) -> float:
    if rank is None or population < 1:
        return 1000.0
    percentile = 1 - math.log(max(1, rank)) / math.log(max(2, population))
    return round(1000 + 1000 * max(0, min(1, percentile)))


async def ensure_rating(
    session: AsyncSession, user_id: int, ruleset_id: int, variant_id: int, format: str
) -> SomsaiRating:
    row = (
        await session.exec(
            select(SomsaiRating).where(
                SomsaiRating.user_id == user_id,
                SomsaiRating.ruleset_id == ruleset_id,
                SomsaiRating.variant_id == variant_id,
                SomsaiRating.format == format,
            )
        )
    ).first()
    if row is not None:
        return row
    mode = (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA)[ruleset_id]
    ranking = (
        select(
            UserStatistics.user_id,
            func.row_number()
            .over(order_by=(col(UserStatistics.pp).desc(), col(UserStatistics.user_id)))
            .label("position"),
            func.count().over().label("population"),
        )
        .where(*public_ranking_conditions(mode))
        .subquery()
    )
    position = (
        await session.exec(select(ranking.c.position, ranking.c.population).where(ranking.c.user_id == user_id))
    ).first()
    rank, population = (int(position[0]), int(position[1])) if position else (None, 0)
    seed = initial_rating(rank, population)
    row = SomsaiRating(
        user_id=user_id,
        ruleset_id=ruleset_id,
        variant_id=variant_id,
        format=format,
        rating=seed,
        initial_rating=seed,
        initial_rank=rank,
        initial_population=population,
    )
    session.add(row)
    await session.flush()
    return row


async def rating_payload(session: AsyncSession, rating: SomsaiRating) -> dict:
    higher = (
        await session.exec(
            select(func.count())
            .select_from(SomsaiRating)
            .join(User, col(User.id) == col(SomsaiRating.user_id))
            .where(
                SomsaiRating.ruleset_id == rating.ruleset_id,
                SomsaiRating.variant_id == rating.variant_id,
                SomsaiRating.format == rating.format,
                col(User.is_active).is_(True),
                col(User.is_bot).is_(False),
                ~User.is_restricted_query(col(User.id)),
                (SomsaiRating.rating > rating.rating)
                | ((SomsaiRating.rating == rating.rating) & (SomsaiRating.user_id < rating.user_id)),
            )
        )
    ).one()
    return {
        "rating": round(rating.rating),
        "rank": higher + 1,
        "wins": rating.wins,
        "losses": rating.losses,
        "draws": rating.draws,
        "games": rating.games,
        "provisional": rating.games < 10,
        "last_delta": rating.last_delta,
        "initial_rank": rating.initial_rank,
        "initial_rating": rating.initial_rating,
    }


def performance_impacts(teams: list[list[int]], rounds: list[dict], winning_team: int | None) -> dict[int, int]:
    """1–100 summary of score share, round wins and the final team result."""
    impacts = {}
    for team_id, members in enumerate(teams):
        for uid in members:
            shares = []
            round_wins = 0
            for round_result in rounds:
                players = round_result.get("players", [])
                own = next((p["score"] for p in players if p["user_id"] == uid), 0)
                average = sum(p["score"] for p in players) / max(1, len(players))
                shares.append(min(2, own / average) if average > 0 else 0)
                round_wins += round_result.get("winner_team_id") == team_id
            quality = sum(shares) / max(1, len(shares))
            value = 10 + 30 * quality + 20 * round_wins / max(1, len(rounds))
            value += 20 if winning_team == team_id else (10 if winning_team is None else 0)
            impacts[uid] = round(max(1, min(100, value)))
    return impacts


async def settle_ratings(session: AsyncSession, match, teams: list[list[int]], winner: int | None) -> list[dict]:
    from app.features.somsai.services.somsai_rank_pool import rank_midpoint_rating

    impacts = performance_impacts(teams, match.state.get("history", []), winner)  # только для UI
    rows = {
        uid: await ensure_rating(session, uid, match.ruleset_id, match.variant_id, match.format)
        for team in teams
        for uid in team
    }
    averages = [sum(rows[uid].rating for uid in team) / len(team) for team in teams]
    expected = 1 / (1 + 10 ** (max(-4000, min(4000, averages[1] - averages[0])) / 400))
    changes = []
    for team_id, members in enumerate(teams):
        actual = 0.5 if winner is None else float(winner == team_id)
        expectation = expected if team_id == 0 else 1 - expected
        for uid in members:
            row = rows[uid]
            before = row.rating
            k = k_factor(before, row.games)
            adjustment = k * (actual - expectation)

            pool_rank = match.state.get("pool_rank")
            rank_gap = 0.0
            if pool_rank:
                rank_gap = (rank_midpoint_rating(pool_rank) - before) / 100
                direction = rank_gap if actual > expectation else -rank_gap
                adjustment *= max(0.6, min(1.5, 1 + 0.08 * direction))

            after = max(0, min(5000, before + adjustment))
            row.rating = round(after)  # Elo хранится целым числом
            row.last_delta = row.rating - round(before)
            row.games += 1
            row.wins += winner == team_id
            row.losses += winner is not None and winner != team_id
            row.draws += winner is None
            row.updated_at = utcnow()
            session.add(row)
            changes.append(
                {
                    "user_id": uid,
                    "before": round(before),
                    "after": row.rating,
                    "delta": row.last_delta,
                    "impact": impacts[uid],
                    "pool_rank_gap": round(rank_gap, 2),
                }
            )
    return changes
