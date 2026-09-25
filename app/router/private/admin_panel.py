"""Same-origin backend-for-frontend for the private server admin panel."""

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timedelta
import hashlib
import json
import secrets
import time
from typing import Annotated, Any, Literal
from urllib.parse import urlsplit

from app.auth import (
    authenticate_user,
    check_totp_backup_code,
    get_password_hash,
    validate_username,
    verify_totp_key_with_replay_protection,
)
from app.config import settings
from app.const import BACKUP_CODE_LENGTH, BANCHOBOT_ID
from app.database import (
    AdminAuditEvent,
    Beatmap,
    BeatmapRankingAudit,
    BeatmapRankingEvent,
    BeatmapRankingPolicy,
    Beatmapset,
    BeatmapsetRankingPolicy,
    BeatmapSync,
    LoginSession,
    OAuthToken,
    Score,
    ScoreImport,
    TotpKeys,
    TrustedDevice,
    User,
    UserLoginLog,
    UserStatistics,
)
from app.database.events import Event, EventType
from app.database.user import COUNTRIES
from app.dependencies.database import Database, Redis, with_db
from app.dependencies.fetcher import Fetcher
from app.dependencies.storage import StorageService
from app.features.somsai.services.somsai_mmr_service import MAX_ADMIN_MMR, change_somsai_mmr, list_somsai_mmr
from app.fetcher._base import TokenAuthError
from app.helpers import utcnow
from app.log import log
from app.models.beatmap import BeatmapRankStatus
from app.models.error import ErrorType, RequestError
from app.models.events.score import ScoreDeletedEvent
from app.models.events.user import UserRenamedEvent
from app.plugins import hub
from app.service.admin_score_service import clear_user_profile, delete_user_score
from app.service.beatmap_ranking_service import (
    apply_local_rank,
    apply_local_unrank,
    clear_local_rank,
    get_effective_beatmap_policies,
    get_effective_beatmapset_policy,
    list_pending_ranking_events,
    resolve_ranking_event,
)
from app.service.beatmapset_update_service import get_beatmapset_update_service
from app.service.online_presence_service import get_online_user_ids
from app.service.ranking_cache_service import get_ranking_cache_service
from app.service.replay_retention_service import best_replay_score, lock_replay_candidates, replay_combination_scores
from app.service.score_import_service import (
    MAX_REPLAY_BYTES,
    PreparedOfficialScore,
    ScoreImportConflictError,
    ScoreImportError,
    ScoreImportNotFoundError,
    ScoreImportUpstreamError,
    ScoreRevisionVerification,
    fetch_official_score,
    import_official_score,
    parse_official_score_reference,
    parse_uploaded_osr,
    prepare_uploaded_osr_score,
    verify_official_score_revision,
    verify_uploaded_score_revision,
)
from app.service.user_cache_service import get_user_cache_service
from app.service.user_identity_service import resolve_human_user
from app.service.web_session_service import invalidate_web_sessions

from .router import router

from fastapi import Depends, File, Form, HTTPException, Query, Request, Response, UploadFile, status
from fastapi.encoders import jsonable_encoder
from httpx import HTTPError, HTTPStatusError
from pydantic import BaseModel, ConfigDict, Field, StrictBool, StrictInt, StringConstraints, field_validator
from sqlalchemy import delete, func, or_
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import joinedload
from sqlmodel import col, select

SESSION_COOKIE = "__Host-private_osu_admin"
SESSION_KEY_PREFIX = "admin-panel:session:"
USER_SESSIONS_PREFIX = "admin-panel:user-sessions:"
SESSION_TTL_SECONDS = 8 * 60 * 60
LOGIN_WINDOW_SECONDS = 15 * 60
LOGIN_ATTEMPTS = 8
LOGIN_IP_ATTEMPTS = 32
STARTED_AT = time.time()
LOGIN_FAILURE_SCRIPT = """
local attempts = redis.call('INCR', KEYS[1])
if attempts == 1 then
    redis.call('EXPIRE', KEYS[1], ARGV[1])
end
return attempts
"""
logger = log("AdminPanel")

AuditReason = Annotated[str, StringConstraints(strip_whitespace=True, max_length=500)]
PositiveStrictInt = Annotated[StrictInt, Field(gt=0)]
ScoreSource = Annotated[str, StringConstraints(strip_whitespace=True, min_length=1, max_length=512)]
OfficialRuleset = Literal["osu", "taiko", "fruits", "mania"]


class LoginRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    username: str = Field(min_length=2, max_length=254)
    password: str = Field(min_length=1, max_length=512)
    totp_code: str | None = Field(default=None, min_length=6, max_length=BACKUP_CODE_LENGTH)


class UserUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    username: str | None = Field(default=None, min_length=2, max_length=15)
    country_code: str | None = Field(default=None, pattern=r"^[A-Z]{2}$")
    is_active: StrictBool | None = None
    is_supporter: StrictBool | None = None
    is_admin: StrictBool | None = None
    is_gmt: StrictBool | None = None
    is_qat: StrictBool | None = None
    is_bng: StrictBool | None = None
    is_owner: StrictBool | None = None
    reason: AuditReason = "no reason"

    @field_validator("username", mode="before")
    @classmethod
    def normalise_username(cls, value: Any) -> Any:
        return value.strip() if isinstance(value, str) else value

    @field_validator("country_code", mode="before")
    @classmethod
    def normalise_country(cls, value: Any) -> Any:
        return value.strip().upper() if isinstance(value, str) else value


class PasswordResetRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    new_password: str = Field(min_length=8, max_length=128)
    reason: AuditReason = "no reason"


class SomsaiMmrUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    mmr: Annotated[StrictInt, Field(ge=0, le=MAX_ADMIN_MMR)]
    expected_version: Annotated[str, StringConstraints(pattern=r"^[0-9a-f]{64}$")]
    reason: AuditReason = "no reason"


class ScoreDeleteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    reason: AuditReason = "no reason"


class ProfileClearRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    confirmation: Annotated[str, StringConstraints(strip_whitespace=True, min_length=1, max_length=32)]
    reset_public_profile: StrictBool = True
    reason: AuditReason = "no reason"


class RankingMutationRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    action: Literal["rank", "unrank", "inherit"]
    beatmapset_id: PositiveStrictInt
    beatmap_id: PositiveStrictInt | None = None
    status: StrictInt = 1
    leaderboard_enabled: StrictBool | None = None
    pp_enabled: StrictBool | None = None
    reason: AuditReason = "no reason"

    @field_validator("status")
    @classmethod
    def validate_status(cls, value: int) -> int:
        if value not in {BeatmapRankStatus.RANKED, BeatmapRankStatus.LOVED}:
            raise ValueError("status must be Ranked (1) or Loved (4)")
        return value


class ResolveRankingEventRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    reason: AuditReason = "Reviewed in admin panel"


class ScoreImportPreviewRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    source: ScoreSource
    ruleset: OfficialRuleset | None = None
    allow_unverified_revision: StrictBool = False


class ScoreImportRequest(ScoreImportPreviewRequest):
    target_user_id: PositiveStrictInt
    include_replay: StrictBool = True
    reason: AuditReason = "no reason"


@dataclass(slots=True)
class AdminContext:
    user: User
    csrf_token: str
    session_digest: str


def _session_digest(token: str) -> str:
    return hashlib.sha256(token.encode()).hexdigest()


def _session_key(digest: str) -> str:
    return f"{SESSION_KEY_PREFIX}{digest}"


def _user_sessions_key(user_id: int) -> str:
    return f"{USER_SESSIONS_PREFIX}{user_id}"


def _request_ip(request: Request) -> str | None:
    forwarded = request.headers.get("x-forwarded-for", "").split(",", 1)[0].strip()
    if forwarded:
        return forwarded[:45]
    return request.client.host[:45] if request.client else None


def _expected_origin() -> str:
    parsed = urlsplit(str(settings.server_url))
    return f"{parsed.scheme}://{parsed.netloc}"


def _require_same_origin(request: Request) -> None:
    origin = request.headers.get("origin")
    if origin != _expected_origin():
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Invalid request origin")


def _role_flags(user: User) -> dict[str, bool]:
    owner = bool(user.is_owner)
    administrator = owner or bool(user.is_admin)
    return {
        "owner": owner,
        "administrator": administrator,
        "ranker": administrator or bool(user.is_bng) or bool(user.is_qat),
        "moderator": administrator or bool(user.is_gmt),
    }


def _can_access_admin_panel(user: User) -> bool:
    """The standalone panel is reserved for administrators and the owner."""

    return _role_flags(user)["administrator"]


def _public_user(user: User, *, restricted: bool = False, include_email: bool = False) -> dict[str, Any]:
    result: dict[str, Any] = {
        "id": user.id,
        "server_id": user.server_id,
        "username": user.username,
        "avatar_url": f"/users/{user.id}/avatar",
        "country_code": user.country_code,
        "is_active": bool(user.is_active),
        "is_online": bool(user.is_online),
        "is_supporter": bool(user.is_supporter),
        "is_restricted": bool(restricted),
        "is_bot": bool(user.is_bot),
        "is_owner": bool(user.is_owner),
        "is_admin": bool(user.is_admin),
        "is_gmt": bool(user.is_gmt),
        "is_qat": bool(user.is_qat),
        "is_bng": bool(user.is_bng),
        "roles": _role_flags(user),
        "join_date": user.join_date,
        "last_visit": user.last_visit,
    }
    if include_email:
        result["email"] = user.email
    return result


async def _invalidate_admin_sessions(redis: Redis, user_id: int) -> None:
    membership_key = _user_sessions_key(user_id)
    digests = await redis.smembers(membership_key)
    if digests:
        await redis.delete(*[_session_key(str(digest)) for digest in digests])
    await redis.delete(membership_key)


async def _create_admin_session(redis: Redis, user: User, request: Request) -> tuple[str, str]:
    token = secrets.token_urlsafe(48)
    digest = _session_digest(token)
    csrf_token = secrets.token_urlsafe(32)
    payload = {
        "user_id": user.id,
        "csrf_token": csrf_token,
        "created_at": utcnow().isoformat(),
        "user_agent_hash": hashlib.sha256(request.headers.get("user-agent", "").encode()).hexdigest(),
    }
    await redis.setex(_session_key(digest), SESSION_TTL_SECONDS, json.dumps(payload))
    membership_key = _user_sessions_key(user.id)
    await redis.sadd(membership_key, digest)
    await redis.expire(membership_key, SESSION_TTL_SECONDS)
    return token, csrf_token


async def _record_login_failure(redis: Redis, *keys: str) -> None:
    for key in keys:
        await redis.eval(LOGIN_FAILURE_SCRIPT, 1, key, LOGIN_WINDOW_SECONDS)


async def require_admin_session(request: Request, session: Database, redis: Redis) -> AdminContext:
    token = request.cookies.get(SESSION_COOKIE)
    if not token:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Admin login required")

    digest = _session_digest(token)
    raw = await redis.get(_session_key(digest))
    if not raw:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Admin session expired")

    try:
        data = json.loads(raw)
        user_id = int(data["user_id"])
        csrf_token = str(data["csrf_token"])
        expected_ua = str(data["user_agent_hash"])
        created_at = datetime.fromisoformat(str(data["created_at"]))
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        await redis.delete(_session_key(digest))
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid admin session") from exc

    if created_at.tzinfo is None or utcnow() - created_at >= timedelta(seconds=SESSION_TTL_SECONDS):
        await redis.delete(_session_key(digest))
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Admin session expired")

    actual_ua = hashlib.sha256(request.headers.get("user-agent", "").encode()).hexdigest()
    if not secrets.compare_digest(expected_ua, actual_ua):
        await redis.delete(_session_key(digest))
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Admin session changed browser")

    user = await session.get(User, user_id)
    if user is None or not user.is_active or await user.is_restricted(session) or not _can_access_admin_panel(user):
        await _invalidate_admin_sessions(redis, user_id)
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Administrator access required")

    return AdminContext(user=user, csrf_token=csrf_token, session_digest=digest)


AdminSession = Annotated[AdminContext, Depends(require_admin_session)]


def _require_csrf(request: Request, context: AdminContext) -> None:
    _require_same_origin(request)
    supplied = request.headers.get("x-csrf-token", "")
    if not supplied or not secrets.compare_digest(supplied, context.csrf_token):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Invalid CSRF token")


def _require_capability(context: AdminContext, capability: Literal["owner", "administrator", "ranker"]) -> None:
    if not _role_flags(context.user)[capability]:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=f"{capability} access required")


async def _audit(
    session: Database,
    request: Request,
    actor: User,
    *,
    action: str,
    target_type: str,
    target_id: str | int,
    reason: str,
    before: dict[str, Any] | None,
    after: dict[str, Any] | None,
) -> None:
    session.add(
        AdminAuditEvent(
            actor_user_id=actor.id,
            actor_username=actor.username,
            action=action,
            target_type=target_type,
            target_id=str(target_id),
            reason=(reason or "").strip() or "no reason",
            before=jsonable_encoder(before) if before is not None else None,
            after=jsonable_encoder(after) if after is not None else None,
            ip_address=_request_ip(request),
        )
    )


@router.post("/admin-panel/session", tags=["Admin Panel"], include_in_schema=False)
async def login_admin_panel(
    payload: LoginRequest, request: Request, response: Response, session: Database, redis: Redis
):
    _require_same_origin(request)
    ip = _request_ip(request) or "unknown"
    login_identity = f"{ip}\0{payload.username.strip().casefold()}"
    rate_key = f"admin-panel:login-rate:{hashlib.sha256(login_identity.encode()).hexdigest()}"
    ip_rate_key = f"admin-panel:login-rate-ip:{hashlib.sha256(ip.encode()).hexdigest()}"
    attempts, ip_attempts = [int(value or 0) for value in await redis.mget(rate_key, ip_rate_key)]
    if attempts >= LOGIN_ATTEMPTS or ip_attempts >= LOGIN_IP_ATTEMPTS:
        retry_after = max(1, int(await redis.ttl(rate_key)), int(await redis.ttl(ip_rate_key)))
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many login attempts",
            headers={"Retry-After": str(retry_after)},
        )

    user = await authenticate_user(session, payload.username.strip(), payload.password)
    if user is None:
        await _record_login_failure(redis, rate_key, ip_rate_key)
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid credentials or staff access")

    restricted = await user.is_restricted(session)
    if not user.is_active or restricted or not _can_access_admin_panel(user):
        await _record_login_failure(redis, rate_key, ip_rate_key)
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid credentials or administrator access",
        )

    totp_key = (await session.exec(select(TotpKeys).where(col(TotpKeys.user_id) == user.id).with_for_update())).first()
    if totp_key is not None:
        code = (payload.totp_code or "").strip()
        valid = False
        if len(code) == 6 and code.isdigit():
            valid = await verify_totp_key_with_replay_protection(user.id, totp_key.secret, code, redis)
        elif len(code) == BACKUP_CODE_LENGTH:
            valid = check_totp_backup_code(totp_key, code)
            if valid:
                session.add(totp_key)
                await session.commit()
                await session.refresh(user)
        if not valid:
            await _record_login_failure(redis, rate_key, ip_rate_key)
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail={"message": "Two-factor code required", "totp_required": True},
            )

    await redis.delete(rate_key)
    token, csrf_token = await _create_admin_session(redis, user, request)
    response.set_cookie(
        SESSION_COOKIE,
        token,
        max_age=SESSION_TTL_SECONDS,
        secure=True,
        httponly=True,
        samesite="strict",
        path="/",
    )
    response.headers["Cache-Control"] = "no-store"
    return {
        "authenticated": True,
        "csrf_token": csrf_token,
        "user": _public_user(user, restricted=restricted),
    }


@router.get("/admin-panel/session", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_panel_session(context: AdminSession):
    return {
        "authenticated": True,
        "csrf_token": context.csrf_token,
        "user": _public_user(context.user),
    }


@router.delete("/admin-panel/session", status_code=204, tags=["Admin Panel"], include_in_schema=False)
async def logout_admin_panel(request: Request, response: Response, context: AdminSession, redis: Redis) -> None:
    _require_csrf(request, context)
    await redis.delete(_session_key(context.session_digest))
    await redis.srem(_user_sessions_key(context.user.id), context.session_digest)
    response.delete_cookie(SESSION_COOKIE, path="/", secure=True, httponly=True, samesite="strict")


@router.get("/admin-panel/dashboard", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_dashboard(context: AdminSession, session: Database, redis: Redis):
    del context
    day_ago = utcnow() - timedelta(days=1)
    total_users = (await session.exec(select(func.count(col(User.id))).where(col(User.is_bot).is_(False)))).one()
    online_user_ids = await get_online_user_ids(redis)
    online_users = 0
    if online_user_ids:
        online_users = (
            await session.exec(
                select(func.count(col(User.id))).where(
                    col(User.is_bot).is_(False),
                    col(User.id).in_(online_user_ids),
                )
            )
        ).one()
    total_scores = (await session.exec(select(func.count(col(Score.id))))).one()
    scores_today = (await session.exec(select(func.count(col(Score.id))).where(col(Score.ended_at) >= day_ago))).one()
    active_set_policies = (
        await session.exec(
            select(func.count(col(BeatmapsetRankingPolicy.beatmapset_id))).where(
                col(BeatmapsetRankingPolicy.is_active).is_(True)
            )
        )
    ).one()
    active_diff_policies = (
        await session.exec(
            select(func.count(col(BeatmapRankingPolicy.beatmap_id))).where(
                col(BeatmapRankingPolicy.is_active).is_(True)
            )
        )
    ).one()
    pending_events = (
        await session.exec(
            select(func.count(col(BeatmapRankingEvent.id))).where(col(BeatmapRankingEvent.resolved_at).is_(None))
        )
    ).one()
    redis_ok = bool(await redis.ping())
    return {
        "users": {"total": total_users, "online": online_users},
        "scores": {"total": total_scores, "last_24h": scores_today},
        "ranking": {
            "active_set_policies": active_set_policies,
            "active_difficulty_policies": active_diff_policies,
            "pending_events": pending_events,
        },
        "system": {
            "api": True,
            "database": True,
            "redis": redis_ok,
            "uptime_seconds": max(0, int(time.time() - STARTED_AT)),
            "server_url": str(settings.server_url).rstrip("/"),
            "scoring_mode": str(settings.scoring_mode),
            "auto_beatmap_sync": settings.enable_auto_beatmap_sync,
            "all_beatmap_pp": settings.enable_all_beatmap_pp,
            "all_beatmap_leaderboard": settings.enable_all_beatmap_leaderboard,
            "default_country_code": settings.default_country_code,
        },
    }


@router.get("/admin-panel/countries", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_countries(context: AdminSession):
    del context
    countries = [{"code": code, "name": name} for code, name in COUNTRIES.items()]
    countries.append({"code": "XX", "name": "Unknown / not set"})
    return sorted(countries, key=lambda item: (item["name"], item["code"]))


@router.get("/admin-panel/users", tags=["Admin Panel"], include_in_schema=False)
async def list_admin_users(
    context: AdminSession,
    session: Database,
    query: Annotated[str, Query(max_length=64)] = "",
    page: Annotated[int, Query(ge=1)] = 1,
    page_size: Annotated[int, Query(ge=10, le=100)] = 25,
):
    _require_capability(context, "administrator")
    filters: list[Any] = [col(User.is_bot).is_(False)]
    cleaned_query = query.strip()
    if cleaned_query:
        numeric_query = cleaned_query.removeprefix("#")
        search_filter = func.lower(col(User.username)).contains(cleaned_query.lower())
        if numeric_query.isdigit():
            search_filter = or_(
                search_filter,
                col(User.id) == int(numeric_query),
                col(User.server_id) == int(numeric_query),
            )
        filters.append(search_filter)

    total = (await session.exec(select(func.count(col(User.id))).where(*filters))).one()
    restriction = User.is_restricted_query(col(User.id)).label("restricted")
    rows = (
        await session.exec(
            select(User, restriction)
            .where(*filters)
            .order_by(col(User.join_date).desc(), col(User.id).desc())
            .offset((page - 1) * page_size)
            .limit(page_size)
        )
    ).all()
    return {
        "items": [_public_user(user, restricted=bool(restricted)) for user, restricted in rows],
        "page": page,
        "page_size": page_size,
        "total": total,
        "pages": max(1, (total + page_size - 1) // page_size),
    }


@router.get("/admin-panel/users/{user_id}", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_user(user_id: int, context: AdminSession, session: Database):
    _require_capability(context, "administrator")
    actor_is_owner = _role_flags(context.user)["owner"]
    user = await resolve_human_user(session, user_id)
    if user is None or user.is_bot:
        raise HTTPException(status_code=404, detail="User not found")
    user_id = user.id
    restricted = await user.is_restricted(session)
    statistics = (
        await session.exec(
            select(UserStatistics).where(UserStatistics.user_id == user_id).order_by(UserStatistics.mode)
        )
    ).all()
    recent_logins = (
        await session.exec(
            select(UserLoginLog)
            .where(UserLoginLog.user_id == user_id)
            .order_by(col(UserLoginLog.login_time).desc())
            .limit(10)
        )
    ).all()
    result = _public_user(user, restricted=restricted, include_email=actor_is_owner)
    result["statistics"] = [
        {
            "mode": str(item.mode),
            "pp": item.pp,
            "play_count": item.play_count,
            "play_time": item.play_time,
            "ranked_score": item.ranked_score,
            "total_score": item.total_score,
            "accuracy": item.hit_accuracy,
        }
        for item in statistics
    ]
    result["recent_logins"] = [
        {
            "time": item.login_time,
            "success": item.login_success,
            "method": item.login_method,
            "country_code": item.country_code,
            "ip_address": item.ip_address if actor_is_owner else None,
        }
        for item in recent_logins
    ]
    result["somsai_mmr"] = await list_somsai_mmr(session, user_id)
    return result


@router.patch(
    "/admin-panel/users/{user_id}/somsai-mmr/{ruleset_id}/{variant_id}/{format}",
    tags=["Admin Panel"],
    include_in_schema=False,
)
async def update_admin_somsai_mmr(
    user_id: int,
    ruleset_id: int,
    variant_id: int,
    format: Literal["1v1", "2v2"],
    payload: SomsaiMmrUpdateRequest,
    request: Request,
    context: AdminSession,
    session: Database,
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    target = await resolve_human_user(session, user_id)
    if target is None or target.is_bot or target.id == BANCHOBOT_ID:
        raise HTTPException(status_code=404, detail="Editable user not found")
    if (target.is_owner or target.is_admin) and not _role_flags(context.user)["owner"]:
        raise HTTPException(status_code=403, detail="Only an owner can manage administrators and owners")
    target_id = target.id
    try:
        before, after = await change_somsai_mmr(
            session, target_id, ruleset_id, variant_id, format, payload.mmr, payload.expected_version
        )
        await _audit(
            session,
            request,
            context.user,
            action="user.somsai_mmr.update",
            target_type="user",
            target_id=target_id,
            reason=payload.reason,
            before={key: value for key, value in before.items() if key != "version"},
            after={key: value for key, value in after.items() if key != "version"},
        )
        await session.commit()
    except HTTPException:
        await session.rollback()
        raise
    except IntegrityError as exc:
        await session.rollback()
        raise HTTPException(status_code=409, detail="MMR SOMSAI changed; refresh the player and try again") from exc
    return after


def _admin_score_payload(score: Score, imported_score_ids: set[int]) -> dict[str, Any]:
    beatmap = score.beatmap
    beatmapset = beatmap.beatmapset
    return {
        "id": score.id,
        "ruleset": score.gamemode.value,
        "rank": score.rank.value,
        "accuracy": score.accuracy,
        "pp": round(float(score.pp), 2),
        "total_score": score.total_score,
        "max_combo": score.max_combo,
        "mods": score.mods,
        "passed": score.passed,
        "ended_at": score.ended_at,
        "has_replay": score.has_replay,
        "processed": score.processed,
        "imported": score.id in imported_score_ids,
        "beatmap": {
            "id": beatmap.id,
            "beatmapset_id": beatmap.beatmapset_id,
            "version": beatmap.version,
            "difficulty_rating": beatmap.difficulty_rating,
        },
        "beatmapset": {
            "id": beatmapset.id,
            "artist": beatmapset.artist,
            "title": beatmapset.title,
            "creator": beatmapset.creator,
        },
    }


@router.get("/admin-panel/users/{user_id}/scores", tags=["Admin Panel"], include_in_schema=False)
async def list_admin_user_scores(
    user_id: int,
    context: AdminSession,
    session: Database,
    limit: Annotated[int, Query(ge=1, le=200)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
):
    _require_capability(context, "administrator")
    target = await session.get(User, user_id)
    if target is None or target.is_bot:
        raise HTTPException(status_code=404, detail="User not found")
    visible_score_conditions = (
        col(Score.user_id) == user_id,
        col(Score.passed).is_(True),
    )
    total = int((await session.exec(select(func.count(col(Score.id))).where(*visible_score_conditions))).one())
    scores = list(
        (
            await session.exec(
                select(Score)
                .where(*visible_score_conditions)
                .options(joinedload(Score.beatmap).joinedload(Beatmap.beatmapset))
                .order_by(col(Score.ended_at).desc(), col(Score.id).desc())
                .offset(offset)
                .limit(limit)
            )
        ).all()
    )
    score_ids = [score.id for score in scores]
    imported_score_ids = (
        {
            score_id
            for score_id in (
                await session.exec(select(ScoreImport.score_id).where(col(ScoreImport.score_id).in_(score_ids)))
            ).all()
            if score_id is not None
        }
        if score_ids
        else set()
    )
    return {
        "items": [_admin_score_payload(score, imported_score_ids) for score in scores],
        "total": total,
        "limit": limit,
        "offset": offset,
    }


async def _invalidate_user_gameplay_caches(redis: Redis, user_id: int) -> None:
    try:
        user_cache = get_user_cache_service(redis)
        await user_cache.invalidate_user_all_cache(user_id)
        await user_cache.invalidate_v1_user_cache(user_id)
        ranking_cache = get_ranking_cache_service(redis)
        await ranking_cache.invalidate_cache()
        await ranking_cache.invalidate_country_cache()
        await ranking_cache.invalidate_team_cache()
        await ranking_cache.invalidate_top_scores_cache()
    except Exception:
        logger.exception("Post-deletion cache invalidation failed for user {}", user_id)


async def _delete_stored_files(storage: StorageService, paths: tuple[str, ...]) -> None:
    for path in paths:
        try:
            await storage.delete_file(path)
        except Exception:
            logger.exception("Could not remove stored file {} after its database row was deleted", path)


@router.delete("/admin-panel/users/{user_id}/scores/{score_id}", tags=["Admin Panel"], include_in_schema=False)
async def delete_admin_user_score(
    user_id: int,
    score_id: int,
    payload: ScoreDeleteRequest,
    request: Request,
    context: AdminSession,
    session: Database,
    redis: Redis,
    storage: StorageService,
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    target = await session.get(User, user_id)
    if target is None or target.is_bot:
        raise HTTPException(status_code=404, detail="User not found")
    result = await delete_user_score(session, redis, target, score_id)
    if result is None:
        raise HTTPException(status_code=404, detail="Score not found for this user")
    await _audit(
        session,
        request,
        context.user,
        action="score.delete",
        target_type="score",
        target_id=score_id,
        reason=payload.reason,
        before={"user_id": user_id, "ruleset": result.ruleset.value, "has_replay": bool(result.replay_paths)},
        after=None,
    )
    await session.commit()
    await _delete_stored_files(storage, result.replay_paths)
    await _invalidate_user_gameplay_caches(redis, user_id)
    try:
        hub.emit(ScoreDeletedEvent(score=result.score_data))
    except Exception:
        logger.exception("Post-delete score event failed for score {}", score_id)
    return {"deleted_score_id": score_id}


@router.post("/admin-panel/users/{user_id}/clear-profile", tags=["Admin Panel"], include_in_schema=False)
async def clear_admin_user_profile(
    user_id: int,
    payload: ProfileClearRequest,
    request: Request,
    context: AdminSession,
    session: Database,
    redis: Redis,
    storage: StorageService,
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    target = await session.get(User, user_id)
    if target is None or target.id == BANCHOBOT_ID or target.is_bot:
        raise HTTPException(status_code=404, detail="User not found")
    if not secrets.compare_digest(payload.confirmation.casefold(), target.username.casefold()):
        raise HTTPException(status_code=422, detail="Type the exact username to confirm profile clearing")

    avatar_path = storage.get_file_name_by_url(target.avatar_url) if target.avatar_url else None
    if avatar_path is not None and not avatar_path.startswith(f"avatars/{target.id}_"):
        avatar_path = None
    score_count = int(
        (await session.exec(select(func.count(col(Score.id))).where(col(Score.user_id) == user_id))).one()
    )
    result = await clear_user_profile(
        session,
        target,
        reset_public_profile=payload.reset_public_profile,
        avatar_path=avatar_path,
    )
    await _audit(
        session,
        request,
        context.user,
        action="user.profile.clear",
        target_type="user",
        target_id=user_id,
        reason=payload.reason,
        before={"score_count": score_count, "public_profile_reset": payload.reset_public_profile},
        after={"score_count": 0, "public_profile_reset": payload.reset_public_profile},
    )
    await session.commit()
    paths = (*result.replay_paths, *((result.avatar_path,) if result.avatar_path else ()))
    await _delete_stored_files(storage, paths)
    await _invalidate_user_gameplay_caches(redis, user_id)
    return {
        "cleared_user_id": user_id,
        "deleted_scores": len(result.score_ids),
        "public_profile_reset": payload.reset_public_profile,
    }


def _editable_user_snapshot(user: User) -> dict[str, Any]:
    return {
        "username": user.username,
        "country_code": user.country_code,
        "is_active": bool(user.is_active),
        "is_supporter": bool(user.is_supporter),
        "is_owner": bool(user.is_owner),
        "is_admin": bool(user.is_admin),
        "is_gmt": bool(user.is_gmt),
        "is_qat": bool(user.is_qat),
        "is_bng": bool(user.is_bng),
    }


@router.patch("/admin-panel/users/{user_id}", tags=["Admin Panel"], include_in_schema=False)
async def update_admin_user(
    user_id: int,
    payload: UserUpdateRequest,
    request: Request,
    context: AdminSession,
    session: Database,
    redis: Redis,
):
    _require_csrf(request, context)
    _require_capability(context, "administrator")
    target = await session.get(User, user_id)
    if target is None or target.id == BANCHOBOT_ID or target.is_bot:
        raise HTTPException(status_code=404, detail="Editable user not found")

    actor_is_owner = _role_flags(context.user)["owner"]
    target_is_elevated = bool(target.is_owner or target.is_admin)
    changed_fields = payload.model_fields_set - {"reason"}
    if not changed_fields:
        raise HTTPException(status_code=422, detail="No account changes supplied")
    if any(getattr(payload, field_name) is None for field_name in changed_fields):
        raise HTTPException(status_code=422, detail="Account fields cannot be null")
    if "country_code" in changed_fields and payload.country_code not in {*COUNTRIES, "XX"}:
        raise HTTPException(status_code=422, detail="Unknown ISO 3166 country code")
    if "username" in changed_fields:
        assert payload.username is not None
        username_errors = validate_username(payload.username)
        if username_errors:
            raise HTTPException(
                status_code=422,
                detail={"message": "Username is invalid", "errors": username_errors},
            )
        conflicting_user_id = (
            await session.exec(
                select(col(User.id)).where(
                    func.lower(col(User.username)) == payload.username.casefold(),
                    col(User.id) != target.id,
                )
            )
        ).first()
        if conflicting_user_id is not None:
            raise HTTPException(status_code=409, detail="Username is already taken")
    if not actor_is_owner and (target_is_elevated or changed_fields & {"is_owner", "is_admin"}):
        raise HTTPException(status_code=403, detail="Only an owner can manage administrators and owners")
    if target.id == context.user.id and payload.is_active is False:
        raise HTTPException(status_code=409, detail="You cannot deactivate your own account")
    if target.id == context.user.id and (
        payload.is_owner is False or (payload.is_admin is False and not target.is_owner)
    ):
        raise HTTPException(status_code=409, detail="You cannot remove your own current access")

    removes_active_owner = target.is_owner and (payload.is_owner is False or payload.is_active is False)
    if removes_active_owner:
        remaining_owner_ids = (
            await session.exec(
                select(col(User.id))
                .where(
                    col(User.is_owner).is_(True),
                    col(User.is_active).is_(True),
                    col(User.id) != target.id,
                    ~User.is_restricted_query(col(User.id)),
                )
                .with_for_update()
            )
        ).all()
        if not remaining_owner_ids:
            raise HTTPException(status_code=409, detail="The last active owner cannot be removed")

    old_username = target.username
    username_changed = "username" in changed_fields and payload.username != old_username
    before = _editable_user_snapshot(target)
    for field_name in changed_fields:
        setattr(target, field_name, getattr(payload, field_name))
    if username_changed:
        target.previous_usernames = [*(target.previous_usernames or []), old_username]
        session.add(
            Event(
                created_at=utcnow(),
                type=EventType.USERNAME_CHANGE,
                user_id=target.id,
                event_payload={
                    "user": {
                        "username": target.username,
                        "url": settings.web_url + "users/" + str(target.id),
                        "previous_username": old_username,
                    }
                },
            )
        )
    if target.is_owner:
        target.is_admin = True
    if "is_supporter" in changed_fields:
        target.support_level = 1 if target.is_supporter else 0
        if target.is_supporter:
            target.has_supported = True
    after = _editable_user_snapshot(target)
    if before == after:
        raise HTTPException(status_code=422, detail="The supplied values do not change this account")
    target_id = target.id
    access_changed = target_is_elevated != bool(target.is_owner or target.is_admin)

    session.add(target)
    await _audit(
        session,
        request,
        context.user,
        action="user.update",
        target_type="user",
        target_id=target_id,
        reason=payload.reason,
        before=before,
        after=after,
    )
    if payload.is_active is False:
        await session.exec(delete(LoginSession).where(col(LoginSession.user_id) == target_id))
        await session.exec(delete(TrustedDevice).where(col(TrustedDevice.user_id) == target_id))
        await session.exec(delete(OAuthToken).where(col(OAuthToken.user_id) == target_id))
    try:
        await session.commit()
    except IntegrityError as exc:
        await session.rollback()
        if username_changed:
            raise HTTPException(status_code=409, detail="Username is already taken") from exc
        raise
    await session.refresh(target)
    try:
        cache_service = get_user_cache_service(redis)
        await cache_service.invalidate_user_all_cache(target_id)
        await cache_service.invalidate_v1_user_cache(target_id)
        if changed_fields & {"country_code", "is_active"} or username_changed:
            ranking_cache = get_ranking_cache_service(redis)
            await ranking_cache.invalidate_cache()
            await ranking_cache.invalidate_country_cache()
            if username_changed:
                await ranking_cache.invalidate_team_cache()
                await ranking_cache.invalidate_top_scores_cache()
    except Exception:
        # The account/audit transaction has already committed. Stale cache
        # expiry is better than reporting a false failure for a successful edit.
        logger.exception("Post-update cache invalidation failed for user {}", target_id)
    if payload.is_active is False or access_changed:
        await _invalidate_admin_sessions(redis, target_id)
    if payload.is_active is False:
        await invalidate_web_sessions(redis, target_id)
    if username_changed:
        try:
            hub.emit(UserRenamedEvent(user_id=target_id, old_username=old_username, new_username=target.username))
        except Exception:
            logger.exception("Post-update rename event failed for user {}", target_id)
    return _public_user(
        target,
        restricted=await target.is_restricted(session),
        include_email=actor_is_owner,
    )


@router.post("/admin-panel/users/{user_id}/password", tags=["Admin Panel"], include_in_schema=False)
async def reset_admin_user_password(
    user_id: int,
    payload: PasswordResetRequest,
    request: Request,
    context: AdminSession,
    session: Database,
    redis: Redis,
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    target = await session.get(User, user_id)
    if target is None or target.id == BANCHOBOT_ID or target.is_bot:
        raise HTTPException(status_code=404, detail="Editable user not found")
    target_id = target.id
    target.pw_bcrypt = get_password_hash(payload.new_password)
    session.add(target)
    await session.exec(delete(LoginSession).where(col(LoginSession.user_id) == target_id))
    await session.exec(delete(TrustedDevice).where(col(TrustedDevice.user_id) == target_id))
    await session.exec(delete(OAuthToken).where(col(OAuthToken.user_id) == target_id))
    await _audit(
        session,
        request,
        context.user,
        action="user.password_reset",
        target_type="user",
        target_id=target_id,
        reason=payload.reason,
        before=None,
        after={"sessions_revoked": True},
    )
    await session.commit()
    await _invalidate_admin_sessions(redis, target_id)
    await invalidate_web_sessions(redis, target_id)
    return {"ok": True, "message": "Password changed and existing sessions revoked"}


def _imported_score_payload(score: Score) -> dict[str, Any]:
    return {
        "id": score.id,
        "user_id": score.user_id,
        "beatmap_id": score.beatmap_id,
        "ruleset": score.gamemode.value,
        "rank": score.rank.value,
        "accuracy": score.accuracy,
        "pp": score.pp,
        "total_score": score.total_score,
        "max_combo": score.max_combo,
        "mods": score.mods,
        "ended_at": score.ended_at,
        "has_replay": bool(score.has_replay),
        "processed": bool(score.processed),
        "ranked": bool(score.ranked),
        "leaderboard_eligible": bool(score.leaderboard_eligible),
    }


def _score_import_provenance_payload(
    provenance: ScoreImport,
    usernames: dict[int, str],
) -> dict[str, Any]:
    target_username = usernames.get(provenance.target_user_id)
    importer_username = (
        usernames.get(provenance.imported_by_user_id) if provenance.imported_by_user_id is not None else None
    )
    return {
        "id": provenance.id,
        "score_id": provenance.score_id,
        "target_user": {"id": provenance.target_user_id, "username": target_username},
        "imported_by": (
            {"id": provenance.imported_by_user_id, "username": importer_username}
            if provenance.imported_by_user_id is not None
            else None
        ),
        "source": provenance.source,
        "source_fingerprint": provenance.source_fingerprint,
        "source_ruleset": provenance.source_ruleset,
        "source_score_id": provenance.source_score_id,
        "source_user_id": provenance.source_user_id,
        "source_username": provenance.source_username,
        "source_url": provenance.source_snapshot.get("source_url"),
        "revision_verification": provenance.source_snapshot.get("revision_verification"),
        "pp_pending": provenance.source_snapshot.get("pp_pending") is True,
        "reason": provenance.reason,
        "replay_imported": bool(provenance.replay_imported),
        "imported_at": provenance.imported_at,
    }


async def _prepare_score_import(payload: ScoreImportPreviewRequest, fetcher: Fetcher):
    try:
        reference = parse_official_score_reference(payload.source, payload.ruleset)
        return await fetch_official_score(fetcher, reference)
    except ScoreImportNotFoundError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except ScoreImportUpstreamError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc


async def _read_uploaded_replay(replay: UploadFile) -> tuple[bytes, str | None]:
    filename = replay.filename[:255] if replay.filename else None
    if filename is not None and not filename.lower().endswith(".osr"):
        raise HTTPException(status_code=422, detail="Choose a replay file with the .osr extension")
    try:
        content = await replay.read(MAX_REPLAY_BYTES + 1)
    finally:
        await replay.close()
    if len(content) > MAX_REPLAY_BYTES:
        raise HTTPException(status_code=413, detail="The uploaded replay exceeds the 32 MiB safety limit")
    try:
        parse_uploaded_osr(content)
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    return content, filename


async def _prepare_uploaded_score_import(
    content: bytes,
    filename: str | None,
    fetcher: Fetcher,
    *,
    fallback_beatmap_id: int | None,
) -> PreparedOfficialScore:
    parsed = parse_uploaded_osr(content)
    try:
        try:
            beatmap = await fetcher.get_beatmap(beatmap_checksum=parsed.header.beatmap_checksum)
        except HTTPStatusError as exc:
            if exc.response.status_code != 404 or fallback_beatmap_id is None:
                raise
            beatmap = await fetcher.get_beatmap(beatmap_id=fallback_beatmap_id)
        if fallback_beatmap_id is not None and int(beatmap["id"]) != fallback_beatmap_id:
            raise ScoreImportError("The supplied beatmap ID does not match the replay")
        return prepare_uploaded_osr_score(content, beatmap, filename=filename)
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            detail = "The replay beatmap was not found; enter its difficulty ID and try again"
            raise HTTPException(status_code=422, detail=detail) from exc
        raise HTTPException(
            status_code=503,
            detail=f"Official osu! beatmap lookup returned HTTP {exc.response.status_code}",
        ) from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Could not reach the official osu! beatmap API") from exc
    except TokenAuthError as exc:
        raise HTTPException(status_code=503, detail="Official osu! API credentials were rejected") from exc
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except (KeyError, TypeError, ValueError) as exc:
        raise HTTPException(status_code=422, detail="Official beatmap metadata is invalid") from exc


async def _refresh_score_import_beatmap(prepared: PreparedOfficialScore, fetcher: Fetcher) -> tuple[int, str]:
    """Refresh the complete upstream set and return the resulting local checksum."""

    try:
        beatmapset_id = prepared.beatmapset_id
        if beatmapset_id is None:
            upstream_beatmap = await fetcher.get_beatmap(prepared.beatmap_id)
            raw_beatmapset_id = upstream_beatmap.get("beatmapset_id")
            if raw_beatmapset_id is None:
                raise ValueError("beatmapset_id is required")
            beatmapset_id = int(raw_beatmapset_id)
            prepared.beatmapset_id = beatmapset_id
        snapshot = await get_beatmapset_update_service().refresh_beatmapset(beatmapset_id)
        refreshed = next((item for item in snapshot["beatmaps"] if item["id"] == prepared.beatmap_id), None)
        if refreshed is None:
            raise ScoreImportError("The difficulty no longer belongs to its official beatmapset")
        async with with_db() as metadata_session:
            beatmap = await metadata_session.get(Beatmap, prepared.beatmap_id)
            if beatmap is None or beatmap.beatmapset_id != beatmapset_id or beatmap.deleted_at is not None:
                raise ScoreImportError("The refreshed beatmap is unavailable locally")
            local_checksum = beatmap.checksum.lower()
        if local_checksum != str(refreshed["checksum"]).lower():
            raise ScoreImportUpstreamError("The refreshed beatmap checksum was not applied locally")
        return beatmapset_id, local_checksum
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            raise HTTPException(status_code=422, detail="The beatmap for this official score was not found") from exc
        raise HTTPException(
            status_code=503,
            detail=f"Official osu! beatmap lookup returned HTTP {exc.response.status_code}",
        ) from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Could not reach the official osu! beatmap API") from exc
    except TokenAuthError as exc:
        raise HTTPException(status_code=503, detail="Official osu! API credentials were rejected") from exc
    except ScoreImportUpstreamError as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except (KeyError, ValueError) as exc:
        raise HTTPException(status_code=422, detail="Official beatmap metadata is invalid") from exc


def _revision_warnings(
    prepared: PreparedOfficialScore,
    verification: ScoreRevisionVerification,
    *,
    allow_unverified_revision: bool,
) -> list[str]:
    warnings: list[str] = []
    if prepared.source_checksum is None:
        warnings.append("The score response does not include a current beatmap metadata checksum")
    elif not verification.current_metadata_matches:
        warnings.append("The score response references different current beatmap metadata")

    match verification.replay_status:
        case "not_advertised":
            warnings.append("The official score has no replay, so its played beatmap revision cannot be verified")
        case "unavailable":
            warnings.append("The official replay could not be downloaded, so its beatmap revision is unverified")
        case "too_large":
            warnings.append("The official replay exceeds the 32 MiB safety limit")
        case "empty" | "invalid":
            warnings.append("The official replay is empty or invalid and cannot verify the beatmap revision")
        case "ruleset_mismatch":
            warnings.append("The replay ruleset does not match the official score")
        case "checksum_mismatch":
            warnings.append("The replay was played on a different beatmap revision")

    if not verification.revision_verified_for(prepared):
        if allow_unverified_revision:
            warnings.append("Owner override enabled: normal local ranking rules will use the current map revision")
        else:
            warnings.append("The score will stay visible, but will not enter leaderboards or award PP")
    return warnings


def _revision_checks(
    prepared: PreparedOfficialScore,
    verification: ScoreRevisionVerification,
    *,
    allow_unverified_revision: bool,
) -> dict[str, Any]:
    snapshot = verification.snapshot(prepared, allow_unverified_revision=allow_unverified_revision)
    return {
        # Keep the original field for the existing admin UI. It only compares
        # current API metadata and is deliberately not treated as proof.
        "checksum_matches": verification.current_metadata_matches,
        **snapshot,
        "warnings": _revision_warnings(
            prepared,
            verification,
            allow_unverified_revision=allow_unverified_revision,
        ),
    }


async def _delete_owned_import_replay(
    storage: StorageService,
    replay_path: str,
    expected_sha256: str,
) -> None:
    try:
        content = await storage.read_file(replay_path)
        actual_sha256 = hashlib.sha256(content).hexdigest()
        if not secrets.compare_digest(actual_sha256, expected_sha256):
            logger.error("Refusing to remove non-matching replay at {}", replay_path)
            return
        await storage.delete_file(replay_path)
    except FileNotFoundError:
        return
    except Exception:
        logger.exception("Failed to remove an unfinished official replay import")


async def _import_replay_metadata_is_committed(*, score_id: int, provenance_id: int) -> bool:
    async with with_db() as session:
        score = await session.get(Score, score_id)
        provenance = await session.get(ScoreImport, provenance_id)
        return bool(
            score is not None
            and provenance is not None
            and provenance.score_id == score_id
            and score.has_replay
            and provenance.replay_imported
        )


async def _finalise_import_replay(
    storage: StorageService,
    *,
    score_id: int,
    provenance_id: int,
    replay_path: str,
    replay_content: bytes,
) -> Literal["stored", "not_retained", "unavailable"]:
    """Publish a committed import under the same replay policy as client uploads."""

    replay_sha256 = hashlib.sha256(replay_content).hexdigest()
    write_started = False
    commit_started = False
    superseded_paths: tuple[str, ...] = ()
    try:
        if await storage.is_exists(replay_path):
            logger.error("Refusing to overwrite existing replay path {}", replay_path)
            return "unavailable"
        async with with_db() as session:
            locator = await session.get(Score, score_id)
            if locator is None:
                raise RuntimeError("Imported score disappeared before replay finalization")
            user_id, beatmap_id = locator.user_id, locator.beatmap_id
            # End the discovery snapshot before acquiring the same ordered
            # score locks used by spectator uploads, then refresh the rows.
            await session.rollback()
            candidates = await lock_replay_candidates(session, user_id, beatmap_id)
            score = next((candidate for candidate in candidates if candidate.id == score_id), None)
            provenance = await session.get(ScoreImport, provenance_id, with_for_update=True)
            if score is None or provenance is None or provenance.score_id != score_id:
                raise RuntimeError("Imported score disappeared before replay finalization")
            if score.replay_filename != replay_path:
                raise RuntimeError("Imported replay path does not match its score")
            winner = best_replay_score(candidates, score)
            if winner is None or winner.id != score.id:
                return "not_retained"
            superseded = [
                candidate for candidate in replay_combination_scores(candidates, score) if candidate.id != score.id
            ]
            superseded_paths = tuple(candidate.replay_filename for candidate in superseded)
            # Recheck under the score locks: a concurrent client upload may
            # have published this exact score after the optimistic check.
            if await storage.is_exists(replay_path):
                return "unavailable"
            write_started = True
            await storage.write_file(replay_path, replay_content, "application/x-osu-replay")
            score.has_replay = True
            provenance.replay_imported = True
            session.add(score)
            session.add(provenance)
            for previous in superseded:
                if previous.has_replay:
                    previous.has_replay = False
                    session.add(previous)
            commit_started = True
            await session.commit()
    except BaseException as exc:
        metadata_committed = False
        if commit_started:
            try:
                metadata_committed = await asyncio.shield(
                    _import_replay_metadata_is_committed(score_id=score_id, provenance_id=provenance_id)
                )
            except BaseException:
                # The commit outcome is unknown. Keep the file rather than
                # risk leaving a committed has_replay row pointing at nothing.
                metadata_committed = True
                logger.exception("Could not verify replay metadata after finalization failure")
        if not metadata_committed and write_started:
            try:
                await asyncio.shield(
                    _delete_owned_import_replay(
                        storage,
                        replay_path,
                        replay_sha256,
                    )
                )
            except BaseException:
                logger.exception("Replay cleanup was interrupted for imported score {}", score_id)
        if isinstance(exc, Exception):
            logger.exception("Failed to finalize replay for imported score {}", score_id)
            return "stored" if metadata_committed else "unavailable"
        raise
    # The replacement and its availability are committed before old files
    # disappear. A cleanup failure must not undo a successful publication.
    for superseded_path in superseded_paths:
        try:
            await storage.delete_file(superseded_path)
        except Exception:
            logger.exception("Failed to remove superseded replay {}", superseded_path)
    return "stored"


@router.post("/admin-panel/score-imports/preview", tags=["Admin Panel"], include_in_schema=False)
async def preview_admin_score_import(
    payload: ScoreImportPreviewRequest,
    request: Request,
    context: AdminSession,
    fetcher: Fetcher,
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    prepared = await _prepare_score_import(payload, fetcher)
    beatmapset_id, current_checksum = await _refresh_score_import_beatmap(prepared, fetcher)
    try:
        verification = await verify_official_score_revision(
            fetcher,
            prepared,
            beatmapset_id=beatmapset_id,
            current_checksum=current_checksum,
        )
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    async with with_db() as check_session:
        duplicate = (
            await check_session.exec(
                select(ScoreImport).where(ScoreImport.source_fingerprint == prepared.source_fingerprint)
            )
        ).first()
    checks = _revision_checks(
        prepared,
        verification,
        allow_unverified_revision=payload.allow_unverified_revision,
    )
    if duplicate is not None:
        checks["warnings"].append("This official score has already been imported")
    checks.update(
        {
            "duplicate": duplicate is not None,
            "existing_score_id": duplicate.score_id if duplicate is not None else None,
            "existing_target_user_id": duplicate.target_user_id if duplicate is not None else None,
        }
    )
    return {
        "score": prepared.public_preview(),
        "checks": checks,
        # An unverified score is still safe to import because the default
        # outcome is a visible, non-ranked history entry.
        "can_import": duplicate is None,
    }


@router.post("/admin-panel/score-imports/osr/preview", tags=["Admin Panel"], include_in_schema=False)
async def preview_admin_osr_import(
    request: Request,
    context: AdminSession,
    fetcher: Fetcher,
    replay: Annotated[UploadFile, File(...)],
    beatmap_id: Annotated[int | None, Form(gt=0)] = None,
    allow_unverified_revision: Annotated[Literal["true", "false"], Form()] = "false",
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    content, filename = await _read_uploaded_replay(replay)
    prepared = await _prepare_uploaded_score_import(
        content,
        filename,
        fetcher,
        fallback_beatmap_id=beatmap_id,
    )
    beatmapset_id, current_checksum = await _refresh_score_import_beatmap(prepared, fetcher)
    verification = verify_uploaded_score_revision(
        prepared,
        content,
        beatmapset_id=beatmapset_id,
        current_checksum=current_checksum,
    )
    allow_override = allow_unverified_revision == "true"
    async with with_db() as check_session:
        duplicate = (
            await check_session.exec(
                select(ScoreImport).where(ScoreImport.source_fingerprint == prepared.source_fingerprint)
            )
        ).first()
    checks = _revision_checks(
        prepared,
        verification,
        allow_unverified_revision=allow_override,
    )
    if duplicate is not None:
        checks["warnings"].append("This replay has already been imported")
    checks.update(
        {
            "duplicate": duplicate is not None,
            "existing_score_id": duplicate.score_id if duplicate is not None else None,
            "existing_target_user_id": duplicate.target_user_id if duplicate is not None else None,
        }
    )
    return {"score": prepared.public_preview(), "checks": checks, "can_import": duplicate is None}


@router.post(
    "/admin-panel/score-imports/osr",
    status_code=status.HTTP_201_CREATED,
    tags=["Admin Panel"],
    include_in_schema=False,
)
async def create_admin_osr_import(
    request: Request,
    context: AdminSession,
    redis: Redis,
    fetcher: Fetcher,
    storage: StorageService,
    replay: Annotated[UploadFile, File(...)],
    target_user_id: Annotated[int, Form(gt=0)],
    reason: Annotated[str, Form(max_length=500)] = "no reason",
    beatmap_id: Annotated[int | None, Form(gt=0)] = None,
    allow_unverified_revision: Annotated[Literal["true", "false"], Form()] = "false",
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    content, filename = await _read_uploaded_replay(replay)
    prepared = await _prepare_uploaded_score_import(
        content,
        filename,
        fetcher,
        fallback_beatmap_id=beatmap_id,
    )
    beatmapset_id, current_checksum = await _refresh_score_import_beatmap(prepared, fetcher)
    verification = verify_uploaded_score_revision(
        prepared,
        content,
        beatmapset_id=beatmapset_id,
        current_checksum=current_checksum,
    )
    clean_reason = reason.strip() or "no reason"
    return await _materialise_admin_score_import(
        request,
        context,
        redis,
        fetcher,
        storage,
        prepared=prepared,
        verification=verification,
        target_id=target_user_id,
        reason=clean_reason,
        include_replay=True,
        allow_unverified_revision=allow_unverified_revision == "true",
    )


@router.post(
    "/admin-panel/score-imports",
    status_code=status.HTTP_201_CREATED,
    tags=["Admin Panel"],
    include_in_schema=False,
)
async def create_admin_score_import(
    payload: ScoreImportRequest,
    request: Request,
    context: AdminSession,
    redis: Redis,
    fetcher: Fetcher,
    storage: StorageService,
):
    _require_csrf(request, context)
    _require_capability(context, "owner")
    prepared = await _prepare_score_import(payload, fetcher)
    beatmapset_id, current_checksum = await _refresh_score_import_beatmap(prepared, fetcher)
    try:
        verification = await verify_official_score_revision(
            fetcher,
            prepared,
            beatmapset_id=beatmapset_id,
            current_checksum=current_checksum,
        )
    except ScoreImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    return await _materialise_admin_score_import(
        request,
        context,
        redis,
        fetcher,
        storage,
        prepared=prepared,
        verification=verification,
        target_id=payload.target_user_id,
        reason=payload.reason,
        include_replay=payload.include_replay,
        allow_unverified_revision=payload.allow_unverified_revision,
    )


async def _materialise_admin_score_import(
    request: Request,
    context: AdminSession,
    redis: Redis,
    fetcher: Fetcher,
    storage: StorageService,
    *,
    prepared: PreparedOfficialScore,
    verification: ScoreRevisionVerification,
    target_id: int,
    reason: str,
    include_replay: bool,
    allow_unverified_revision: bool,
):
    reason = reason.strip() or "no reason"
    actor_id = context.user.id
    async with with_db() as import_session:
        actor = await import_session.get(User, actor_id)
        if actor is None or not actor.is_active or not actor.is_owner or await actor.is_restricted(import_session):
            raise HTTPException(status_code=403, detail="Owner access required")
        target = await resolve_human_user(import_session, target_id, for_update=True)
        if target is None or target.is_bot:
            raise HTTPException(status_code=404, detail="Target user not found")
        if not target.is_active:
            raise HTTPException(status_code=422, detail="Scores cannot be imported into an inactive account")
        if await target.is_restricted(import_session):
            raise HTTPException(status_code=422, detail="Scores cannot be imported into a restricted account")
        actor_username = actor.username
        target_username = target.username

        try:
            imported = await import_official_score(
                import_session,
                redis,
                fetcher,
                target_user=target,
                imported_by=actor,
                prepared=prepared,
                verification=verification,
                reason=reason,
                include_replay=include_replay,
                allow_unverified_revision=allow_unverified_revision,
                commit=False,
            )
            score = imported.score
            provenance = imported.provenance
            verification_snapshot = provenance.source_snapshot["revision_verification"]
            await _audit(
                import_session,
                request,
                actor,
                action="score.import",
                target_type="score",
                target_id=score.id,
                reason=reason,
                before=None,
                after={
                    "source": provenance.source,
                    "source_fingerprint": provenance.source_fingerprint,
                    "source_ruleset": provenance.source_ruleset,
                    "source_score_id": provenance.source_score_id,
                    "target_user_id": provenance.target_user_id,
                    "revision_verification": verification_snapshot,
                    "replay_requested": include_replay,
                    "replay_ready_for_finalization": imported.replay_content is not None,
                    "pp_pending": imported.pp_pending,
                    "ranked": bool(score.ranked),
                    "leaderboard_eligible": bool(score.leaderboard_eligible),
                },
            )
            await import_session.commit()
        except ScoreImportConflictError as exc:
            await import_session.rollback()
            raise HTTPException(status_code=409, detail=str(exc)) from exc
        except ScoreImportError as exc:
            await import_session.rollback()
            raise HTTPException(status_code=422, detail=str(exc)) from exc
        except IntegrityError as exc:
            await import_session.rollback()
            duplicate = (
                await import_session.exec(
                    select(ScoreImport.id).where(
                        ScoreImport.source_fingerprint == prepared.source_fingerprint,
                    )
                )
            ).first()
            if duplicate is not None:
                raise HTTPException(status_code=409, detail="This official score has already been imported") from exc
            raise
        except Exception:
            await import_session.rollback()
            raise

        await import_session.refresh(score)
        await import_session.refresh(provenance)
        score_id = score.id
        provenance_id = provenance.id
        if score_id is None or provenance_id is None:
            raise RuntimeError("Committed score import has no primary key")
        replay_path = score.replay_filename
        score_mode = score.gamemode

    replay_status = "unavailable"
    if imported.replay_content is not None:
        replay_status = await _finalise_import_replay(
            storage,
            score_id=score_id,
            provenance_id=provenance_id,
            replay_path=replay_path,
            replay_content=imported.replay_content,
        )
    replay_unavailable = imported.replay_unavailable or (
        imported.replay_content is not None and replay_status == "unavailable"
    )

    pp_retry_queued = False
    if imported.pp_pending:
        try:
            # Keep task imports out of the router import graph. The application
            # has initialized scheduled tasks before this endpoint can run.
            from app.tasks.recalculate_failed_score import enqueue_missing_score_retries

            await enqueue_missing_score_retries(redis, [score_id])
            pp_retry_queued = True
        except Exception:
            logger.exception("Failed to enqueue PP retry for imported score {}", score_id)

    async with with_db() as response_session:
        response_score = await response_session.get(Score, score_id)
        response_provenance = await response_session.get(ScoreImport, provenance_id)
        if response_score is None or response_provenance is None:
            raise RuntimeError("Committed score import disappeared")
        score_payload = _imported_score_payload(response_score)
        provenance_payload = _score_import_provenance_payload(
            response_provenance,
            {actor_id: actor_username, target_id: target_username},
        )

    try:
        user_cache = get_user_cache_service(redis)
        await user_cache.invalidate_user_all_cache(target_id)
        await user_cache.invalidate_v1_user_cache(target_id)
        ranking_cache = get_ranking_cache_service(redis)
        await ranking_cache.invalidate_cache(score_mode)
        await ranking_cache.invalidate_country_cache(score_mode)
        await ranking_cache.invalidate_team_cache(score_mode)
        await ranking_cache.invalidate_top_scores_cache(score_mode)
    except Exception:
        # The score and its audit record are already committed. Cache expiry is
        # preferable to returning a misleading failure for a completed import.
        logger.exception("Post-import cache invalidation failed for score {}", score_id)
    return {
        "score": score_payload,
        "provenance": provenance_payload,
        "replay_unavailable": replay_unavailable,
        "replay_not_retained": replay_status == "not_retained",
        "pp_pending": imported.pp_pending,
        "pp_retry_queued": pp_retry_queued,
    }


@router.get("/admin-panel/score-imports", tags=["Admin Panel"], include_in_schema=False)
async def list_admin_score_imports(
    context: AdminSession,
    session: Database,
    target_user_id: Annotated[int | None, Query(gt=0)] = None,
    limit: Annotated[int, Query(ge=1, le=200)] = 50,
    offset: Annotated[int, Query(ge=0)] = 0,
):
    _require_capability(context, "owner")
    filters: list[Any] = [ScoreImport.source == "official_osu"]
    if target_user_id is not None:
        target = await resolve_human_user(session, target_user_id)
        if target is None:
            return {"items": [], "total": 0}
        filters.append(ScoreImport.target_user_id == target.id)
    total = int((await session.exec(select(func.count(col(ScoreImport.id))).where(*filters))).one())
    rows = (
        await session.exec(
            select(ScoreImport, Score)
            .join(Score, col(Score.id) == col(ScoreImport.score_id), isouter=True)
            .where(*filters)
            .order_by(col(ScoreImport.imported_at).desc(), col(ScoreImport.id).desc())
            .offset(offset)
            .limit(limit)
        )
    ).all()
    user_ids = {
        user_id
        for provenance, _score in rows
        for user_id in (provenance.target_user_id, provenance.imported_by_user_id)
        if user_id is not None
    }
    user_rows = (
        (await session.exec(select(User.id, User.username).where(col(User.id).in_(user_ids)))).all() if user_ids else []
    )
    usernames = dict(user_rows)
    return {
        "items": [
            {
                "score": _imported_score_payload(score) if score is not None else None,
                "provenance": _score_import_provenance_payload(provenance, usernames),
            }
            for provenance, score in rows
        ],
        "total": total,
    }


async def _refresh_ranking_target(beatmapset_id: int, beatmap_id: int | None) -> None:
    try:
        snapshot = await get_beatmapset_update_service().refresh_beatmapset(beatmapset_id)
    except HTTPStatusError as exc:
        error = ErrorType.BEATMAP_NOT_FOUND if beatmap_id is not None else ErrorType.BEATMAPSET_NOT_FOUND
        if exc.response.status_code == 404:
            raise RequestError(error) from exc
        raise HTTPException(status_code=503, detail="Official beatmap metadata is unavailable") from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Official beatmap metadata is unavailable") from exc
    if beatmap_id is not None and not any(item["id"] == beatmap_id for item in snapshot["beatmaps"]):
        raise HTTPException(status_code=422, detail="Difficulty does not belong to this beatmapset")


def _ranking_policy_payload(policy: BeatmapsetRankingPolicy | BeatmapRankingPolicy | None) -> dict[str, Any] | None:
    if policy is None:
        return None
    result: dict[str, Any] = {
        "status": int(policy.status),
        "leaderboard_enabled": policy.leaderboard_enabled,
        "pp_enabled": policy.pp_enabled,
        "force_unranked": policy.force_unranked,
        "is_active": policy.is_active,
        "reason": policy.reason,
        "created_by_user_id": policy.created_by_user_id,
        "updated_by_user_id": policy.updated_by_user_id,
        "created_at": policy.created_at,
        "updated_at": policy.updated_at,
    }
    if isinstance(policy, BeatmapRankingPolicy):
        result.update(
            beatmap_id=policy.beatmap_id,
            blocks_set_policy=policy.blocks_set_policy,
            ranked_checksum=policy.ranked_checksum,
            invalidated_at=policy.invalidated_at,
            invalidation_reason=policy.invalidation_reason,
        )
    else:
        result["revision_manifest"] = policy.revision_manifest
    return result


async def _ranking_beatmapset_payload(session: Database, beatmapset_id: int) -> dict[str, Any]:
    beatmapset = await session.get(Beatmapset, beatmapset_id)
    if beatmapset is None:
        raise HTTPException(status_code=404, detail="Beatmapset is not cached yet; refresh it first")

    beatmaps = list(
        (
            await session.exec(
                select(Beatmap)
                .where(Beatmap.beatmapset_id == beatmapset_id)
                .order_by(col(Beatmap.difficulty_rating), col(Beatmap.id))
            )
        ).all()
    )
    effective = await get_effective_beatmap_policies(session, beatmaps)
    effective_set = await get_effective_beatmapset_policy(session, beatmapset)
    set_policy = await session.get(BeatmapsetRankingPolicy, beatmapset_id)
    difficulty_policies = (
        await session.exec(select(BeatmapRankingPolicy).where(BeatmapRankingPolicy.beatmapset_id == beatmapset_id))
    ).all()
    policy_by_beatmap = {policy.beatmap_id: policy for policy in difficulty_policies}
    pending_rows = (
        await session.exec(
            select(col(BeatmapRankingEvent.beatmap_id), func.count(col(BeatmapRankingEvent.id)))
            .where(
                col(BeatmapRankingEvent.beatmapset_id) == beatmapset_id,
                col(BeatmapRankingEvent.resolved_at).is_(None),
            )
            .group_by(col(BeatmapRankingEvent.beatmap_id))
        )
    ).all()
    pending_by_beatmap = dict(pending_rows)
    sync = await session.get(BeatmapSync, beatmapset_id)

    return {
        "beatmapset": {
            "id": beatmapset.id,
            "artist": beatmapset.artist,
            "title": beatmapset.title,
            "creator": beatmapset.creator,
            "upstream_status": int(beatmapset.beatmap_status),
            "last_updated": beatmapset.last_updated,
            "effective": {
                "status": int(effective_set.status),
                "source": str(effective_set.source),
                "leaderboard_enabled": effective_set.leaderboard_enabled,
                "pp_enabled": effective_set.pp_enabled,
                "locally_ranked_difficulty_count": effective_set.locally_ranked_difficulty_count,
            },
            "policy": _ranking_policy_payload(set_policy),
        },
        "difficulties": [
            {
                "id": beatmap.id,
                "version": beatmap.version,
                "mode": str(beatmap.mode),
                "stars": beatmap.difficulty_rating,
                "checksum": beatmap.checksum,
                "last_updated": beatmap.last_updated,
                "deleted": beatmap.deleted_at is not None,
                "upstream_status": int(beatmap.beatmap_status),
                "effective": {
                    "status": int(effective[beatmap.id].status),
                    "source": str(effective[beatmap.id].source),
                    "leaderboard_enabled": effective[beatmap.id].leaderboard_enabled,
                    "pp_enabled": effective[beatmap.id].pp_enabled,
                },
                "policy": _ranking_policy_payload(policy_by_beatmap.get(beatmap.id)),
                "pending_event_count": pending_by_beatmap.get(beatmap.id, 0),
            }
            for beatmap in beatmaps
        ],
        "sync": (
            {
                "consecutive_no_change": sync.consecutive_no_change,
                "next_sync_time": sync.next_sync_time,
                "updated_at": sync.updated_at,
            }
            if sync is not None
            else None
        ),
    }


@router.get("/admin-panel/ranking/beatmapsets/{beatmapset_id}", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_ranking_beatmapset(beatmapset_id: int, context: AdminSession, session: Database):
    _require_capability(context, "ranker")
    return await _ranking_beatmapset_payload(session, beatmapset_id)


@router.post(
    "/admin-panel/ranking/beatmapsets/{beatmapset_id}/refresh",
    tags=["Admin Panel"],
    include_in_schema=False,
)
async def refresh_admin_ranking_beatmapset(
    beatmapset_id: int,
    request: Request,
    context: AdminSession,
    session: Database,
):
    _require_csrf(request, context)
    _require_capability(context, "ranker")
    await _refresh_ranking_target(beatmapset_id, None)
    await session.rollback()
    return await _ranking_beatmapset_payload(session, beatmapset_id)


@router.post("/admin-panel/ranking", tags=["Admin Panel"], include_in_schema=False)
async def mutate_admin_ranking(
    payload: RankingMutationRequest,
    request: Request,
    context: AdminSession,
):
    _require_csrf(request, context)
    _require_capability(context, "ranker")
    actor_user_id = context.user.id
    if payload.action == "rank":
        await _refresh_ranking_target(payload.beatmapset_id, payload.beatmap_id)
        rank_status = BeatmapRankStatus(payload.status)
        leaderboard = payload.leaderboard_enabled
        pp = payload.pp_enabled
        if leaderboard is None:
            leaderboard = rank_status.has_leaderboard()
        if pp is None:
            pp = rank_status.has_pp()
        try:
            async with with_db() as mutation_session:
                await apply_local_rank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=payload.beatmapset_id,
                    beatmap_id=payload.beatmap_id,
                    status=rank_status,
                    leaderboard_enabled=leaderboard,
                    pp_enabled=pp,
                    reason=payload.reason,
                )
        except (LookupError, ValueError) as exc:
            raise HTTPException(status_code=422, detail=str(exc)) from exc
        return {"ok": True, "action": "rank", "status": int(rank_status)}

    try:
        async with with_db() as mutation_session:
            if payload.action == "unrank":
                await apply_local_unrank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=payload.beatmapset_id,
                    beatmap_id=payload.beatmap_id,
                    reason=payload.reason,
                )
            else:
                await clear_local_rank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=payload.beatmapset_id,
                    beatmap_id=payload.beatmap_id,
                    reason=payload.reason,
                )
    except (LookupError, ValueError) as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    if payload.action == "unrank":
        async with with_db() as state_session:
            beatmaps = list(
                (await state_session.exec(select(Beatmap).where(Beatmap.beatmapset_id == payload.beatmapset_id))).all()
            )
            effective_by_id = await get_effective_beatmap_policies(state_session, beatmaps)
            if payload.beatmap_id is not None:
                effective = effective_by_id.get(payload.beatmap_id)
                if effective is None:
                    raise HTTPException(status_code=404, detail="Beatmap not found")
                response_status = effective.status
                leaderboard_enabled = effective.leaderboard_enabled
                pp_enabled = effective.pp_enabled
                force_unranked = effective.source.value != "upstream" and not effective.leaderboard_enabled
            else:
                beatmapset = await state_session.get(Beatmapset, payload.beatmapset_id)
                if beatmapset is None:
                    raise HTTPException(status_code=404, detail="Beatmapset not found")
                effective_set = await get_effective_beatmapset_policy(state_session, beatmapset)
                response_status = effective_set.status
                leaderboard_enabled = effective_set.leaderboard_enabled
                pp_enabled = effective_set.pp_enabled
                force_unranked = effective_set.source.value != "upstream" and not effective_set.leaderboard_enabled
        return {
            "ok": True,
            "action": "unrank",
            "status": int(response_status),
            "leaderboard_enabled": leaderboard_enabled,
            "pp_enabled": pp_enabled,
            "force_unranked": force_unranked,
        }
    return {"ok": True, "action": "inherit"}


@router.get("/admin-panel/ranking/policies", tags=["Admin Panel"], include_in_schema=False)
async def list_admin_ranking_policies(
    context: AdminSession,
    session: Database,
    limit: Annotated[int, Query(ge=1, le=300)] = 100,
):
    _require_capability(context, "ranker")
    set_policies = (
        await session.exec(
            select(BeatmapsetRankingPolicy)
            .where(col(BeatmapsetRankingPolicy.is_active).is_(True))
            .order_by(col(BeatmapsetRankingPolicy.updated_at).desc())
            .limit(limit)
        )
    ).all()
    diff_policies = (
        await session.exec(
            select(BeatmapRankingPolicy)
            .where(col(BeatmapRankingPolicy.is_active).is_(True))
            .order_by(col(BeatmapRankingPolicy.updated_at).desc())
            .limit(limit)
        )
    ).all()
    set_ids = {item.beatmapset_id for item in set_policies} | {item.beatmapset_id for item in diff_policies}
    beatmapsets = (
        (await session.exec(select(Beatmapset).where(col(Beatmapset.id).in_(set_ids)))).all() if set_ids else []
    )
    metadata = {item.id: {"artist": item.artist, "title": item.title, "creator": item.creator} for item in beatmapsets}
    return {
        "beatmapsets": [
            {
                "scope": "beatmapset",
                "beatmapset_id": item.beatmapset_id,
                "beatmap_id": None,
                "status": int(item.status),
                "leaderboard_enabled": item.leaderboard_enabled,
                "pp_enabled": item.pp_enabled,
                "force_unranked": item.force_unranked,
                "reason": item.reason,
                "updated_at": item.updated_at,
                "metadata": metadata.get(item.beatmapset_id),
            }
            for item in set_policies
        ],
        "difficulties": [
            {
                "scope": "beatmap",
                "beatmapset_id": item.beatmapset_id,
                "beatmap_id": item.beatmap_id,
                "status": int(item.status),
                "leaderboard_enabled": item.leaderboard_enabled,
                "pp_enabled": item.pp_enabled,
                "force_unranked": item.force_unranked,
                "blocks_set_policy": item.blocks_set_policy,
                "reason": item.reason,
                "updated_at": item.updated_at,
                "metadata": metadata.get(item.beatmapset_id),
            }
            for item in diff_policies
        ],
    }


@router.get("/admin-panel/ranking/events", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_ranking_events(
    context: AdminSession,
    session: Database,
    include_resolved: bool = False,
    limit: Annotated[int, Query(ge=1, le=200)] = 100,
):
    _require_capability(context, "ranker")
    if include_resolved:
        events = (
            await session.exec(
                select(BeatmapRankingEvent).order_by(col(BeatmapRankingEvent.created_at).desc()).limit(limit)
            )
        ).all()
    else:
        events = await list_pending_ranking_events(session, limit=limit, offset=0)
    return {
        "items": [
            {
                "id": item.id,
                "event_type": str(item.event_type),
                "scope": str(item.scope),
                "beatmapset_id": item.beatmapset_id,
                "beatmap_id": item.beatmap_id,
                "reason": item.reason,
                "old_checksum": item.old_checksum,
                "new_checksum": item.new_checksum,
                "created_at": item.created_at,
                "resolved_at": item.resolved_at,
                "resolved_by_user_id": item.resolved_by_user_id,
            }
            for item in events
        ]
    }


@router.post("/admin-panel/ranking/events/{event_id}/resolve", tags=["Admin Panel"], include_in_schema=False)
async def resolve_admin_ranking_event(
    event_id: int,
    payload: ResolveRankingEventRequest,
    request: Request,
    context: AdminSession,
    session: Database,
):
    _require_csrf(request, context)
    _require_capability(context, "ranker")
    event = await session.get(BeatmapRankingEvent, event_id)
    if event is None:
        raise HTTPException(status_code=404, detail="Ranking event not found")
    if event.resolved_at is None:
        await resolve_ranking_event(
            session,
            event_id=event_id,
            actor_user_id=context.user.id,
            reason=payload.reason,
        )
    return {"ok": True, "reason": payload.reason}


@router.get("/admin-panel/audit", tags=["Admin Panel"], include_in_schema=False)
async def get_admin_audit(
    context: AdminSession,
    session: Database,
    limit: Annotated[int, Query(ge=1, le=300)] = 100,
):
    _require_capability(context, "administrator")
    actor_is_owner = _role_flags(context.user)["owner"]
    admin_events = (
        await session.exec(select(AdminAuditEvent).order_by(col(AdminAuditEvent.created_at).desc()).limit(limit))
    ).all()
    ranking_events = (
        await session.exec(
            select(BeatmapRankingAudit).order_by(col(BeatmapRankingAudit.created_at).desc()).limit(limit)
        )
    ).all()
    return {
        "account": [
            {
                "id": item.id,
                "actor_user_id": item.actor_user_id,
                "actor_username": item.actor_username,
                "action": item.action,
                "target_type": item.target_type,
                "target_id": item.target_id,
                "reason": item.reason,
                "before": item.before,
                "after": item.after,
                "ip_address": item.ip_address if actor_is_owner else None,
                "created_at": item.created_at,
            }
            for item in admin_events
        ],
        "ranking": [
            {
                "id": item.id,
                "actor_user_id": item.actor_user_id,
                "action": str(item.action),
                "scope": str(item.scope),
                "beatmapset_id": item.beatmapset_id,
                "beatmap_id": item.beatmap_id,
                "reason": item.reason,
                "before": item.before,
                "after": item.after,
                "created_at": item.created_at,
            }
            for item in ranking_events
        ],
    }
