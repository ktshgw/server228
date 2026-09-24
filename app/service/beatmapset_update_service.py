"""Beatmapset update service.

Handles synchronization of beatmapset data with official osu! API,
including tracking map changes, status updates, and scheduled syncs.
"""

import asyncio
import datetime
from datetime import timedelta
from enum import Enum
import math
import random
from typing import TYPE_CHECKING, NamedTuple, cast

from app.config import settings
from app.database.beatmap import Beatmap, BeatmapDict
from app.database.beatmap_sync import BeatmapSync, SavedBeatmapMeta
from app.database.beatmapset import Beatmapset, BeatmapsetDict
from app.dependencies.database import get_redis, with_db
from app.helpers import bg_tasks, utcnow
from app.log import logger
from app.models.beatmap import BeatmapRankStatus

from .beatmap_ranking_reconciliation_service import (
    ScorePolicyTransition,
    ScoreReconciliationResult,
    invalidate_score_reconciliation_caches,
    plan_score_policy_transitions,
    reconcile_disabled_score_features,
)
from .beatmap_ranking_service import (
    LOCAL_RANK_MAX_SYNC_INTERVAL,
    EffectiveBeatmapPolicy,
    beatmapset_has_active_local_rank,
    beatmapset_has_pending_local_rank_review,
    get_effective_beatmap_policy,
    invalidate_for_upstream_change,
)
from .beatmapset_cache_service import get_beatmapset_cache_service

from httpx import HTTPError, HTTPStatusError
from sqlalchemy.exc import IntegrityError
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

if TYPE_CHECKING:
    from app.fetcher import Fetcher


class BeatmapChangeType(Enum):
    MAP_UPDATED = "map_updated"
    MAP_DELETED = "map_deleted"
    MAP_ADDED = "map_added"
    STATUS_CHANGED = "status_changed"


class BeatmapsetChangeType(Enum):
    STATUS_CHANGED = "status_changed"
    HYPE_CHANGED = "hype_changed"
    NOMINATIONS_CHANGED = "nominations_changed"
    RANKED_DATE_CHANGED = "ranked_date_changed"
    PLAYCOUNT_CHANGED = "playcount_changed"


class ChangedBeatmap(NamedTuple):
    beatmap_id: int
    type: BeatmapChangeType


BASE = 1200
TAU = 3600
JITTER_MIN = -30
JITTER_MAX = 30
MIN_DELTA = 1200
GROWTH = 2.0
GRAVEYARD_DOUBLING_PERIOD_DAYS = 30
GRAVEYARD_MAX_DAYS = 365
STATUS_FACTOR: dict[BeatmapRankStatus, float] = {
    BeatmapRankStatus.WIP: 0.5,
    BeatmapRankStatus.PENDING: 0.5,
    BeatmapRankStatus.GRAVEYARD: 1,
}
SCHEDULER_INTERVAL_MINUTES = 2


def _comparable_timestamp(value: datetime.datetime) -> datetime.datetime:
    """Normalise API/driver timestamps for monotonic freshness checks."""

    if value.tzinfo is not None:
        return value.astimezone(datetime.UTC).replace(tzinfo=None)
    return value


class EnsuredBeatmap(BeatmapDict):
    checksum: str
    ranked: int


class EnsuredBeatmapset(BeatmapsetDict):
    ranked: int
    ranked_date: datetime.datetime
    last_updated: datetime.datetime
    play_count: int
    beatmaps: list[EnsuredBeatmap]


class ProcessingBeatmapset:
    def __init__(self, beatmapset: EnsuredBeatmapset, record: BeatmapSync) -> None:
        self.beatmapset = beatmapset
        self.status = BeatmapRankStatus(self.beatmapset["ranked"])
        self.record = record

    def calculate_next_sync_time(
        self,
    ) -> timedelta | None:
        if self.status.ranked():
            return None

        now = utcnow()
        if self.status == BeatmapRankStatus.QUALIFIED:
            assert self.beatmapset["ranked_date"] is not None, "ranked_date should not be None for qualified maps"
            time_to_ranked = (self.beatmapset["ranked_date"] + timedelta(days=7) - now).total_seconds()
            baseline = max(MIN_DELTA, time_to_ranked / 2)
            next_delta = max(MIN_DELTA, baseline)
        elif self.status in {BeatmapRankStatus.WIP, BeatmapRankStatus.PENDING}:
            seconds_since_update = (now - self.beatmapset["last_updated"]).total_seconds()
            factor_update = max(1.0, seconds_since_update / TAU)
            factor_play = 1.0 + math.log(1.0 + self.beatmapset["play_count"])
            status_factor = STATUS_FACTOR[self.status]
            baseline = BASE * factor_play / factor_update * status_factor
            next_delta = max(MIN_DELTA, baseline * (GROWTH ** (self.record.consecutive_no_change + 1)))
        elif self.status == BeatmapRankStatus.GRAVEYARD:
            days_since_update = (now - self.beatmapset["last_updated"]).days
            doubling_periods = days_since_update / GRAVEYARD_DOUBLING_PERIOD_DAYS
            delta = MIN_DELTA * (2**doubling_periods)
            max_seconds = GRAVEYARD_MAX_DAYS * 86400
            next_delta = min(max_seconds, delta)
        else:
            next_delta = MIN_DELTA

        if next_delta > 86400:
            minor = round(next_delta / 10)
            jitter = timedelta(seconds=random.randint(-minor, minor))
        else:
            jitter = timedelta(minutes=random.randint(JITTER_MIN, JITTER_MAX))
        return timedelta(seconds=next_delta) + jitter

    @property
    def beatmapset_changed(self) -> bool:
        return self.record.beatmap_status != BeatmapRankStatus(self.beatmapset["ranked"])

    @property
    def changed_beatmaps(self) -> list[ChangedBeatmap]:
        changed_beatmaps = []
        for bm in self.beatmapset["beatmaps"]:
            saved = next((s for s in self.record.beatmaps if s["beatmap_id"] == bm["id"]), None)
            if not saved or saved["is_deleted"]:
                changed_beatmaps.append(ChangedBeatmap(bm["id"], BeatmapChangeType.MAP_ADDED))
            elif saved["md5"] != bm["checksum"]:
                changed_beatmaps.append(ChangedBeatmap(bm["id"], BeatmapChangeType.MAP_UPDATED))
            elif saved["beatmap_status"] != BeatmapRankStatus(bm["ranked"]):
                changed_beatmaps.append(ChangedBeatmap(bm["id"], BeatmapChangeType.STATUS_CHANGED))
        for saved in self.record.beatmaps:
            if (
                not any(bm["id"] == saved["beatmap_id"] for bm in self.beatmapset["beatmaps"])
                and not saved["is_deleted"]
            ):
                changed_beatmaps.append(ChangedBeatmap(saved["beatmap_id"], BeatmapChangeType.MAP_DELETED))
        return changed_beatmaps


def _metadata_only_beatmaps(
    beatmaps: list[EnsuredBeatmap],
    changed: list[ChangedBeatmap],
) -> list[EnsuredBeatmap]:
    """Return snapshot rows which need metadata refresh but no rank transition."""

    changed_ids = {change.beatmap_id for change in changed}
    return [beatmap for beatmap in beatmaps if beatmap["id"] not in changed_ids]


class BeatmapsetUpdateService:
    def __init__(self, fetcher: "Fetcher"):
        self.fetcher = fetcher
        self._adding_missing = False
        self._sync_locks: dict[int, asyncio.Lock] = {}

    def _sync_lock(self, beatmapset_id: int) -> asyncio.Lock:
        """Serialize fetch/apply/manifest work in the single-server deployment."""

        return self._sync_locks.setdefault(beatmapset_id, asyncio.Lock())

    async def refresh_beatmapset(self, beatmapset_id: int) -> EnsuredBeatmapset:
        """Fetch and durably apply one full upstream beatmapset snapshot."""

        async with self._sync_lock(beatmapset_id):
            beatmapset = cast(EnsuredBeatmapset, await self.fetcher.get_beatmapset(beatmapset_id))
            await self._sync_immediately_locked(beatmapset)
            return beatmapset

    async def add_missing_beatmapset(self, beatmapset_id: int, immediate: bool = False) -> bool:
        if immediate:
            async with self._sync_lock(beatmapset_id):
                beatmapset = await self.fetcher.get_beatmapset(beatmapset_id)
                await self._sync_immediately_locked(cast(EnsuredBeatmapset, beatmapset))
            logger.debug(f"triggered immediate sync for beatmapset {beatmapset_id} ")
            return True
        beatmapset = await self.fetcher.get_beatmapset(beatmapset_id)
        await self.add(beatmapset)
        logger.debug(f"added missing beatmapset {beatmapset_id} ")
        return True

    async def add_missing_beatmapsets(self):
        if self._adding_missing:
            return
        self._adding_missing = True
        async with with_db() as session:
            missings = await session.exec(
                select(Beatmapset.id)
                .where(
                    col(Beatmapset.beatmap_status).in_(
                        [
                            BeatmapRankStatus.WIP,
                            BeatmapRankStatus.PENDING,
                            BeatmapRankStatus.GRAVEYARD,
                            BeatmapRankStatus.QUALIFIED,
                        ]
                    ),
                    col(Beatmapset.id).notin_(select(BeatmapSync.beatmapset_id)),
                )
                .order_by(col(Beatmapset.last_updated).desc())
            )
            total = 0
            for missing in missings:
                try:
                    if await self.add_missing_beatmapset(missing):
                        total += 1
                except HTTPStatusError as e:
                    if e.response.status_code == 404:
                        logger.opt(colors=True).warning(f"beatmapset {missing} not found (404), skipping")

                        session.add(
                            BeatmapSync(
                                beatmapset_id=missing,
                                beatmap_status=BeatmapRankStatus.GRAVEYARD,
                                next_sync_time=datetime.datetime(year=6000, month=1, day=1),
                                beatmaps=[],
                            )
                        )
                    else:
                        logger.error(f"failed to add missing beatmapset {missing}: [{e.__class__.__name__}] {e}")
                except Exception as e:
                    logger.error(f"failed to add missing beatmapset {missing}: {e}")
            if total > 0:
                logger.opt(colors=True).info(f"added {total} missing beatmapset")
            await session.commit()
        self._adding_missing = False

    async def add(self, set: BeatmapsetDict, calculate_next_sync: bool = True):
        beatmapset = cast(EnsuredBeatmapset, set)
        async with with_db() as session:
            beatmapset_id = beatmapset["id"]
            sync_record = await session.get(BeatmapSync, beatmapset_id)
            if not sync_record:
                database_beatmapset = await session.get(Beatmapset, beatmapset_id)
                if database_beatmapset:
                    status = BeatmapRankStatus(database_beatmapset.beatmap_status)
                    await database_beatmapset.awaitable_attrs.beatmaps
                    beatmaps = [
                        SavedBeatmapMeta(
                            beatmap_id=bm.id,
                            md5=bm.checksum,
                            is_deleted=False,
                            beatmap_status=BeatmapRankStatus(bm.beatmap_status),
                        )
                        for bm in database_beatmapset.beatmaps
                    ]
                else:
                    ranked = beatmapset.get("ranked")
                    if ranked is None:
                        raise ValueError("ranked field is required")
                    status = BeatmapRankStatus(ranked)
                    beatmap_list = beatmapset.get("beatmaps", [])
                    beatmaps = []
                    for bm in beatmap_list:
                        bm_id = bm.get("id")
                        checksum = bm.get("checksum")
                        ranked = bm.get("ranked")
                        if bm_id is None or checksum is None or ranked is None:
                            continue
                        beatmaps.append(
                            SavedBeatmapMeta(
                                beatmap_id=bm_id,
                                md5=checksum,
                                is_deleted=False,
                                beatmap_status=BeatmapRankStatus(ranked),
                            )
                        )

                sync_record = BeatmapSync(
                    beatmapset_id=beatmapset_id,
                    beatmaps=beatmaps,
                    beatmap_status=status,
                )
                session.add(sync_record)
                await session.commit()
                await session.refresh(sync_record)
            else:
                # Never replace the durable comparison manifest with fetched
                # metadata before processing its delta. This path can be hit
                # by a concurrent/manual add for an already tracked set.
                if calculate_next_sync:
                    await self.sync(sync_record, session, beatmapset=beatmapset)
                    await session.commit()
                    return
                ranked = beatmapset.get("ranked")
                if ranked is None:
                    raise ValueError("ranked field is required")
                beatmap_list = beatmapset.get("beatmaps", [])
                beatmaps = []
                for bm in beatmap_list:
                    bm_id = bm.get("id")
                    checksum = bm.get("checksum")
                    bm_ranked = bm.get("ranked")
                    if bm_id is None or checksum is None or bm_ranked is None:
                        continue
                    beatmaps.append(
                        SavedBeatmapMeta(
                            beatmap_id=bm_id,
                            md5=checksum,
                            is_deleted=False,
                            beatmap_status=BeatmapRankStatus(bm_ranked),
                        )
                    )
                sync_record.beatmaps = beatmaps
                sync_record.beatmap_status = BeatmapRankStatus(ranked)
            if calculate_next_sync:
                processing = ProcessingBeatmapset(beatmapset, sync_record)
                next_time_delta = processing.calculate_next_sync_time()
                if not next_time_delta:
                    # for qualified -> ranked, run immediate sync
                    await BeatmapsetUpdateService._sync_immediately(self, beatmapset)
                    return
                sync_record.next_sync_time = utcnow() + next_time_delta
            beatmapset_id = beatmapset.get("id")
            if beatmapset_id:
                logger.opt(colors=True).debug(f"<g>[{beatmapset_id}]</g> next sync at {sync_record.next_sync_time}")
            await session.commit()

    async def _sync_immediately(self, beatmapset: EnsuredBeatmapset) -> None:
        async with self._sync_lock(beatmapset["id"]):
            await self._sync_immediately_locked(beatmapset)

    async def _sync_immediately_locked(self, beatmapset: EnsuredBeatmapset) -> None:
        async with with_db() as session:
            record = await session.get(BeatmapSync, beatmapset["id"])
            if not record:
                database_beatmapset = await session.get(Beatmapset, beatmapset["id"])
                if database_beatmapset is None:
                    # BeatmapSync has a foreign key to Beatmapset. A rank
                    # command is often the first request for an abandoned
                    # upstream set, so import the parent and all difficulties
                    # before creating its comparison manifest.
                    await session.rollback()
                    if not await self._process_changed_beatmapset(beatmapset):
                        return
                    await self._process_changed_beatmaps(
                        [
                            ChangedBeatmap(beatmap["id"], BeatmapChangeType.MAP_ADDED)
                            for beatmap in beatmapset["beatmaps"]
                        ],
                        beatmapset["beatmaps"],
                        beatmapset_id=beatmapset["id"],
                    )

                    # Serialize tracker creation with a concurrent rank/apply,
                    # which uses the same Beatmapset row as its per-set lock.
                    await session.exec(select(Beatmapset).where(Beatmapset.id == beatmapset["id"]).with_for_update())
                    record = await session.get(BeatmapSync, beatmapset["id"])
                    if record is None:
                        record = BeatmapSync(
                            beatmapset_id=beatmapset["id"],
                            beatmaps=[
                                SavedBeatmapMeta(
                                    beatmap_id=beatmap["id"],
                                    md5=beatmap["checksum"],
                                    is_deleted=False,
                                    beatmap_status=BeatmapRankStatus(beatmap["ranked"]),
                                )
                                for beatmap in beatmapset["beatmaps"]
                            ],
                            beatmap_status=BeatmapRankStatus(beatmapset["ranked"]),
                        )
                        session.add(record)
                    await session.commit()
                    return

                database_beatmaps = list(
                    (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset["id"]))).all()
                )
                record = BeatmapSync(
                    beatmapset_id=beatmapset["id"],
                    beatmaps=[
                        SavedBeatmapMeta(
                            beatmap_id=beatmap.id,
                            md5=beatmap.checksum,
                            is_deleted=beatmap.deleted_at is not None,
                            beatmap_status=BeatmapRankStatus(beatmap.beatmap_status),
                        )
                        for beatmap in database_beatmaps
                    ],
                    beatmap_status=(
                        BeatmapRankStatus(database_beatmapset.beatmap_status)
                        if database_beatmapset is not None
                        else BeatmapRankStatus(beatmapset["ranked"])
                    ),
                )
                session.add(record)
                await session.commit()
                await session.refresh(record)
            await self._sync_locked(record, session, beatmapset=beatmapset)
            await session.commit()

    async def sync(
        self,
        record: BeatmapSync,
        session: AsyncSession,
        *,
        beatmapset: EnsuredBeatmapset | None = None,
    ):
        async with self._sync_lock(record.beatmapset_id):
            await self._sync_locked(record, session, beatmapset=beatmapset)
            # The lock must cover the manifest write, not just its staging;
            # callers may still perform a harmless extra commit afterwards.
            await session.commit()

    async def _sync_locked(
        self,
        record: BeatmapSync,
        session: AsyncSession,
        *,
        beatmapset: EnsuredBeatmapset | None = None,
    ):
        logger.opt(colors=True).debug(f"<g>[{record.beatmapset_id}]</g> syncing...")
        if beatmapset is None:
            try:
                beatmapset = cast(EnsuredBeatmapset, await self.fetcher.get_beatmapset(record.beatmapset_id))
            except Exception as e:
                if isinstance(e, HTTPStatusError) and e.response.status_code == 404:
                    logger.opt(colors=True).warning(
                        f"<g>[{record.beatmapset_id}]</g> beatmapset not found (404); "
                        "marking cached difficulties as deleted"
                    )
                    active_ids = list(
                        (
                            await session.exec(
                                select(Beatmap.id).where(
                                    Beatmap.beatmapset_id == record.beatmapset_id,
                                    col(Beatmap.deleted_at).is_(None),
                                )
                            )
                        ).all()
                    )
                    if active_ids:
                        await self._process_changed_beatmaps(
                            [ChangedBeatmap(beatmap_id, BeatmapChangeType.MAP_DELETED) for beatmap_id in active_ids],
                            [],
                            beatmapset_id=record.beatmapset_id,
                        )

                    # Take the common parent lock only after child transactions
                    # finish, then make the tracker decision from current rows.
                    # This serializes with a rank command which may have raced
                    # the failed upstream fetch.
                    database_beatmapset = (
                        await session.exec(
                            select(Beatmapset)
                            .where(Beatmapset.id == record.beatmapset_id)
                            .with_for_update()
                            .execution_options(populate_existing=True)
                        )
                    ).first()
                    keep_tracker = await beatmapset_has_active_local_rank(
                        session,
                        record.beatmapset_id,
                        for_update=True,
                    ) or await beatmapset_has_pending_local_rank_review(
                        session,
                        record.beatmapset_id,
                        for_update=True,
                    )
                    if database_beatmapset is not None:
                        database_beatmapset.beatmap_status = BeatmapRankStatus.GRAVEYARD
                    if keep_tracker:
                        record.beatmaps = [
                            SavedBeatmapMeta(
                                beatmap_id=meta["beatmap_id"],
                                md5=meta["md5"],
                                is_deleted=True,
                                beatmap_status=meta["beatmap_status"],
                            )
                            for meta in record.beatmaps
                        ]
                        record.beatmap_status = BeatmapRankStatus.GRAVEYARD
                        record.next_sync_time = utcnow() + LOCAL_RANK_MAX_SYNC_INTERVAL
                    else:
                        logger.opt(colors=True).warning(
                            f"<g>[{record.beatmapset_id}]</g> beatmapset not found (404), removing from sync list"
                        )
                        await session.delete(record)
                    return
                if isinstance(e, HTTPError):
                    logger.opt(colors=True).warning(
                        f"<g>[{record.beatmapset_id}]</g> "
                        f"failed to fetch beatmapset: [{e.__class__.__name__}] {e}, retrying later"
                    )
                else:
                    logger.opt(colors=True).exception(
                        f"<g>[{record.beatmapset_id}]</g> unexpected error: {e}, retrying later"
                    )
                record.next_sync_time = utcnow() + timedelta(seconds=MIN_DELTA)
                return
        processing = ProcessingBeatmapset(beatmapset, record)
        changes_by_id = {change.beatmap_id: change for change in processing.changed_beatmaps}
        for database_change in await self._database_consistency_changes(beatmapset):
            changes_by_id[database_change.beatmap_id] = database_change
        changed_beatmaps = [changes_by_id[beatmap_id] for beatmap_id in sorted(changes_by_id)]
        changed = bool(processing.beatmapset_changed or changed_beatmaps)

        # Refresh parent and unchanged-child metadata on every accepted
        # snapshot. Revision/status changes remain on the transition path
        # below, so a metadata-only refresh cannot bypass invalidation.
        metadata_beatmaps = _metadata_only_beatmaps(beatmapset["beatmaps"], changed_beatmaps)
        if not await self._process_changed_beatmapset(
            beatmapset,
            metadata_beatmaps=metadata_beatmaps,
        ):
            logger.opt(colors=True).warning(f"<g>[{record.beatmapset_id}]</g> ignored an older upstream snapshot")
            return

        # Process durable ranking work before advancing the comparison
        # manifest. On error the old manifest remains and the next sync retries
        # the same delta. The processing operations are safe to replay.
        if changed_beatmaps:
            await self._process_changed_beatmaps(
                changed_beatmaps,
                beatmapset["beatmaps"],
                beatmapset_id=record.beatmapset_id,
            )

        # Lock before dirtying BeatmapSync. Ranking mutations use the same
        # Beatmapset -> BeatmapSync order, preventing a stale scheduler commit
        # from deleting or postponing a tracker created by an admin command.
        locked_beatmapset = (
            await session.exec(
                select(Beatmapset)
                .where(Beatmapset.id == record.beatmapset_id)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).first()
        if locked_beatmapset is None:
            await session.delete(record)
            return

        if changed:
            record.beatmaps = [
                SavedBeatmapMeta(
                    beatmap_id=bm["id"],
                    md5=bm["checksum"],
                    is_deleted=False,
                    beatmap_status=BeatmapRankStatus(bm["ranked"]),
                )
                for bm in beatmapset["beatmaps"]
            ]
            record.beatmap_status = BeatmapRankStatus(beatmapset["ranked"])
            record.consecutive_no_change = 0
        else:
            record.consecutive_no_change += 1

        next_time_delta = processing.calculate_next_sync_time()
        if await beatmapset_has_active_local_rank(session, record.beatmapset_id, for_update=True):
            next_time_delta = min(
                next_time_delta or LOCAL_RANK_MAX_SYNC_INTERVAL,
                LOCAL_RANK_MAX_SYNC_INTERVAL,
            )
        if not next_time_delta:
            logger.opt(colors=True).info(
                f"<yellow>[{beatmapset['id']}]</yellow> beatmapset has transformed to ranked or loved,"
                f" removing from sync list"
            )
            await session.delete(record)
        else:
            record.next_sync_time = utcnow() + next_time_delta
            logger.opt(colors=True).debug(f"<g>[{record.beatmapset_id}]</g> next sync at {record.next_sync_time}")

    async def _update_beatmaps(self):
        async with with_db() as session:
            logger.info("checking for beatmapset updates...")
            now = utcnow()
            beatmapset_ids = list(
                (
                    await session.exec(
                        select(BeatmapSync.beatmapset_id)
                        .where(BeatmapSync.next_sync_time <= now)
                        .order_by(col(BeatmapSync.next_sync_time).desc())
                    )
                ).all()
            )
        for beatmapset_id in beatmapset_ids:
            async with with_db() as session:
                record = await session.get(BeatmapSync, beatmapset_id)
                if record is not None:
                    await self.sync(record, session)

    async def _database_consistency_changes(
        self,
        beatmapset: EnsuredBeatmapset,
    ) -> list[ChangedBeatmap]:
        """Detect DB/manifest divergence left by an interrupted prior sync."""

        async with with_db() as session:
            database_beatmaps = list(
                (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset["id"]))).all()
            )

        database_by_id = {beatmap.id: beatmap for beatmap in database_beatmaps}
        upstream_by_id = {beatmap["id"]: beatmap for beatmap in beatmapset["beatmaps"]}
        changes: list[ChangedBeatmap] = []
        for beatmap_id, upstream in upstream_by_id.items():
            database = database_by_id.get(beatmap_id)
            if database is None or database.deleted_at is not None:
                changes.append(ChangedBeatmap(beatmap_id, BeatmapChangeType.MAP_ADDED))
            elif database.checksum != upstream["checksum"]:
                changes.append(ChangedBeatmap(beatmap_id, BeatmapChangeType.MAP_UPDATED))
            elif BeatmapRankStatus(database.beatmap_status) != BeatmapRankStatus(upstream["ranked"]):
                changes.append(ChangedBeatmap(beatmap_id, BeatmapChangeType.STATUS_CHANGED))
        for beatmap_id, database in database_by_id.items():
            if beatmap_id not in upstream_by_id and database.deleted_at is None:
                changes.append(ChangedBeatmap(beatmap_id, BeatmapChangeType.MAP_DELETED))
        return changes

    async def _process_changed_beatmapset(
        self,
        beatmapset: EnsuredBeatmapset,
        *,
        metadata_beatmaps: list[EnsuredBeatmap] | None = None,
    ) -> bool:
        async with with_db() as session:
            db_beatmapset = (
                await session.exec(
                    select(Beatmapset)
                    .where(Beatmapset.id == beatmapset["id"])
                    .with_for_update()
                    .execution_options(populate_existing=True)
                )
            ).first()
            if db_beatmapset is not None and _comparable_timestamp(db_beatmapset.last_updated) > _comparable_timestamp(
                beatmapset["last_updated"]
            ):
                logger.opt(colors=True).warning(
                    f"<g>[{beatmapset['id']}]</g> refusing stale snapshot dated "
                    f"{beatmapset['last_updated']} (database has {db_beatmapset.last_updated})"
                )
                return False

            metadata_beatmaps = metadata_beatmaps or []
            metadata_by_id = {beatmap["id"]: beatmap for beatmap in metadata_beatmaps}
            if len(metadata_by_id) != len(metadata_beatmaps):
                raise ValueError(f"Beatmapset {beatmapset['id']} snapshot contains duplicate difficulty IDs")
            if any(beatmap["beatmapset_id"] != beatmapset["id"] for beatmap in metadata_beatmaps):
                raise ValueError(f"Beatmapset {beatmapset['id']} snapshot contains a difficulty from another set")

            # Lock after the parent row, matching score/ranking writers. These
            # rows were classified as checksum/status-unchanged before entry;
            # revalidate under the lock so metadata refresh can never conceal a
            # concurrent revision that requires rank invalidation.
            existing_metadata_beatmaps: dict[int, Beatmap] = {}
            if metadata_by_id:
                existing_metadata_beatmaps = {
                    beatmap.id: beatmap
                    for beatmap in (
                        await session.exec(
                            select(Beatmap)
                            .where(
                                Beatmap.beatmapset_id == beatmapset["id"],
                                col(Beatmap.id).in_(metadata_by_id),
                            )
                            .with_for_update()
                            .execution_options(populate_existing=True)
                        )
                    ).all()
                }
                for beatmap_id, upstream in metadata_by_id.items():
                    existing = existing_metadata_beatmaps.get(beatmap_id)
                    if (
                        existing is None
                        or existing.deleted_at is not None
                        or existing.checksum != upstream["checksum"]
                        or BeatmapRankStatus(existing.beatmap_status) != BeatmapRankStatus(upstream["ranked"])
                    ):
                        logger.opt(colors=True).warning(
                            f"<g>[{beatmapset['id']}]</g> refusing metadata-only refresh for difficulty "
                            f"<blue>{beatmap_id}</blue>; its revision/status changed concurrently"
                        )
                        return False

            new_beatmapset = await Beatmapset.from_resp_no_save(beatmapset)  # pyright: ignore[reportArgumentType]
            if db_beatmapset:
                await session.merge(new_beatmapset)
            else:
                session.add(new_beatmapset)
                try:
                    await session.flush()
                except IntegrityError:
                    # Two first-time rank requests can both observe the parent
                    # as absent. The winner inserts it; the loser retries as an
                    # update instead of leaking a duplicate-key failure.
                    await session.rollback()
                    db_beatmapset = (
                        await session.exec(
                            select(Beatmapset)
                            .where(Beatmapset.id == beatmapset["id"])
                            .with_for_update()
                            .execution_options(populate_existing=True)
                        )
                    ).first()
                    if db_beatmapset is None:
                        raise
                    if _comparable_timestamp(db_beatmapset.last_updated) > _comparable_timestamp(
                        beatmapset["last_updated"]
                    ):
                        return False
                    retry_beatmapset = await Beatmapset.from_resp_no_save(
                        beatmapset  # pyright: ignore[reportArgumentType]
                    )
                    await session.merge(retry_beatmapset)

            for upstream in metadata_beatmaps:
                refreshed_beatmap = await Beatmap.from_resp_no_save(
                    session,
                    upstream,  # pyright: ignore[reportArgumentType]
                )
                await session.merge(refreshed_beatmap)

            from app.service.negative_pp_service import reconcile_attribution

            pp_changes = await reconcile_attribution(session, list(metadata_by_id))
            await session.commit()
            await invalidate_score_reconciliation_caches(pp_changes)
            await get_beatmapset_cache_service(get_redis()).invalidate_local_ranking_caches(
                beatmapset["id"],
                list(metadata_by_id),
            )
            return True

    async def _process_changed_beatmaps(
        self,
        changed: list[ChangedBeatmap],
        beatmaps_list: list[EnsuredBeatmap],
        *,
        beatmapset_id: int,
    ):
        beatmaps = {bm["id"]: bm for bm in beatmaps_list}

        async with with_db() as session:

            async def _invalidate_caches_after_commit(
                beatmapset_id: int,
                beatmap_id: int,
                reconciliation: ScoreReconciliationResult,
                *,
                content_changed: bool,
            ) -> None:
                await invalidate_score_reconciliation_caches(reconciliation)
                lookup_ids = sorted({*beatmaps, beatmap_id})
                cache_service = get_beatmapset_cache_service(get_redis())
                await cache_service.invalidate_local_ranking_caches(
                    beatmapset_id,
                    lookup_ids,
                )
                if content_changed:
                    await cache_service.invalidate_beatmap_content_cache(beatmap_id)

            async def _reconcile_effective_transition(
                beatmap_id: int,
                before: EffectiveBeatmapPolicy,
                after: EffectiveBeatmapPolicy,
            ) -> ScoreReconciliationResult:
                transitions = plan_score_policy_transitions(
                    {beatmap_id: before},
                    {beatmap_id: after},
                    enable_all_beatmap_leaderboard=settings.enable_all_beatmap_leaderboard,
                    enable_all_beatmap_pp=settings.enable_all_beatmap_pp,
                )
                return await reconcile_disabled_score_features(session, transitions)

            async def _retire_revision_derivatives(
                beatmap_id: int,
                before: EffectiveBeatmapPolicy,
            ) -> ScoreReconciliationResult:
                """Retire indexes tied to old chart data, regardless of new eligibility."""

                return await reconcile_disabled_score_features(
                    session,
                    [
                        ScorePolicyTransition(
                            beatmap_id=beatmap_id,
                            disable_leaderboard=True,
                            disable_pp=True,
                            disable_ranked_score=(
                                before.status.ranked()
                                and (before.leaderboard_enabled or settings.enable_all_beatmap_leaderboard)
                            )
                            or settings.enable_all_beatmap_pp,
                        )
                    ],
                )

            for change in changed:
                reconciliation = ScoreReconciliationResult()
                try:
                    locked_beatmapset = (
                        await session.exec(
                            select(Beatmapset)
                            .where(Beatmapset.id == beatmapset_id)
                            .with_for_update()
                            .execution_options(populate_existing=True)
                        )
                    ).first()
                    if locked_beatmapset is None:
                        raise LookupError(f"Beatmapset {beatmapset_id} does not exist")

                    if change.type == BeatmapChangeType.MAP_ADDED:
                        beatmap = beatmaps.get(change.beatmap_id)
                        if not beatmap:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} was classified as added but is absent from the snapshot"
                            )
                        if beatmap["beatmapset_id"] != beatmapset_id:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} belongs to unexpected set "
                                f"{beatmap['beatmapset_id']} (expected {beatmapset_id})"
                            )
                        logger.opt(colors=True).info(
                            f"<g>[{beatmap['beatmapset_id']}]</g> adding beatmap <blue>{beatmap['id']}</blue>"
                        )
                        existing_beatmap = await session.get(Beatmap, change.beatmap_id)
                        if existing_beatmap is not None and existing_beatmap.beatmapset_id != beatmapset_id:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} belongs to unexpected set "
                                f"{existing_beatmap.beatmapset_id} (expected {beatmapset_id})"
                            )
                        upstream_last_updated = beatmap.get("last_updated")
                        was_deleted = existing_beatmap is not None and existing_beatmap.deleted_at is not None
                        revision_changed = (
                            existing_beatmap is None or was_deleted or existing_beatmap.checksum != beatmap["checksum"]
                        )
                        old_checksum = existing_beatmap.checksum if existing_beatmap is not None else None
                        old_last_updated = existing_beatmap.last_updated if existing_beatmap is not None else None
                        effective_before = (
                            await get_effective_beatmap_policy(session, existing_beatmap)
                            if existing_beatmap is not None and not was_deleted and revision_changed
                            else None
                        )
                        new_db_beatmap = await Beatmap.from_resp_no_save(
                            session,
                            beatmap,  # pyright: ignore[reportArgumentType]
                        )
                        await session.merge(new_db_beatmap)
                        await session.flush()
                        if revision_changed:
                            # A genuinely new/reappeared difficulty must not
                            # silently inherit an older set-wide review. If the
                            # DB already has this exact revision, this is only a
                            # BeatmapSync-manifest retry and is a no-op.
                            await invalidate_for_upstream_change(
                                session,
                                beatmapset_id=beatmapset_id,
                                beatmap_id=change.beatmap_id,
                                old_checksum=old_checksum,
                                new_checksum=beatmap["checksum"],
                                old_last_updated=old_last_updated,
                                new_last_updated=upstream_last_updated,
                                commit=False,
                            )
                            if effective_before is not None:
                                reconciliation = await _retire_revision_derivatives(
                                    change.beatmap_id,
                                    effective_before,
                                )
                    elif change.type == BeatmapChangeType.MAP_DELETED:
                        existing_beatmap = await session.get(Beatmap, change.beatmap_id)
                        if existing_beatmap is None:
                            logger.opt(colors=True).warning(
                                f"<g>[beatmap: {change.beatmap_id}]</g> MAP_DELETED received "
                                "but beatmap is already absent from the database"
                            )
                            await session.commit()
                            continue
                        if existing_beatmap.beatmapset_id != beatmapset_id:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} belongs to unexpected set "
                                f"{existing_beatmap.beatmapset_id} (expected {beatmapset_id})"
                            )
                        if existing_beatmap.deleted_at is None:
                            effective_before = await get_effective_beatmap_policy(session, existing_beatmap)
                            old_checksum = existing_beatmap.checksum
                            old_last_updated = existing_beatmap.last_updated
                            await invalidate_for_upstream_change(
                                session,
                                beatmapset_id=beatmapset_id,
                                beatmap_id=change.beatmap_id,
                                old_checksum=old_checksum,
                                new_checksum=None,
                                old_last_updated=old_last_updated,
                                new_last_updated=None,
                                commit=False,
                            )
                            existing_beatmap.deleted_at = utcnow()
                            await session.flush()
                            reconciliation = await _retire_revision_derivatives(
                                change.beatmap_id,
                                effective_before,
                            )
                        else:
                            logger.opt(colors=True).debug(
                                f"<g>[beatmap: {change.beatmap_id}]</g> deletion was already applied"
                            )
                    else:
                        beatmap = beatmaps.get(change.beatmap_id)
                        if not beatmap:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} changed but is absent from the upstream snapshot"
                            )
                        if beatmap["beatmapset_id"] != beatmapset_id:
                            raise ValueError(
                                f"Beatmap {change.beatmap_id} belongs to unexpected set "
                                f"{beatmap['beatmapset_id']} (expected {beatmapset_id})"
                            )
                        logger.opt(colors=True).info(
                            f"<g>[{beatmap['beatmapset_id']}]</g> processing beatmap <blue>{beatmap['id']}</blue> "
                            f"change <cyan>{change.type}</cyan>"
                        )
                        new_db_beatmap = await Beatmap.from_resp_no_save(
                            session,
                            beatmap,  # pyright: ignore[reportArgumentType]
                        )
                        existing_beatmap = await session.get(Beatmap, change.beatmap_id)
                        if existing_beatmap is None:
                            logger.opt(colors=True).warning(
                                f"<g>[beatmap: {change.beatmap_id}]</g> "
                                "changed upstream but is missing locally; repairing it as an added difficulty"
                            )
                            await session.merge(new_db_beatmap)
                            await session.flush()
                            await invalidate_for_upstream_change(
                                session,
                                beatmapset_id=beatmapset_id,
                                beatmap_id=change.beatmap_id,
                                old_checksum=None,
                                new_checksum=beatmap["checksum"],
                                old_last_updated=None,
                                new_last_updated=beatmap.get("last_updated"),
                                commit=False,
                            )
                        else:
                            if existing_beatmap.beatmapset_id != beatmapset_id:
                                raise ValueError(
                                    f"Beatmap {change.beatmap_id} belongs to unexpected set "
                                    f"{existing_beatmap.beatmapset_id} (expected {beatmapset_id})"
                                )
                            effective_before = await get_effective_beatmap_policy(session, existing_beatmap)
                            upstream_last_updated = beatmap.get("last_updated")
                            revision_changed = change.type == BeatmapChangeType.MAP_UPDATED and (
                                existing_beatmap.deleted_at is not None
                                or existing_beatmap.checksum != beatmap["checksum"]
                            )
                            if revision_changed:
                                await invalidate_for_upstream_change(
                                    session,
                                    beatmapset_id=beatmapset_id,
                                    beatmap_id=change.beatmap_id,
                                    old_checksum=existing_beatmap.checksum,
                                    new_checksum=beatmap["checksum"],
                                    old_last_updated=existing_beatmap.last_updated,
                                    new_last_updated=upstream_last_updated,
                                    commit=False,
                                )
                            managed_beatmap = await session.merge(new_db_beatmap)
                            await session.flush()
                            if revision_changed:
                                reconciliation = await _retire_revision_derivatives(
                                    change.beatmap_id,
                                    effective_before,
                                )
                            elif change.type == BeatmapChangeType.STATUS_CHANGED:
                                effective_after = await get_effective_beatmap_policy(session, managed_beatmap)
                                reconciliation = await _reconcile_effective_transition(
                                    change.beatmap_id,
                                    effective_before,
                                    effective_after,
                                )

                    from app.service.negative_pp_service import reconcile_attribution

                    pp_changes = await reconcile_attribution(session, [change.beatmap_id])
                    await session.commit()
                    await invalidate_score_reconciliation_caches(pp_changes)
                except Exception:
                    await session.rollback()
                    raise

                if reconciliation.leaderboard_rows_removed or reconciliation.pp_rows_removed:
                    logger.opt(colors=True).info(
                        f"<g>[beatmap: {change.beatmap_id}]</g> retired "
                        f"{reconciliation.leaderboard_rows_removed} leaderboard and "
                        f"{reconciliation.pp_rows_removed} PP-best rows"
                    )
                await _invalidate_caches_after_commit(
                    beatmapset_id,
                    change.beatmap_id,
                    reconciliation,
                    content_changed=change.type
                    in {
                        BeatmapChangeType.MAP_ADDED,
                        BeatmapChangeType.MAP_UPDATED,
                        BeatmapChangeType.MAP_DELETED,
                    },
                )


service: BeatmapsetUpdateService | None = None


def init_beatmapset_update_service(fetcher: "Fetcher") -> BeatmapsetUpdateService:
    global service
    if service is None:
        service = BeatmapsetUpdateService(fetcher)
    if settings.enable_auto_beatmap_sync:
        bg_tasks.add_task(service.add_missing_beatmapsets)
    return service


def get_beatmapset_update_service() -> BeatmapsetUpdateService:
    assert service is not None, "BeatmapsetUpdateService is not initialized"
    return service
