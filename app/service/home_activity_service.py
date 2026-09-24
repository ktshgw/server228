"""Local releases and real client concurrency for the public homepage."""

from datetime import UTC, datetime

from app.database import Beatmap, Beatmapset, User
from app.database.beatmap_ranking import BeatmapRankingAudit, RankingPolicyAction
from app.models.beatmap import BeatmapRankStatus
from app.service.beatmap_ranking_service import get_effective_beatmap_policies
from app.service.online_presence_service import get_online_user_ids

from sqlalchemy.orm import lazyload
from sqlmodel import col, func, select

HISTORY_KEY = "soms:online-history:v1"
SAMPLE_SECONDS = 600
HISTORY_SECONDS = 24 * 60 * 60


async def real_online_count(session, redis) -> int:
    ids = await get_online_user_ids(redis)
    if not ids:
        return 0
    return int(
        (
            await session.exec(
                select(func.count(col(User.id))).where(
                    col(User.id).in_(ids),
                    col(User.is_active).is_(True),
                    col(User.is_bot).is_(False),
                    ~User.is_restricted_query(col(User.id)),
                )
            )
        ).one()
    )


async def record_online_sample(session, redis, *, now: datetime | None = None):
    timestamp = int((now or datetime.now(UTC)).timestamp())
    bucket = timestamp // SAMPLE_SECONDS * SAMPLE_SECONDS
    count = await real_online_count(session, redis)
    previous = await redis.hkeys(HISTORY_KEY)
    expired = [key for key in previous if int(key) < bucket - HISTORY_SECONDS]
    async with redis.pipeline(transaction=True) as pipeline:
        pipeline.hset(HISTORY_KEY, str(bucket), count)
        if expired:
            pipeline.hdel(HISTORY_KEY, *expired)
        pipeline.expire(HISTORY_KEY, HISTORY_SECONDS * 2)
        await pipeline.execute()


async def online_history(redis, *, now: datetime | None = None):
    end = int((now or datetime.now(UTC)).timestamp())
    start = end - HISTORY_SECONDS
    values = await redis.hgetall(HISTORY_KEY)
    points = sorted((int(key), int(value)) for key, value in values.items() if start <= int(key) <= end)
    return {
        "start": start,
        "end": end,
        "interval": SAMPLE_SECONDS,
        "points": [{"time": timestamp, "users": count} for timestamp, count in points],
    }


async def local_releases(session, limit: int = 8):
    """Audit chronology, filtered against today's effective difficulty policies.

    Official ranking dates never qualify a map for this feed. An invalidated,
    cleared, or superseded local decision must disappear from it.
    """
    result = []
    seen = set()
    before_id = None
    while len(result) < limit:
        query = select(BeatmapRankingAudit).where(BeatmapRankingAudit.action == RankingPolicyAction.APPLY)
        if before_id is not None:
            query = query.where(BeatmapRankingAudit.id < before_id)
        audits = list((await session.exec(query.order_by(col(BeatmapRankingAudit.id).desc()).limit(100))).all())
        if not audits:
            break
        before_id = audits[-1].id
        set_ids = {item.beatmapset_id for item in audits} - seen
        sets = {
            item.id: item
            for item in (
                await session.exec(select(Beatmapset).options(lazyload("*")).where(col(Beatmapset.id).in_(set_ids)))
            ).all()
        }
        maps = list(
            (
                await session.exec(
                    select(Beatmap).options(lazyload("*")).where(col(Beatmap.beatmapset_id).in_(set_ids))
                )
            ).all()
        )
        policies = await get_effective_beatmap_policies(session, maps)
        for audit in audits:
            after = audit.after or {}
            status = after.get("status")
            if status not in (int(BeatmapRankStatus.RANKED), int(BeatmapRankStatus.LOVED)):
                continue
            if (audit.before or {}).get("is_active") and (audit.before or {}).get("status") == status:
                continue
            if audit.beatmapset_id in seen or audit.beatmapset_id not in sets:
                continue
            matching = [
                item
                for item in maps
                if item.beatmapset_id == audit.beatmapset_id
                and (audit.beatmap_id is None or item.id == audit.beatmap_id)
                and policies[item.id].source.value != "upstream"
                and int(policies[item.id].status) == status
            ]
            if not matching:
                continue
            item = sets[audit.beatmapset_id]
            seen.add(item.id)
            covers = item.covers or {}
            result.append(
                {
                    "id": item.id,
                    "title": item.title,
                    "artist": item.artist,
                    "creator": item.creator,
                    "covers": covers,
                    "cover_url": covers.get("card") or covers.get("cover"),
                    "status": BeatmapRankStatus(status).name.lower(),
                    "is_locally_ranked": True,
                    "local_policy": {
                        "source": audit.scope.value,
                        "status": BeatmapRankStatus(status).name.lower(),
                        "leaderboard_enabled": any(policies[b.id].leaderboard_enabled for b in matching),
                        "pp_enabled": any(policies[b.id].pp_enabled for b in matching),
                    },
                    "ranked_date": audit.created_at,
                    "status_changed_at": audit.created_at,
                    "beatmaps": [
                        {
                            "id": b.id,
                            "version": b.version,
                            "difficulty_rating": b.difficulty_rating,
                            "mode": b.mode.value,
                        }
                        for b in matching
                    ],
                }
            )
            if len(result) == limit:
                break
    return result
