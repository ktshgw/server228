"""Protected private APIs for local beatmap ranking operations."""

from datetime import datetime
from typing import Annotated, Any, Literal

from app.database.beatmap import Beatmap
from app.database.beatmap_ranking import BeatmapRankingEvent, RankingEventType, RankingPolicyScope
from app.database.beatmapset import Beatmapset
from app.dependencies.database import Database, with_db
from app.dependencies.staff import BeatmapModeratorUser, RankerUser
from app.models.beatmap import BeatmapRankStatus
from app.models.error import ErrorType, RequestError
from app.models.model import UTCBaseModel
from app.service.beatmap_ranking_service import (
    apply_local_rank,
    apply_local_unrank,
    clear_local_rank,
    get_effective_beatmap_policy,
    get_effective_beatmapset_policy,
    list_pending_ranking_events,
    resolve_ranking_event,
)
from app.service.beatmapset_update_service import get_beatmapset_update_service

from .router import router

from fastapi import Path, Query
from httpx import HTTPStatusError
from pydantic import BaseModel, ConfigDict, Field, StrictBool, StrictInt, StringConstraints, field_validator

AuditReason = Annotated[str, StringConstraints(strip_whitespace=True, max_length=1000)]
PositiveStrictInt = Annotated[StrictInt, Field(gt=0)]
RankableStatus = Literal[BeatmapRankStatus.RANKED, BeatmapRankStatus.LOVED]
ClientRankingAction = Literal["rank", "unrank", "love"]


class ApplyLocalRankRequest(BaseModel):
    """Apply a set-wide policy or override one difficulty."""

    model_config = ConfigDict(extra="forbid")

    beatmapset_id: PositiveStrictInt
    beatmap_id: PositiveStrictInt | None = None
    status: RankableStatus = BeatmapRankStatus.RANKED
    leaderboard_enabled: StrictBool | None = None
    pp_enabled: StrictBool | None = None
    reason: AuditReason = "no reason"

    @field_validator("status", mode="before")
    @classmethod
    def validate_status(cls, value: Any) -> BeatmapRankStatus:
        if isinstance(value, bool):
            raise ValueError("status must be the numeric Ranked (1) or Loved (4) value")
        try:
            status = BeatmapRankStatus(value)
        except (TypeError, ValueError) as exc:
            raise ValueError("status must be the numeric Ranked (1) or Loved (4) value") from exc
        if status not in {BeatmapRankStatus.RANKED, BeatmapRankStatus.LOVED}:
            raise ValueError("only Ranked (1) and Loved (4) can be applied locally")
        return status


class ClearLocalRankRequest(BaseModel):
    """Select a set-wide target or one difficulty for a policy mutation."""

    model_config = ConfigDict(extra="forbid")

    beatmapset_id: PositiveStrictInt
    beatmap_id: PositiveStrictInt | None = None
    reason: AuditReason = "no reason"


class RankingActionResponse(BaseModel):
    """Acknowledgement returned after an audited ranking mutation."""

    action: Literal["rank", "unrank", "inherit"]
    scope: RankingPolicyScope
    beatmapset_id: int
    beatmap_id: int | None
    status: BeatmapRankStatus | None
    leaderboard_enabled: bool | None
    pp_enabled: bool | None
    force_unranked: bool | None = None


class RankingEventResponse(UTCBaseModel):
    """An upstream revision change awaiting operator review."""

    model_config = ConfigDict(from_attributes=True)

    id: int
    event_type: RankingEventType
    scope: RankingPolicyScope
    beatmapset_id: int
    beatmap_id: int
    old_checksum: str | None
    new_checksum: str | None
    old_last_updated: datetime | None
    new_last_updated: datetime | None
    reason: str
    details: dict[str, Any]
    created_at: datetime
    resolved_at: datetime | None
    resolved_by_user_id: int | None


class PendingRankingEventsResponse(BaseModel):
    """A page of unresolved ranking events for the admin inbox."""

    events: list[RankingEventResponse]
    limit: int
    offset: int


class ClientBeatmapModerationRequest(BaseModel):
    """A set-wide or single-difficulty action issued by the native lazer overlay."""

    model_config = ConfigDict(extra="forbid")

    action: ClientRankingAction
    beatmap_id: PositiveStrictInt | None = None


class ClientBeatmapModerationState(BaseModel):
    """Current effective status and actions available to the authenticated operator."""

    allowed: Literal[True] = True
    scope: RankingPolicyScope
    beatmapset_id: int
    beatmap_id: int | None = None
    status: BeatmapRankStatus
    status_name: str
    source: str
    leaderboard_enabled: bool
    pp_enabled: bool
    can_rank: bool
    can_unrank: bool
    can_love: bool
    applied_action: ClientRankingAction | None = None


async def _ensure_ranking_target(
    beatmapset_id: int,
    beatmap_id: int | None,
) -> None:
    """Refresh official metadata and verify a difficulty belongs to its set."""

    try:
        snapshot = await get_beatmapset_update_service().refresh_beatmapset(beatmapset_id)
        if beatmap_id is not None and not any(beatmap["id"] == beatmap_id for beatmap in snapshot["beatmaps"]):
            raise RequestError(
                ErrorType.INVALID_REQUEST,
                {
                    "reason": "beatmap_id does not belong to beatmapset_id",
                },
            )
    except HTTPStatusError as exc:
        if exc.response.status_code != 404:
            raise
        error_type = ErrorType.BEATMAP_NOT_FOUND if beatmap_id is not None else ErrorType.BEATMAPSET_NOT_FOUND
        raise RequestError(error_type) from exc


async def _client_moderation_state(
    session: Database,
    beatmapset_id: int,
    *,
    beatmap_id: int | None = None,
    applied_action: ClientRankingAction | None = None,
) -> ClientBeatmapModerationState:
    beatmapset = await session.get(Beatmapset, beatmapset_id)
    if beatmapset is None:
        raise RequestError(ErrorType.BEATMAPSET_NOT_FOUND)

    if beatmap_id is None:
        policy = await get_effective_beatmapset_policy(session, beatmapset)
        scope = RankingPolicyScope.BEATMAPSET
    else:
        beatmap = await session.get(Beatmap, beatmap_id)
        if beatmap is None:
            raise RequestError(ErrorType.BEATMAP_NOT_FOUND)
        if beatmap.beatmapset_id != beatmapset_id:
            raise RequestError(
                ErrorType.INVALID_REQUEST,
                {"reason": "beatmap_id does not belong to beatmapset_id"},
            )
        policy = await get_effective_beatmap_policy(session, beatmap)
        scope = RankingPolicyScope.BEATMAP

    has_leaderboard = policy.status.has_leaderboard()
    return ClientBeatmapModerationState(
        scope=scope,
        beatmapset_id=beatmapset_id,
        beatmap_id=beatmap_id,
        status=policy.status,
        status_name=policy.status.name.lower(),
        source=policy.source.value,
        leaderboard_enabled=policy.leaderboard_enabled,
        pp_enabled=policy.pp_enabled,
        can_rank=not has_leaderboard,
        can_unrank=has_leaderboard,
        can_love=policy.status != BeatmapRankStatus.LOVED,
        applied_action=applied_action,
    )


@router.get(
    "/client/beatmap-ranking/{beatmapset_id}",
    name="Get native client beatmap moderation state",
    tags=["Local Beatmap Ranking", "g0v0 API", "Client"],
    response_model=ClientBeatmapModerationState,
)
async def get_client_beatmap_moderation_state(
    beatmapset_id: Annotated[int, Path(gt=0)],
    current_user: BeatmapModeratorUser,
    beatmap_id: Annotated[int | None, Query(gt=0)] = None,
) -> ClientBeatmapModerationState:
    """Expose native controls only to BNG members and administrators."""

    await _ensure_ranking_target(beatmapset_id, beatmap_id)
    # Authentication already opened the request-scoped transaction before the
    # independent upstream refresh. Read through a new session so a first-time
    # beatmapset insert is visible under MySQL's repeatable-read isolation.
    async with with_db() as state_session:
        return await _client_moderation_state(state_session, beatmapset_id, beatmap_id=beatmap_id)


@router.post(
    "/client/beatmap-ranking/{beatmapset_id}",
    name="Apply native client beatmap moderation action",
    tags=["Local Beatmap Ranking", "g0v0 API", "Client"],
    response_model=ClientBeatmapModerationState,
)
async def apply_client_beatmap_moderation_action(
    beatmapset_id: Annotated[int, Path(gt=0)],
    request: ClientBeatmapModerationRequest,
    current_user: BeatmapModeratorUser,
) -> ClientBeatmapModerationState:
    """Apply an audited set-wide or single-difficulty ranking decision."""

    await _ensure_ranking_target(beatmapset_id, request.beatmap_id)
    target = f"beatmap {request.beatmap_id}" if request.beatmap_id is not None else "all difficulties"
    reason = f"lazer client {request.action} action for {target} by {current_user.username}"
    try:
        async with with_db() as mutation_session:
            if request.action == "unrank":
                await apply_local_unrank(
                    mutation_session,
                    actor_user_id=current_user.id,
                    beatmapset_id=beatmapset_id,
                    beatmap_id=request.beatmap_id,
                    replace_difficulty_overrides=request.beatmap_id is None,
                    reason=reason,
                )
            else:
                status = BeatmapRankStatus.LOVED if request.action == "love" else BeatmapRankStatus.RANKED
                await apply_local_rank(
                    mutation_session,
                    actor_user_id=current_user.id,
                    beatmapset_id=beatmapset_id,
                    beatmap_id=request.beatmap_id,
                    status=status,
                    leaderboard_enabled=True,
                    pp_enabled=status.has_pp(),
                    replace_difficulty_overrides=request.beatmap_id is None,
                    reason=reason,
                )
    except LookupError as exc:
        raise RequestError(ErrorType.NOT_FOUND, {"reason": str(exc)}) from exc
    except ValueError as exc:
        raise RequestError(ErrorType.INVALID_REQUEST, {"reason": str(exc)}) from exc

    # Use a fresh session because ranking mutations commit independently and
    # the request-scoped session may still hold the pre-mutation identity map.
    async with with_db() as state_session:
        return await _client_moderation_state(
            state_session,
            beatmapset_id,
            beatmap_id=request.beatmap_id,
            applied_action=request.action,
        )


@router.get(
    "/admin/beatmap-ranking/notifications",
    name="List pending local ranking notifications",
    tags=["Local Beatmap Ranking", "g0v0 API", "Admin"],
    response_model=PendingRankingEventsResponse,
    description="List unresolved upstream-revision events which removed a local Ranked or Loved status.",
)
async def get_pending_ranking_notifications(
    session: Database,
    current_user: RankerUser,
    limit: Annotated[int, Query(ge=1, le=200)] = 50,
    offset: Annotated[int, Query(ge=0)] = 0,
) -> PendingRankingEventsResponse:
    events = await list_pending_ranking_events(session, limit=limit, offset=offset)
    return PendingRankingEventsResponse(
        events=[RankingEventResponse.model_validate(event) for event in events],
        limit=limit,
        offset=offset,
    )


@router.post(
    "/admin/beatmap-ranking/rank",
    name="Apply local beatmap rank",
    tags=["Local Beatmap Ranking", "g0v0 API", "Admin"],
    response_model=RankingActionResponse,
    description="Apply an audited server-local Ranked or Loved policy to a set or one difficulty.",
)
async def rank_beatmap_target(
    request: ApplyLocalRankRequest,
    session: Database,
    current_user: RankerUser,
) -> RankingActionResponse:
    actor_user_id = current_user.id
    await _ensure_ranking_target(request.beatmapset_id, request.beatmap_id)

    leaderboard_enabled = (
        request.leaderboard_enabled if request.leaderboard_enabled is not None else request.status.has_leaderboard()
    )
    pp_enabled = request.pp_enabled if request.pp_enabled is not None else request.status.has_pp()
    try:
        async with with_db() as mutation_session:
            await apply_local_rank(
                mutation_session,
                actor_user_id=actor_user_id,
                beatmapset_id=request.beatmapset_id,
                beatmap_id=request.beatmap_id,
                status=request.status,
                leaderboard_enabled=leaderboard_enabled,
                pp_enabled=pp_enabled,
                reason=request.reason,
            )
    except LookupError as exc:
        raise RequestError(ErrorType.NOT_FOUND, {"reason": str(exc)}) from exc
    except ValueError as exc:
        raise RequestError(ErrorType.INVALID_REQUEST, {"reason": str(exc)}) from exc

    return RankingActionResponse(
        action="rank",
        scope=RankingPolicyScope.BEATMAP if request.beatmap_id is not None else RankingPolicyScope.BEATMAPSET,
        beatmapset_id=request.beatmapset_id,
        beatmap_id=request.beatmap_id,
        status=request.status,
        leaderboard_enabled=leaderboard_enabled,
        pp_enabled=pp_enabled,
        force_unranked=False,
    )


@router.post(
    "/admin/beatmap-ranking/unrank",
    name="Apply local beatmap unrank",
    tags=["Local Beatmap Ranking", "g0v0 API", "Admin"],
    response_model=RankingActionResponse,
    description="Apply an audited, sync-stable local override which disables leaderboard and PP.",
)
async def unrank_beatmap_target(
    request: ClearLocalRankRequest,
    session: Database,
    current_user: RankerUser,
) -> RankingActionResponse:
    actor_user_id = current_user.id
    try:
        async with with_db() as mutation_session:
            await apply_local_unrank(
                mutation_session,
                actor_user_id=actor_user_id,
                beatmapset_id=request.beatmapset_id,
                beatmap_id=request.beatmap_id,
                reason=request.reason,
            )
    except LookupError as exc:
        raise RequestError(ErrorType.NOT_FOUND, {"reason": str(exc)}) from exc
    except ValueError as exc:
        raise RequestError(ErrorType.INVALID_REQUEST, {"reason": str(exc)}) from exc

    async with with_db() as state_session:
        state = await _client_moderation_state(
            state_session,
            request.beatmapset_id,
            beatmap_id=request.beatmap_id,
        )
    return RankingActionResponse(
        action="unrank",
        scope=RankingPolicyScope.BEATMAP if request.beatmap_id is not None else RankingPolicyScope.BEATMAPSET,
        beatmapset_id=request.beatmapset_id,
        beatmap_id=request.beatmap_id,
        status=state.status,
        leaderboard_enabled=state.leaderboard_enabled,
        pp_enabled=state.pp_enabled,
        force_unranked=state.source != "upstream" and not state.leaderboard_enabled,
    )


@router.post(
    "/admin/beatmap-ranking/inherit",
    name="Restore inherited beatmap rank",
    tags=["Local Beatmap Ranking", "g0v0 API", "Admin"],
    response_model=RankingActionResponse,
    description="Deactivate the target's local override so it inherits its set policy or official osu! status.",
)
async def inherit_beatmap_target(
    request: ClearLocalRankRequest,
    session: Database,
    current_user: RankerUser,
) -> RankingActionResponse:
    actor_user_id = current_user.id
    try:
        async with with_db() as mutation_session:
            await clear_local_rank(
                mutation_session,
                actor_user_id=actor_user_id,
                beatmapset_id=request.beatmapset_id,
                beatmap_id=request.beatmap_id,
                reason=request.reason,
            )
    except LookupError as exc:
        raise RequestError(ErrorType.NOT_FOUND, {"reason": str(exc)}) from exc
    except ValueError as exc:
        raise RequestError(ErrorType.INVALID_REQUEST, {"reason": str(exc)}) from exc

    return RankingActionResponse(
        action="inherit",
        scope=RankingPolicyScope.BEATMAP if request.beatmap_id is not None else RankingPolicyScope.BEATMAPSET,
        beatmapset_id=request.beatmapset_id,
        beatmap_id=request.beatmap_id,
        status=None,
        leaderboard_enabled=None,
        pp_enabled=None,
        force_unranked=None,
    )


@router.post(
    "/admin/beatmap-ranking/notifications/{event_id}/resolve",
    name="Resolve local ranking notification",
    tags=["Local Beatmap Ranking", "g0v0 API", "Admin"],
    status_code=204,
    description="Mark an upstream-revision notification as reviewed without changing map policy.",
)
async def resolve_ranking_notification(
    event_id: Annotated[int, Path(gt=0)],
    session: Database,
    current_user: RankerUser,
) -> None:
    event = await session.get(BeatmapRankingEvent, event_id)
    if event is None:
        raise RequestError(ErrorType.NOT_FOUND, {"event_id": event_id})
    if event.resolved_at is None:
        await resolve_ranking_event(session, event_id=event_id, actor_user_id=current_user.id)


__all__ = [
    "ApplyLocalRankRequest",
    "ClearLocalRankRequest",
    "ClientBeatmapModerationRequest",
    "ClientBeatmapModerationState",
    "PendingRankingEventsResponse",
    "RankingActionResponse",
    "RankingEventResponse",
]
