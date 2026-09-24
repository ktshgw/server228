"""Transactional score deletion and user gameplay-profile rebuilding."""

from dataclasses import dataclass

from app.config import settings
from app.database.achievement import UserAchievement
from app.database.beatmap import Beatmap
from app.database.beatmap_playcounts import BeatmapPlaycounts
from app.database.best_scores import BestScore
from app.database.counts import MonthlyPlaycounts, ReplayWatchedCount
from app.database.daily_challenge import DailyChallengeStats
from app.database.events import Event
from app.database.item_attempts_count import ItemAttemptsCount
from app.database.playlist_best_score import PlaylistBestScore, process_playlist_best_score
from app.database.rank_history import RankHistory, RankTop
from app.database.score import Score, _process_statistics
from app.database.score_import import ScoreImport
from app.database.score_token import ScoreToken
from app.database.statistics import UserStatistics
from app.database.total_score_best_scores import TotalScoreBestScore
from app.database.user import User, UserProfileCover
from app.models.mods import mods_can_get_pp
from app.models.score import GameMode, ScoreData
from app.service.beatmap_ranking_service import get_effective_beatmap_policies

from redis.asyncio import Redis
from sqlalchemy import delete
from sqlalchemy.orm import joinedload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


@dataclass(frozen=True, slots=True)
class ScoreDeletionResult:
    score_id: int
    ruleset: GameMode
    replay_paths: tuple[str, ...]
    score_data: ScoreData


@dataclass(frozen=True, slots=True)
class ProfileClearResult:
    score_ids: tuple[int, ...]
    replay_paths: tuple[str, ...]
    avatar_path: str | None


def _reset_statistics(statistics: UserStatistics) -> None:
    for name in (
        "count_100",
        "count_300",
        "count_50",
        "count_miss",
        "ranked_score",
        "total_score",
        "total_hits",
        "maximum_combo",
        "play_count",
        "play_time",
        "replays_watched_by_others",
        "grade_ss",
        "grade_ssh",
        "grade_s",
        "grade_sh",
        "grade_a",
        "grade_b",
        "grade_c",
        "grade_d",
    ):
        setattr(statistics, name, 0)
    statistics.pp = 0.0
    statistics.hit_accuracy = 0.0
    statistics.level_current = 1.0


async def rebuild_user_gameplay_aggregates(session: AsyncSession, redis: Redis, user: User) -> None:
    """Rebuild every score-derived aggregate after an administrative deletion."""

    user_id = user.id
    await session.execute(delete(BestScore).where(col(BestScore.user_id) == user_id))
    await session.execute(delete(TotalScoreBestScore).where(col(TotalScoreBestScore.user_id) == user_id))
    await session.execute(delete(PlaylistBestScore).where(col(PlaylistBestScore.user_id) == user_id))
    await session.execute(delete(ItemAttemptsCount).where(col(ItemAttemptsCount.user_id) == user_id))
    await session.execute(delete(MonthlyPlaycounts).where(col(MonthlyPlaycounts.user_id) == user_id))
    await session.execute(delete(BeatmapPlaycounts).where(col(BeatmapPlaycounts.user_id) == user_id))

    statistics_rows = list(
        (await session.exec(select(UserStatistics).where(col(UserStatistics.user_id) == user_id))).all()
    )
    for statistics in statistics_rows:
        _reset_statistics(statistics)
        session.add(statistics)
    await session.flush()

    scores = list(
        (
            await session.exec(
                select(Score)
                .where(
                    col(Score.user_id) == user_id,
                    col(Score.processed).is_(True),
                )
                .options(joinedload(Score.beatmap))
                .order_by(col(Score.ended_at), col(Score.id))
            )
        ).all()
    )
    beatmaps_by_id: dict[int, Beatmap] = {score.beatmap.id: score.beatmap for score in scores}
    policies = await get_effective_beatmap_policies(session, list(beatmaps_by_id.values()))

    pp_best: dict[tuple[GameMode, int], Score] = {}
    for score in scores:
        policy = policies[score.beatmap_id]
        can_award_pp = (
            score.passed
            and score.ranked
            and score.ranked_score_eligible
            and (policy.pp_enabled or settings.enable_all_beatmap_pp)
            and mods_can_get_pp(int(score.gamemode), score.mods)
            and score.pp > 0
        )
        if not can_award_pp:
            continue
        key = (score.gamemode, score.beatmap_id)
        previous = pp_best.get(key)
        if previous is None or score.pp > previous.pp:
            pp_best[key] = score
    session.add_all(
        [
            BestScore(
                user_id=user_id,
                score_id=score.id,
                beatmap_id=score.beatmap_id,
                gamemode=score.gamemode,
                pp=score.pp,
                acc=score.accuracy,
            )
            for score in pp_best.values()
        ]
    )
    await session.flush()

    # Reuse the same aggregate code as a normal score submission, but do not
    # send live-client playtime events or create activity entries again.
    for score in scores:
        await _process_statistics(
            session,
            redis,
            user,
            score,
            score_token=0,
            beatmap_length=score.beatmap.hit_length,
            beatmap_policy=policies[score.beatmap_id],
            emit_playtime_event=False,
        )

    multiplayer_scores = [score for score in scores if score.room_id is not None and score.playlist_item_id is not None]
    for score in multiplayer_scores:
        room_id = score.room_id
        playlist_item_id = score.playlist_item_id
        if room_id is None or playlist_item_id is None:
            continue
        await process_playlist_best_score(
            room_id,
            playlist_item_id,
            user_id,
            score.id,
            score.total_score,
            session,
        )
    for room_id in {score.room_id for score in multiplayer_scores if score.room_id is not None}:
        await ItemAttemptsCount.get_or_create(room_id, user_id, session)
    await session.flush()


async def delete_user_score(
    session: AsyncSession,
    redis: Redis,
    user: User,
    score_id: int,
) -> ScoreDeletionResult | None:
    score = (
        await session.exec(
            select(Score)
            .where(col(Score.id) == score_id, col(Score.user_id) == user.id)
            .options(joinedload(Score.beatmap))
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if score is None:
        return None

    replay_paths = (score.replay_filename,) if score.has_replay else ()
    ruleset = score.gamemode
    score_data = score.to_score_data()
    await session.execute(delete(ScoreImport).where(col(ScoreImport.score_id) == score_id))
    await session.execute(delete(ScoreToken).where(col(ScoreToken.score_id) == score_id))
    await session.execute(delete(BestScore).where(col(BestScore.score_id) == score_id))
    await session.execute(delete(TotalScoreBestScore).where(col(TotalScoreBestScore.score_id) == score_id))
    await session.execute(delete(PlaylistBestScore).where(col(PlaylistBestScore.score_id) == score_id))
    await session.execute(delete(Score).where(col(Score.id) == score_id))
    await session.flush()
    await rebuild_user_gameplay_aggregates(session, redis, user)
    return ScoreDeletionResult(
        score_id=score_id,
        ruleset=ruleset,
        replay_paths=replay_paths,
        score_data=score_data,
    )


async def clear_user_profile(
    session: AsyncSession,
    user: User,
    *,
    reset_public_profile: bool,
    avatar_path: str | None,
) -> ProfileClearResult:
    score_rows = list((await session.exec(select(Score).where(col(Score.user_id) == user.id).with_for_update())).all())
    score_ids = tuple(score.id for score in score_rows)
    replay_paths = tuple(score.replay_filename for score in score_rows if score.has_replay)
    user_id = user.id

    await session.execute(delete(ScoreImport).where(col(ScoreImport.target_user_id) == user_id))
    await session.execute(delete(ScoreToken).where(col(ScoreToken.user_id) == user_id))
    await session.execute(delete(BestScore).where(col(BestScore.user_id) == user_id))
    await session.execute(delete(TotalScoreBestScore).where(col(TotalScoreBestScore.user_id) == user_id))
    await session.execute(delete(PlaylistBestScore).where(col(PlaylistBestScore.user_id) == user_id))
    await session.execute(delete(ItemAttemptsCount).where(col(ItemAttemptsCount.user_id) == user_id))
    await session.execute(delete(MonthlyPlaycounts).where(col(MonthlyPlaycounts.user_id) == user_id))
    await session.execute(delete(ReplayWatchedCount).where(col(ReplayWatchedCount.user_id) == user_id))
    await session.execute(delete(BeatmapPlaycounts).where(col(BeatmapPlaycounts.user_id) == user_id))
    await session.execute(delete(UserAchievement).where(col(UserAchievement.user_id) == user_id))
    await session.execute(delete(RankHistory).where(col(RankHistory.user_id) == user_id))
    await session.execute(delete(RankTop).where(col(RankTop.user_id) == user_id))
    await session.execute(delete(Event).where(col(Event.user_id) == user_id))
    if score_ids:
        await session.execute(delete(Score).where(col(Score.id).in_(score_ids)))

    statistics_rows = list(
        (await session.exec(select(UserStatistics).where(col(UserStatistics.user_id) == user_id))).all()
    )
    for statistics in statistics_rows:
        _reset_statistics(statistics)
        session.add(statistics)

    daily = await session.get(DailyChallengeStats, user_id)
    if daily is None:
        session.add(DailyChallengeStats(user_id=user_id))
    else:
        for name in (
            "daily_streak_best",
            "daily_streak_current",
            "playcount",
            "top_10p_placements",
            "top_50p_placements",
            "weekly_streak_best",
            "weekly_streak_current",
        ):
            setattr(daily, name, 0)
        daily.last_update = None
        daily.last_day_streak = None
        daily.last_weekly_streak = None
        session.add(daily)

    if reset_public_profile:
        user.avatar_url = ""
        user.page = {"html": "", "raw": ""}
        user.cover = UserProfileCover(url="")
        user.location = None
        user.interests = None
        user.occupation = None
        user.discord = None
        user.website = None
        user.twitter = None
        user.title = None
        user.title_url = None
        user.profile_colour = None
        user.profile_hue = None
        user.playstyle = []
        session.add(user)

    await session.flush()
    return ProfileClearResult(
        score_ids=score_ids,
        replay_paths=replay_paths,
        avatar_path=avatar_path if reset_public_profile else None,
    )
