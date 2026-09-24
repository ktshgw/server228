"""Reconcile derived score state after a local ranking-policy change.

Scores and replay files are immutable history.  Local unranking only removes
the derived rows which make a score appear on a beatmap leaderboard or count
towards a user's PP, then repairs the affected aggregate statistics.
"""

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Protocol

from app.database.best_scores import BestScore
from app.database.score import Score, calculate_user_pp
from app.database.statistics import UserStatistics, change_grade_count
from app.database.total_score_best_scores import TotalScoreBestScore
from app.models.beatmap import BeatmapRankStatus
from app.models.score import GameMode, Rank

from sqlmodel import col, exists, func, select, update
from sqlmodel.ext.asyncio.session import AsyncSession


class PolicyFlags(Protocol):
    """Minimal policy surface accepted by the pure transition planner."""

    @property
    def leaderboard_enabled(self) -> bool: ...

    @property
    def pp_enabled(self) -> bool: ...

    @property
    def status(self) -> BeatmapRankStatus: ...


@dataclass(frozen=True, slots=True)
class ScorePolicyTransition:
    """Derived score features which must be retired for one difficulty."""

    beatmap_id: int
    disable_leaderboard: bool
    disable_pp: bool
    disable_ranked_score: bool
    reset_ranked_score_epoch: bool = False


@dataclass(frozen=True, slots=True)
class ScoreReconciliationResult:
    """Summary of database and cache state affected by reconciliation."""

    leaderboard_rows_removed: int = 0
    pp_rows_removed: int = 0
    ranked_score_contributions_removed: int = 0
    affected_user_modes: frozenset[tuple[int, GameMode]] = frozenset()


def plan_score_policy_transitions(
    before: Mapping[int, PolicyFlags],
    after: Mapping[int, PolicyFlags],
    *,
    enable_all_beatmap_leaderboard: bool = False,
    enable_all_beatmap_pp: bool = False,
) -> tuple[ScorePolicyTransition, ...]:
    """Plan retirement/reset work without promoting historical raw scores.

    Requiring identical beatmap IDs makes accidental partial snapshots fail
    loudly. When ranked-score eligibility starts after a leaderboard-only
    epoch, its old leaderboard indexes are removed so they cannot suppress the
    first genuinely eligible contribution. Raw scores are never promoted.
    """

    if before.keys() != after.keys():
        raise ValueError("Before and after policy snapshots must cover the same beatmaps")

    transitions: list[ScorePolicyTransition] = []
    for beatmap_id in sorted(before):
        previous = before[beatmap_id]
        current = after[beatmap_id]
        previous_leaderboard = previous.leaderboard_enabled or enable_all_beatmap_leaderboard
        current_leaderboard = current.leaderboard_enabled or enable_all_beatmap_leaderboard
        previous_pp = previous.pp_enabled or enable_all_beatmap_pp
        current_pp = current.pp_enabled or enable_all_beatmap_pp
        disable_leaderboard = previous_leaderboard and not current_leaderboard
        disable_pp = previous_pp and not current_pp
        previous_ranked_score = (previous.status.ranked() and previous_leaderboard) or enable_all_beatmap_pp
        current_ranked_score = (current.status.ranked() and current_leaderboard) or enable_all_beatmap_pp
        disable_ranked_score = previous_ranked_score and not current_ranked_score
        reset_ranked_score_epoch = not previous_ranked_score and current_ranked_score
        if disable_leaderboard or disable_pp or disable_ranked_score or reset_ranked_score_epoch:
            transitions.append(
                ScorePolicyTransition(
                    beatmap_id=beatmap_id,
                    disable_leaderboard=disable_leaderboard,
                    disable_pp=disable_pp,
                    disable_ranked_score=disable_ranked_score,
                    reset_ranked_score_epoch=reset_ranked_score_epoch,
                )
            )
    return tuple(transitions)


def _global_best_rows(
    rows: Sequence[TotalScoreBestScore],
) -> dict[tuple[int, int, GameMode], TotalScoreBestScore]:
    """Select the global best from the mod-specific association rows.

    The submission path defines global best by ``total_score``.  The oldest
    score wins a tie, matching the fact that an equal score does not replace
    the grade/ranked-score contribution already recorded in statistics.
    """

    best: dict[tuple[int, int, GameMode], TotalScoreBestScore] = {}
    for row in rows:
        key = (row.user_id, row.beatmap_id, row.gamemode)
        current = best.get(key)
        if (
            current is None
            or row.total_score > current.total_score
            or (row.total_score == current.total_score and row.score_id < current.score_id)
        ):
            best[key] = row
    return best


def _decrement_grade(statistics: UserStatistics, rank: Rank) -> None:
    change_grade_count(statistics, rank, -1)


def _user_mode_sort_key(value: tuple[int, GameMode]) -> tuple[int, int, str]:
    """Return a total lock order, including special modes sharing an ID."""

    user_id, mode = value
    return user_id, int(mode), mode.value


async def _retire_ranked_score_contributions(
    rows: Sequence[TotalScoreBestScore],
    beatmap_ids: set[int],
    statistics_by_user_mode: Mapping[tuple[int, GameMode], UserStatistics],
) -> tuple[int, set[tuple[int, GameMode]]]:
    """Remove one aggregate contribution per user/map/mode global best."""

    removed = 0
    affected_user_modes: set[tuple[int, GameMode]] = set()
    for (user_id, beatmap_id, mode), row in _global_best_rows(rows).items():
        if beatmap_id not in beatmap_ids:
            continue
        statistics = statistics_by_user_mode.get((user_id, mode))
        if statistics is None:
            continue

        # ranked_score tracks the current global best delta, not every
        # mod-specific association. total_score is lifetime play score and is
        # deliberately left unchanged while the raw Score rows remain.
        statistics.ranked_score = max(0, statistics.ranked_score - row.score.get_display_score())
        _decrement_grade(statistics, row.rank)
        affected_user_modes.add((user_id, mode))
        removed += 1
    return removed, affected_user_modes


async def _repair_maximum_combo(
    session: AsyncSession,
    affected_user_modes: set[tuple[int, GameMode]],
    statistics_by_user_mode: Mapping[tuple[int, GameMode], UserStatistics],
) -> None:
    """Rebuild combo from immutable post-migration eligibility snapshots.

    Legacy rows have no trustworthy eligibility snapshot. If any exist for a
    user/mode, preserve the historic aggregate as a conservative floor rather
    than incorrectly lowering it during an unrelated map transition.
    """

    for user_id, mode in sorted(affected_user_modes, key=_user_mode_sort_key):
        statistics = statistics_by_user_mode.get((user_id, mode))
        if statistics is None:
            continue
        maximum_combo = (
            await session.exec(
                select(func.max(Score.max_combo)).where(
                    Score.user_id == user_id,
                    Score.gamemode == mode,
                    col(Score.passed).is_(True),
                    col(Score.ranked_score_eligible).is_(True),
                    col(Score.ranking_eligibility_snapshotted).is_(True),
                )
            )
        ).one()
        has_legacy_scores = bool(
            (
                await session.exec(
                    select(
                        exists().where(
                            col(Score.user_id) == user_id,
                            col(Score.gamemode) == mode,
                            col(Score.ranking_eligibility_snapshotted).is_(False),
                        )
                    )
                )
            ).one()
        )
        rebuilt_maximum = int(maximum_combo or 0)
        statistics.maximum_combo = (
            max(statistics.maximum_combo, rebuilt_maximum) if has_legacy_scores else rebuilt_maximum
        )


async def _repair_pp(
    session: AsyncSession,
    affected_user_modes: set[tuple[int, GameMode]],
    statistics_by_user_mode: Mapping[tuple[int, GameMode], UserStatistics],
) -> None:
    """Recalculate PP and weighted accuracy once per affected user/mode."""

    for user_id, mode in sorted(affected_user_modes, key=_user_mode_sort_key):
        statistics = statistics_by_user_mode.get((user_id, mode))
        if statistics is not None:
            statistics.pp, statistics.hit_accuracy = await calculate_user_pp(
                session,
                user_id,
                mode,
                for_update=True,
            )


async def _lock_user_statistics(
    session: AsyncSession,
    user_modes: set[tuple[int, GameMode]],
) -> dict[tuple[int, GameMode], UserStatistics]:
    """Lock aggregate rows in one global order before mutating derivatives."""

    locked: dict[tuple[int, GameMode], UserStatistics] = {}
    for user_id, mode in sorted(user_modes, key=_user_mode_sort_key):
        statistics = (
            await session.exec(
                select(UserStatistics)
                .where(
                    UserStatistics.user_id == user_id,
                    UserStatistics.mode == mode,
                )
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if statistics is not None:
            locked[(user_id, mode)] = statistics
    return locked


async def invalidate_score_reconciliation_caches(result: ScoreReconciliationResult) -> None:
    """Invalidate user/ranking caches after the caller commits DB changes."""

    affected_user_modes = set(result.affected_user_modes)
    if not affected_user_modes:
        return

    # Local imports keep database model initialisation independent of service
    # packages and make the pure transition planner usable without Redis.
    from app.dependencies.database import get_redis
    from app.service.ranking_cache_service import get_ranking_cache_service
    from app.service.user_cache_service import get_user_cache_service

    redis = get_redis()
    user_cache = get_user_cache_service(redis)
    ranking_cache = get_ranking_cache_service(redis)
    for user_id in {user_id for user_id, _mode in affected_user_modes}:
        await user_cache.invalidate_user_all_cache(user_id)
    for mode in {mode for _user_id, mode in affected_user_modes}:
        await ranking_cache.invalidate_cache(mode)
        await ranking_cache.invalidate_team_cache(mode)
        await ranking_cache.invalidate_top_scores_cache(mode)


async def reconcile_disabled_score_features(
    session: AsyncSession,
    transitions: Sequence[ScorePolicyTransition],
) -> ScoreReconciliationResult:
    """Stage removal of derivatives for enabled-to-disabled transitions.

    The caller owns the transaction and must commit before invoking
    :func:`invalidate_score_reconciliation_caches`.
    """

    leaderboard_beatmaps = {
        transition.beatmap_id
        for transition in transitions
        if transition.disable_leaderboard or transition.reset_ranked_score_epoch
    }
    pp_beatmaps = [transition.beatmap_id for transition in transitions if transition.disable_pp]
    ranked_score_beatmaps = {transition.beatmap_id for transition in transitions if transition.disable_ranked_score}
    reset_epoch_beatmaps = {transition.beatmap_id for transition in transitions if transition.reset_ranked_score_epoch}
    pending_beatmaps = {
        transition.beatmap_id
        for transition in transitions
        if transition.disable_leaderboard
        or transition.disable_pp
        or transition.disable_ranked_score
        or transition.reset_ranked_score_epoch
    }
    if not leaderboard_beatmaps and not pp_beatmaps and not ranked_score_beatmaps and not pending_beatmaps:
        return ScoreReconciliationResult()

    association_rows: Sequence[TotalScoreBestScore] = []
    pp_rows: Sequence[BestScore] = []
    # TotalScoreBestScore is both the leaderboard index and the source used to
    # subtract ranked-score contributions. A Ranked -> Qualified transition
    # must inspect it without deleting it, because Qualified still has a
    # leaderboard even though it no longer contributes ranked score.
    association_beatmaps = leaderboard_beatmaps | ranked_score_beatmaps
    if association_beatmaps:
        association_rows = (
            await session.exec(
                select(TotalScoreBestScore).where(col(TotalScoreBestScore.beatmap_id).in_(association_beatmaps))
            )
        ).all()
    if pp_beatmaps:
        pp_rows = (await session.exec(select(BestScore).where(col(BestScore.beatmap_id).in_(pp_beatmaps)))).all()
    pending_score_rows = (
        await session.exec(
            select(Score.user_id, Score.gamemode).where(
                col(Score.beatmap_id).in_(pending_beatmaps),
                col(Score.processed).is_(False),
            )
        )
    ).all()

    discovered_user_modes = (
        {(row.user_id, row.gamemode) for row in association_rows}
        | {(row.user_id, row.gamemode) for row in pp_rows}
        | {(user_id, GameMode(mode)) for user_id, mode in pending_score_rows}
    )
    statistics_by_user_mode = await _lock_user_statistics(session, discovered_user_modes)

    # Queued workers must not resurrect a retired eligibility epoch after a
    # later re-rank.  Submissions take the Beatmapset lock before recording
    # these snapshots, so the target set cannot gain a new pending score while
    # this reconciliation owns that lock.
    clear_leaderboard = leaderboard_beatmaps | reset_epoch_beatmaps
    if clear_leaderboard:
        await session.exec(
            update(Score)
            .where(
                col(Score.beatmap_id).in_(clear_leaderboard),
            )
            .values(leaderboard_eligible=False, ranked_score_eligible=False)
        )
    if pp_beatmaps:
        await session.exec(
            update(Score)
            .where(
                col(Score.beatmap_id).in_(pp_beatmaps),
            )
            .values(ranked=False, pp=0)
        )
    clear_ranked_score = ranked_score_beatmaps | reset_epoch_beatmaps
    if clear_ranked_score:
        await session.exec(
            update(Score)
            .where(
                col(Score.beatmap_id).in_(clear_ranked_score),
            )
            .values(ranked_score_eligible=False)
        )

    # Re-read target derivatives as current, locked rows after aggregate locks.
    # This avoids stale REPEATABLE READ snapshots and gives all writers the
    # order Beatmapset -> UserStatistics -> derived/Score rows.
    if association_beatmaps:
        association_rows = (
            await session.exec(
                select(TotalScoreBestScore)
                .where(col(TotalScoreBestScore.beatmap_id).in_(association_beatmaps))
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).all()
    if pp_beatmaps:
        pp_rows = (
            await session.exec(
                select(BestScore)
                .where(col(BestScore.beatmap_id).in_(pp_beatmaps))
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).all()

    leaderboard_rows = [row for row in association_rows if row.beatmap_id in leaderboard_beatmaps]
    leaderboard_user_modes = {(row.user_id, row.gamemode) for row in leaderboard_rows}
    pp_user_modes = {(row.user_id, row.gamemode) for row in pp_rows}

    ranked_score_removed, ranked_score_user_modes = await _retire_ranked_score_contributions(
        association_rows,
        ranked_score_beatmaps,
        statistics_by_user_mode,
    )

    # These rows are mod-specific leaderboard indexes, not independent score
    # contributions. Deleting them directly prevents repeated aggregate
    # subtraction and preserves lifetime total_score while raw scores remain.
    for row in leaderboard_rows:
        await session.delete(row)

    # Delete all PP-best rows first, then recalculate once against the final set.
    for row in pp_rows:
        await session.delete(row)
    await session.flush()

    await _repair_maximum_combo(
        session,
        leaderboard_user_modes | ranked_score_user_modes,
        statistics_by_user_mode,
    )
    await _repair_pp(session, pp_user_modes, statistics_by_user_mode)
    await session.flush()

    pending_user_modes = {(user_id, GameMode(mode)) for user_id, mode in pending_score_rows}
    affected_user_modes = leaderboard_user_modes | pp_user_modes | ranked_score_user_modes | pending_user_modes
    return ScoreReconciliationResult(
        leaderboard_rows_removed=len(leaderboard_rows),
        pp_rows_removed=len(pp_rows),
        ranked_score_contributions_removed=ranked_score_removed,
        affected_user_modes=frozenset(affected_user_modes),
    )
