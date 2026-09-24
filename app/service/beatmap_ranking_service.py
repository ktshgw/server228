"""Business rules for server-local beatmap ranking.

Official osu! metadata is the immutable fallback.  A local policy is valid only
for the exact beatmap revision reviewed by a staff member; when the upstream
revision changes the synchronizer invalidates the decision and creates an
operator event instead of silently carrying Ranked/PP to new chart data.
"""

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import StrEnum
from typing import TYPE_CHECKING, Any

from app.config import settings
from app.database.beatmap import Beatmap
from app.database.beatmap_ranking import (
    BeatmapRankingAudit,
    BeatmapRankingEvent,
    BeatmapRankingPolicy,
    BeatmapsetRankingPolicy,
    RankingEventType,
    RankingPolicyAction,
    RankingPolicyScope,
)
from app.database.beatmap_sync import BeatmapSync, SavedBeatmapMeta
from app.database.beatmapset import Beatmapset
from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus

from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

if TYPE_CHECKING:
    from app.service.beatmap_ranking_reconciliation_service import ScoreReconciliationResult


LOCAL_RANK_MAX_SYNC_INTERVAL = timedelta(hours=6)


class EffectivePolicySource(StrEnum):
    """Where an effective client-facing status came from."""

    UPSTREAM = "upstream"
    BEATMAPSET = "beatmapset"
    BEATMAP = "beatmap"


@dataclass(frozen=True, slots=True)
class EffectiveBeatmapPolicy:
    """Resolved status and score treatment for one difficulty."""

    status: BeatmapRankStatus
    leaderboard_enabled: bool
    pp_enabled: bool
    source: EffectivePolicySource


@dataclass(frozen=True, slots=True)
class EffectiveBeatmapsetPolicy:
    """Resolved display/search policy for a beatmapset."""

    status: BeatmapRankStatus
    leaderboard_enabled: bool
    pp_enabled: bool
    source: EffectivePolicySource
    locally_ranked_difficulty_count: int


def _normalise_reason(reason: str) -> str:
    return reason.strip() or "no reason"


def _resolve_flags(
    status: BeatmapRankStatus,
    leaderboard_enabled: bool | None,
    pp_enabled: bool | None,
) -> tuple[bool, bool]:
    leaderboard = status.has_leaderboard() if leaderboard_enabled is None else leaderboard_enabled
    pp = status.has_pp() if pp_enabled is None else pp_enabled
    if pp and not leaderboard:
        raise ValueError("PP cannot be enabled when the beatmap leaderboard is disabled")
    if leaderboard != status.has_leaderboard() or pp != status.has_pp():
        raise ValueError(
            "Local score flags must match lazer semantics: Ranked enables leaderboard and PP; "
            "Loved enables leaderboard without PP"
        )
    return leaderboard, pp


def _status_policy(status: BeatmapRankStatus, source: EffectivePolicySource) -> EffectiveBeatmapPolicy:
    return EffectiveBeatmapPolicy(
        status=status,
        leaderboard_enabled=status.has_leaderboard(),
        pp_enabled=status.has_pp(),
        source=source,
    )


def _is_effective_local_rank(policy: EffectiveBeatmapPolicy) -> bool:
    """Return whether a visible leaderboard comes from a local ranking policy."""

    return policy.source != EffectivePolicySource.UPSTREAM and policy.leaderboard_enabled


def _policy_snapshot(policy: BeatmapsetRankingPolicy | BeatmapRankingPolicy | None) -> dict[str, Any] | None:
    if policy is None:
        return None
    snapshot: dict[str, Any] = {
        "status": int(policy.status),
        "leaderboard_enabled": policy.leaderboard_enabled,
        "pp_enabled": policy.pp_enabled,
        "force_unranked": policy.force_unranked,
        "is_active": policy.is_active,
        "reason": policy.reason,
    }
    if isinstance(policy, BeatmapsetRankingPolicy):
        snapshot["beatmapset_id"] = policy.beatmapset_id
        snapshot["revision_manifest"] = dict(policy.revision_manifest)
    else:
        snapshot["beatmapset_id"] = policy.beatmapset_id
        snapshot["beatmap_id"] = policy.beatmap_id
        snapshot["blocks_set_policy"] = policy.blocks_set_policy
        snapshot["ranked_checksum"] = policy.ranked_checksum
        snapshot["invalidated_at"] = policy.invalidated_at.isoformat() if policy.invalidated_at else None
        snapshot["invalidation_reason"] = policy.invalidation_reason
    return snapshot


def _difficulty_policy_applies(policy: BeatmapRankingPolicy, current_checksum: str) -> bool:
    """Return whether an active difficulty row controls the current map.

    A tombstone is a durable instruction to follow upstream and block a set
    default, so it intentionally survives revision changes. A real local
    override remains pinned to the exact reviewed checksum.
    """

    return policy.is_active and (
        policy.force_unranked or policy.blocks_set_policy or policy.ranked_checksum == current_checksum
    )


def _set_policy_applies_to_difficulty(
    policy: BeatmapsetRankingPolicy,
    *,
    beatmap_id: int,
    current_checksum: str,
    has_pending_revision_event: bool,
) -> bool:
    """Return whether a set policy controls one current difficulty."""

    if not policy.is_active:
        return False
    if policy.force_unranked:
        # Restrictive decisions deliberately survive revisions and also cover
        # newly-added difficulties. They cannot grant ranking to new content.
        return True
    return policy.revision_manifest.get(str(beatmap_id)) == current_checksum and not has_pending_revision_event


async def beatmapset_has_active_local_rank(
    session: AsyncSession,
    beatmapset_id: int,
    *,
    for_update: bool = False,
) -> bool:
    """Return whether a set has an active, non-tombstone local rank."""

    set_statement = select(BeatmapsetRankingPolicy).where(BeatmapsetRankingPolicy.beatmapset_id == beatmapset_id)
    difficulty_statement = (
        select(BeatmapRankingPolicy.beatmap_id)
        .where(
            BeatmapRankingPolicy.beatmapset_id == beatmapset_id,
            col(BeatmapRankingPolicy.is_active).is_(True),
            col(BeatmapRankingPolicy.blocks_set_policy).is_(False),
        )
        .limit(1)
    )
    if for_update:
        set_statement = set_statement.with_for_update().execution_options(populate_existing=True)
        difficulty_statement = difficulty_statement.with_for_update().execution_options(populate_existing=True)

    set_policy = (await session.exec(set_statement)).first()
    if set_policy is not None and set_policy.is_active:
        return True
    difficulty_id = (await session.exec(difficulty_statement)).first()
    return difficulty_id is not None


async def beatmapset_has_pending_local_rank_review(
    session: AsyncSession,
    beatmapset_id: int,
    *,
    for_update: bool = False,
) -> bool:
    """Return whether upstream invalidation left an unresolved admin event."""

    statement = (
        select(BeatmapRankingEvent.id)
        .where(
            BeatmapRankingEvent.beatmapset_id == beatmapset_id,
            col(BeatmapRankingEvent.resolved_at).is_(None),
        )
        .limit(1)
    )
    if for_update:
        statement = statement.with_for_update().execution_options(populate_existing=True)
    return (await session.exec(statement)).first() is not None


async def _schedule_local_rank_sync(session: AsyncSession, beatmapset_id: int, now: datetime) -> None:
    """Create or bring an upstream sync record inside the local-rank SLA."""

    sync_record = await session.get(BeatmapSync, beatmapset_id)
    deadline = now + LOCAL_RANK_MAX_SYNC_INTERVAL
    if sync_record is None:
        beatmapset = await session.get(Beatmapset, beatmapset_id)
        if beatmapset is None:
            return
        beatmaps = list((await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset_id))).all())
        session.add(
            BeatmapSync(
                beatmapset_id=beatmapset_id,
                beatmaps=[
                    SavedBeatmapMeta(
                        beatmap_id=beatmap.id,
                        md5=beatmap.checksum,
                        is_deleted=beatmap.deleted_at is not None,
                        beatmap_status=BeatmapRankStatus(beatmap.beatmap_status),
                    )
                    for beatmap in beatmaps
                ],
                beatmap_status=BeatmapRankStatus(beatmapset.beatmap_status),
                next_sync_time=deadline,
            )
        )
    else:
        stored_deadline = deadline
        if sync_record.next_sync_time.tzinfo is None:
            # BeatmapSync uses a legacy timezone-naive DateTime column on some
            # deployments, while utcnow() is timezone-aware.
            stored_deadline = deadline.replace(tzinfo=None)
        if sync_record.next_sync_time > stored_deadline:
            sync_record.next_sync_time = stored_deadline


async def _lock_beatmapset(session: AsyncSession, beatmapset_id: int) -> Beatmapset | None:
    """Acquire the common per-set lock used by ranking mutations."""

    return (
        await session.exec(
            select(Beatmapset)
            .where(Beatmapset.id == beatmapset_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()


async def get_effective_beatmap_policies(
    session: AsyncSession,
    beatmaps: Sequence[Beatmap],
    *,
    for_update: bool = False,
) -> dict[int, EffectiveBeatmapPolicy]:
    """Resolve local policies for many difficulties with a bounded query count.

    Precedence is difficulty override, then beatmapset default, then official
    upstream status.  A local policy is ignored unless its pinned checksum is
    equal to the current upstream checksum.
    """

    if not beatmaps:
        return {}

    beatmap_ids = [beatmap.id for beatmap in beatmaps]
    beatmapset_ids = list({beatmap.beatmapset_id for beatmap in beatmaps})

    difficulty_statement = select(BeatmapRankingPolicy).where(col(BeatmapRankingPolicy.beatmap_id).in_(beatmap_ids))
    set_statement = select(BeatmapsetRankingPolicy).where(
        col(BeatmapsetRankingPolicy.beatmapset_id).in_(beatmapset_ids)
    )
    event_statement = select(BeatmapRankingEvent.beatmap_id, BeatmapRankingEvent.beatmapset_id).where(
        BeatmapRankingEvent.scope == RankingPolicyScope.BEATMAPSET,
        col(BeatmapRankingEvent.resolved_at).is_(None),
        col(BeatmapRankingEvent.beatmap_id).in_(beatmap_ids),
    )
    if for_update:
        difficulty_statement = difficulty_statement.with_for_update().execution_options(populate_existing=True)
        set_statement = set_statement.with_for_update().execution_options(populate_existing=True)
        event_statement = event_statement.with_for_update().execution_options(populate_existing=True)

    difficulty_policies = (await session.exec(difficulty_statement)).all()
    set_policies = (await session.exec(set_statement)).all()
    unresolved_set_events = (await session.exec(event_statement)).all()

    difficulty_by_id = {policy.beatmap_id: policy for policy in difficulty_policies}
    set_by_id = {policy.beatmapset_id: policy for policy in set_policies}
    invalidated_set_difficulties = {(beatmap_id, beatmapset_id) for beatmap_id, beatmapset_id in unresolved_set_events}

    resolved: dict[int, EffectiveBeatmapPolicy] = {}
    for beatmap in beatmaps:
        if beatmap.deleted_at is not None:
            resolved[beatmap.id] = _status_policy(
                BeatmapRankStatus.GRAVEYARD,
                EffectivePolicySource.UPSTREAM,
            )
            continue

        difficulty_policy = difficulty_by_id.get(beatmap.id)
        if difficulty_policy is not None and _difficulty_policy_applies(difficulty_policy, beatmap.checksum):
            if difficulty_policy.force_unranked:
                resolved[beatmap.id] = EffectiveBeatmapPolicy(
                    status=BeatmapRankStatus(difficulty_policy.status),
                    leaderboard_enabled=difficulty_policy.leaderboard_enabled,
                    pp_enabled=difficulty_policy.pp_enabled,
                    source=EffectivePolicySource.BEATMAP,
                )
                continue
            if difficulty_policy.blocks_set_policy:
                resolved[beatmap.id] = _status_policy(
                    BeatmapRankStatus(beatmap.beatmap_status),
                    EffectivePolicySource.UPSTREAM,
                )
                continue
            resolved[beatmap.id] = EffectiveBeatmapPolicy(
                status=BeatmapRankStatus(difficulty_policy.status),
                leaderboard_enabled=difficulty_policy.leaderboard_enabled,
                pp_enabled=difficulty_policy.pp_enabled,
                source=EffectivePolicySource.BEATMAP,
            )
            continue

        set_policy = set_by_id.get(beatmap.beatmapset_id)
        if set_policy is not None and _set_policy_applies_to_difficulty(
            set_policy,
            beatmap_id=beatmap.id,
            current_checksum=beatmap.checksum,
            has_pending_revision_event=(beatmap.id, beatmap.beatmapset_id) in invalidated_set_difficulties,
        ):
            resolved[beatmap.id] = EffectiveBeatmapPolicy(
                status=BeatmapRankStatus(set_policy.status),
                leaderboard_enabled=set_policy.leaderboard_enabled,
                pp_enabled=set_policy.pp_enabled,
                source=EffectivePolicySource.BEATMAPSET,
            )
            continue

        resolved[beatmap.id] = _status_policy(
            BeatmapRankStatus(beatmap.beatmap_status),
            EffectivePolicySource.UPSTREAM,
        )
    return resolved


async def get_effective_beatmap_policy(
    session: AsyncSession,
    beatmap: Beatmap,
    *,
    for_update: bool = False,
) -> EffectiveBeatmapPolicy:
    """Resolve the effective server policy for one difficulty."""

    return (await get_effective_beatmap_policies(session, [beatmap], for_update=for_update))[beatmap.id]


_BEATMAPSET_STATUS_PRIORITY: dict[BeatmapRankStatus, int] = {
    BeatmapRankStatus.RANKED: 7,
    BeatmapRankStatus.APPROVED: 6,
    BeatmapRankStatus.LOVED: 5,
    BeatmapRankStatus.QUALIFIED: 4,
    BeatmapRankStatus.PENDING: 3,
    BeatmapRankStatus.WIP: 2,
    BeatmapRankStatus.GRAVEYARD: 1,
}


def _aggregate_effective_difficulties(
    policies: Sequence[EffectiveBeatmapPolicy],
) -> tuple[BeatmapRankStatus, bool, bool]:
    """Aggregate a mixed difficulty set without hiding unaffected maps."""

    if not policies:
        raise ValueError("At least one effective difficulty policy is required")
    strongest = max(policies, key=lambda policy: _BEATMAPSET_STATUS_PRIORITY[policy.status])
    return (
        strongest.status,
        any(policy.leaderboard_enabled for policy in policies),
        any(policy.pp_enabled for policy in policies),
    )


async def get_effective_beatmapset_policy(
    session: AsyncSession,
    beatmapset: Beatmapset,
) -> EffectiveBeatmapsetPolicy:
    """Resolve a deterministic display/search policy for a beatmapset.

    An active set policy is authoritative for the set-level badge only while at
    least one current difficulty validly inherits it. Otherwise valid explicit
    difficulty policies cause every current difficulty (including inherited
    official ones) to be aggregated using the fixed priority Ranked > Approved
    > Loved > Qualified > Pending > WIP > Graveyard. Score treatment remains
    per difficulty.
    """

    set_policy = await session.get(BeatmapsetRankingPolicy, beatmapset.id)
    beatmaps = (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset.id))).all()
    effective_difficulties = await get_effective_beatmap_policies(session, beatmaps)
    set_policy_difficulties = [
        policy for policy in effective_difficulties.values() if policy.source == EffectivePolicySource.BEATMAPSET
    ]
    difficulty_overrides = [
        policy for policy in effective_difficulties.values() if policy.source == EffectivePolicySource.BEATMAP
    ]
    local_difficulty_count = len(set_policy_difficulties) + len(difficulty_overrides)

    if set_policy is not None and set_policy.is_active and set_policy_difficulties:
        return EffectiveBeatmapsetPolicy(
            status=BeatmapRankStatus(set_policy.status),
            leaderboard_enabled=set_policy.leaderboard_enabled,
            pp_enabled=set_policy.pp_enabled,
            source=EffectivePolicySource.BEATMAPSET,
            locally_ranked_difficulty_count=local_difficulty_count,
        )

    if difficulty_overrides:
        status, leaderboard_enabled, pp_enabled = _aggregate_effective_difficulties(
            tuple(effective_difficulties.values())
        )
        return EffectiveBeatmapsetPolicy(
            status=status,
            leaderboard_enabled=leaderboard_enabled,
            pp_enabled=pp_enabled,
            source=EffectivePolicySource.BEATMAP,
            locally_ranked_difficulty_count=len(difficulty_overrides),
        )

    upstream_status = BeatmapRankStatus(beatmapset.beatmap_status)
    return EffectiveBeatmapsetPolicy(
        status=upstream_status,
        leaderboard_enabled=upstream_status.has_leaderboard(),
        pp_enabled=upstream_status.has_pp(),
        source=EffectivePolicySource.UPSTREAM,
        locally_ranked_difficulty_count=0,
    )


async def _resolve_events(
    session: AsyncSession,
    *,
    scope: RankingPolicyScope | None = None,
    actor_user_id: int,
    beatmapset_id: int,
    beatmap_id: int | None = None,
) -> None:
    statement = select(BeatmapRankingEvent).where(
        BeatmapRankingEvent.beatmapset_id == beatmapset_id,
        col(BeatmapRankingEvent.resolved_at).is_(None),
    )
    if scope is not None:
        statement = statement.where(BeatmapRankingEvent.scope == scope)
    if beatmap_id is not None:
        statement = statement.where(BeatmapRankingEvent.beatmap_id == beatmap_id)
    statement = statement.with_for_update()
    now = utcnow()
    for event in (await session.exec(statement)).all():
        event.resolved_at = now
        event.resolved_by_user_id = actor_user_id


async def _finish_manual_policy_change(
    session: AsyncSession,
    *,
    affected_beatmap_ids: list[int],
    before: dict[int, EffectiveBeatmapPolicy],
) -> "ScoreReconciliationResult":
    """Stage score reconciliation against the flushed policy mutation."""

    # Keep service imports local so app.database can initialise its models
    # without importing the full service package in a cycle.
    from app.service.beatmap_ranking_reconciliation_service import (
        plan_score_policy_transitions,
        reconcile_disabled_score_features,
    )

    affected_beatmaps = list(
        (await session.exec(select(Beatmap).where(col(Beatmap.id).in_(affected_beatmap_ids)))).all()
    )
    after = await get_effective_beatmap_policies(session, affected_beatmaps)
    transitions = plan_score_policy_transitions(
        before,
        after,
        enable_all_beatmap_leaderboard=settings.enable_all_beatmap_leaderboard,
        enable_all_beatmap_pp=settings.enable_all_beatmap_pp,
    )
    return await reconcile_disabled_score_features(session, transitions)


async def _invalidate_manual_policy_caches(
    beatmapset_id: int,
    cached_beatmap_ids: list[int],
    reconciliation: "ScoreReconciliationResult",
) -> None:
    """Invalidate disposable caches only after the DB transaction commits."""

    from app.dependencies.database import get_redis
    from app.service.beatmap_ranking_reconciliation_service import invalidate_score_reconciliation_caches
    from app.service.beatmapset_cache_service import get_beatmapset_cache_service

    await get_beatmapset_cache_service(get_redis()).invalidate_local_ranking_caches(
        beatmapset_id,
        cached_beatmap_ids,
    )
    await invalidate_score_reconciliation_caches(reconciliation)


async def apply_local_rank(
    session: AsyncSession,
    *,
    actor_user_id: int,
    beatmapset_id: int,
    status: BeatmapRankStatus,
    reason: str,
    beatmap_id: int | None = None,
    leaderboard_enabled: bool | None = None,
    pp_enabled: bool | None = None,
    force_unranked: bool = False,
    replace_difficulty_overrides: bool = False,
) -> BeatmapsetRankingPolicy | BeatmapRankingPolicy:
    """Apply or replace a set default or a per-difficulty local policy.

    ``replace_difficulty_overrides`` makes a set action literal: existing
    per-difficulty exceptions are deactivated so the new status reaches every
    difficulty. It is used by the native client's "all difficulties" action.
    """

    reason = _normalise_reason(reason)
    status = BeatmapRankStatus(status)
    leaderboard, pp = _resolve_flags(status, leaderboard_enabled, pp_enabled)
    if force_unranked and (leaderboard or pp):
        raise ValueError("A forced local unrank must disable leaderboard and PP")
    now = utcnow()
    beatmapset = await _lock_beatmapset(session, beatmapset_id)
    if beatmapset is None:
        raise LookupError(f"Beatmapset {beatmapset_id} does not exist")

    if beatmap_id is None:
        beatmaps = (
            await session.exec(
                select(Beatmap).where(
                    Beatmap.beatmapset_id == beatmapset_id,
                    col(Beatmap.deleted_at).is_(None),
                )
            )
        ).all()
        if not beatmaps:
            raise ValueError(f"Beatmapset {beatmapset_id} has no cached difficulties to rank")

        affected_beatmaps = list(beatmaps)
        affected_beatmap_ids = [beatmap.id for beatmap in affected_beatmaps]
        cached_beatmap_ids = [beatmap.id for beatmap in beatmaps]
        effective_before = await get_effective_beatmap_policies(session, affected_beatmaps)

        if replace_difficulty_overrides:
            difficulty_policies = (
                await session.exec(
                    select(BeatmapRankingPolicy)
                    .where(
                        BeatmapRankingPolicy.beatmapset_id == beatmapset_id,
                        col(BeatmapRankingPolicy.is_active).is_(True),
                    )
                    .with_for_update()
                    .execution_options(populate_existing=True)
                )
            ).all()
            for difficulty_policy in difficulty_policies:
                difficulty_before = _policy_snapshot(difficulty_policy)
                difficulty_policy.is_active = False
                difficulty_policy.reason = reason
                difficulty_policy.updated_by_user_id = actor_user_id
                difficulty_policy.updated_at = now
                session.add(
                    BeatmapRankingAudit(
                        action=RankingPolicyAction.CLEAR,
                        scope=RankingPolicyScope.BEATMAP,
                        beatmapset_id=beatmapset_id,
                        beatmap_id=difficulty_policy.beatmap_id,
                        actor_user_id=actor_user_id,
                        reason=reason,
                        before=difficulty_before,
                        after=_policy_snapshot(difficulty_policy),
                        created_at=now,
                    )
                )

        policy = await session.get(BeatmapsetRankingPolicy, beatmapset_id)
        before = _policy_snapshot(policy)
        revision_manifest = {str(beatmap.id): beatmap.checksum for beatmap in beatmaps}
        if policy is None:
            policy = BeatmapsetRankingPolicy(
                beatmapset_id=beatmapset_id,
                status=status,
                leaderboard_enabled=leaderboard,
                pp_enabled=pp,
                force_unranked=force_unranked,
                revision_manifest=revision_manifest,
                is_active=True,
                reason=reason,
                created_by_user_id=actor_user_id,
                updated_by_user_id=actor_user_id,
                created_at=now,
                updated_at=now,
            )
            session.add(policy)
        else:
            policy.status = status
            policy.leaderboard_enabled = leaderboard
            policy.pp_enabled = pp
            policy.force_unranked = force_unranked
            policy.revision_manifest = revision_manifest
            policy.is_active = True
            policy.reason = reason
            policy.updated_by_user_id = actor_user_id
            policy.updated_at = now

        await _resolve_events(
            session,
            actor_user_id=actor_user_id,
            beatmapset_id=beatmapset_id,
        )
        scope = RankingPolicyScope.BEATMAPSET
    else:
        beatmap = await session.get(Beatmap, beatmap_id)
        if beatmap is None:
            raise LookupError(f"Beatmap {beatmap_id} does not exist")
        if beatmap.beatmapset_id != beatmapset_id:
            raise ValueError(f"Beatmap {beatmap_id} does not belong to beatmapset {beatmapset_id}")
        if beatmap.deleted_at is not None:
            raise ValueError(f"Beatmap {beatmap_id} is deleted and cannot be ranked")

        affected_beatmaps = [beatmap]
        affected_beatmap_ids = [beatmap.id]
        cached_beatmaps = (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset_id))).all()
        cached_beatmap_ids = [cached_beatmap.id for cached_beatmap in cached_beatmaps]
        effective_before = await get_effective_beatmap_policies(session, affected_beatmaps)

        policy = await session.get(BeatmapRankingPolicy, beatmap_id)
        before = _policy_snapshot(policy)
        if policy is None:
            policy = BeatmapRankingPolicy(
                beatmap_id=beatmap_id,
                beatmapset_id=beatmapset_id,
                status=status,
                leaderboard_enabled=leaderboard,
                pp_enabled=pp,
                force_unranked=force_unranked,
                blocks_set_policy=False,
                ranked_checksum=beatmap.checksum,
                is_active=True,
                reason=reason,
                created_by_user_id=actor_user_id,
                updated_by_user_id=actor_user_id,
                created_at=now,
                updated_at=now,
            )
            session.add(policy)
        else:
            policy.status = status
            policy.leaderboard_enabled = leaderboard
            policy.pp_enabled = pp
            policy.force_unranked = force_unranked
            policy.blocks_set_policy = False
            policy.ranked_checksum = beatmap.checksum
            policy.is_active = True
            policy.reason = reason
            policy.invalidated_at = None
            policy.invalidation_reason = None
            policy.updated_by_user_id = actor_user_id
            policy.updated_at = now

        await _resolve_events(
            session,
            actor_user_id=actor_user_id,
            beatmapset_id=beatmapset_id,
            beatmap_id=beatmap_id,
        )
        scope = RankingPolicyScope.BEATMAP

    session.add(
        BeatmapRankingAudit(
            action=RankingPolicyAction.APPLY,
            scope=scope,
            beatmapset_id=beatmapset_id,
            beatmap_id=beatmap_id,
            actor_user_id=actor_user_id,
            reason=reason,
            before=before,
            after=_policy_snapshot(policy),
            created_at=now,
        )
    )
    try:
        await _schedule_local_rank_sync(session, beatmapset_id, now)
        await session.flush()
        reconciliation = await _finish_manual_policy_change(
            session,
            affected_beatmap_ids=affected_beatmap_ids,
            before=effective_before,
        )
        await session.refresh(policy)
        from app.features.somsai.services.soms_activity_service import stage_local_map_release

        label = f"{beatmapset.artist} — {beatmapset.title}"
        if beatmap_id is not None:
            label += f" [{affected_beatmaps[0].version}]"
        stage_local_map_release(
            session,
            status=status,
            previous_statuses=[previous.status for previous in effective_before.values()],
            beatmapset_id=beatmapset_id,
            beatmap_id=beatmap_id,
            label=label,
            actor_id=actor_user_id,
            changed_at=now,
        )
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    await _invalidate_manual_policy_caches(beatmapset_id, cached_beatmap_ids, reconciliation)
    return policy


async def apply_local_unrank(
    session: AsyncSession,
    *,
    actor_user_id: int,
    beatmapset_id: int,
    reason: str,
    beatmap_id: int | None = None,
    replace_difficulty_overrides: bool = False,
) -> BeatmapsetRankingPolicy | BeatmapRankingPolicy:
    """Derank a target without losing its immutable Bancho category.

    Removing an active SOMS Ranked/Loved decision restores the target to its
    upstream status. A target whose visible leaderboard already comes from
    Bancho instead receives a restrictive local Graveyard policy. This mirrors
    the two distinct operator intents while avoiding the old synthetic Pending
    status.
    """

    reason = _normalise_reason(reason)
    now = utcnow()
    if await _lock_beatmapset(session, beatmapset_id) is None:
        raise LookupError(f"Beatmapset {beatmapset_id} does not exist")

    if beatmap_id is not None:
        beatmap = await session.get(Beatmap, beatmap_id)
        if beatmap is None:
            raise LookupError(f"Beatmap {beatmap_id} does not exist")
        if beatmap.beatmapset_id != beatmapset_id:
            raise ValueError(f"Beatmap {beatmap_id} does not belong to beatmapset {beatmapset_id}")
        if beatmap.deleted_at is not None:
            raise ValueError(f"Beatmap {beatmap_id} is deleted and cannot be unranked")

        effective_before = await get_effective_beatmap_policies(session, [beatmap])
        if not _is_effective_local_rank(effective_before[beatmap.id]):
            return await apply_local_rank(
                session,
                actor_user_id=actor_user_id,
                beatmapset_id=beatmapset_id,
                beatmap_id=beatmap_id,
                status=BeatmapRankStatus.GRAVEYARD,
                leaderboard_enabled=False,
                pp_enabled=False,
                force_unranked=True,
                reason=reason,
            )

        cached_beatmaps = (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset_id))).all()
        cached_beatmap_ids = [cached_beatmap.id for cached_beatmap in cached_beatmaps]
        policy = await session.get(BeatmapRankingPolicy, beatmap_id)
        before = _policy_snapshot(policy)
        upstream_status = BeatmapRankStatus(beatmap.beatmap_status)
        if policy is None:
            policy = BeatmapRankingPolicy(
                beatmap_id=beatmap_id,
                beatmapset_id=beatmapset_id,
                status=upstream_status,
                leaderboard_enabled=upstream_status.has_leaderboard(),
                pp_enabled=upstream_status.has_pp(),
                force_unranked=False,
                blocks_set_policy=True,
                ranked_checksum=beatmap.checksum,
                is_active=True,
                reason=reason,
                created_by_user_id=actor_user_id,
                updated_by_user_id=actor_user_id,
                created_at=now,
                updated_at=now,
            )
            session.add(policy)
        else:
            policy.status = upstream_status
            policy.leaderboard_enabled = upstream_status.has_leaderboard()
            policy.pp_enabled = upstream_status.has_pp()
            policy.force_unranked = False
            policy.blocks_set_policy = True
            policy.ranked_checksum = beatmap.checksum
            policy.is_active = True
            policy.reason = reason
            policy.invalidated_at = None
            policy.invalidation_reason = None
            policy.updated_by_user_id = actor_user_id
            policy.updated_at = now

        await _resolve_events(
            session,
            actor_user_id=actor_user_id,
            beatmapset_id=beatmapset_id,
            beatmap_id=beatmap_id,
        )
        session.add(
            BeatmapRankingAudit(
                action=RankingPolicyAction.CLEAR,
                scope=RankingPolicyScope.BEATMAP,
                beatmapset_id=beatmapset_id,
                beatmap_id=beatmap_id,
                actor_user_id=actor_user_id,
                reason=reason,
                before=before,
                after=_policy_snapshot(policy),
                created_at=now,
            )
        )
        try:
            await session.flush()
            reconciliation = await _finish_manual_policy_change(
                session,
                affected_beatmap_ids=[beatmap.id],
                before=effective_before,
            )
            await session.refresh(policy)
            await session.commit()
        except Exception:
            await session.rollback()
            raise
        await _invalidate_manual_policy_caches(beatmapset_id, cached_beatmap_ids, reconciliation)
        return policy

    beatmaps = list(
        (
            await session.exec(
                select(Beatmap).where(
                    Beatmap.beatmapset_id == beatmapset_id,
                    col(Beatmap.deleted_at).is_(None),
                )
            )
        ).all()
    )
    if not beatmaps:
        raise ValueError(f"Beatmapset {beatmapset_id} has no cached difficulties to unrank")
    effective_before = await get_effective_beatmap_policies(session, beatmaps)
    locally_ranked_ids = {
        current_beatmap_id
        for current_beatmap_id, policy in effective_before.items()
        if _is_effective_local_rank(policy)
    }
    if not locally_ranked_ids:
        return await apply_local_rank(
            session,
            actor_user_id=actor_user_id,
            beatmapset_id=beatmapset_id,
            status=BeatmapRankStatus.GRAVEYARD,
            leaderboard_enabled=False,
            pp_enabled=False,
            force_unranked=True,
            replace_difficulty_overrides=replace_difficulty_overrides,
            reason=reason,
        )

    active_set_policy = await session.get(BeatmapsetRankingPolicy, beatmapset_id)
    difficulty_policies = list(
        (
            await session.exec(
                select(BeatmapRankingPolicy)
                .where(
                    BeatmapRankingPolicy.beatmapset_id == beatmapset_id,
                    col(BeatmapRankingPolicy.is_active).is_(True),
                )
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).all()
    )
    policies_to_clear: list[BeatmapsetRankingPolicy | BeatmapRankingPolicy] = []
    if active_set_policy is not None and active_set_policy.is_active and not active_set_policy.force_unranked:
        policies_to_clear.append(active_set_policy)
    policies_to_clear.extend(
        policy
        for policy in difficulty_policies
        if replace_difficulty_overrides or policy.beatmap_id in locally_ranked_ids
    )
    if not policies_to_clear:
        raise ValueError(f"Beatmapset {beatmapset_id} has no active local rank to remove")

    for policy in policies_to_clear:
        before = _policy_snapshot(policy)
        policy.is_active = False
        policy.reason = reason
        policy.updated_by_user_id = actor_user_id
        policy.updated_at = now
        policy_beatmap_id = policy.beatmap_id if isinstance(policy, BeatmapRankingPolicy) else None
        session.add(
            BeatmapRankingAudit(
                action=RankingPolicyAction.CLEAR,
                scope=(
                    RankingPolicyScope.BEATMAP
                    if isinstance(policy, BeatmapRankingPolicy)
                    else RankingPolicyScope.BEATMAPSET
                ),
                beatmapset_id=beatmapset_id,
                beatmap_id=policy_beatmap_id,
                actor_user_id=actor_user_id,
                reason=reason,
                before=before,
                after=_policy_snapshot(policy),
                created_at=now,
            )
        )

    await _resolve_events(
        session,
        actor_user_id=actor_user_id,
        beatmapset_id=beatmapset_id,
    )
    try:
        await session.flush()
        reconciliation = await _finish_manual_policy_change(
            session,
            affected_beatmap_ids=[beatmap.id for beatmap in beatmaps],
            before=effective_before,
        )
        result_policy = policies_to_clear[0]
        await session.refresh(result_policy)
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    await _invalidate_manual_policy_caches(
        beatmapset_id,
        [beatmap.id for beatmap in beatmaps],
        reconciliation,
    )
    return result_policy


async def clear_local_rank(
    session: AsyncSession,
    *,
    actor_user_id: int,
    beatmapset_id: int,
    reason: str,
    beatmap_id: int | None = None,
) -> BeatmapsetRankingPolicy | BeatmapRankingPolicy:
    """Deactivate the target policy so it inherits its parent or upstream."""

    reason = _normalise_reason(reason)
    now = utcnow()
    if await _lock_beatmapset(session, beatmapset_id) is None:
        raise LookupError(f"Beatmapset {beatmapset_id} does not exist")
    if beatmap_id is None:
        policy = await session.get(BeatmapsetRankingPolicy, beatmapset_id)
        scope = RankingPolicyScope.BEATMAPSET
        if policy is None:
            raise LookupError(f"No local ranking policy exists for beatmapset {beatmapset_id}")
        affected_beatmaps = list(
            (
                await session.exec(
                    select(Beatmap).where(
                        Beatmap.beatmapset_id == beatmapset_id,
                        col(Beatmap.deleted_at).is_(None),
                    )
                )
            ).all()
        )
        affected_beatmap_ids = [beatmap.id for beatmap in affected_beatmaps]
        cached_beatmap_ids = [beatmap.id for beatmap in affected_beatmaps]
        effective_before = await get_effective_beatmap_policies(session, affected_beatmaps)
        before = _policy_snapshot(policy)
        policy.is_active = False
        policy.reason = reason
        policy.updated_by_user_id = actor_user_id
        policy.updated_at = now
    else:
        beatmap = await session.get(Beatmap, beatmap_id)
        if beatmap is None:
            raise LookupError(f"Beatmap {beatmap_id} does not exist")
        if beatmap.beatmapset_id != beatmapset_id:
            raise ValueError(f"Beatmap {beatmap_id} does not belong to beatmapset {beatmapset_id}")
        if beatmap.deleted_at is not None:
            raise ValueError(f"Beatmap {beatmap_id} is deleted and cannot be unranked")
        affected_beatmaps = [beatmap]
        affected_beatmap_ids = [beatmap.id]
        cached_beatmaps = (await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset_id))).all()
        cached_beatmap_ids = [cached_beatmap.id for cached_beatmap in cached_beatmaps]
        effective_before = await get_effective_beatmap_policies(session, affected_beatmaps)
        policy = await session.get(BeatmapRankingPolicy, beatmap_id)
        scope = RankingPolicyScope.BEATMAP
        before = _policy_snapshot(policy)
        if policy is None:
            raise LookupError(f"No local ranking policy exists for beatmap {beatmap_id}")
        policy.is_active = False
        policy.reason = reason
        policy.updated_by_user_id = actor_user_id
        policy.updated_at = now
    await _resolve_events(
        session,
        actor_user_id=actor_user_id,
        beatmapset_id=beatmapset_id,
        beatmap_id=beatmap_id,
    )
    session.add(
        BeatmapRankingAudit(
            action=RankingPolicyAction.CLEAR,
            scope=scope,
            beatmapset_id=beatmapset_id,
            beatmap_id=beatmap_id,
            actor_user_id=actor_user_id,
            reason=reason,
            before=before,
            after=_policy_snapshot(policy),
            created_at=now,
        )
    )
    try:
        await session.flush()
        reconciliation = await _finish_manual_policy_change(
            session,
            affected_beatmap_ids=affected_beatmap_ids,
            before=effective_before,
        )
        await session.refresh(policy)
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    await _invalidate_manual_policy_caches(beatmapset_id, cached_beatmap_ids, reconciliation)
    return policy


async def _upsert_revision_event(
    session: AsyncSession,
    *,
    scope: RankingPolicyScope,
    beatmapset_id: int,
    beatmap_id: int,
    old_checksum: str | None,
    new_checksum: str | None,
    old_last_updated: datetime | None,
    new_last_updated: datetime | None,
    reason: str,
) -> BeatmapRankingEvent:
    existing = (
        await session.exec(
            select(BeatmapRankingEvent)
            .where(
                BeatmapRankingEvent.event_type == RankingEventType.UPSTREAM_REVISION_CHANGED,
                BeatmapRankingEvent.scope == scope,
                BeatmapRankingEvent.beatmapset_id == beatmapset_id,
                BeatmapRankingEvent.beatmap_id == beatmap_id,
                col(BeatmapRankingEvent.resolved_at).is_(None),
            )
            .with_for_update()
        )
    ).first()
    if existing is not None:
        details = dict(existing.details)
        details["change_count"] = int(details.get("change_count", 1)) + 1
        existing.new_checksum = new_checksum
        existing.new_last_updated = new_last_updated
        existing.reason = reason
        existing.details = details
        return existing

    event = BeatmapRankingEvent(
        event_type=RankingEventType.UPSTREAM_REVISION_CHANGED,
        scope=scope,
        beatmapset_id=beatmapset_id,
        beatmap_id=beatmap_id,
        old_checksum=old_checksum,
        new_checksum=new_checksum,
        old_last_updated=old_last_updated,
        new_last_updated=new_last_updated,
        reason=reason,
        details={"change_count": 1},
        created_at=utcnow(),
    )
    session.add(event)
    return event


async def invalidate_for_upstream_change(
    session: AsyncSession,
    *,
    beatmapset_id: int,
    beatmap_id: int,
    old_checksum: str | None,
    new_checksum: str | None,
    old_last_updated: datetime | None = None,
    new_last_updated: datetime | None = None,
    commit: bool = True,
) -> list[BeatmapRankingEvent]:
    """Invalidate every local decision affected by an upstream revision.

    Per-difficulty policies are deactivated but retained.  A set policy stays
    active for its unchanged difficulties; the changed difficulty is removed
    from its pinned manifest until the set is explicitly ranked again.  The
    operator event is therefore an inbox notification, not the source of truth
    for invalidation. Pass ``commit=False`` when a synchronizer needs
    policy invalidation, metadata replacement and score reconciliation to share
    one transaction.
    """

    if old_checksum == new_checksum and old_last_updated == new_last_updated:
        return []
    locked_beatmapset = await _lock_beatmapset(session, beatmapset_id)
    if locked_beatmapset is None:
        return []
    beatmap = await session.get(Beatmap, beatmap_id)
    if beatmap is None or beatmap.beatmapset_id != beatmapset_id:
        return []

    reason = "Upstream beatmap revision changed; local ranking requires review"
    now = utcnow()
    events: list[BeatmapRankingEvent] = []
    difficulty_policy = await session.get(BeatmapRankingPolicy, beatmap_id)
    if (
        difficulty_policy is not None
        and difficulty_policy.is_active
        and not difficulty_policy.force_unranked
        and not difficulty_policy.blocks_set_policy
        and difficulty_policy.ranked_checksum != new_checksum
    ):
        before = _policy_snapshot(difficulty_policy)
        difficulty_policy.is_active = False
        difficulty_policy.invalidated_at = now
        difficulty_policy.invalidation_reason = reason
        difficulty_policy.updated_at = now
        difficulty_policy.updated_by_user_id = None
        event = await _upsert_revision_event(
            session,
            scope=RankingPolicyScope.BEATMAP,
            beatmapset_id=beatmap.beatmapset_id,
            beatmap_id=beatmap_id,
            old_checksum=old_checksum,
            new_checksum=new_checksum,
            old_last_updated=old_last_updated,
            new_last_updated=new_last_updated,
            reason=reason,
        )
        events.append(event)
        session.add(
            BeatmapRankingAudit(
                action=RankingPolicyAction.INVALIDATE,
                scope=RankingPolicyScope.BEATMAP,
                beatmapset_id=beatmap.beatmapset_id,
                beatmap_id=beatmap_id,
                actor_user_id=None,
                reason=reason,
                before=before,
                after=_policy_snapshot(difficulty_policy),
                created_at=now,
            )
        )

    set_policy = await session.get(BeatmapsetRankingPolicy, beatmap.beatmapset_id)
    pinned_set_checksum = set_policy.revision_manifest.get(str(beatmap_id)) if set_policy is not None else None
    if (
        set_policy is not None
        and set_policy.is_active
        and not set_policy.force_unranked
        and pinned_set_checksum != new_checksum
    ):
        before = _policy_snapshot(set_policy)
        revision_manifest = dict(set_policy.revision_manifest)
        revision_manifest.pop(str(beatmap_id), None)
        set_policy.revision_manifest = revision_manifest
        set_policy.updated_at = now
        set_policy.updated_by_user_id = None
        event = await _upsert_revision_event(
            session,
            scope=RankingPolicyScope.BEATMAPSET,
            beatmapset_id=beatmap.beatmapset_id,
            beatmap_id=beatmap_id,
            old_checksum=old_checksum,
            new_checksum=new_checksum,
            old_last_updated=old_last_updated,
            new_last_updated=new_last_updated,
            reason=reason,
        )
        events.append(event)
        session.add(
            BeatmapRankingAudit(
                action=RankingPolicyAction.INVALIDATE,
                scope=RankingPolicyScope.BEATMAPSET,
                beatmapset_id=beatmap.beatmapset_id,
                beatmap_id=beatmap_id,
                actor_user_id=None,
                reason=reason,
                before=before,
                after={**(_policy_snapshot(set_policy) or {}), "invalidated_beatmap_id": beatmap_id},
                created_at=now,
            )
        )

    if events:
        if commit:
            await session.commit()
            for event in events:
                await session.refresh(event)
        else:
            await session.flush()
    return events


async def list_pending_ranking_events(
    session: AsyncSession,
    *,
    limit: int = 50,
    offset: int = 0,
) -> list[BeatmapRankingEvent]:
    """Return the unresolved operator inbox, newest first."""

    if not 1 <= limit <= 200:
        raise ValueError("limit must be between 1 and 200")
    if offset < 0:
        raise ValueError("offset must not be negative")
    return list(
        (
            await session.exec(
                select(BeatmapRankingEvent)
                .where(col(BeatmapRankingEvent.resolved_at).is_(None))
                .order_by(
                    col(BeatmapRankingEvent.created_at).desc(),
                    col(BeatmapRankingEvent.id).desc(),
                )
                .offset(offset)
                .limit(limit)
            )
        ).all()
    )


async def resolve_ranking_event(
    session: AsyncSession,
    *,
    event_id: int,
    actor_user_id: int,
    reason: str | None = None,
) -> BeatmapRankingEvent:
    """Acknowledge an event without restoring the invalidated rank."""

    event = (
        await session.exec(select(BeatmapRankingEvent).where(BeatmapRankingEvent.id == event_id).with_for_update())
    ).first()
    if event is None:
        raise LookupError(f"Ranking event {event_id} does not exist")
    if event.resolved_at is None:
        event.resolved_at = utcnow()
        event.resolved_by_user_id = actor_user_id
        session.add(
            BeatmapRankingAudit(
                action=RankingPolicyAction.RESOLVE_EVENT,
                scope=event.scope,
                beatmapset_id=event.beatmapset_id,
                beatmap_id=event.beatmap_id,
                actor_user_id=actor_user_id,
                reason=_normalise_reason(reason) if reason is not None else f"Resolved ranking event {event_id}",
                before={"event_id": event_id, "resolved": False},
                after={"event_id": event_id, "resolved": True},
                created_at=event.resolved_at,
            )
        )
        await session.commit()
        await session.refresh(event)
    return event
