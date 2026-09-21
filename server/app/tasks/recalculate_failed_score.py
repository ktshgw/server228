"""Retry PP calculation without crossing a ranking-policy epoch."""

from app.config import settings
from app.database import Beatmap, Beatmapset, BestScore, Score, ScoreImport, ScoreToken, User, UserStatistics
from app.database.achievement import process_achievements
from app.database.score import _process_score_pp, calculate_user_pp, process_user
from app.dependencies.database import get_redis, with_db
from app.dependencies.fetcher import get_fetcher
from app.dependencies.scheduler import get_scheduler
from app.fetcher.beatmap_raw import NoBeatmapError
from app.log import logger
from app.models.mods import mods_can_get_pp
from app.models.score import GameMode
from app.service.beatmap_ranking_reconciliation_service import (
    ScoreReconciliationResult,
    invalidate_score_reconciliation_caches,
)
from app.service.beatmap_ranking_service import get_effective_beatmap_policy

from sqlmodel import col, exists, select

UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE = 1000
SCORE_RETRY_QUEUE_KEY = "score:need_recalculate"
SCORE_RETRY_PROCESSING_KEY = "score:need_recalculate:processing"
CLASSIC_PP_BACKFILL_MARKER_KEY = "score:classic-pp-backfill:v1"
ADJUSTED_MODS_PP_BACKFILL_MARKER_KEY = "score:adjusted-mods-pp-backfill:v1"


async def enqueue_missing_score_retries(redis, score_ids: list[int]) -> int:
    """Add missing score IDs without duplicating either retry list."""

    enqueued = 0
    for score_id in score_ids:
        value = str(score_id)
        queued = await redis.lpos(SCORE_RETRY_QUEUE_KEY, value)
        processing = await redis.lpos(SCORE_RETRY_PROCESSING_KEY, value)
        if queued is not None or processing is not None:
            continue
        await redis.rpush(SCORE_RETRY_QUEUE_KEY, value)
        enqueued += 1
    return enqueued


async def enqueue_unprocessed_scores(redis) -> int:
    """Recover committed scores whose post-submit task was never registered."""

    enqueued = 0
    after_id = 0
    while True:
        async with with_db() as session:
            score_ids = list(
                (
                    await session.exec(
                        select(Score.id)
                        .where(
                            col(Score.processed).is_(False),
                            col(Score.id) > after_id,
                        )
                        .order_by(col(Score.id))
                        .limit(UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE)
                    )
                ).all()
            )
        if not score_ids:
            break
        enqueued += await enqueue_missing_score_retries(redis, score_ids)
        after_id = score_ids[-1]
        if len(score_ids) < UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE:
            break
    return enqueued


async def enqueue_pending_score_import_retries(redis) -> int:
    """Recover imported scores whose post-commit PP enqueue was interrupted."""

    enqueued = 0
    after_id = 0
    while True:
        async with with_db() as session:
            rows = list(
                (
                    await session.exec(
                        select(ScoreImport.id, ScoreImport.score_id, ScoreImport.source_snapshot)
                        .where(
                            col(ScoreImport.id) > after_id,
                            col(ScoreImport.score_id).is_not(None),
                        )
                        .order_by(col(ScoreImport.id))
                        .limit(UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE)
                    )
                ).all()
            )
        if not rows:
            break
        pending_score_ids = [
            score_id
            for _import_id, score_id, snapshot in rows
            if score_id is not None and isinstance(snapshot, dict) and snapshot.get("pp_pending") is True
        ]
        enqueued += await enqueue_missing_score_retries(redis, pending_score_ids)
        after_id = rows[-1][0]
        if len(rows) < UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE:
            break
    return enqueued


def _has_classic_mod(mods: object) -> bool:
    return isinstance(mods, list) and any(
        isinstance(mod, dict) and str(mod.get("acronym", "")).upper() == "CL" for mod in mods
    )


async def enqueue_classic_pp_backfill(redis) -> int:
    """Queue historic CL plays skipped by the previous ranked-mod policy once."""

    if await redis.get(CLASSIC_PP_BACKFILL_MARKER_KEY) is not None:
        return 0

    enqueued = 0
    after_id = 0
    while True:
        async with with_db() as session:
            rows = list(
                (
                    await session.exec(
                        select(Score.id, Score.mods)
                        .where(
                            col(Score.id) > after_id,
                            col(Score.passed).is_(True),
                            col(Score.processed).is_(True),
                            col(Score.ranked).is_(True),
                            col(Score.pp) == 0,
                            col(Score.room_id).is_(None),
                            col(Score.playlist_item_id).is_(None),
                            ~exists().where(col(BestScore.score_id) == col(Score.id)),
                        )
                        .order_by(col(Score.id))
                        .limit(UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE)
                    )
                ).all()
            )
        if not rows:
            break
        enqueued += await enqueue_missing_score_retries(
            redis,
            [score_id for score_id, mods in rows if _has_classic_mod(mods)],
        )
        after_id = rows[-1][0]
        if len(rows) < UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE:
            break

    # The normal retry list remains durable if calculation is temporarily
    # unavailable. This marker prevents a full-table scan on every 5m run.
    await redis.set(CLASSIC_PP_BACKFILL_MARKER_KEY, "1")
    return enqueued


async def enqueue_adjusted_mods_pp_backfill(redis) -> int:
    """Recover zero-PP adjusted-speed plays allowed by SOMS!'s mod policy."""

    if await redis.get(ADJUSTED_MODS_PP_BACKFILL_MARKER_KEY) is not None:
        return 0

    enqueued = 0
    after_id = 0
    while True:
        async with with_db() as session:
            rows = list(
                (
                    await session.exec(
                        select(Score.id, Score.gamemode, Score.mods)
                        .where(
                            col(Score.id) > after_id,
                            col(Score.passed).is_(True),
                            col(Score.processed).is_(True),
                            col(Score.ranked).is_(True),
                            col(Score.pp) == 0,
                            col(Score.room_id).is_(None),
                            col(Score.playlist_item_id).is_(None),
                        )
                        .order_by(col(Score.id))
                        .limit(UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE)
                    )
                ).all()
            )
        if not rows:
            break
        score_ids = [
            score_id
            for score_id, mode, mods in rows
            if any(
                mod["acronym"] in {"DT", "NC"} and mod.get("settings", {}).get("speed_change", 1.5) != 1.5
                for mod in mods
            )
            and mods_can_get_pp(int(GameMode(mode)), list(mods))
        ]
        enqueued += await enqueue_missing_score_retries(redis, score_ids)
        after_id = rows[-1][0]
        if len(rows) < UNPROCESSED_SCORE_RECOVERY_BATCH_SIZE:
            break

    # The retry worker rechecks the current map policy/checksum and updates
    # the best score, total PP and rank caches without counting the play twice.
    await redis.set(ADJUSTED_MODS_PP_BACKFILL_MARKER_KEY, "1")
    return enqueued


async def _clear_score_import_pp_pending(session, score_id: int) -> None:
    provenance = (
        await session.exec(
            select(ScoreImport)
            .where(ScoreImport.score_id == score_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if provenance is None or provenance.source_snapshot.get("pp_pending") is not True:
        return
    provenance.source_snapshot = {**provenance.source_snapshot, "pp_pending": False}
    session.add(provenance)


@get_scheduler().scheduled_job("interval", id="recalculate_failed_score", minutes=5)
async def recalculate_failed_score() -> None:
    """Retry queued PP work under the normal set -> stats -> score locks.

    A retry may create a ``BestScore`` only when both the immutable submission
    snapshot and current policy allow PP, and only while the played checksum
    is current. If the original atomic finalizer rolled back, replay it whole.
    """

    redis = get_redis()
    fetcher = await get_fetcher()
    queue_key = SCORE_RETRY_QUEUE_KEY
    processing_key = SCORE_RETRY_PROCESSING_KEY

    # Recover claims left behind by a terminated previous run. RPOPLPUSH is
    # atomic, so a crash can duplicate idempotent work but cannot lose it.
    while await redis.rpoplpush(processing_key, queue_key) is not None:
        pass

    recovered = await enqueue_unprocessed_scores(redis)
    recovered += await enqueue_pending_score_import_retries(redis)
    recovered += await enqueue_classic_pp_backfill(redis)
    if recovered:
        logger.info(f"Recovered {recovered} committed score(s) with unfinished post-submit work")

    while True:
        raw_score_id = await redis.rpoplpush(queue_key, processing_key)
        if raw_score_id is None:
            break
        claimed_score_id = raw_score_id.decode() if isinstance(raw_score_id, bytes) else raw_score_id
        score_id = int(claimed_score_id)
        affected_user_mode: tuple[int, GameMode] | None = None
        retry_needed = False
        try:
            # Achievement evaluation precedes the statistics transaction. Once
            # process_user sets Score.processed, both phases have completed.
            async with with_db() as achievement_session:
                processed = (
                    await achievement_session.exec(select(Score.processed).where(Score.id == score_id))
                ).first()
                if processed is False:
                    await process_achievements(achievement_session, redis, score_id)

            async with with_db() as session:
                locator = (
                    await session.exec(
                        select(Score.beatmap_id, Score.user_id, Score.gamemode, Beatmap.beatmapset_id)
                        .join(Beatmap, col(Beatmap.id) == col(Score.beatmap_id))
                        .where(Score.id == score_id)
                    )
                ).first()
                if locator is None:
                    continue
                beatmap_id, user_id, raw_mode, beatmapset_id = locator
                mode = GameMode(raw_mode)

                # End the discovery snapshot. Every eligibility writer takes
                # this same parent row before touching derivatives.
                await session.rollback()
                locked_beatmapset = (
                    await session.exec(
                        select(Beatmapset)
                        .where(Beatmapset.id == beatmapset_id)
                        .with_for_update()
                        .execution_options(populate_existing=True)
                    )
                ).first()
                if locked_beatmapset is None:
                    continue

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
                score = (
                    await session.exec(
                        select(Score)
                        .where(Score.id == score_id)
                        .with_for_update()
                        .execution_options(populate_existing=True)
                    )
                ).first()
                beatmap = (
                    await session.exec(
                        select(Beatmap)
                        .where(Beatmap.id == beatmap_id)
                        .with_for_update()
                        .execution_options(populate_existing=True)
                    )
                ).first()
                if statistics is None or score is None or beatmap is None:
                    continue

                policy = await get_effective_beatmap_policy(session, beatmap, for_update=True)
                checksum_matches = score.map_md5 == beatmap.checksum
                if not checksum_matches:
                    score.ranked = False
                    score.leaderboard_eligible = False
                    score.ranked_score_eligible = False

                if not score.processed:
                    user = await session.get(User, user_id)
                    score_token = (
                        await session.exec(select(ScoreToken.id).where(ScoreToken.score_id == score_id))
                    ).first()
                    if user is None or score_token is None:
                        retry_needed = True
                        continue
                    pp_ready = await process_user(
                        session,
                        redis,
                        fetcher,
                        user,
                        score,
                        score_token,
                        beatmap.total_length,
                        policy,
                        queue_pp_failure=False,
                    )
                    retry_needed = not pp_ready
                    affected_user_mode = (user_id, mode)
                else:
                    pp_allowed = (
                        checksum_matches and score.ranked and (policy.pp_enabled or settings.enable_all_beatmap_pp)
                    )
                    if not pp_allowed:
                        await _clear_score_import_pp_pending(session, score_id)
                        await session.commit()
                        continue
                    succeeded = await _process_score_pp(
                        score,
                        session,
                        redis,
                        fetcher,
                        raise_when_not_found=True,
                        queue_on_failure=False,
                    )
                    if not succeeded:
                        await session.rollback()
                        retry_needed = True
                        continue
                    await session.flush()
                    statistics.pp, statistics.hit_accuracy = await calculate_user_pp(
                        session,
                        user_id,
                        mode,
                        for_update=True,
                    )
                    await _clear_score_import_pp_pending(session, score_id)
                    await session.commit()
                    affected_user_mode = (user_id, mode)

            if affected_user_mode is not None:
                await invalidate_score_reconciliation_caches(
                    ScoreReconciliationResult(affected_user_modes=frozenset({affected_user_mode}))
                )
        except NoBeatmapError:
            logger.warning(f"Beatmap for score {score_id} was not found; dropping its PP retry")
        except Exception:
            logger.exception(f"Failed to retry PP for score {score_id}; retaining it for the next run")
            retry_needed = True
        finally:
            if not retry_needed:
                try:
                    await redis.lrem(processing_key, 1, claimed_score_id)
                except Exception:
                    # The claim remains recoverable in the processing list and
                    # may be replayed idempotently on the next scheduled run.
                    logger.exception(f"Failed to acknowledge PP retry for score {score_id}")

    # Deferred failures remain claimable after the job exits, without being
    # consumed again in a hot loop during the same upstream outage.
    while await redis.rpoplpush(processing_key, queue_key) is not None:
        pass
