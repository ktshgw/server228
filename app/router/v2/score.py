"""Score endpoints for osu! API v2.

This module provides endpoints for score submission, leaderboards, pinned scores,
replay downloads, and room/playlist score management.
"""

import asyncio
from datetime import UTC, date
from typing import Annotated, Literal
from urllib.parse import quote

from app.calculating import clamp
from app.config import settings
from app.const import NEW_SCORE_FORMAT_VER
from app.database import (
    Beatmap,
    Beatmapset,
    Playlist,
    Room,
    Score,
    ScoreToken,
    ScoreTokenResp,
    User,
    UserStatistics,
)
from app.database.achievement import process_achievements
from app.database.counts import ReplayWatchedCount
from app.database.daily_challenge import process_daily_challenge_score
from app.database.item_attempts_count import ItemAttemptsCount
from app.database.playlist_best_score import (
    PlaylistBestScore,
    process_playlist_best_score,
)
from app.database.relationship import Relationship, RelationshipType
from app.database.score import (
    LegacyScoreResp,
    MultiplayerScores,
    MultiplayScoreDict,
    ScoreModel,
    get_leaderboard,
    get_score_position_by_id,
    process_score,
    process_user,
)
from app.dependencies.api_version import APIVersion
from app.dependencies.cache import UserCacheService
from app.dependencies.client_verification import ClientVerificationService
from app.dependencies.database import Database, Redis, get_redis, with_db
from app.dependencies.fetcher import Fetcher, get_fetcher
from app.dependencies.rate_limit import create_rate_limiter
from app.dependencies.storage import StorageService
from app.dependencies.user import ClientUser, get_optional_user
from app.helpers import api_doc, utcnow
from app.log import log
from app.models.error import ErrorType, RequestError
from app.models.events.score import (
    MultiplayerScoreCreatedEvent,
    MultiplayerScoreSubmittedEvent,
    ReplayDownloadedEvent,
    ScoreProcessedEvent,
    ScoreType,
    SoloScoreCreatedEvent,
    SoloScoreSubmittedEvent,
)
from app.models.room import RoomCategory
from app.models.score import (
    GameMode,
    LeaderboardType,
    Rank,
    SoloScoreSubmissionInfo,
)
from app.plugins import hub
from app.service.beatmap_cache_service import get_beatmap_cache_service
from app.service.beatmap_ranking_service import get_effective_beatmap_policy
from app.service.score_import_service import ServerReplayScoreMetadata, rewrite_osr_server_score_metadata
from app.service.score_pin_service import lock_score_pin_state, reordered_score_pin_ids
from app.features.somsai.services.somsai_score_service import validate_somsai_score_submission, validate_somsai_score_token
from app.service.user_cache_service import refresh_user_cache_background
from app.v2_ipc import get_ipc_client

from .router import router

from fastapi import (
    BackgroundTasks,
    Body,
    Depends,
    Form,
    Path,
    Query,
    Response,
    Security,
)
from pydantic import BaseModel
from pyrate_limiter import Duration, Rate
from sqlalchemy import and_, or_
from sqlalchemy.exc import OperationalError
from sqlalchemy.orm import lazyload
from sqlmodel import col, exists, func, select
from sqlmodel.ext.asyncio.session import AsyncSession

logger = log("Score")

SCORE_DETAIL_INCLUDES = [
    "beatmap",
    "beatmapset",
    "current_user_attributes",
    "position",
    "rank_country",
    "rank_global",
    "user",
    "user.country",
    "user.cover",
    "user.team",
]


async def _process_user_achievement(score_id: int):
    """Process achievements for a submitted score.

    Args:
        score_id: The score ID to process achievements for.
    """
    async with with_db() as session:
        await process_achievements(session, get_redis(), score_id)


async def _process_user(score_id: int, user_id: int, redis: Redis, fetcher: Fetcher):
    """Durably finalizable post-submit work, with achievements before stats.

    Args:
        score_id: The submitted score ID.
        user_id: The user ID who submitted the score.
        redis: Redis connection.
        fetcher: Fetcher service for external data.
    """
    # ``Score.processed`` is the durable completion marker used by the recovery
    # job. Run achievements first so setting it below also proves the medal
    # pass completed. A crash between the two leaves the score discoverable.
    await _process_user_achievement(score_id)

    async with with_db() as session:
        score_locator = (
            await session.exec(
                select(Score.beatmap_id, Score.gamemode, Beatmap.beatmapset_id)
                .join(Beatmap, col(Beatmap.id) == col(Score.beatmap_id))
                .where(Score.id == score_id)
            )
        ).first()
        if score_locator is None:
            logger.warning(
                "Score {score_id} not found when processing user {user_id}", score_id=score_id, user_id=user_id
            )
            return
        beatmap_id, raw_gamemode, beatmapset_id = score_locator
        gamemode = GameMode(raw_gamemode)

        # End the discovery read-view. The first statement in the actual work
        # transaction is the same per-set lock used by rank/unrank/update, so
        # MySQL REPEATABLE READ cannot leave this worker with a stale policy.
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
            logger.warning(
                "Beatmapset for beatmap {beatmap_id} not found while processing score {score_id}",
                beatmap_id=beatmap_id,
                score_id=score_id,
            )
            return

        # Every statistics mutation (including reconciliation on another set)
        # takes this lock, preventing lost ranked-score/PP updates.
        locked_statistics = (
            await session.exec(
                select(UserStatistics)
                .where(
                    UserStatistics.user_id == user_id,
                    UserStatistics.mode == gamemode,
                )
                .with_for_update()
            )
        ).all()
        if not locked_statistics:
            logger.warning(
                "Statistics for user {user_id} and mode {mode} not found while processing score {score_id}",
                user_id=user_id,
                mode=gamemode,
                score_id=score_id,
            )
            return

        # Locking the score after the aggregate row keeps the global lock order
        # consistent and makes duplicate background deliveries idempotent.
        score = (await session.exec(select(Score).where(Score.id == score_id).with_for_update())).first()
        if score is None:
            return
        if score.processed:
            logger.debug("Score {score_id} was already processed; skipping duplicate task", score_id=score_id)
            return

        user = await session.get(User, user_id)
        if not user:
            logger.warning(
                "User {user_id} not found when processing score {score_id}", user_id=user_id, score_id=score_id
            )
            return
        score_token = (await session.exec(select(ScoreToken.id).where(ScoreToken.score_id == score_id))).first()
        if not score_token:
            logger.warning(
                "ScoreToken for score {score_id} not found when processing user {user_id}",
                score_id=score_id,
                user_id=user_id,
            )
            return
        beatmap = (await session.exec(select(Beatmap).where(Beatmap.id == beatmap_id).with_for_update())).first()
        if not beatmap:
            logger.warning(
                "Beatmap {beatmap_id} not found when processing user {user_id} for score {score_id}",
                beatmap_id=score.beatmap_id,
                user_id=user_id,
                score_id=score_id,
            )
            return
        beatmap_policy = await get_effective_beatmap_policy(session, beatmap, for_update=True)
        if score.map_md5 != beatmap.checksum:
            # Keep the raw play and lifetime statistics, but never derive PP,
            # leaderboard, or ranked-score state from a different chart
            # revision than the one which was actually played.
            score.ranked = False
            score.leaderboard_eligible = False
            score.ranked_score_eligible = False
        await process_user(session, redis, fetcher, user, score, score_token, beatmap.total_length, beatmap_policy)
        await refresh_user_cache_background(redis, user_id, gamemode)

        if settings.enable_v2_ipc:
            await get_ipc_client().send_notice("realtime", "score_processed", {"score_id": score_id})
        else:
            await redis.publish("osu-channel:score:processed", f'{{"ScoreId": {score_id}}}')

        # refresh score
        await session.refresh(score)
        hub.emit(ScoreProcessedEvent(score=score.to_score_data()))


def _schedule_score_finalization(
    background_task: BackgroundTasks,
    score_id: int,
    user_id: int,
    redis: Redis,
    fetcher: Fetcher,
) -> None:
    """Schedule the single ordered score-finalization worker."""

    background_task.add_task(_process_user, score_id, user_id, redis, fetcher)


async def submit_score(
    background_task: BackgroundTasks,
    info: SoloScoreSubmissionInfo,
    token: int,
    current_user: User,
    db: AsyncSession,
    redis: Redis,
    fetcher: Fetcher,
    *,
    defer_commit: bool = False,
):
    """Submit a score using a score token.

    Args:
        background_task: Background tasks handler.
        info: Score submission information.
        token: Score token ID.
        current_user: The authenticated user.
        db: Database session.
        redis: Redis connection.
        fetcher: Fetcher service for external data.
        defer_commit: Leave the new score and token link in the caller-owned
            transaction. Used by playlist submission so every persistent
            side effect is committed atomically.

    Returns:
        dict: The submitted score response.

    Raises:
        RequestError: If token not found, score not found, or beatmap not found.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id

    if not info.passed:
        info.rank = Rank.F
    score_token = (
        await db.exec(
            select(ScoreToken).where(ScoreToken.id == token).with_for_update().execution_options(populate_existing=True)
        )
    ).first()
    if not score_token or score_token.user_id != user_id:
        raise RequestError(ErrorType.SCORE_TOKEN_NOT_FOUND)
    created = not bool(score_token.score_id)
    if score_token.score_id:
        score = (
            await db.exec(
                select(Score)
                .where(
                    Score.id == score_token.score_id,
                    Score.user_id == user_id,
                )
                .with_for_update()
            )
        ).first()
        if not score:
            raise RequestError(ErrorType.SCORE_NOT_FOUND)
    else:
        beatmap = score_token.beatmap_id
        try:
            cache_service = get_beatmap_cache_service(redis, fetcher)
            await cache_service.smart_preload_for_score(beatmap, score_token.beatmap_checksum)
        except Exception as e:
            logger.debug(f"Beatmap preload failed for {beatmap}: {e}")

        db_beatmap = await db.get(Beatmap, beatmap)
        if db_beatmap is None:
            logger.warning(f"Score submission failed: beatmap {beatmap} not found for user {user_id}, token {token}")
            raise RequestError(ErrorType.BEATMAP_NOT_FOUND)
        score = await process_score(
            user=current_user,
            beatmap=db_beatmap,
            score_token=score_token,
            info=info,
            session=db,
        )
        score_id = score.id
        score_token.score_id = score_id
        if not defer_commit:
            await db.commit()
            await db.refresh(score_token)
            await db.refresh(score)

    resp = await ScoreModel.transform(
        score,
    )
    if not created:
        needs_finalization = not score.processed
        # Release the token/score row locks before a FastAPI background task
        # can open its independent transaction. The duplicate path is read-only.
        await db.rollback()
        if needs_finalization:
            # A previous request may have committed the raw score and crashed
            # before registering its background task. Duplicate PUT is the
            # fastest recovery path; the periodic DB scanner is the backstop.
            _schedule_score_finalization(background_task, resp["id"], user_id, redis, fetcher)
        return resp, False
    logger.info(
        f"Score {resp['id']} submitted by user {user_id}; beatmap={score.beatmap_id}, "
        f"mode={score.gamemode}, passed={score.passed}, pp={score.pp}"
    )
    if not defer_commit:
        _schedule_score_finalization(background_task, resp["id"], user_id, redis, fetcher)
    return resp, True


async def _preload_beatmap_for_pp_calculation(beatmap_id: int, expected_checksum: str) -> None:
    """Pre-cache beatmap file to speed up PP calculation.

    When a player starts playing, asynchronously preload the beatmap raw file to Redis cache.

    Args:
        beatmap_id: The beatmap ID to preload.
        expected_checksum: Exact chart revision expected by the client.
    """
    # Check if beatmap preload feature is enabled
    if not settings.enable_beatmap_preload:
        return

    try:
        # Asynchronously get fetcher and redis connection
        fetcher = await get_fetcher()
        redis = get_redis()

        # Check if already cached to avoid duplicate downloads
        cache_key = f"beatmap:{beatmap_id}:{expected_checksum.lower()}:raw"
        if await redis.exists(cache_key):
            logger.debug(f"Beatmap {beatmap_id} already cached, skipping preload")
            return

        await fetcher.get_or_fetch_beatmap_raw(redis, beatmap_id, expected_checksum)
        logger.debug(f"Successfully preloaded beatmap {beatmap_id} for PP calculation")

    except Exception as e:
        # Preload failure should not affect normal gameplay
        logger.warning(f"Failed to preload beatmap {beatmap_id}: {e}")


LeaderboardScoreType = ScoreModel.generate_typeddict(tuple(ScoreModel.DEFAULT_SCORE_INCLUDES)) | LegacyScoreResp


class BeatmapUserScore(BaseModel):
    """Response model for a user's score on a beatmap.

    Attributes:
        position: The user's position on the leaderboard.
        score: The score data.
    """

    position: int
    score: LeaderboardScoreType  # pyright: ignore[reportInvalidTypeForm]


class BeatmapScores(BaseModel):
    """Response model for beatmap leaderboard scores.

    Attributes:
        scores: List of scores on the leaderboard.
        user_score: The current user's score (if any).
        score_count: Total number of scores on the leaderboard.
    """

    scores: list[LeaderboardScoreType]  # pyright: ignore[reportInvalidTypeForm]
    user_score: BeatmapUserScore | None = None
    score_count: int = 0


@router.get(
    "/beatmaps/{beatmap_id}/scores",
    tags=["Scores"],
    responses={
        200: {
            "model": BeatmapScores,
            "description": (
                "Leaderboard and current user's score.\n\n"
                f"If `x-api-version >= {NEW_SCORE_FORMAT_VER}`, returns `BeatmapScores[Score]`"
                f" (includes: {', '.join([f'`{inc}`' for inc in ScoreModel.DEFAULT_SCORE_INCLUDES])}), "
                "otherwise returns `BeatmapScores[LegacyScoreResp]`."
            ),
        }
    },
    name="Get beatmap leaderboard",
    description="Get the leaderboard and current user's score for a specific beatmap under certain conditions.",
)
async def get_beatmap_scores(
    db: Database,
    api_version: APIVersion,
    beatmap_id: Annotated[int, Path(description="Beatmap ID")],
    mode: Annotated[GameMode, Query(description="Specified ruleset")],
    mods: Annotated[
        list[str],
        Query(default_factory=set, alias="mods[]", description="Filter by mods (optional, multiple values)"),
    ],
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
    legacy_only: Annotated[bool | None, Query(description="Whether to only query Stable scores")] = None,
    type: Annotated[
        LeaderboardType,
        Query(
            description=("Leaderboard type: GLOBAL / COUNTRY / FRIENDS / TEAM"),
        ),
    ] = LeaderboardType.GLOBAL,
    limit: Annotated[int, Query(ge=1, le=200, description="Number of results (1-200)")] = 50,
):
    """Get beatmap leaderboard scores.

    Args:
        db: Database session dependency.
        api_version: API version from request headers.
        beatmap_id: The beatmap ID.
        mode: The game mode.
        mods: Optional mod filter.
        current_user: The authenticated user.
        legacy_only: Whether to only query Stable scores.
        type: Leaderboard type filter.
        limit: Maximum number of results.

    Returns:
        dict: Leaderboard scores with user score and count.
    """
    all_scores, user_score, count = await get_leaderboard(
        db,
        beatmap_id,
        mode,
        type=type,
        user=current_user,
        limit=limit,
        mods=sorted(mods),
    )

    user_score_resp = (
        await user_score.to_resp(db, api_version, includes=ScoreModel.DEFAULT_SCORE_INCLUDES) if user_score else None
    )
    return {
        "scores": [
            await score.to_resp(db, api_version, includes=ScoreModel.DEFAULT_SCORE_INCLUDES) for score in all_scores
        ],
        "user_score": (
            {
                "score": user_score_resp,
                "position": (
                    await get_score_position_by_id(
                        db,
                        user_score.beatmap_id,
                        user_score.id,
                        mode=user_score.gamemode,
                        user=user_score.user,
                    )
                    or 0
                ),
            }
            if user_score and user_score_resp
            else None
        ),
        "score_count": count,
    }


@router.get(
    "/beatmaps/{beatmap_id}/scores/users/{user_id}",
    tags=["Scores"],
    responses={
        200: {
            "model": BeatmapUserScore,
            "description": (
                "User's best score on the specified beatmap\n\n"
                f"If `x-api-version >= {NEW_SCORE_FORMAT_VER}`, returns `BeatmapUserScore[Score]`, "
                f" (includes: {', '.join([f'`{inc}`' for inc in ScoreModel.DEFAULT_SCORE_INCLUDES])}), "
                "otherwise returns `BeatmapUserScore[LegacyScoreResp]`."
            ),
        }
    },
    name="Get user's best beatmap score",
    description="Get the best score for a specific user on a specific beatmap.",
)
async def get_user_beatmap_score(
    db: Database,
    api_version: APIVersion,
    beatmap_id: Annotated[int, Path(description="Beatmap ID")],
    user_id: Annotated[int, Path(description="User ID")],
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
    legacy_only: Annotated[bool | None, Query(description="Whether to only query Stable scores")] = None,
    mode: Annotated[GameMode | None, Query(description="Specified ruleset (optional)")] = None,
    mods: Annotated[str | None, Query(description="Filter by mods (not implemented)")] = None,
):
    """Get user's best score on a beatmap.

    Args:
        db: Database session dependency.
        api_version: API version from request headers.
        beatmap_id: The beatmap ID.
        user_id: The user ID.
        current_user: The authenticated user.
        legacy_only: Whether to only query Stable scores.
        mode: Optional game mode filter.
        mods: Mod filter (not implemented).

    Returns:
        dict: User's best score with position.

    Raises:
        RequestError: If the score is not found.
    """
    user_score = (
        await db.exec(
            select(Score)
            .where(
                Score.gamemode == mode if mode is not None else True,
                Score.beatmap_id == beatmap_id,
                Score.user_id == user_id,
                col(Score.passed).is_(True),
            )
            .order_by(col(Score.total_score).desc())
            .limit(1)
        )
    ).first()

    if not user_score:
        raise RequestError(
            ErrorType.SCORE_NOT_FOUND,
            {"user_id": user_id, "beatmap_id": beatmap_id},
        )
    else:
        resp = await user_score.to_resp(db, api_version=api_version, includes=ScoreModel.DEFAULT_SCORE_INCLUDES)
        return {
            "position": (
                await get_score_position_by_id(
                    db,
                    user_score.beatmap_id,
                    user_score.id,
                    mode=user_score.gamemode,
                    user=user_score.user,
                )
                or 0
            ),
            "score": resp,
        }


@router.get(
    "/beatmaps/{beatmap_id}/scores/users/{user_id}/all",
    tags=["Scores"],
    responses={
        200: api_doc(
            (
                "All user scores on beatmap\n\n"
                f"If `x-api-version >= {NEW_SCORE_FORMAT_VER}`, returns `Score` list, "
                "otherwise returns `LegacyScoreResp` list."
            ),
            list[ScoreModel] | list[LegacyScoreResp],
            ScoreModel.DEFAULT_SCORE_INCLUDES,
        )
    },
    name="Get all user beatmap scores",
    description="Get all scores for a specific user on a specific beatmap.",
)
async def get_user_all_beatmap_scores(
    db: Database,
    api_version: APIVersion,
    beatmap_id: Annotated[int, Path(description="Beatmap ID")],
    user_id: Annotated[int, Path(description="User ID")],
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
    legacy_only: Annotated[bool | None, Query(description="Whether to only query Stable scores")] = None,
    ruleset: Annotated[GameMode | None, Query(description="Specified ruleset (optional)")] = None,
):
    """Get all user scores on a beatmap.

    Args:
        db: Database session dependency.
        api_version: API version from request headers.
        beatmap_id: The beatmap ID.
        user_id: The user ID.
        current_user: The authenticated user.
        legacy_only: Whether to only query Stable scores.
        ruleset: Optional game mode filter.

    Returns:
        list: All user scores on the beatmap.
    """
    all_user_scores = (
        await db.exec(
            select(Score)
            .where(
                Score.gamemode == ruleset if ruleset is not None else True,
                Score.beatmap_id == beatmap_id,
                Score.user_id == user_id,
                col(Score.passed).is_(True),
                ~User.is_restricted_query(col(Score.user_id)),
            )
            .order_by(col(Score.total_score).desc())
        )
    ).all()

    return [
        await score.to_resp(db, api_version, includes=ScoreModel.DEFAULT_SCORE_INCLUDES) for score in all_user_scores
    ]


@router.get(
    "/scores/{score_id}/download",
    name="Download score replay",
    description="Download the replay file for a specific score.",
    tags=["Scores"],
    dependencies=[Depends(create_rate_limiter(Rate(10, Duration.MINUTE), bucket_key="rate-limit:v2:score-download"))],
)
async def download_score_replay(
    score_id: int,
    db: Database,
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
    storage_service: StorageService,
):
    """Download a score replay.

    Args:
        score_id: The score ID.
        db: Database session dependency.
        current_user: The authenticated user.
        storage_service: Storage service for file access.

    Returns:
        RedirectResponse: Redirect to the replay file URL.

    Raises:
        RequestError: If score or replay file is not found.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id if current_user is not None else None

    score = (await db.exec(select(Score).where(Score.id == score_id))).first()
    if not score:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)

    filepath = score.replay_filename
    if not score.has_replay:
        raise RequestError(ErrorType.REPLAY_FILE_NOT_FOUND)
    owner_id = score.user_id
    owner_username = score.user.username
    gamemode = score.gamemode
    ended_at = score.ended_at
    beatmap_id = score.beatmap_id
    replay_score_metadata = ServerReplayScoreMetadata.from_score(score)

    if not await storage_service.is_exists(filepath):
        raise RequestError(ErrorType.REPLAY_FILE_NOT_FOUND)

    is_friend = user_id is not None and (
        score.user_id == user_id
        or (
            await db.exec(
                select(exists()).where(
                    Relationship.user_id == user_id,
                    Relationship.target_id == score.user_id,
                    Relationship.type == RelationshipType.FOLLOW,
                )
            )
        ).first()
    )
    if not is_friend:
        replay_watched_count = (
            await db.exec(
                select(ReplayWatchedCount).where(
                    ReplayWatchedCount.user_id == score.user_id,
                    ReplayWatchedCount.year == date.today().year,
                    ReplayWatchedCount.month == date.today().month,
                )
            )
        ).first()
        if replay_watched_count is None:
            replay_watched_count = ReplayWatchedCount(
                user_id=score.user_id, year=date.today().year, month=date.today().month
            )
            db.add(replay_watched_count)
        replay_watched_count.count += 1
        await db.commit()

    hub.emit(
        ReplayDownloadedEvent(
            score_id=score_id,
            owner_user_id=owner_id,
            downloader_user_id=user_id,
        )
    )

    beatmap = await db.get(Beatmap, beatmap_id)
    if beatmap is None:
        raise RequestError(ErrorType.BEATMAP_NOT_FOUND)

    filename = f"{owner_username} playing {beatmap.beatmapset.artist} - {beatmap.beatmapset.title} [{beatmap.version}] {gamemode.readable} ({ended_at:%Y-%m-%d}).osr"  # noqa: E501
    encoded_filename = quote(filename)
    if encoded_filename != filename:
        content_disposition = f"attatchment; filename*=utf-8'' filename = \"{filename}\""  # RFC 5987
    else:
        content_disposition = f'attachment; filename="{filename}"'

    replay_content = await storage_service.read_file(filepath)
    try:
        # Both spectator and imported files can lack the current local user/score
        # IDs. The database owns that identity; frames and detailed lazer hit
        # statistics remain those recorded in the replay.
        replay_content = rewrite_osr_server_score_metadata(
            replay_content,
            replay_score_metadata,
            owner_username,
        )
    except ValueError:
        logger.exception("Could not rewrite replay metadata for score {score_id}", score_id=score_id)

    return Response(
        replay_content,
        headers={"Content-Type": "application/x-osu-replay", "Content-Disposition": content_disposition},
    )


@router.get(
    "/scores/{score}",
    tags=["Scores"],
    responses={
        200: api_doc(
            "Get score details.",
            ScoreModel | LegacyScoreResp,
            SCORE_DETAIL_INCLUDES,
        )
    },
    name="Get a score",
    description="Get the details of a specific score.",
)
async def get_score(
    db: Database,
    api_version: APIVersion,
    score: Annotated[int, Path(description="Score ID")],
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
):
    """Get a score by ID.

    Args:
        db: Database session dependency.
        api_version: API version from request headers.
        score: The score ID.
        current_user: The authenticated user.

    Returns:
        dict: The score details.

    Raises:
        RequestError: If the score is not found.
    """
    score_record = (await db.exec(select(Score).where(Score.id == score))).first()
    if not score_record:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)

    return await score_record.to_resp(db, api_version, includes=SCORE_DETAIL_INCLUDES)


@router.get(
    "/scores/{ruleset}/{score}",
    tags=["Scores"],
    responses={
        200: api_doc(
            "Get score details for a specific ruleset.",
            ScoreModel | LegacyScoreResp,
            SCORE_DETAIL_INCLUDES,
        )
    },
    name="Get a score by ruleset",
    description="Get the details of a specific score under the requested ruleset.",
)
async def get_score_by_ruleset(
    db: Database,
    api_version: APIVersion,
    ruleset: Annotated[GameMode, Path(description="Specified ruleset")],
    score: Annotated[int, Path(description="Score ID")],
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
):
    """Get a score by ruleset and ID.

    Args:
        db: Database session dependency.
        api_version: API version from request headers.
        ruleset: The requested ruleset.
        score: The score ID.
        current_user: The authenticated user.

    Returns:
        dict: The score details.

    Raises:
        RequestError: If the score is not found or the ruleset does not match.
    """
    score_record = (await db.exec(select(Score).where(Score.id == score, Score.gamemode == ruleset))).first()
    if not score_record:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)

    return await score_record.to_resp(db, api_version, includes=SCORE_DETAIL_INCLUDES)


@router.post(
    "/beatmaps/{beatmap_id}/solo/scores",
    tags=["Gameplay"],
    response_model=ScoreTokenResp,
    name="Create solo score token",
    description="\nCreate a one-time score submission token for a specific beatmap.",
)
async def create_solo_score(
    background_task: BackgroundTasks,
    db: Database,
    fetcher: Fetcher,
    verification_service: ClientVerificationService,
    beatmap_id: Annotated[int, Path(description="Beatmap ID")],
    beatmap_hash: Annotated[str, Form(description="Beatmap file hash")],
    ruleset_id: Annotated[int, Form(..., description="Ruleset numeric ID (0-3)")],
    current_user: ClientUser,
    version_hash: Annotated[str, Form(description="Game version hash")] = "",
    ruleset_hash: Annotated[str, Form(description="Ruleset version hash")] = "",
):
    """Create a solo score submission token.

    Args:
        background_task: Background tasks handler.
        db: Database session dependency.
        fetcher: Fetcher service.
        verification_service: Client verification service.
        beatmap_id: The beatmap ID.
        beatmap_hash: The beatmap file hash.
        ruleset_id: The ruleset numeric ID (0-3).
        current_user: The authenticated client user.
        version_hash: Game version hash.
        ruleset_hash: Ruleset version hash.

    Returns:
        ScoreTokenResp: The created score token.

    Raises:
        RequestError: If validation fails.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id

    try:
        gamemode = GameMode.from_int(ruleset_id)
    except ValueError:
        raise RequestError(ErrorType.INVALID_RULESET_ID)

    if not (
        client_version := await verification_service.validate_client_version(
            version_hash,
        )
    ):
        logger.info(
            f"Client version check failed for user {current_user.id} on beatmap {beatmap_id} "
            f"(version hash: {version_hash})"
        )
        raise RequestError(ErrorType.INVALID_CLIENT_HASH)

    beatmap = await Beatmap.get_or_fetch(db, fetcher, md5=beatmap_hash)
    if not beatmap or beatmap.id != beatmap_id:
        raise RequestError(ErrorType.INVALID_OR_MISSING_BEATMAP_HASH)

    result = gamemode.check_ruleset_version(ruleset_hash)
    if not result:
        logger.info(
            f"Ruleset version check failed for user {current_user.id} on beatmap {beatmap_id} "
            f"(ruleset: {ruleset_id}, hash: {ruleset_hash})"
        )

        details = {"ruleset_id": ruleset_id, "ruleset_hash": ruleset_hash}

        # The result may have useful information in its own message
        if result.error_msg:
            details.update({"error": result.error_msg})

        raise RequestError(ErrorType.RULESET_VERSION_CHECK_FAILED, details)

    background_task.add_task(_preload_beatmap_for_pp_calculation, beatmap_id, beatmap_hash)
    async with db:
        score_token = ScoreToken(
            user_id=user_id,
            beatmap_id=beatmap_id,
            beatmap_checksum=beatmap_hash.lower(),
            ruleset_id=GameMode.from_int(ruleset_id),
            client_version=client_version.version if client_version else "",
        )
        db.add(score_token)
        await db.commit()
        await db.refresh(score_token)
        logger.debug(
            "User {user_id} created solo score {score_token} for beatmap {beatmap_id} "
            "(mode: {mode}), using client {client_version}",
            user_id=user_id,
            score_token=score_token.id,
            beatmap_id=beatmap_id,
            mode=ruleset_id,
            client_version=client_version,
        )

        hub.emit(
            SoloScoreCreatedEvent(
                user_id=user_id,
                beatmap_id=beatmap_id,
                beatmap_hash=beatmap_hash,
                gamemode=GameMode.from_int(ruleset_id),
                score_token=score_token.id,
                client_version=client_version.version,
            )
        )

        return ScoreTokenResp.from_db(score_token)


@router.put(
    "/beatmaps/{beatmap_id}/solo/scores/{token}",
    tags=["Gameplay"],
    name="Submit solo score",
    description="\nSubmit a solo score using a token.",
    responses={200: api_doc("Solo score submission result.", ScoreModel)},
)
async def submit_solo_score(
    background_task: BackgroundTasks,
    db: Database,
    beatmap_id: Annotated[int, Path(description="Beatmap ID")],
    token: Annotated[int, Path(description="Score token ID")],
    info: Annotated[SoloScoreSubmissionInfo, Body(description="Score submission information")],
    current_user: ClientUser,
    redis: Redis,
    fetcher: Fetcher,
):
    """Submit a solo score.

    Args:
        background_task: Background tasks handler.
        db: Database session dependency.
        beatmap_id: The beatmap ID.
        token: The score token ID.
        info: Score submission information.
        current_user: The authenticated client user.
        redis: Redis connection.
        fetcher: Fetcher service.

    Returns:
        dict: The submitted score.
    """
    score_resp, created = await submit_score(
        background_task,
        info,
        token,
        current_user,
        db,
        redis,
        fetcher,
    )
    if created:
        hub.emit(
            SoloScoreSubmittedEvent(
                submission_info=info,
                user_id=current_user.id,
            )
        )
    return score_resp


@router.post(
    "/rooms/{room_id}/playlist/{playlist_id}/scores",
    tags=["Gameplay"],
    response_model=ScoreTokenResp,
    name="Create room item score token",
    description="\nCreate a score submission token for a room playlist item.",
)
async def create_playlist_score(
    session: Database,
    background_task: BackgroundTasks,
    fetcher: Fetcher,
    room_id: int,
    playlist_id: int,
    verification_service: ClientVerificationService,
    beatmap_id: Annotated[int, Form(description="Beatmap ID")],
    beatmap_hash: Annotated[str, Form(description="Beatmap file hash")],
    ruleset_id: Annotated[int, Form(..., description="Ruleset numeric ID (0-3)")],
    current_user: ClientUser,
    version_hash: Annotated[str, Form(description="Game version hash")] = "",
    ruleset_hash: Annotated[str, Form(description="Ruleset version hash")] = "",
):
    """Create a score token for a playlist item.

    Args:
        session: Database session dependency.
        background_task: Background tasks handler.
        room_id: The room ID.
        playlist_id: The playlist item ID.
        verification_service: Client verification service.
        beatmap_id: The beatmap ID.
        beatmap_hash: The beatmap file hash.
        ruleset_id: The ruleset numeric ID.
        current_user: The authenticated client user.
        version_hash: Game version hash.
        ruleset_hash: Ruleset version hash.

    Returns:
        ScoreTokenResp: The created score token.

    Raises:
        RequestError: If validation fails.
    """
    try:
        gamemode = GameMode.from_int(ruleset_id)
    except ValueError:
        raise RequestError(ErrorType.INVALID_RULESET_ID)

    if not (
        client_version := await verification_service.validate_client_version(
            version_hash,
        )
    ):
        logger.info(
            f"Client version check failed for user {current_user.id} on room {room_id}, playlist {playlist_id} "
            f"(version hash: {version_hash})"
        )
        raise RequestError(ErrorType.INVALID_CLIENT_HASH)

    result = gamemode.check_ruleset_version(ruleset_hash)
    if not result:
        logger.info(
            f"Ruleset version check failed for user {current_user.id} on room {room_id}, playlist {playlist_id},"
            f" (ruleset: {ruleset_id}, hash: {ruleset_hash})"
        )

        details = {"ruleset_id": ruleset_id, "ruleset_hash": ruleset_hash}

        # The result may have useful information in its own message
        if result.error_msg:
            details.update({"error": result.error_msg})

        raise RequestError(ErrorType.RULESET_VERSION_CHECK_FAILED, details)

    if await current_user.is_restricted(session):
        raise RequestError(ErrorType.ACCOUNT_RESTRICTED)

    user_id = current_user.id

    room = await session.get(Room, room_id)
    if not room:
        raise RequestError(ErrorType.ROOM_NOT_FOUND)
    db_room_time = room.ends_at.replace(tzinfo=UTC) if room.ends_at else None
    if db_room_time and db_room_time < utcnow().replace(tzinfo=UTC):
        raise RequestError(ErrorType.ROOM_HAS_ENDED)
    item = (await session.exec(select(Playlist).where(Playlist.id == playlist_id, Playlist.room_id == room_id))).first()
    if not item:
        raise RequestError(ErrorType.PLAYLIST_NOT_FOUND)

    if room.category == RoomCategory.REALTIME:
        await validate_somsai_score_token(session, room_id, playlist_id, user_id)

    # validate
    if not item.freestyle:
        if item.ruleset_id != ruleset_id:
            raise RequestError(ErrorType.RULESET_MISMATCH_PLAYLIST_ITEM)
        if item.beatmap_id != beatmap_id:
            raise RequestError(ErrorType.BEATMAP_ID_MISMATCH_PLAYLIST_ITEM)
    agg = await session.exec(
        select(ItemAttemptsCount).where(
            ItemAttemptsCount.room_id == room_id,
            ItemAttemptsCount.user_id == user_id,
        )
    )
    agg = agg.first()
    if agg and room.max_attempts and agg.attempts >= room.max_attempts:
        raise RequestError(ErrorType.MAX_ATTEMPTS_REACHED)
    if item.expired:
        raise RequestError(ErrorType.PLAYLIST_ITEM_EXPIRED)
    if item.played_at:
        raise RequestError(ErrorType.PLAYLIST_ITEM_ALREADY_PLAYED)
    beatmap = await Beatmap.get_or_fetch(session, fetcher, md5=beatmap_hash)
    if not beatmap or beatmap.id != beatmap_id:
        raise RequestError(ErrorType.INVALID_OR_MISSING_BEATMAP_HASH)
    # Mod validation should not be needed here
    background_task.add_task(_preload_beatmap_for_pp_calculation, beatmap_id, beatmap_hash)
    score_token = ScoreToken(
        user_id=user_id,
        beatmap_id=beatmap_id,
        beatmap_checksum=beatmap_hash.lower(),
        ruleset_id=GameMode.from_int(ruleset_id),
        playlist_item_id=playlist_id,
        room_id=room_id,
        client_version=client_version.version if client_version else "",
    )
    session.add(score_token)
    await session.commit()
    await session.refresh(score_token)
    logger.debug(
        "User {user_id} created playlist score {score_token} for beatmap {beatmap_id} "
        "(mode: {mode}, room {room_id}, item {playlist_id}), using client {client_version}",
        user_id=user_id,
        score_token=score_token.id,
        beatmap_id=beatmap_id,
        mode=ruleset_id,
        room_id=room_id,
        playlist_id=playlist_id,
        client_version=client_version,
    )

    hub.emit(
        MultiplayerScoreCreatedEvent(
            user_id=user_id,
            beatmap_id=beatmap_id,
            beatmap_hash=beatmap_hash,
            gamemode=GameMode.from_int(ruleset_id),
            score_token=score_token.id,
            score_type=ScoreType.MULTIPLAYER,
            room_id=room_id,
            playlist_id=playlist_id,
            client_version=client_version.version,
        )
    )
    return ScoreTokenResp.from_db(score_token)


@router.put(
    "/rooms/{room_id}/playlist/{playlist_id}/scores/{token}",
    tags=["Gameplay"],
    name="Submit room item score",
    description="\nSubmit a score for a room playlist item.",
    responses={200: api_doc("Solo score submission result.", ScoreModel)},
)
async def submit_playlist_score(
    background_task: BackgroundTasks,
    session: Database,
    room_id: int,
    playlist_id: int,
    token: int,
    info: SoloScoreSubmissionInfo,
    current_user: ClientUser,
    redis: Redis,
    fetcher: Fetcher,
):
    """Submit a playlist score.

    Args:
        background_task: Background tasks handler.
        session: Database session dependency.
        room_id: The room ID.
        playlist_id: The playlist item ID.
        token: The score token ID.
        info: Score submission information.
        current_user: The authenticated client user.
        redis: Redis connection.
        fetcher: Fetcher service.

    Returns:
        dict: The submitted score.

    Raises:
        RequestError: If validation fails.
    """
    for attempt in range(3):
        try:
            return await _submit_playlist_score_once(
                background_task, session, room_id, playlist_id, token, info, current_user, redis, fetcher
            )
        except OperationalError as exc:
            await session.rollback()
            if exc.orig is None or not exc.orig.args or exc.orig.args[0] not in (1205, 1213) or attempt == 2:
                raise
            logger.warning("Retrying playlist score token {token} after a database lock conflict", token=token)
            await asyncio.sleep(0.05 * (attempt + 1))
            # Rollback expires ORM attributes, including the authenticated user.
            await session.refresh(current_user)


async def _submit_playlist_score_once(
    background_task: BackgroundTasks,
    session: Database,
    room_id: int,
    playlist_id: int,
    token: int,
    info: SoloScoreSubmissionInfo,
    current_user: ClientUser,
    redis: Redis,
    fetcher: Fetcher,
):
    if await current_user.is_restricted(session):
        raise RequestError(ErrorType.ACCOUNT_RESTRICTED)

    user_id = current_user.id

    # Serialize submissions within a room before aggregate updates. The helper
    # queries below also use current locking reads, preventing both lost
    # increments and stale MySQL REPEATABLE READ snapshots after a wait.
    room = (await session.exec(select(Room).options(lazyload("*")).where(Room.id == room_id).with_for_update())).first()
    if not room:
        raise RequestError(ErrorType.ROOM_NOT_FOUND)
    item = (
        await session.exec(
            select(Playlist)
            .options(lazyload("*"))
            .where(Playlist.id == playlist_id, Playlist.room_id == room_id)
            .with_for_update()
        )
    ).first()
    if not item:
        raise RequestError(ErrorType.PLAYLIST_ITEM_NOT_FOUND)
    score_token = (await session.exec(select(ScoreToken).where(ScoreToken.id == token))).first()
    if not score_token:
        raise RequestError(ErrorType.SCORE_TOKEN_NOT_FOUND)
    if score_token.user_id != user_id:
        raise RequestError(ErrorType.SCORE_TOKEN_USER_MISMATCH)
    if score_token.playlist_item_id != playlist_id:
        raise RequestError(ErrorType.SCORE_TOKEN_PLAYLIST_ITEM_MISMATCH)
    if score_token.room_id != room_id:
        raise RequestError(ErrorType.SCORE_TOKEN_ROOM_MISMATCH)
    if room.category == RoomCategory.REALTIME:
        await validate_somsai_score_submission(session, room_id, item, user_id, info.mods, ruleset_id=info.ruleset_id)
    room_category = room.category
    score_resp, created = await submit_score(
        background_task,
        info,
        token,
        current_user,
        session,
        redis,
        fetcher,
        defer_commit=True,
    )
    if not created:
        score_record = await session.get(Score, score_resp["id"])
        if score_record is None:
            raise RequestError(ErrorType.SCORE_NOT_FOUND)
        return await ScoreModel.transform(
            score_record,
            includes=[*Score.MULTIPLAYER_BASE_INCLUDES, "position", "scores_around"],
            playlist_id=playlist_id,
            room_id=room_id,
            is_playlist=room_category != RoomCategory.REALTIME,
        )
    await process_playlist_best_score(
        room_id,
        playlist_id,
        user_id,
        score_resp["id"],
        score_resp["total_score"],
        session,
    )
    if room_category == RoomCategory.DAILY_CHALLENGE and score_resp["passed"]:
        await process_daily_challenge_score(session, user_id, room_id)
    await ItemAttemptsCount.get_or_create(room_id, user_id, session)

    # The raw Score, one-time ScoreToken link, playlist best/attempt counts,
    # and daily-challenge aggregates are one transaction. Therefore an
    # existing token on a duplicate PUT means all persistent side effects were
    # committed, rather than just the raw score.
    await session.commit()
    _schedule_score_finalization(background_task, score_resp["id"], user_id, redis, fetcher)

    hub.emit(
        MultiplayerScoreSubmittedEvent(
            submission_info=info,
            room_id=room_id,
            playlist_id=playlist_id,
            user_id=user_id,
        )
    )
    try:
        if await redis.exists(f"multiplayer:{room_id}:gameplay:players"):
            await redis.decr(f"multiplayer:{room_id}:gameplay:players")
    except Exception:
        # This is transient realtime presence state. Never turn a successfully
        # committed score into an apparent submission failure because Redis
        # became unavailable after the database commit.
        logger.exception(
            "Failed to decrement gameplay player counter after playlist score {score_id}",
            score_id=score_resp["id"],
        )
    score_record = await session.get(Score, score_resp["id"])
    if score_record is None:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)
    return await ScoreModel.transform(
        score_record,
        includes=[*Score.MULTIPLAYER_BASE_INCLUDES, "position", "scores_around"],
        playlist_id=playlist_id,
        room_id=room_id,
        is_playlist=room_category != RoomCategory.REALTIME,
    )


class IndexedScoreResp(MultiplayerScores):
    """Response model for indexed multiplayer scores.

    Attributes:
        total: Total number of scores.
        user_score: The current user's score (if any).
    """

    total: int
    user_score: MultiplayScoreDict | None = None  # pyright: ignore[reportInvalidTypeForm]


@router.get(
    "/rooms/{room_id}/playlist/{playlist_id}/scores",
    name="Get room item leaderboard",
    description="Get the leaderboard for a room playlist item.",
    tags=["Scores"],
    responses={
        200: {
            "description": (
                f"Room item leaderboard.\n\n"
                f"Includes: {', '.join([f'`{inc}`' for inc in Score.MULTIPLAYER_BASE_INCLUDES])}"
            ),
            "model": IndexedScoreResp,
        }
    },
)
async def index_playlist_scores(
    session: Database,
    room_id: int,
    playlist_id: int,
    current_user: Annotated[User | None, Security(get_optional_user, scopes=["public"])],
    limit: Annotated[int, Query(ge=1, le=50, description="Number of results (1-50)")] = 50,
    sort: Annotated[Literal["score_desc", "score_asc"], Query(description="Score ordering")] = "score_desc",
    cursor: Annotated[
        int | None,
        Query(alias="cursor[total_score]", description="Pagination cursor score value"),
    ] = None,
    cursor_score_id: Annotated[
        int | None,
        Query(alias="cursor[score_id]", description="Pagination cursor tie-breaker"),
    ] = None,
):
    """Get playlist item leaderboard.

    Args:
        session: Database session dependency.
        room_id: The room ID.
        playlist_id: The playlist item ID.
        current_user: The authenticated user.
        limit: Maximum number of results.
        sort: Total-score ordering used by the lazer pagination contract.
        cursor: Pagination cursor total score.
        cursor_score_id: Pagination cursor score ID tie-breaker.

    Returns:
        IndexedScoreResp: Leaderboard scores with pagination.

    Raises:
        RequestError: If the room is not found.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id if current_user is not None else None

    room = await session.get(Room, room_id)
    if not room:
        raise RequestError(ErrorType.ROOM_NOT_FOUND)
    playitem = (
        await session.exec(
            select(Playlist).where(
                Playlist.id == playlist_id,
                Playlist.room_id == room_id,
            )
        )
    ).first()
    if not playitem:
        raise RequestError(ErrorType.PLAYLIST_ITEM_NOT_FOUND)
    limit = clamp(limit, 1, 50)
    base_conditions = [
        PlaylistBestScore.playlist_id == playlist_id,
        PlaylistBestScore.room_id == room_id,
        ~User.is_restricted_query(col(PlaylistBestScore.user_id)),
    ]
    if room.category != RoomCategory.REALTIME:
        base_conditions.append(col(PlaylistBestScore.score).has(col(Score.passed).is_(True)))
    score_conditions = base_conditions.copy()

    if sort == "score_asc":
        sort_columns = (
            col(PlaylistBestScore.total_score).asc(),
            col(PlaylistBestScore.score_id).desc(),
        )
        if cursor is not None:
            if cursor_score_id is None:
                score_conditions.append(col(PlaylistBestScore.total_score) > cursor)
            else:
                score_conditions.append(
                    or_(
                        col(PlaylistBestScore.total_score) > cursor,
                        and_(
                            col(PlaylistBestScore.total_score) == cursor,
                            col(PlaylistBestScore.score_id) < cursor_score_id,
                        ),
                    )
                )
    else:
        sort_columns = (
            col(PlaylistBestScore.total_score).desc(),
            col(PlaylistBestScore.score_id).asc(),
        )
        if cursor is not None:
            if cursor_score_id is None:
                score_conditions.append(col(PlaylistBestScore.total_score) < cursor)
            else:
                score_conditions.append(
                    or_(
                        col(PlaylistBestScore.total_score) < cursor,
                        and_(
                            col(PlaylistBestScore.total_score) == cursor,
                            col(PlaylistBestScore.score_id) > cursor_score_id,
                        ),
                    )
                )

    # ``total`` is the complete restricted-visible leaderboard size, not the
    # size of this page.  This is consumed by IndexedMultiplayerScores.
    total = (await session.exec(select(func.count(col(PlaylistBestScore.score_id))).where(*base_conditions))).one()

    scores = (
        await session.exec(select(PlaylistBestScore).where(*score_conditions).order_by(*sort_columns).limit(limit + 1))
    ).all()
    has_more = len(scores) > limit
    if has_more:
        scores = scores[:-1]

    score_resp = [await ScoreModel.transform(score.score, includes=Score.MULTIPLAYER_BASE_INCLUDES) for score in scores]
    user_score = None
    if user_id is not None:
        user_record = (
            await session.exec(
                select(PlaylistBestScore).where(
                    PlaylistBestScore.playlist_id == playlist_id,
                    PlaylistBestScore.room_id == room_id,
                    PlaylistBestScore.user_id == user_id,
                    ~User.is_restricted_query(col(PlaylistBestScore.user_id)),
                    (
                        col(PlaylistBestScore.score).has(col(Score.passed).is_(True))
                        if room.category != RoomCategory.REALTIME
                        else True
                    ),
                )
            )
        ).first()
        if user_record is not None:
            user_score = await ScoreModel.transform(
                user_record.score,
                includes=[*Score.MULTIPLAYER_BASE_INCLUDES, "position"],
                playlist_id=playlist_id,
                room_id=room_id,
            )

    resp = IndexedScoreResp(
        scores=score_resp,
        user_score=user_score,
        total=total,
        params={
            "limit": limit,
            "sort": sort,
        },
    )
    if has_more:
        resp.cursor = {
            "total_score": scores[-1].total_score,
            "score_id": scores[-1].score_id,
        }
    return resp


@router.get(
    "/rooms/{room_id}/playlist/{playlist_id}/scores/{score_id}",
    name="Get room item score",
    description="Get details for a specific score in a room playlist item.",
    tags=["Scores"],
    responses={
        200: api_doc(
            "Room item score details.",
            ScoreModel,
            [*Score.MULTIPLAYER_BASE_INCLUDES, "position", "scores_around"],
        )
    },
)
async def show_playlist_score(
    session: Database,
    room_id: int,
    playlist_id: int,
    score_id: int,
    current_user: ClientUser,
    redis: Redis,
):
    """Get a specific playlist score.

    Args:
        session: Database session dependency.
        room_id: The room ID.
        playlist_id: The playlist item ID.
        score_id: The score ID.
        current_user: The authenticated client user.
        redis: Redis connection.

    Returns:
        dict: Score details with position and surrounding scores.

    Raises:
        RequestError: If room or score is not found.
    """
    room = await session.get(Room, room_id)
    if not room:
        raise RequestError(ErrorType.ROOM_NOT_FOUND)

    is_playlist = room.category != RoomCategory.REALTIME
    score_record = (
        await session.exec(
            select(PlaylistBestScore).where(
                PlaylistBestScore.score_id == score_id,
                PlaylistBestScore.playlist_id == playlist_id,
                PlaylistBestScore.room_id == room_id,
                ~User.is_restricted_query(col(PlaylistBestScore.user_id)),
            )
        )
    ).first()
    if not score_record:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)
    resp = await ScoreModel.transform(
        score_record.score,
        includes=[*Score.MULTIPLAYER_BASE_INCLUDES, "position", "scores_around"],
        playlist_id=playlist_id,
        room_id=room_id,
        is_playlist=is_playlist,
    )
    return resp


@router.get(
    "/rooms/{room_id}/playlist/{playlist_id}/scores/users/{user_id}",
    responses={
        200: api_doc(
            "Room item score details.",
            ScoreModel,
            [*Score.MULTIPLAYER_BASE_INCLUDES, "position", "scores_around"],
        )
    },
    name="Get user's room item score",
    description="Get a specific user's score in a room playlist item.",
    tags=["Scores"],
)
async def get_user_playlist_score(
    session: Database,
    room_id: int,
    playlist_id: int,
    user_id: int,
    current_user: ClientUser,
):
    """Get a user's playlist score.

    Args:
        session: Database session dependency.
        room_id: The room ID.
        playlist_id: The playlist item ID.
        user_id: The user ID.
        current_user: The authenticated client user.

    Returns:
        dict: Score details with position and surrounding scores.

    Raises:
        RequestError: If the score is not found.
    """
    score_record = (
        await session.exec(
            select(PlaylistBestScore).where(
                PlaylistBestScore.user_id == user_id,
                PlaylistBestScore.playlist_id == playlist_id,
                PlaylistBestScore.room_id == room_id,
                ~User.is_restricted_query(col(PlaylistBestScore.user_id)),
            )
        )
    ).first()
    if not score_record:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)
    room = await session.get(Room, room_id)
    if not room:
        raise RequestError(ErrorType.ROOM_NOT_FOUND)

    resp = await ScoreModel.transform(
        score_record.score,
        includes=[
            *Score.MULTIPLAYER_BASE_INCLUDES,
            "position",
            "scores_around",
        ],
        playlist_id=playlist_id,
        room_id=room_id,
        is_playlist=room.category != RoomCategory.REALTIME,
    )
    return resp


async def _invalidate_score_pin_cache(
    user_cache_service: UserCacheService,
    user_id: int,
    game_mode: GameMode,
) -> None:
    """Do not turn a committed pin mutation into a failed client request."""

    try:
        await user_cache_service.invalidate_user_scores_cache(user_id, game_mode)
    except Exception:
        logger.exception("Post-score-pin cache invalidation failed for user {} in {}", user_id, game_mode.value)


@router.put(
    "/score-pins/{score_id}",
    status_code=204,
    name="Pin score",
    description="\nPin a score to the user's profile (in order).",
    tags=["Scores"],
)
async def pin_score(
    db: Database,
    current_user: ClientUser,
    user_cache_service: UserCacheService,
    score_id: Annotated[int, Path(description="Score ID")],
):
    """Pin a score to the user's profile.

    Args:
        db: Database session dependency.
        current_user: The authenticated client user.
        user_cache_service: User cache service.
        score_id: The score ID to pin.

    Raises:
        RequestError: If the score is not found.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id

    state = await lock_score_pin_state(db, user_id, score_id)
    score_record = state.score
    if score_record is None or not score_record.passed:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)

    game_mode = score_record.gamemode
    changed = state.order_normalised
    if score_record.pinned_order > 0:
        if changed:
            await db.commit()
            await _invalidate_score_pin_cache(user_cache_service, user_id, game_mode)
        return

    score_record.pinned_order = len(state.pinned_scores) + 1
    db.add(score_record)
    await db.commit()
    await _invalidate_score_pin_cache(user_cache_service, user_id, game_mode)


@router.delete(
    "/score-pins/{score_id}",
    status_code=204,
    name="Unpin score",
    description="\nUnpin a score from the user's profile.",
    tags=["Scores"],
)
async def unpin_score(
    db: Database,
    user_cache_service: UserCacheService,
    score_id: Annotated[int, Path(description="Score ID")],
    current_user: ClientUser,
):
    """Unpin a score from the user's profile.

    Args:
        db: Database session dependency.
        user_cache_service: User cache service.
        score_id: The score ID to unpin.
        current_user: The authenticated client user.

    Raises:
        RequestError: If the score is not found.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id

    state = await lock_score_pin_state(db, user_id, score_id)
    score_record = state.score
    if score_record is None:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)

    game_mode = score_record.gamemode
    changed = state.order_normalised
    if score_record.pinned_order == 0:
        if changed:
            await db.commit()
            await _invalidate_score_pin_cache(user_cache_service, user_id, game_mode)
        return

    removed_order = score_record.pinned_order
    for pinned_score in state.pinned_scores:
        if pinned_score.id == score_id:
            pinned_score.pinned_order = 0
        elif pinned_score.pinned_order > removed_order:
            pinned_score.pinned_order -= 1
        db.add(pinned_score)
    score_record.pinned_order = 0
    await db.commit()
    await _invalidate_score_pin_cache(user_cache_service, user_id, game_mode)


@router.post(
    "/score-pins/{score_id}/reorder",
    status_code=204,
    name="Reorder pinned score",
    description=(
        "\nReorder the display order of a pinned score. Provide only one of after_score_id or before_score_id."
    ),
    tags=["Scores"],
)
async def reorder_score_pin(
    db: Database,
    user_cache_service: UserCacheService,
    current_user: ClientUser,
    score_id: Annotated[int, Path(description="Score ID")],
    after_score_id: Annotated[int | None, Body(description="Place after this score")] = None,
    before_score_id: Annotated[int | None, Body(description="Place before this score")] = None,
):
    """Reorder a pinned score.

    Args:
        db: Database session dependency.
        user_cache_service: User cache service.
        current_user: The authenticated client user.
        score_id: The score ID to reorder.
        after_score_id: Place after this score ID.
        before_score_id: Place before this score ID.

    Raises:
        RequestError: If score not found, not pinned, or invalid parameters.
    """
    # Get user ID immediately to avoid lazy loading issues
    user_id = current_user.id

    if (after_score_id is None) == (before_score_id is None):
        raise RequestError(
            ErrorType.INVALID_REQUEST,
            {"error": "Either after_score_id or before_score_id must be provided (but not both)"},
        )

    state = await lock_score_pin_state(db, user_id, score_id)
    score_record = state.score
    if score_record is None:
        raise RequestError(ErrorType.SCORE_NOT_FOUND)
    if score_record.pinned_order == 0:
        raise RequestError(ErrorType.SCORE_NOT_PINNED)

    game_mode = score_record.gamemode
    current_ids = [pinned_score.id for pinned_score in state.pinned_scores]
    try:
        ordered_ids = reordered_score_pin_ids(
            current_ids,
            score_id,
            before_score_id=before_score_id,
            after_score_id=after_score_id,
        )
    except ValueError as exc:
        if "Reference score" in str(exc):
            detail = "After score not found" if after_score_id else "Before score not found"
            raise RequestError(ErrorType.SCORE_NOT_FOUND, {"error": detail}) from exc
        raise RequestError(ErrorType.INVALID_REQUEST, {"error": str(exc)}) from exc

    if not state.order_normalised and ordered_ids == current_ids:
        return
    by_id = {pinned_score.id: pinned_score for pinned_score in state.pinned_scores}
    for pinned_order, pinned_score_id in enumerate(ordered_ids, start=1):
        pinned_score = by_id[pinned_score_id]
        pinned_score.pinned_order = pinned_order
        db.add(pinned_score)
    await db.commit()
    await _invalidate_score_pin_cache(user_cache_service, user_id, game_mode)
