"""Same-origin backend-for-frontend for the public private-server website."""

from __future__ import annotations

import base64
from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
import io
import json
import math
import re
import secrets
from typing import Annotated, Any, Literal
from urllib.parse import urlsplit

from app.auth import (
    authenticate_user,
    check_totp_backup_code,
    get_password_hash,
    validate_password,
    validate_username,
    verify_totp_key_with_replay_protection,
)
from app.config import settings
from app.const import BACKUP_CODE_LENGTH
from app.database import (
    AdminAuditEvent,
    Beatmap,
    BeatmapPlaycounts,
    BeatmapRankingPolicy,
    Beatmapset,
    BeatmapsetRankingPolicy,
    BestScore,
    DailyChallengeStats,
    ItemAttemptsCount,
    LoginSession,
    MonthlyPlaycounts,
    OAuthToken,
    Playlist,
    RankHistory,
    Relationship,
    RelationshipType,
    Room,
    Score,
    ScoreImport,
    SearchBeatmapsetsResp,
    Team,
    TeamMember,
    TotpKeys,
    TrustedDevice,
    User,
    UserAchievement,
    UserRecoveryWord,
    UserStatistics,
)
from app.database.somsai import SomsaiRating
from app.database.statistics import get_rank, has_ranked_pp
from app.database.user import COUNTRIES
from app.database.user_preference import DEFAULT_ORDER, UserPreference
from app.dependencies.beatmap_download import DownloadService
from app.dependencies.cache import UserCacheService
from app.dependencies.database import Database, Redis, with_db
from app.dependencies.fetcher import Fetcher
from app.dependencies.geoip import get_geoip_helper
from app.dependencies.storage import StorageService
from app.helpers import utcnow
from app.log import log
from app.models.achievement_catalog import (
    CLIENTSIDE_ACHIEVEMENT_IDS,
    AchievementCatalogEntry,
    get_achievement_catalogue,
)
from app.models.beatmap import BeatmapRankStatus, Genre, Language, SearchQueryModel
from app.models.events import PluginEvent
from app.models.events.relationship import UserRelationshipChangedEvent
from app.models.events.score import ScoreDeletedEvent
from app.models.events.user import UserPageUpdatedEvent, UserPreferencesUpdatedEvent, UserRegisteredEvent
from app.models.mods import API_MODS, mods_can_get_pp
from app.models.room import RoomCategory
from app.models.score import GameMode
from app.models.user import Page
from app.models.userpage import UserpageError
from app.plugins import hub
from app.service.admin_score_service import delete_user_score
from app.service.bbcode_service import bbcode_service
from app.service.beatmap_ranking_service import (
    apply_local_rank,
    apply_local_unrank,
    get_effective_beatmap_policies,
    get_effective_beatmapset_policy,
)
from app.service.beatmap_search_overlay_service import overlay_local_ranked_beatmapsets
from app.service.beatmapset_cache_service import generate_hash, get_beatmapset_cache_service
from app.service.beatmapset_update_service import get_beatmapset_update_service
from app.service.home_activity_service import local_releases, online_history, real_online_count
from app.service.online_presence_service import get_online_user_ids
from app.service.ranking_cache_service import get_ranking_cache_service
from app.service.score_pin_service import lock_score_pin_state, reordered_score_pin_ids
from app.service.somsai_mmr_service import somsai_profile_payload
from app.service.user_identity_service import assign_server_id, resolve_human_user
from app.service.web_session_service import (
    WEB_SESSION_COOKIE,
    WEB_SESSION_TTL_SECONDS,
    InvalidWebSessionError,
    WebSessionData,
    create_web_session,
    delete_web_session,
    invalidate_web_sessions,
    read_web_session,
)

from .router import router

from fastapi import Depends, File, HTTPException, Query, Request, Response, UploadFile, status
from fastapi.responses import RedirectResponse
from httpx import HTTPError, HTTPStatusError
from PIL import Image, ImageOps, UnidentifiedImageError
from pydantic import BaseModel, ConfigDict, EmailStr, Field, StringConstraints, field_validator, model_validator
from sqlalchemy import (
    and_,
    delete,
    func,
    or_,
    select as sa_select,
    text,
)
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import joinedload
from sqlmodel import col, select
from starlette.concurrency import run_in_threadpool

logger = log("WebSite")
WEB_API_PREFIX = "/web-site"
LOGIN_WINDOW_SECONDS = 15 * 60
LOGIN_ATTEMPTS = 10
LOGIN_IP_ATTEMPTS = 40
REGISTER_WINDOW_SECONDS = 60 * 60
REGISTER_ATTEMPTS = 5
AVATAR_WINDOW_SECONDS = 60
AVATAR_ATTEMPTS = 5
SECURITY_WINDOW_SECONDS = 15 * 60
SECURITY_ATTEMPTS = 10
WEB_BEST_SCORE_LIMIT = 200
LEGACY_DEFAULT_AVATAR_URLS = {
    "https://lazer.g0v0.top/default.jpg",
    "https://lazer-data.g0v0.top/default.jpg",
}
RATE_INCREMENT_SCRIPT = """
local attempts = redis.call('INCR', KEYS[1])
if attempts == 1 then
    redis.call('EXPIRE', KEYS[1], ARGV[1])
end
return attempts
"""
ModeName = Literal["osu", "taiko", "fruits", "mania", "osurx", "osuap"]
ProfileText = Annotated[str, StringConstraints(strip_whitespace=True, max_length=255)]
WEB_PROFILE_DEFAULT_ORDER = [
    "top_ranks",
    "me",
    "medals",
    "historical",
    "recent_activity",
    "kudosu",
    "beatmaps",
]

ACHIEVEMENT_CATEGORIES: tuple[tuple[str, str], ...] = (
    ("beatmap-challenge-packs", "Beatmap Challenge Packs"),
    ("beatmap-packs", "Beatmap Packs"),
    ("beatmap-spotlights", "Beatmap Spotlights"),
    ("hush-hush", "Hush-Hush"),
    ("hush-hush-expert", "Hush-Hush (Expert)"),
    ("skill-dedication", "Skill & Dedication"),
    ("mod-introduction", "Mod Introduction"),
    ("legacy", "Legacy"),
)

ACHIEVEMENT_CATEGORY_KEYS = {name: key for key, name in ACHIEVEMENT_CATEGORIES if key != "legacy"}


class WebLoginRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    username: str = Field(min_length=1, max_length=254)
    password: str = Field(min_length=1, max_length=512)
    totp_code: str | None = Field(default=None, min_length=6, max_length=BACKUP_CODE_LENGTH)


class WebRegisterRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    username: str = Field(min_length=2, max_length=15)
    email: str = Field(min_length=3, max_length=254)
    password: str = Field(min_length=8, max_length=128)
    country_code: str = Field(default="XX", pattern=r"^[A-Z]{2}$")

    @field_validator("username", "email", mode="before")
    @classmethod
    def strip_text(cls, value: Any) -> Any:
        return value.strip() if isinstance(value, str) else value

    @field_validator("country_code", mode="before")
    @classmethod
    def normalise_country(cls, value: Any) -> Any:
        return value.strip().upper() if isinstance(value, str) else value


class WebProfileUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    country_code: str | None = Field(default=None, pattern=r"^[A-Z]{2}$")
    playmode: ModeName | None = None
    location: ProfileText | None = None
    interests: ProfileText | None = None
    occupation: ProfileText | None = None
    discord: ProfileText | None = None
    website: ProfileText | None = None

    @field_validator("country_code", mode="before")
    @classmethod
    def normalise_country(cls, value: Any) -> Any:
        return value.strip().upper() if isinstance(value, str) else value


class WebProfileLayoutRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    order: list[str] = Field(min_length=1, max_length=len(DEFAULT_ORDER))

    @field_validator("order")
    @classmethod
    def validate_order(cls, value: list[str]) -> list[str]:
        aliases = {"scores": "top_ranks", "about": "me"}
        canonical = [aliases.get(section, section) for section in value]
        if len(canonical) != len(set(canonical)):
            raise ValueError("Profile sections must not be repeated")
        unknown = set(canonical) - set(DEFAULT_ORDER)
        if unknown:
            raise ValueError(f"Unknown profile sections: {', '.join(sorted(unknown))}")
        required = {"me", "top_ranks", "medals"}
        if not required.issubset(canonical):
            raise ValueError("The about, records, and medals sections are required")
        return [*canonical, *(section for section in WEB_PROFILE_DEFAULT_ORDER if section not in canonical)]


class WebUserpageUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    body: str = Field(max_length=60_000)


class WebScorePinReorderRequest(BaseModel):
    """A single relative destination for a pinned profile score."""

    model_config = ConfigDict(extra="forbid")

    before_score_id: int | None = Field(default=None, gt=0)
    after_score_id: int | None = Field(default=None, gt=0)

    @model_validator(mode="after")
    def require_one_reference(self) -> "WebScorePinReorderRequest":
        if (self.before_score_id is None) == (self.after_score_id is None):
            raise ValueError("Provide exactly one of before_score_id or after_score_id")
        return self


class WebBeatmapModerationRequest(BaseModel):
    """One-click set-wide ranking action exposed on the community site."""

    model_config = ConfigDict(extra="forbid")

    action: Literal["rank", "unrank", "love"]


class WebEmailChangeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    current_password: str = Field(min_length=1, max_length=512)
    email: EmailStr


class WebPasswordChangeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    current_password: str = Field(min_length=1, max_length=512)
    new_password: str = Field(min_length=8, max_length=128)


class WebCodewordUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    current_password: str = Field(min_length=1, max_length=512)
    codeword: str = Field(min_length=6, max_length=64)

    @field_validator("codeword")
    @classmethod
    def normalise_codeword(cls, value: str) -> str:
        cleaned = value.strip()
        if len(cleaned) < 6:
            raise ValueError("Codeword must contain at least 6 characters")
        return cleaned


@dataclass(slots=True)
class WebContext:
    user: User
    session: WebSessionData


def _request_ip(request: Request) -> str:
    forwarded = request.headers.get("x-forwarded-for", "").split(",", 1)[0].strip()
    if forwarded:
        return forwarded[:45]
    return request.client.host[:45] if request.client else "unknown"


def _expected_origin() -> str:
    parsed = urlsplit(str(settings.server_url))
    return f"{parsed.scheme}://{parsed.netloc}"


def _require_same_origin(request: Request) -> None:
    if request.headers.get("origin") != _expected_origin():
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Invalid request origin")


def _require_csrf(request: Request, context: WebContext) -> None:
    _require_same_origin(request)
    supplied = request.headers.get("x-csrf-token", "")
    if not supplied or not secrets.compare_digest(supplied, context.session.csrf_token):
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Invalid CSRF token")


async def _rate_increment(redis: Redis, key: str, window_seconds: int) -> int:
    return int(await redis.eval(RATE_INCREMENT_SCRIPT, 1, key, window_seconds))


async def require_web_session(request: Request, session: Database, redis: Redis) -> WebContext:
    try:
        data = await read_web_session(redis, request)
    except InvalidWebSessionError as exc:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Website login required") from exc
    user = await session.get(User, data.user_id)
    if user is None or not user.is_active:
        await invalidate_web_sessions(redis, data.user_id)
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Account is not active")
    return WebContext(user=user, session=data)


WebSession = Annotated[WebContext, Depends(require_web_session)]


def _roles(user: User) -> list[str]:
    roles: list[str] = []
    if user.is_owner:
        roles.append("owner")
    if user.is_admin:
        roles.append("admin")
    if user.is_gmt:
        roles.append("moderator")
    if user.is_bng or user.is_qat:
        roles.append("ranker")
    if user.is_supporter:
        roles.append("supporter")
    return roles


def _web_permissions(user: User) -> dict[str, bool]:
    """Expose only the capabilities the community site actually needs."""

    administrator = bool(user.is_owner or user.is_admin)
    return {
        "admin_panel": administrator,
        "beatmap_moderation": administrator or bool(user.is_bng),
        "score_delete": administrator,
    }


def _require_web_permission(context: WebContext, permission: Literal["beatmap_moderation", "score_delete"]) -> None:
    if not _web_permissions(context.user)[permission]:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="Administrator access required")


def _as_utc(value: datetime | None) -> datetime | None:
    if value is None:
        return None
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)


def _achievement_category(achievement: AchievementCatalogEntry | None) -> tuple[str, str]:
    """Return the stable website group for an achievement definition."""

    if achievement is None:
        return "legacy", "Legacy"
    return ACHIEVEMENT_CATEGORY_KEYS[achievement.grouping], achievement.grouping


def _achievement_payload(
    achievement: AchievementCatalogEntry | None,
    record: UserAchievement | None,
) -> dict[str, Any]:
    if achievement is not None:
        achievement_id = achievement.id
    elif record is not None:
        achievement_id = record.achievement_id
    else:
        raise ValueError("Achievement definition or unlock record is required")
    category, category_name = _achievement_category(achievement)
    slug = achievement.slug if achievement is not None else None
    return {
        "id": achievement_id,
        "name": achievement.name if achievement is not None else f"Achievement #{achievement_id}",
        "description": achievement.description if achievement is not None else "",
        "slug": slug,
        "image_url": achievement.icon_url if achievement is not None else None,
        "image_url_2x": achievement.icon_url_2x if achievement is not None else None,
        "category": category,
        "category_name": category_name,
        "ruleset": achievement.mode if achievement is not None and achievement.mode is not None else "all",
        "is_secret": category in {"hush-hush", "hush-hush-expert"},
        "is_clientside": achievement_id in CLIENTSIDE_ACHIEVEMENT_IDS,
        "unlocked": record is not None,
        "achieved_at": _as_utc(record.achieved_at) if record is not None else None,
    }


def _achievement_collection_payload(
    records: list[UserAchievement],
    *,
    mode: ModeName,
    latest_limit: int,
    include_locked: bool,
) -> dict[str, Any]:
    """Combine definitions with one user's unlock records for the requested audience."""

    all_definitions_by_id = {achievement.id: achievement for achievement in get_achievement_catalogue()}
    definitions_by_id = {
        achievement_id: achievement
        for achievement_id, achievement in all_definitions_by_id.items()
        if achievement.mode is None or achievement.mode == mode
    }
    records_by_id: dict[int, UserAchievement] = {}
    for record in records:
        # The query is newest-first. Preserve the newest row if a legacy
        # database predates the unique user/achievement constraint.
        records_by_id.setdefault(record.achievement_id, record)

    items_by_id: dict[int, dict[str, Any]] = {}
    for achievement_id, achievement in definitions_by_id.items():
        items_by_id[achievement_id] = _achievement_payload(
            achievement,
            records_by_id.get(achievement_id),
        )
    for achievement_id, record in records_by_id.items():
        # Known medals from another ruleset belong to that ruleset's profile
        # tab. Unknown legacy rows have no mode metadata, so keep them visible
        # rather than silently losing a user's unlock.
        if achievement_id not in items_by_id and achievement_id not in all_definitions_by_id:
            items_by_id[achievement_id] = _achievement_payload(None, record)

    category_order = {key: index for index, (key, _name) in enumerate(ACHIEVEMENT_CATEGORIES)}
    items = sorted(
        items_by_id.values(),
        key=lambda item: (
            category_order.get(item["category"], len(category_order)),
            definitions_by_id[item["id"]].ordering if item["id"] in definitions_by_id else 0,
            item["id"],
        ),
    )
    if not include_locked:
        items = [item for item in items if item["unlocked"]]
    groups = []
    for category, name in ACHIEVEMENT_CATEGORIES:
        achievements = [item for item in items if item["category"] == category]
        if not achievements:
            continue
        groups.append(
            {
                "key": category,
                "name": name,
                "total": len(achievements),
                "unlocked_count": sum(1 for item in achievements if item["unlocked"]),
                "achievements": achievements,
            }
        )

    latest = []
    for record in records_by_id.values():
        item = items_by_id.get(record.achievement_id)
        if item is not None and item["unlocked"]:
            latest.append(item)
        if len(latest) == latest_limit:
            break

    return {
        "total": len(items),
        "unlocked_count": sum(1 for item in items_by_id.values() if item["unlocked"]),
        "catalog_visible": include_locked,
        "latest": latest,
        "groups": groups,
    }


async def _user_payload(session: Database, user: User, *, private: bool = False) -> dict[str, Any]:
    from app.service.negative_pp_service import negative_pp_title, negative_score_counts

    negative_count = (await negative_score_counts(session)).get(user.id, 0)
    roles = _roles(user)
    page = user.page if isinstance(user.page, dict) else {}
    avatar_url = (
        "/site/soms-default-avatar.png"
        if not user.avatar_url or user.avatar_url in LEGACY_DEFAULT_AVATAR_URLS
        else f"/users/{user.id}/avatar"
    )
    result: dict[str, Any] = {
        "id": user.id,
        "server_id": user.server_id,
        "username": user.username,
        "avatar_url": avatar_url,
        "has_custom_avatar": avatar_url != "/site/soms-default-avatar.png",
        "country_code": user.country_code,
        "is_online": bool(user.is_online),
        "roles": roles,
        "negative_pp_badge": negative_count > 0,
        "negative_pp_score_count": negative_count,
        "negative_pp_title": negative_pp_title(negative_count),
        "join_date": _as_utc(user.join_date),
        "last_visit": _as_utc(user.last_visit),
        "playmode": str(user.g0v0_playmode),
        "location": user.location,
        "interests": user.interests,
        "occupation": user.occupation,
        "discord": user.discord,
        "website": user.website,
        "profile_text": page.get("raw", ""),
        "profile_html": page.get("html", ""),
    }
    if private:
        result["email"] = user.email
        result["is_staff"] = any(role in roles for role in {"owner", "admin", "moderator", "ranker"})
    return result


async def _statistics_payload(
    session: Database,
    user: User,
    mode: GameMode,
    *,
    include_rank: bool = True,
) -> dict[str, Any]:
    statistics = (
        await session.exec(select(UserStatistics).where(UserStatistics.user_id == user.id, UserStatistics.mode == mode))
    ).first()
    if statistics is None:
        return {
            "mode": mode.value,
            "pp": 0.0,
            "global_rank": None,
            "country_rank": None,
            "hit_accuracy": 0.0,
            "play_count": 0,
            "play_time": 0,
            "ranked_score": 0,
            "total_score": 0,
            "total_hits": 0,
            "maximum_combo": 0,
            "level": 1,
            "grade_counts": {"ssh": 0, "ss": 0, "sh": 0, "s": 0, "a": 0, "b": 0, "c": 0, "d": 0},
        }

    global_rank: int | None = None
    country_rank: int | None = None
    if include_rank and statistics.is_ranked:
        # Use the same database-side window ranking as every other public API.
        # Comparing a database FLOAT with its round-tripped Python value can make
        # the current user's own row satisfy `pp > current_pp` and add one place.
        global_rank = await get_rank(session, statistics)
        country_rank = await get_rank(session, statistics, user.country_code)

    return {
        "mode": mode.value,
        "pp": round(float(statistics.pp), 2),
        "global_rank": global_rank,
        "country_rank": country_rank,
        "hit_accuracy": round(float(statistics.hit_accuracy), 2),
        "play_count": statistics.play_count,
        "play_time": statistics.play_time,
        "ranked_score": statistics.ranked_score,
        "total_score": statistics.total_score,
        "total_hits": statistics.total_hits,
        "maximum_combo": statistics.maximum_combo,
        "level": float(statistics.level_current),
        "grade_counts": {
            "ssh": statistics.grade_ssh,
            "ss": statistics.grade_ss,
            "sh": statistics.grade_sh,
            "s": statistics.grade_s,
            "a": statistics.grade_a,
            "b": statistics.grade_b,
            "c": statistics.grade_c,
            "d": statistics.grade_d,
        },
    }


def _normalise_profile_order(order: list[str] | None) -> list[str]:
    supplied = order if isinstance(order, list) else []
    if not supplied or supplied == DEFAULT_ORDER:
        return list(WEB_PROFILE_DEFAULT_ORDER)
    cleaned: list[str] = []
    for section in supplied:
        if section in DEFAULT_ORDER and section not in cleaned:
            cleaned.append(section)
    cleaned.extend(section for section in DEFAULT_ORDER if section not in cleaned)
    return cleaned


async def _profile_extras_payload(
    session: Database,
    user: User,
    mode: GameMode,
    current_rank: int | None,
) -> dict[str, Any]:
    preference = await session.get(UserPreference, user.id)

    rank_rows = list(
        (
            await session.exec(
                select(RankHistory)
                .where(RankHistory.user_id == user.id, RankHistory.mode == mode)
                .order_by(col(RankHistory.date).desc(), col(RankHistory.id).desc())
                .limit(365)
            )
        ).all()
    )
    ranks_by_date: dict[str, int] = {}
    for row in rank_rows:
        ranks_by_date.setdefault(row.date.isoformat(), int(row.rank))
    if current_rank is not None:
        ranks_by_date[utcnow().date().isoformat()] = current_rank
    rank_history = [{"date": date, "rank": rank} for date, rank in sorted(ranks_by_date.items()) if rank > 0]

    monthly_rows = list(
        (
            await session.exec(
                select(MonthlyPlaycounts)
                .where(MonthlyPlaycounts.user_id == user.id)
                .order_by(col(MonthlyPlaycounts.year), col(MonthlyPlaycounts.month))
            )
        ).all()
    )
    monthly_playcounts = [
        {
            "date": f"{row.year:04d}-{row.month:02d}-01",
            "year": row.year,
            "month": row.month,
            "count": max(0, int(row.count)),
        }
        for row in monthly_rows
    ]

    most_played_rows = list(
        (
            await session.exec(
                select(BeatmapPlaycounts, Beatmap, Beatmapset)
                .join(Beatmap, col(Beatmap.id) == col(BeatmapPlaycounts.beatmap_id))
                .join(Beatmapset, col(Beatmapset.id) == col(Beatmap.beatmapset_id))
                .where(BeatmapPlaycounts.user_id == user.id, Beatmap.mode == mode)
                .order_by(col(BeatmapPlaycounts.playcount).desc(), col(BeatmapPlaycounts.id).desc())
                .limit(50)
            )
        ).all()
    )
    most_played = [
        {
            "play_count": max(0, int(playcount.playcount)),
            "beatmap": {
                "id": beatmap.id,
                "beatmapset_id": beatmap.beatmapset_id,
                "version": beatmap.version,
                "difficulty_rating": beatmap.difficulty_rating,
                "mode": str(beatmap.mode),
            },
            "beatmapset": {
                "id": beatmapset.id,
                "title": beatmapset.title,
                "artist": beatmapset.artist,
                "creator": beatmapset.creator,
                "cover_url": _cover_url(beatmapset),
            },
        }
        for playcount, beatmap, beatmapset in most_played_rows
    ]

    return {
        "profile_order": _normalise_profile_order(preference.extras_order if preference is not None else None),
        "rank_history": rank_history,
        "monthly_playcounts": monthly_playcounts,
        "most_played": most_played,
    }


def _cover_url(beatmapset: Beatmapset) -> str | None:
    covers = beatmapset.covers
    if not isinstance(covers, dict):
        return None
    return covers.get("cover@2x") or covers.get("cover") or covers.get("list@2x") or covers.get("list")


async def _score_payloads(session: Database, scores: list[Score]) -> list[dict[str, Any]]:
    from app.service.negative_pp_service import negative_map_ids

    negative_maps = await negative_map_ids(session, [score.beatmap_id for score in scores])
    beatmaps = {score.beatmap.id: score.beatmap for score in scores}
    policies = await get_effective_beatmap_policies(session, list(beatmaps.values()))
    score_ids = [score.id for score in scores]
    imports = (
        (await session.exec(select(ScoreImport).where(col(ScoreImport.score_id).in_(score_ids)))).all()
        if score_ids
        else []
    )
    imports_by_score = {item.score_id: item for item in imports}
    payloads: list[dict[str, Any]] = []
    for score in scores:
        negative = score.beatmap_id in negative_maps and score.ranked and score.passed and score.pp > 0
        beatmap = score.beatmap
        beatmapset = beatmap.beatmapset
        imported = imports_by_score.get(score.id)
        import_payload = None
        if imported is not None:
            source_url = imported.source_snapshot.get("source_url") if imported.source_snapshot else None
            verification_value = (
                imported.source_snapshot.get("revision_verification") if imported.source_snapshot else None
            )
            verification = verification_value if isinstance(verification_value, dict) else {}
            import_payload = {
                "source": imported.source,
                "source_score_id": imported.source_score_id,
                "source_user_id": imported.source_user_id,
                "source_username": imported.source_username,
                "source_url": source_url,
                "imported_at": _as_utc(imported.imported_at),
                "replay_imported": imported.replay_imported,
                "revision_verified": bool(verification.get("revision_verified")),
                "unverified_override_used": bool(verification.get("unverified_override_used")),
                "ranking_outcome": verification.get("ranking_outcome"),
            }
        payloads.append(
            {
                "id": score.id,
                "is_pinned": score.pinned_order > 0,
                "pinned_order": score.pinned_order if score.pinned_order > 0 else None,
                "user": await _user_payload(session, score.user),
                "beatmap": {
                    "id": beatmap.id,
                    "beatmapset_id": beatmap.beatmapset_id,
                    "version": beatmap.version,
                    "difficulty_rating": beatmap.difficulty_rating,
                    "mode": str(beatmap.mode),
                    "max_combo": beatmap.max_combo,
                    "status": policies[beatmap.id].status.name.lower(),
                },
                "beatmapset": {
                    "id": beatmapset.id,
                    "artist": beatmapset.artist,
                    "title": beatmapset.title,
                    "creator": beatmapset.creator,
                    "cover_url": _cover_url(beatmapset),
                },
                "ruleset": score.gamemode.value,
                "rank": score.rank.value,
                "accuracy": score.accuracy,
                "pp": round(float(score.pp) * (-1 if negative else 1), 2),
                "raw_pp": round(float(score.pp), 2),
                "negative_pp": negative,
                "total_score": score.total_score,
                "max_combo": score.max_combo,
                "statistics": {
                    "great": score.n300,
                    "ok": score.n100,
                    "meh": score.n50,
                    "miss": score.nmiss,
                    "perfect": score.ngeki,
                    "good": score.nkatu,
                },
                "mods": score.mods,
                "passed": score.passed,
                "ended_at": _as_utc(score.ended_at),
                "has_replay": score.has_replay,
                # Reuse the audited v2 replay handler: it validates storage,
                # applies its download rate limit and rewrites imported replay
                # identity before serving the file.
                "replay_url": f"/api/v2/scores/{score.id}/download" if score.has_replay else None,
                "beatmap_url": f"/site/#beatmap/{beatmap.beatmapset_id}/{beatmap.id}",
                "processed": score.processed,
                "ranked": score.ranked,
                "leaderboard_eligible": score.leaderboard_eligible,
                "import": import_payload,
            }
        )
    return payloads


def _score_query_options():
    return joinedload(Score.beatmap).joinedload(Beatmap.beatmapset)


def _score_page_window(score_type: str, page: int, page_size: int, total: int) -> tuple[int, int, int]:
    """Return the public total, offset and bounded query limit for a score page."""

    offset = (page - 1) * page_size
    if score_type != "best":
        return total, offset, page_size
    bounded_total = min(total, WEB_BEST_SCORE_LIMIT)
    return bounded_total, offset, max(0, min(page_size, WEB_BEST_SCORE_LIMIT - offset))


async def _recent_scores(session: Database, mode: GameMode, limit: int = 8) -> list[Score]:
    # Mod settings must pass the same live policy as PP calculation. A saved
    # ranked/leaderboard flag describes the map, not the score's mods.
    base = (
        select(Score)
        .join(User, col(User.id) == col(Score.user_id))
        .where(
            Score.gamemode == mode,
            col(Score.processed).is_(True),
            col(Score.passed).is_(True),
            or_(Score.pp > 0, col(Score.leaderboard_eligible).is_(True)),
            col(User.is_active).is_(True),
            col(User.is_bot).is_(False),
            ~User.is_restricted_query(col(User.id)),
        )
        .options(_score_query_options())
        .order_by(col(Score.ended_at).desc(), col(Score.id).desc())
    )
    scores: list[Score] = []
    cursor: tuple[datetime, int] | None = None
    batch_size = max(32, limit * 4)
    while len(scores) < limit:
        query = base
        if cursor is not None:
            ended_at, score_id = cursor
            query = query.where(or_(Score.ended_at < ended_at, and_(Score.ended_at == ended_at, Score.id < score_id)))
        candidates = list((await session.exec(query.limit(batch_size))).all())
        eligible = [score for score in candidates if mods_can_get_pp(int(mode), score.mods)]
        zero_pp_maps = {score.beatmap_id: score.beatmap for score in eligible if round(score.pp, 2) <= 0}
        policies = await get_effective_beatmap_policies(session, list(zero_pp_maps.values()))
        for score in eligible:
            # Loved results have no PP by design and display a heart. Resolve
            # SOMS overrides too; a pending upstream map can be locally Loved.
            if round(score.pp, 2) > 0 or (
                score.leaderboard_eligible and policies[score.beatmap_id].status == BeatmapRankStatus.LOVED
            ):
                scores.append(score)
                if len(scores) == limit:
                    return scores
        if len(candidates) < batch_size:
            break
        # Refill after filtering, without skipping older valid scores or
        # shifting pages when a new score arrives between batch queries.
        cursor = (candidates[-1].ended_at, candidates[-1].id)
    return scores


def _set_web_cookie(response: Response, token: str) -> None:
    response.set_cookie(
        WEB_SESSION_COOKIE,
        token,
        max_age=WEB_SESSION_TTL_SECONDS,
        secure=True,
        httponly=True,
        samesite="lax",
        path="/",
    )
    response.headers["Cache-Control"] = "no-store"


async def _session_response(user: User, data: WebSessionData, session: Database) -> dict[str, Any]:
    restricted = await user.is_restricted(session)
    has_codeword = await session.get(UserRecoveryWord, user.id) is not None
    return {
        "authenticated": True,
        "csrf_token": data.csrf_token,
        "user": await _user_payload(session, user, private=True),
        "restricted": restricted,
        "permissions": _web_permissions(user),
        "has_codeword": has_codeword,
        "session_expires_in": WEB_SESSION_TTL_SECONDS,
    }


@router.post(f"{WEB_API_PREFIX}/session", tags=["Website"], include_in_schema=False)
async def login_web_site(
    payload: WebLoginRequest,
    request: Request,
    response: Response,
    session: Database,
    redis: Redis,
):
    _require_same_origin(request)
    ip = _request_ip(request)
    identity = f"{ip}\0{payload.username.strip().casefold()}"
    rate_key = f"web-site:login-rate:{hashlib.sha256(identity.encode()).hexdigest()}"
    ip_key = f"web-site:login-rate-ip:{hashlib.sha256(ip.encode()).hexdigest()}"
    attempts = await _rate_increment(redis, rate_key, LOGIN_WINDOW_SECONDS)
    ip_attempts = await _rate_increment(redis, ip_key, LOGIN_WINDOW_SECONDS)
    if attempts > LOGIN_ATTEMPTS or ip_attempts > LOGIN_IP_ATTEMPTS:
        retry_after = max(1, int(await redis.ttl(rate_key)), int(await redis.ttl(ip_key)))
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many login attempts",
            headers={"Retry-After": str(retry_after)},
        )

    user = await authenticate_user(session, payload.username.strip(), payload.password)
    if user is None or not user.is_active:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Invalid username or password")

    totp = (await session.exec(select(TotpKeys).where(col(TotpKeys.user_id) == user.id).with_for_update())).first()
    if totp is not None:
        code = (payload.totp_code or "").strip()
        valid = False
        if len(code) == 6 and code.isdigit():
            valid = await verify_totp_key_with_replay_protection(user.id, totp.secret, code, redis)
        elif len(code) == BACKUP_CODE_LENGTH:
            valid = check_totp_backup_code(totp, code)
            if valid:
                session.add(totp)
                await session.commit()
                await session.refresh(user)
        if not valid:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail={"message": "Two-factor code required", "totp_required": True},
            )

    await redis.delete(rate_key)
    token, data = await create_web_session(redis, user.id, request)
    _set_web_cookie(response, token)
    return await _session_response(user, data, session)


@router.get(f"{WEB_API_PREFIX}/session", tags=["Website"], include_in_schema=False)
async def get_web_site_session(context: WebSession, session: Database):
    return await _session_response(context.user, context.session, session)


@router.delete(f"{WEB_API_PREFIX}/session", status_code=204, tags=["Website"], include_in_schema=False)
async def logout_web_site(request: Request, response: Response, context: WebSession, redis: Redis) -> None:
    _require_csrf(request, context)
    await delete_web_session(redis, context.session)
    response.delete_cookie(WEB_SESSION_COOKIE, path="/", secure=True, httponly=True, samesite="lax")


@router.post(f"{WEB_API_PREFIX}/register", tags=["Website"], include_in_schema=False)
async def register_web_site(
    payload: WebRegisterRequest,
    request: Request,
    response: Response,
    session: Database,
    redis: Redis,
):
    _require_same_origin(request)
    if settings.enable_turnstile_verification:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Website registration is disabled while Turnstile verification is enabled; register in osu!lazer",
        )
    ip_hash = hashlib.sha256(_request_ip(request).encode()).hexdigest()
    rate_key = f"web-site:register-rate:{ip_hash}"
    attempts = await _rate_increment(redis, rate_key, REGISTER_WINDOW_SECONDS)
    if attempts > REGISTER_ATTEMPTS:
        retry_after = max(1, int(await redis.ttl(rate_key)))
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many registration attempts",
            headers={"Retry-After": str(retry_after)},
        )

    errors = {
        "username": validate_username(payload.username),
        "email": (
            []
            if re.fullmatch(r"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", payload.email)
            else ["Invalid email"]
        ),
        "password": validate_password(payload.password),
    }
    if payload.country_code not in {*COUNTRIES, "XX"}:
        errors["country_code"] = ["Unknown country code"]
    if (await session.exec(select(User.id).where(func.lower(User.username) == payload.username.casefold()))).first():
        errors["username"].append("Username is already taken")
    email = payload.email.casefold()
    if (await session.exec(select(User.id).where(func.lower(User.email) == email))).first():
        errors["email"].append("Email is already taken")
    if any(errors.values()):
        raise HTTPException(status_code=422, detail={"message": "Registration data is invalid", "fields": errors})

    try:
        result = await session.execute(
            text(
                "SELECT AUTO_INCREMENT FROM information_schema.TABLES "
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'lazer_users'"
            )
        )
        next_id = int(result.one()[0])
        if next_id < settings.private_user_id_base:
            await session.execute(text(f"ALTER TABLE lazer_users AUTO_INCREMENT = {settings.private_user_id_base}"))
            await session.commit()

        user = User(
            username=payload.username,
            email=email,
            pw_bcrypt=get_password_hash(payload.password),
            priv=1,
            country_code=payload.country_code,
            join_date=utcnow(),
            last_visit=utcnow(),
            is_supporter=settings.enable_supporter_for_all_users,
            support_level=int(settings.enable_supporter_for_all_users),
        )
        await assign_server_id(session, user)
        for mode in (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
            session.add(UserStatistics(mode=mode, user_id=user.id))
        if settings.soms_osu_assist_modes or settings.enable_rx:
            for mode in (
                (GameMode.OSURX,)
                if settings.soms_osu_assist_modes
                else (GameMode.OSURX, GameMode.TAIKORX, GameMode.FRUITSRX)
            ):
                session.add(UserStatistics(mode=mode, user_id=user.id))
        if settings.soms_osu_assist_modes or settings.enable_ap:
            session.add(UserStatistics(mode=GameMode.OSUAP, user_id=user.id))
        session.add(DailyChallengeStats(user_id=user.id))
        await session.commit()
        await session.refresh(user)
    except IntegrityError as exc:
        await session.rollback()
        raise HTTPException(status_code=409, detail="Username or email is already registered") from exc

    hub.emit(UserRegisteredEvent(user_id=user.id, username=user.username, country_code=user.country_code))
    token, data = await create_web_session(redis, user.id, request)
    _set_web_cookie(response, token)
    return await _session_response(user, data, session)


@router.get(f"{WEB_API_PREFIX}/countries", tags=["Website"], include_in_schema=False)
async def list_web_countries():
    countries = [{"code": code, "name": name} for code, name in COUNTRIES.items()]
    countries.append({"code": "XX", "name": "Не указана"})
    return sorted(countries, key=lambda item: (item["name"], item["code"]))


def _web_mod_catalog_payload() -> dict[str, dict[str, dict[str, Any]]]:
    """Return the official lazer mod categories and configurable settings used by the site."""

    catalog: dict[str, dict[str, dict[str, Any]]] = {}
    for mode in (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
        ruleset_mods: dict[str, dict[str, Any]] = {}
        for acronym, mod in API_MODS.get(int(mode), {}).items():
            ruleset_mods[acronym] = {
                "name": mod["Name"],
                "type": mod["Type"],
                "settings": [
                    {
                        "name": setting["Name"],
                        "label": setting["Label"],
                        "type": setting["Type"],
                        "default": setting["DefaultValue"],
                    }
                    for setting in mod["Settings"]
                ],
            }
        catalog[mode.value] = ruleset_mods
    catalog["osurx"] = catalog["osu"]
    catalog["osuap"] = catalog["osu"]
    return catalog


@router.get(f"{WEB_API_PREFIX}/mods", tags=["Website"], include_in_schema=False)
async def list_web_mods():
    return {"rulesets": _web_mod_catalog_payload()}


@router.get(f"{WEB_API_PREFIX}/home", tags=["Website"], include_in_schema=False)
async def get_web_home(
    session: Database,
    redis: Redis,
    mode: ModeName = "osu",
):
    game_mode = GameMode(mode)
    visible_users = [
        col(User.is_bot).is_(False),
        col(User.is_active).is_(True),
        ~User.is_restricted_query(col(User.id)),
    ]
    total_users = int((await session.exec(select(func.count(col(User.id))).where(*visible_users))).one())
    online_ids = await get_online_user_ids(redis)
    online_players = (
        list(
            (
                await session.exec(
                    select(User)
                    .where(*visible_users, col(User.id).in_(online_ids))
                    .order_by(col(User.username), col(User.id))
                )
            ).all()
        )
        if online_ids
        else []
    )
    total_scores = int(
        (
            await session.exec(
                select(func.count(col(Score.id)))
                .join(User, col(User.id) == col(Score.user_id))
                .where(*visible_users, col(Score.processed).is_(True))
            )
        ).one()
    )
    recent = await _recent_scores(session, game_mode, 8)
    ranking_rows = (
        await session.exec(
            select(UserStatistics, User)
            .join(User, col(User.id) == col(UserStatistics.user_id))
            .where(
                UserStatistics.mode == game_mode,
                col(UserStatistics.is_ranked).is_(True),
                has_ranked_pp(),
                col(User.is_active).is_(True),
                col(User.is_bot).is_(False),
                ~User.is_restricted_query(col(User.id)),
            )
            .order_by(col(UserStatistics.pp).desc(), col(User.id))
            .limit(5)
        )
    ).all()
    leaders = []
    for index, (statistics, user) in enumerate(ranking_rows, start=1):
        leaders.append(
            {
                "rank": index,
                "user": await _user_payload(session, user),
                "pp": round(float(statistics.pp), 2),
                "accuracy": round(float(statistics.hit_accuracy), 2),
                "play_count": statistics.play_count,
            }
        )
    return {
        "server": {"name": "private osu!", "mode": game_mode.value},
        "counts": {"users": total_users, "online": len(online_players), "scores": total_scores},
        "online_users": [await _user_payload(session, user) for user in online_players],
        "leaders": leaders,
        "local_releases": await local_releases(session),
        "online_history": await online_history(redis),
        "recent_scores": await _score_payloads(session, recent),
    }


@router.get(f"{WEB_API_PREFIX}/home/online", tags=["Website"], include_in_schema=False)
async def get_web_online(session: Database, redis: Redis):
    return {"online": await real_online_count(session, redis), "online_history": await online_history(redis)}


RankingSection = Literal["world", "countries", "scores", "teams", "playlists", "somsai", "daily"]
RankingSort = Literal["performance", "score"]
RankingScope = Literal["all", "friends"]


async def _optional_web_user(request: Request, session: Database, redis: Redis) -> User | None:
    """Resolve a website viewer without turning a public ranking into an auth wall."""

    try:
        data = await read_web_session(redis, request)
    except InvalidWebSessionError:
        return None
    user = await session.get(User, data.user_id)
    return user if user is not None and user.is_active else None


async def _ranking_friend_ids(
    request: Request,
    session: Database,
    redis: Redis,
    scope: RankingScope,
) -> set[int] | None:
    if scope == "all":
        return None
    viewer = await _optional_web_user(request, session, redis)
    if viewer is None:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Войдите, чтобы открыть рейтинг друзей")
    followed_ids = set(
        (
            await session.exec(
                select(Relationship.target_id).where(
                    Relationship.user_id == viewer.id,
                    Relationship.type == RelationshipType.FOLLOW,
                )
            )
        ).all()
    )
    followed_ids.add(viewer.id)
    return followed_ids


def _ranking_pages(total: int, page_size: int) -> int:
    return max(1, (total + page_size - 1) // page_size)


def _ranking_response(
    *,
    items: list[dict[str, Any]],
    section: RankingSection,
    sort: RankingSort,
    mode: GameMode,
    page: int,
    page_size: int,
    total: int,
) -> dict[str, Any]:
    return {
        "items": items,
        "section": section,
        "sort": sort,
        "mode": mode.value,
        "page": page,
        "page_size": page_size,
        "pages": _ranking_pages(total, page_size),
        "total": total,
    }


async def _ranking_users_by_id(session: Database, user_ids: list[int]) -> dict[int, User]:
    if not user_ids:
        return {}
    users = (await session.exec(select(User).where(col(User.id).in_(user_ids)))).all()
    return {user.id: user for user in users}


@router.get(f"{WEB_API_PREFIX}/rankings", tags=["Website"], include_in_schema=False)
async def get_web_rankings(
    session: Database,
    redis: Redis,
    request: Request,
    mode: ModeName = "osu",
    section: RankingSection = "world",
    somsai_variant: int = 4,
    somsai_format: Literal["1v1", "2v2"] = "1v1",
    sort: RankingSort = "performance",
    scope: RankingScope = "all",
    country: Annotated[str | None, Query(min_length=2, max_length=2)] = None,
    page: Annotated[int, Query(ge=1)] = 1,
    page_size: Annotated[int, Query(ge=10, le=100)] = 50,
):
    """Return the selected website ranking instead of a decorative one-size-fits-all table."""
    if somsai_variant not in (4, 7):
        raise HTTPException(status_code=422, detail="SOMSAI mania variant must be 4 or 7")

    game_mode = GameMode(mode)
    normalized_country = country.upper() if country else None
    if normalized_country is not None and normalized_country not in COUNTRIES:
        raise HTTPException(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail="Unknown country code")
    friend_ids = (
        await _ranking_friend_ids(request, session, redis, scope) if section not in {"countries", "teams"} else None
    )
    offset = (page - 1) * page_size
    visible_users = [
        col(User.is_active).is_(True),
        col(User.is_bot).is_(False),
        ~User.is_restricted_query(col(User.id)),
    ]
    if normalized_country is not None:
        visible_users.append(User.country_code == normalized_country)
    if friend_ids is not None:
        visible_users.append(col(User.id).in_(friend_ids))

    if section == "world":
        filters = [
            UserStatistics.mode == game_mode,
            col(UserStatistics.is_ranked).is_(True),
            has_ranked_pp(),
            *visible_users,
        ]
        total = int(
            (await session.exec(select(func.count()).select_from(UserStatistics).join(User).where(*filters))).one()
        )
        order_column = col(UserStatistics.pp) if sort == "performance" else col(UserStatistics.ranked_score)
        rows = (
            await session.exec(
                select(UserStatistics, User)
                .join(User, col(User.id) == col(UserStatistics.user_id))
                .where(*filters)
                .order_by(order_column.desc(), col(User.id))
                .offset(offset)
                .limit(page_size)
            )
        ).all()
        items = [
            {
                "rank": offset + row_offset + 1,
                "user": await _user_payload(session, user),
                "pp": round(float(statistics.pp), 2),
                "accuracy": round(float(statistics.hit_accuracy), 2),
                "play_count": statistics.play_count,
                "play_time": statistics.play_time,
                "ranked_score": statistics.ranked_score,
                "grade_counts": {
                    "ssh": statistics.grade_ssh,
                    "ss": statistics.grade_ss,
                    "sh": statistics.grade_sh,
                    "s": statistics.grade_s,
                    "a": statistics.grade_a,
                    "b": statistics.grade_b,
                    "c": statistics.grade_c,
                    "d": statistics.grade_d,
                },
            }
            for row_offset, (statistics, user) in enumerate(rows)
        ]
        return _ranking_response(
            items=items,
            section=section,
            sort=sort,
            mode=game_mode,
            page=page,
            page_size=page_size,
            total=total,
        )

    if section == "countries":
        filters = [
            UserStatistics.mode == game_mode,
            col(UserStatistics.is_ranked).is_(True),
            has_ranked_pp(),
            col(User.is_active).is_(True),
            col(User.is_bot).is_(False),
            ~User.is_restricted_query(col(User.id)),
        ]
        rows = list(
            (
                await session.execute(
                    sa_select(
                        col(User.country_code),
                        func.count(col(UserStatistics.user_id)),
                        func.sum(col(UserStatistics.play_count)),
                        func.sum(col(UserStatistics.ranked_score)),
                        func.sum(col(UserStatistics.pp)),
                    )
                    .select_from(UserStatistics)
                    .join(User, col(User.id) == col(UserStatistics.user_id))
                    .where(*filters)
                    .group_by(col(User.country_code))
                )
            ).all()
        )
        sort_index = 4 if sort == "performance" else 3
        rows.sort(key=lambda row: (float(row[sort_index] or 0), str(row[0])), reverse=True)
        page_rows = rows[offset : offset + page_size]
        items = [
            {
                "rank": offset + row_offset + 1,
                "code": code,
                "name": COUNTRIES.get(code, code),
                "active_users": int(active_users or 0),
                "play_count": int(play_count or 0),
                "ranked_score": int(ranked_score or 0),
                "pp": round(float(pp or 0), 2),
            }
            for row_offset, (code, active_users, play_count, ranked_score, pp) in enumerate(page_rows)
        ]
        return _ranking_response(
            items=items,
            section=section,
            sort=sort,
            mode=game_mode,
            page=page,
            page_size=page_size,
            total=len(rows),
        )

    if section == "scores":
        filters = [
            BestScore.gamemode == game_mode,
            Score.gamemode == game_mode,
            col(Score.processed).is_(True),
            col(Score.passed).is_(True),
            col(Score.ranked).is_(True),
            col(Score.leaderboard_eligible).is_(True),
            *visible_users,
        ]
        count_statement = (
            select(func.count())
            .select_from(BestScore)
            .join(Score, col(Score.id) == col(BestScore.score_id))
            .join(User, col(User.id) == col(Score.user_id))
            .where(*filters)
        )
        total = int((await session.exec(count_statement)).one())
        order_column = col(BestScore.pp) if sort == "performance" else col(Score.total_score)
        scores = list(
            (
                await session.exec(
                    select(Score)
                    .join(BestScore, col(BestScore.score_id) == col(Score.id))
                    .join(User, col(User.id) == col(Score.user_id))
                    .where(*filters)
                    .options(_score_query_options())
                    .order_by(order_column.desc(), col(Score.id))
                    .offset(offset)
                    .limit(page_size)
                )
            ).all()
        )
        items = await _score_payloads(session, scores)
        for row_offset, item in enumerate(items):
            item["rank_position"] = offset + row_offset + 1
        return _ranking_response(
            items=items,
            section=section,
            sort=sort,
            mode=game_mode,
            page=page,
            page_size=page_size,
            total=total,
        )

    if section == "teams":
        team_rows = list(
            (
                await session.execute(
                    sa_select(
                        col(Team.id),
                        col(Team.name),
                        col(Team.short_name),
                        col(Team.flag_url),
                        func.count(col(TeamMember.user_id)),
                        func.sum(col(UserStatistics.play_count)),
                        func.sum(col(UserStatistics.ranked_score)),
                        func.sum(col(UserStatistics.pp)),
                    )
                    .select_from(Team)
                    .join(TeamMember, col(TeamMember.team_id) == col(Team.id))
                    .join(User, col(User.id) == col(TeamMember.user_id))
                    .join(UserStatistics, col(UserStatistics.user_id) == col(User.id))
                    .where(
                        col(Team.playmode) == game_mode,
                        col(UserStatistics.mode) == game_mode,
                        col(UserStatistics.is_ranked).is_(True),
                        has_ranked_pp(),
                        col(User.is_active).is_(True),
                        col(User.is_bot).is_(False),
                        ~User.is_restricted_query(col(User.id)),
                    )
                    .group_by(col(Team.id), col(Team.name), col(Team.short_name), col(Team.flag_url))
                )
            ).all()
        )
        sort_index = 7 if sort == "performance" else 6
        team_rows.sort(key=lambda row: (float(row[sort_index] or 0), int(row[0])), reverse=True)
        page_rows = team_rows[offset : offset + page_size]
        items = [
            {
                "rank": offset + row_offset + 1,
                "team": {"id": team_id, "name": name, "short_name": short_name, "flag_url": flag_url},
                "member_count": int(member_count or 0),
                "play_count": int(play_count or 0),
                "ranked_score": int(ranked_score or 0),
                "pp": round(float(pp or 0), 2),
            }
            for row_offset, (
                team_id,
                name,
                short_name,
                flag_url,
                member_count,
                play_count,
                ranked_score,
                pp,
            ) in enumerate(page_rows)
        ]
        return _ranking_response(
            items=items,
            section=section,
            sort=sort,
            mode=game_mode,
            page=page,
            page_size=page_size,
            total=len(team_rows),
        )

    if section == "somsai":
        filters = [
            SomsaiRating.ruleset_id == int(game_mode),
            SomsaiRating.variant_id == (somsai_variant if game_mode == GameMode.MANIA else 0),
            SomsaiRating.format == somsai_format,
            *visible_users,
        ]
        total = (
            await session.exec(
                select(func.count())
                .select_from(SomsaiRating)
                .join(User, col(User.id) == col(SomsaiRating.user_id))
                .where(*filters)
            )
        ).one()
        rows = (
            await session.exec(
                select(SomsaiRating, User)
                .join(User, col(User.id) == col(SomsaiRating.user_id))
                .where(*filters)
                .order_by(col(SomsaiRating.rating).desc(), col(User.id))
                .offset(offset)
                .limit(page_size)
            )
        ).all()
        return _ranking_response(
            items=[
                {
                    "rank": offset + index + 1,
                    "user": await _user_payload(session, user),
                    "mmr": row.rating,
                    "games": row.games,
                    "wins": row.wins,
                }
                for index, (row, user) in enumerate(rows)
            ],
            section=section,
            sort=sort,
            mode=game_mode,
            page=page,
            page_size=page_size,
            total=total,
        )

    room_category_filter = (
        col(Room.category) == RoomCategory.DAILY_CHALLENGE
        if section == "daily"
        else col(Room.category).notin_([RoomCategory.DAILY_CHALLENGE, RoomCategory.REALTIME])
    )
    mode_room_ids = select(col(Playlist.room_id)).where(col(Playlist.ruleset_id) == int(game_mode)).distinct()
    activity_rows = list(
        (
            await session.execute(
                sa_select(
                    col(User.id),
                    func.sum(col(ItemAttemptsCount.pp)),
                    func.sum(col(ItemAttemptsCount.total_score)),
                    func.sum(col(ItemAttemptsCount.attempts)),
                    func.sum(col(ItemAttemptsCount.completed)),
                    func.avg(col(ItemAttemptsCount.accuracy)),
                )
                .select_from(ItemAttemptsCount)
                .join(Room, col(Room.id) == col(ItemAttemptsCount.room_id))
                .join(User, col(User.id) == col(ItemAttemptsCount.user_id))
                .where(room_category_filter, col(Room.id).in_(mode_room_ids), *visible_users)
                .group_by(col(User.id))
            )
        ).all()
    )
    users = await _ranking_users_by_id(session, [int(row[0]) for row in activity_rows])
    daily_stats: dict[int, DailyChallengeStats] = {}
    if section == "daily" and users:
        daily_stats = {
            stats.user_id: stats
            for stats in (
                await session.exec(select(DailyChallengeStats).where(col(DailyChallengeStats.user_id).in_(users)))
            ).all()
            if stats.user_id is not None
        }
    activity_items = []
    for user_id, pp, total_score, attempts, completed, accuracy in activity_rows:
        user = users.get(int(user_id))
        if user is None:
            continue
        item: dict[str, Any] = {
            "user": await _user_payload(session, user),
            "pp": round(float(pp or 0), 2),
            "total_score": int(total_score or 0),
            "attempts": int(attempts or 0),
            "completed": int(completed or 0),
            "accuracy": float(accuracy or 0),
        }
        if section == "daily":
            stats = daily_stats.get(int(user_id))
            item.update(
                {
                    "daily_streak_current": stats.daily_streak_current if stats else 0,
                    "daily_streak_best": stats.daily_streak_best if stats else 0,
                    "play_count": int(completed or 0),
                }
            )
        activity_items.append(item)
    if section == "daily":
        key = "daily_streak_current" if sort == "performance" else "play_count"
    else:
        key = "pp" if sort == "performance" else "total_score"
    activity_items.sort(key=lambda item: (item[key], item["user"]["id"]), reverse=True)
    page_items = activity_items[offset : offset + page_size]
    for row_offset, item in enumerate(page_items):
        item["rank"] = offset + row_offset + 1
    return _ranking_response(
        items=page_items,
        section=section,
        sort=sort,
        mode=game_mode,
        page=page,
        page_size=page_size,
        total=len(activity_items),
    )


@router.get(f"{WEB_API_PREFIX}/users/search", tags=["Website"], include_in_schema=False)
async def search_web_users(
    session: Database,
    q: Annotated[str, Query(max_length=64)] = "",
    mode: ModeName = "osu",
    limit: Annotated[int, Query(ge=1, le=20)] = 10,
):
    """Search visible local players for the website's global search overlay."""

    cleaned_query = q.strip().removeprefix("@")
    if not cleaned_query:
        return {"items": [], "total": 0, "query": ""}

    numeric_query = cleaned_query.removeprefix("#")
    username_match = func.lower(col(User.username)).contains(cleaned_query.casefold())
    search_filter = username_match
    if numeric_query.isdigit():
        search_filter = or_(
            search_filter,
            col(User.id) == int(numeric_query),
            col(User.server_id) == int(numeric_query),
        )

    game_mode = GameMode(mode)
    rows = (
        await session.exec(
            select(User, UserStatistics)
            .outerjoin(
                UserStatistics,
                and_(
                    col(UserStatistics.user_id) == col(User.id),
                    col(UserStatistics.mode) == game_mode,
                ),
            )
            .where(
                search_filter,
                col(User.is_bot).is_(False),
                col(User.is_active).is_(True),
                ~User.is_restricted_query(col(User.id)),
            )
            .order_by(
                (func.lower(col(User.username)) == cleaned_query.casefold()).desc(),
                col(User.username),
                col(User.id),
            )
            .limit(limit)
        )
    ).all()
    items = []
    for user, statistics in rows:
        items.append(
            {
                "user": await _user_payload(session, user),
                "pp": round(float(statistics.pp), 2) if statistics is not None else 0.0,
                "accuracy": round(float(statistics.hit_accuracy), 2) if statistics is not None else 0.0,
                "play_count": statistics.play_count if statistics is not None else 0,
            }
        )
    return {"items": items, "total": len(items), "query": cleaned_query}


async def _find_public_user(session: Database, identifier: str) -> User:
    if identifier.isdigit():
        user = await resolve_human_user(session, int(identifier))
    else:
        username = identifier.removeprefix("@").replace("_", " ")
        user = (await session.exec(select(User).where(func.lower(User.username) == username.casefold()))).first()
    if user is None or user.is_bot or not user.is_active or await user.is_restricted(session):
        raise HTTPException(status_code=404, detail="User not found")
    return user


async def _web_follower_count(session: Database, target_user_id: int) -> int:
    """Count distinct users following a profile, ignoring any legacy duplicate rows."""

    return int(
        (
            await session.exec(
                select(func.count(func.distinct(Relationship.user_id)))
                .select_from(Relationship)
                .where(
                    Relationship.target_id == target_user_id,
                    Relationship.type == RelationshipType.FOLLOW,
                )
            )
        ).one()
    )


async def _web_friendship_payload(
    session: Database,
    *,
    viewer_user_id: int,
    target_user_id: int,
) -> dict[str, Any]:
    """Return bancho-style one-way and mutual friendship state for a profile."""

    follower_count = await _web_follower_count(session, target_user_id)
    relationships = list(
        (
            await session.exec(
                select(Relationship).where(
                    Relationship.type == RelationshipType.FOLLOW,
                    or_(
                        and_(
                            col(Relationship.user_id) == viewer_user_id,
                            col(Relationship.target_id) == target_user_id,
                        ),
                        and_(
                            col(Relationship.user_id) == target_user_id,
                            col(Relationship.target_id) == viewer_user_id,
                        ),
                    ),
                )
            )
        ).all()
    )
    is_following = any(
        relationship.user_id == viewer_user_id and relationship.target_id == target_user_id
        for relationship in relationships
    )
    is_followed = any(
        relationship.user_id == target_user_id and relationship.target_id == viewer_user_id
        for relationship in relationships
    )
    return {
        "target_user_id": target_user_id,
        "follower_count": follower_count,
        "is_following": is_following,
        "is_followed": is_followed,
        "mutual": is_following and is_followed,
    }


@router.get(f"{WEB_API_PREFIX}/users/{{identifier}}", tags=["Website"], include_in_schema=False)
async def get_web_user(
    identifier: str,
    session: Database,
    mode: ModeName = "osu",
    somsai_variant: int = 4,
    somsai_format: Literal["1v1", "2v2"] = "1v1",
):
    if somsai_variant not in (4, 7):
        raise HTTPException(status_code=422, detail="SOMSAI mania variant must be 4 or 7")
    user = await _find_public_user(session, identifier)
    game_mode = GameMode(mode)
    statistics = await _statistics_payload(session, user, game_mode)
    extras = await _profile_extras_payload(session, user, game_mode, statistics["global_rank"])
    user_payload = await _user_payload(session, user)
    user_payload["follower_count"] = await _web_follower_count(session, user.id)
    somsai = await somsai_profile_payload(session, user.id, game_mode, somsai_variant, somsai_format)
    return {"user": user_payload, "statistics": statistics, "somsai": somsai, **extras}


@router.get(f"{WEB_API_PREFIX}/users/{{identifier}}/friendship", tags=["Website"], include_in_schema=False)
async def get_web_friendship(
    identifier: str,
    context: WebSession,
    session: Database,
    response: Response,
):
    target = await _find_public_user(session, identifier)
    response.headers["Cache-Control"] = "private, no-store"
    return await _web_friendship_payload(
        session,
        viewer_user_id=context.user.id,
        target_user_id=target.id,
    )


@router.put(f"{WEB_API_PREFIX}/users/{{identifier}}/friendship", tags=["Website"], include_in_schema=False)
@router.delete(f"{WEB_API_PREFIX}/users/{{identifier}}/friendship", tags=["Website"], include_in_schema=False)
async def update_web_friendship(
    identifier: str,
    request: Request,
    context: WebSession,
    session: Database,
    response: Response,
):
    """Follow or unfollow a profile using the website session."""

    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot manage friends")

    viewer_user_id = context.user.id
    target = await _find_public_user(session, identifier)
    target_user_id = target.id
    if target_user_id == viewer_user_id:
        raise HTTPException(status_code=422, detail="Cannot add yourself as a friend")

    # Lock the viewer row so concurrent clicks cannot create duplicate outgoing
    # relationships even though older installations lack a composite unique key.
    (await session.exec(select(User.id).where(User.id == viewer_user_id).with_for_update())).first()
    outgoing = list(
        (
            await session.exec(
                select(Relationship)
                .where(
                    Relationship.user_id == viewer_user_id,
                    Relationship.target_id == target_user_id,
                )
                .order_by(col(Relationship.id).asc())
                .with_for_update()
            )
        ).all()
    )

    following = request.method == "PUT"
    newly_followed = following and not any(item.type == RelationshipType.FOLLOW for item in outgoing)
    changed = False
    event_action: Literal["add", "update", "delete"] | None = None
    if following:
        if outgoing:
            primary = outgoing[0]
            if primary.type != RelationshipType.FOLLOW:
                primary.type = RelationshipType.FOLLOW
                session.add(primary)
                changed = True
            for duplicate in outgoing[1:]:
                await session.delete(duplicate)
                changed = True
            if changed:
                event_action = "update"
        else:
            session.add(
                Relationship(
                    user_id=viewer_user_id,
                    target_id=target_user_id,
                    type=RelationshipType.FOLLOW,
                )
            )
            changed = True
            event_action = "add"
    else:
        for relationship in outgoing:
            if relationship.type == RelationshipType.FOLLOW:
                await session.delete(relationship)
                changed = True
        if changed:
            event_action = "delete"

    if changed and event_action is not None:
        if newly_followed:
            from app.service.soms_activity_service import stage_friend_notification

            await session.flush()
            saved_relationship = (
                await session.exec(
                    select(Relationship).where(
                        Relationship.user_id == viewer_user_id,
                        Relationship.target_id == target_user_id,
                        Relationship.type == RelationshipType.FOLLOW,
                    )
                )
            ).first()
            await stage_friend_notification(session, context.user, target_user_id, saved_relationship.id)
        elif not following:
            from app.service.soms_activity_service import stage_friend_notification

            removed_relationship = next(item for item in outgoing if item.type == RelationshipType.FOLLOW)
            await stage_friend_notification(
                session, context.user, target_user_id, removed_relationship.id, removed=True
            )
        await session.commit()
        _emit_profile_event(
            UserRelationshipChangedEvent(
                user_id=viewer_user_id,
                target_user_id=target_user_id,
                relationship_type=RelationshipType.FOLLOW.value,
                action=event_action,
            ),
            viewer_user_id,
        )

    response.headers["Cache-Control"] = "private, no-store"
    return await _web_friendship_payload(
        session,
        viewer_user_id=viewer_user_id,
        target_user_id=target_user_id,
    )


@router.get(f"{WEB_API_PREFIX}/users/{{identifier}}/achievements", tags=["Website"], include_in_schema=False)
async def get_web_user_achievements(
    identifier: str,
    session: Database,
    mode: ModeName = "osu",
    latest_limit: Annotated[int, Query(ge=1, le=32)] = 8,
):
    """Return only achievements that the public profile owner has unlocked."""

    user = await _find_public_user(session, identifier)
    records = list(
        (
            await session.exec(
                select(UserAchievement)
                .where(UserAchievement.user_id == user.id)
                .order_by(col(UserAchievement.achieved_at).desc(), col(UserAchievement.id).desc())
            )
        ).all()
    )
    return {
        "user": await _user_payload(session, user),
        **_achievement_collection_payload(records, mode=mode, latest_limit=latest_limit, include_locked=False),
    }


@router.get(f"{WEB_API_PREFIX}/me/achievements", tags=["Website"], include_in_schema=False)
async def get_web_own_achievements(
    context: WebSession,
    session: Database,
    response: Response,
    mode: ModeName = "osu",
    latest_limit: Annotated[int, Query(ge=1, le=32)] = 8,
):
    """Return the full medal catalogue, including locked medals, to its owner."""

    user_id = context.user.id
    records = list(
        (
            await session.exec(
                select(UserAchievement)
                .where(UserAchievement.user_id == user_id)
                .order_by(col(UserAchievement.achieved_at).desc(), col(UserAchievement.id).desc())
            )
        ).all()
    )
    response.headers["Cache-Control"] = "private, no-store"
    return {
        "user": await _user_payload(session, context.user),
        **_achievement_collection_payload(records, mode=mode, latest_limit=latest_limit, include_locked=True),
    }


@router.get(f"{WEB_API_PREFIX}/users/{{identifier}}/scores", tags=["Website"], include_in_schema=False)
async def get_web_user_scores(
    identifier: str,
    session: Database,
    mode: ModeName = "osu",
    type: Literal["best", "recent", "pinned", "first"] = "best",
    page: Annotated[int, Query(ge=1)] = 1,
    page_size: Annotated[int, Query(ge=5, le=WEB_BEST_SCORE_LIMIT)] = 20,
):
    user = await _find_public_user(session, identifier)
    game_mode = GameMode(mode)
    filters = [
        Score.user_id == user.id,
        Score.gamemode == game_mode,
        col(Score.processed).is_(True),
        col(Score.passed).is_(True),
    ]
    base = select(Score).where(*filters).options(_score_query_options())
    count_query = select(func.count()).select_from(Score).where(*filters)
    if type == "best":
        base = base.join(BestScore, col(BestScore.score_id) == col(Score.id)).order_by(
            col(Score.pp).desc(),
            col(Score.id).desc(),
        )
        count_query = count_query.join(BestScore, col(BestScore.score_id) == col(Score.id))
    elif type == "first":
        from app.service.web_beatmap_leaderboard_service import first_place_score_ids

        winners = first_place_score_ids(user.id, game_mode)
        base = base.where(col(Score.id).in_(winners)).order_by(col(Score.ended_at).desc(), col(Score.id).desc())
        count_query = count_query.where(col(Score.id).in_(winners))
    elif type == "pinned":
        base = base.where(Score.pinned_order > 0).order_by(col(Score.pinned_order), col(Score.id).desc())
        count_query = count_query.where(Score.pinned_order > 0)
    else:
        base = base.order_by(col(Score.ended_at).desc(), col(Score.id).desc())
    total, offset, effective_page_size = _score_page_window(
        type,
        page,
        page_size,
        int((await session.exec(count_query)).one()),
    )
    scores = (
        list((await session.exec(base.offset(offset).limit(effective_page_size))).all())
        if effective_page_size > 0
        else []
    )
    items = await _score_payloads(session, scores)
    if type == "best":
        for index, item in enumerate(items, start=offset):
            item["weight"] = 0.95**index
            item["weighted_pp"] = round(item["pp"] * item["weight"], 2)
    return {
        "items": items,
        "type": type,
        "mode": game_mode.value,
        "page": page,
        "page_size": page_size,
        "pages": max(1, (total + page_size - 1) // page_size),
        "total": total,
        "limit": WEB_BEST_SCORE_LIMIT if type == "best" else None,
    }


async def _invalidate_profile_caches(cache_service: UserCacheService, user_id: int, *, include_v1: bool) -> None:
    """Best-effort cache cleanup after a profile write is already durable."""

    invalidators = [cache_service.invalidate_user_all_cache]
    if include_v1:
        invalidators.append(cache_service.invalidate_v1_user_cache)
    for invalidator in invalidators:
        try:
            await invalidator(user_id)
        except Exception:
            logger.exception("Post-profile cache invalidation failed for user {}", user_id)


def _emit_profile_event(event: PluginEvent, user_id: int) -> None:
    """Do not report a failed save when only post-commit event delivery failed."""

    try:
        hub.emit(event)
    except Exception:
        logger.exception("Post-profile event emission failed for user {}", user_id)


async def _invalidate_score_pin_cache(cache_service: UserCacheService, user_id: int, mode: GameMode) -> None:
    """Keep a committed pin mutation successful during a cache outage."""

    try:
        await cache_service.invalidate_user_scores_cache(user_id, mode)
    except Exception:
        logger.exception("Post-score-pin cache invalidation failed for user {} in {}", user_id, mode.value)


def _reordered_pin_ids(
    ordered_ids: list[int],
    score_id: int,
    *,
    before_score_id: int | None,
    after_score_id: int | None,
) -> list[int]:
    return reordered_score_pin_ids(
        ordered_ids,
        score_id,
        before_score_id=before_score_id,
        after_score_id=after_score_id,
    )


async def _get_owned_score_pin_state(
    session: Database,
    user_id: int,
    score_id: int,
) -> tuple[Score, list[Score], bool]:
    state = await lock_score_pin_state(session, user_id, score_id)
    if state.score is None:
        # Keep ownership private: another user's score is indistinguishable
        # from a nonexistent score.
        raise HTTPException(status_code=404, detail="Score not found")
    return state.score, state.pinned_scores, state.order_normalised


@router.put(f"{WEB_API_PREFIX}/me/score-pins/{{score_id}}", tags=["Website"], include_in_schema=False)
async def pin_web_score(
    score_id: int,
    request: Request,
    context: WebSession,
    session: Database,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot pin scores")

    user_id = context.user.id
    score, pinned, order_normalised = await _get_owned_score_pin_state(session, user_id, score_id)
    if not score.processed or not score.passed:
        raise HTTPException(status_code=422, detail="Only processed, passed scores can be pinned")
    game_mode = score.gamemode
    pinned_order = score.pinned_order
    changed = order_normalised
    if score.pinned_order <= 0:
        pinned_order = len(pinned) + 1
        score.pinned_order = pinned_order
        session.add(score)
        changed = True
    if changed:
        await session.commit()
    # AsyncSession expires ORM attributes on commit. Only use captured scalar
    # values from here so a durable mutation cannot turn into a false 500.
    await _invalidate_score_pin_cache(cache_service, user_id, game_mode)
    return {"score_id": score_id, "pinned": True, "pinned_order": pinned_order}


@router.delete(f"{WEB_API_PREFIX}/me/score-pins/{{score_id}}", tags=["Website"], include_in_schema=False)
async def unpin_web_score(
    score_id: int,
    request: Request,
    context: WebSession,
    session: Database,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot unpin scores")

    user_id = context.user.id
    score, pinned, order_normalised = await _get_owned_score_pin_state(session, user_id, score_id)
    game_mode = score.gamemode
    changed = order_normalised
    if score.pinned_order > 0:
        removed_order = score.pinned_order
        for item in pinned:
            if item.id == score.id:
                item.pinned_order = 0
            elif item.pinned_order > removed_order:
                item.pinned_order -= 1
            session.add(item)
        changed = True
    if changed:
        await session.commit()
    await _invalidate_score_pin_cache(cache_service, user_id, game_mode)
    return {"score_id": score_id, "pinned": False, "pinned_order": None}


@router.patch(f"{WEB_API_PREFIX}/me/score-pins/{{score_id}}/reorder", tags=["Website"], include_in_schema=False)
async def reorder_web_score_pin(
    score_id: int,
    payload: WebScorePinReorderRequest,
    request: Request,
    context: WebSession,
    session: Database,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot reorder scores")

    user_id = context.user.id
    score, pinned, order_normalised = await _get_owned_score_pin_state(session, user_id, score_id)
    if score.pinned_order <= 0:
        raise HTTPException(status_code=422, detail="Score is not pinned")
    game_mode = score.gamemode
    current_ids = [item.id for item in pinned]
    try:
        ordered_ids = _reordered_pin_ids(
            current_ids,
            score_id,
            before_score_id=payload.before_score_id,
            after_score_id=payload.after_score_id,
        )
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    by_id = {item.id: item for item in pinned}
    for pinned_order, pinned_id in enumerate(ordered_ids, start=1):
        item = by_id[pinned_id]
        item.pinned_order = pinned_order
        session.add(item)
    pinned_order = ordered_ids.index(score_id) + 1
    if order_normalised or ordered_ids != current_ids:
        await session.commit()
    await _invalidate_score_pin_cache(cache_service, user_id, game_mode)
    return {
        "score_id": score_id,
        "pinned": True,
        "pinned_order": pinned_order,
        "ordered_score_ids": ordered_ids,
    }


@router.patch(f"{WEB_API_PREFIX}/me/profile", tags=["Website"], include_in_schema=False)
async def update_web_profile(
    payload: WebProfileUpdateRequest,
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot edit profiles")
    changed = payload.model_fields_set
    if not changed:
        raise HTTPException(status_code=422, detail="No profile changes supplied")
    if payload.country_code is not None and payload.country_code not in {*COUNTRIES, "XX"}:
        raise HTTPException(status_code=422, detail="Unknown country code")

    for field in {"location", "interests", "occupation", "discord"} & changed:
        setattr(context.user, field, getattr(payload, field) or None)
    if "website" in changed:
        website = payload.website or None
        if website and not website.startswith(("http://", "https://")):
            website = "https://" + website
        context.user.website = website
    if payload.country_code is not None:
        context.user.country_code = payload.country_code
    if payload.playmode is not None:
        context.user.playmode = GameMode(payload.playmode)
        context.user.g0v0_playmode = GameMode(payload.playmode)

    session.add(context.user)
    await session.commit()
    await session.refresh(context.user)
    await _invalidate_profile_caches(cache_service, context.user.id, include_v1=True)
    if "country_code" in changed:
        ranking_cache = get_ranking_cache_service(redis)
        for invalidator in (ranking_cache.invalidate_cache, ranking_cache.invalidate_country_cache):
            try:
                await invalidator()
            except Exception:
                logger.exception("Post-profile ranking cache invalidation failed for user {}", context.user.id)
    _emit_profile_event(
        UserPreferencesUpdatedEvent(user_id=context.user.id, action="update", updated_fields=sorted(changed)),
        context.user.id,
    )
    return await _user_payload(session, context.user, private=True)


@router.patch(f"{WEB_API_PREFIX}/me/profile-layout", tags=["Website"], include_in_schema=False)
async def update_web_profile_layout(
    payload: WebProfileLayoutRequest,
    request: Request,
    context: WebSession,
    session: Database,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot edit profiles")

    user_id = context.user.id
    preference = await session.get(UserPreference, user_id)
    if preference is None:
        preference = UserPreference(user_id=user_id)
    preference.extras_order = list(payload.order)
    profile_order = list(preference.extras_order)
    session.add(preference)
    await session.commit()
    await _invalidate_profile_caches(cache_service, user_id, include_v1=False)
    _emit_profile_event(
        UserPreferencesUpdatedEvent(
            user_id=user_id,
            action="update",
            updated_fields=["profile_order"],
        ),
        user_id,
    )
    return {"profile_order": profile_order}


def _render_web_userpage(body: str) -> dict[str, str]:
    if not body.strip():
        return {"raw": "", "html": ""}
    try:
        errors = bbcode_service.validate_bbcode(body)
    except TimeoutError as exc:
        raise HTTPException(status_code=422, detail="BBCode validation timed out") from exc
    if errors:
        raise HTTPException(status_code=422, detail={"message": "Invalid BBCode", "errors": errors})
    try:
        return bbcode_service.process_userpage_content(body)
    except UserpageError as exc:
        raise HTTPException(status_code=422, detail=exc.message) from exc


@router.post(f"{WEB_API_PREFIX}/me/userpage/preview", tags=["Website"], include_in_schema=False)
async def preview_web_userpage(
    payload: WebUserpageUpdateRequest,
    request: Request,
    context: WebSession,
):
    _require_csrf(request, context)
    return _render_web_userpage(payload.body)


@router.put(f"{WEB_API_PREFIX}/me/userpage", tags=["Website"], include_in_schema=False)
async def update_web_userpage(
    payload: WebUserpageUpdateRequest,
    request: Request,
    context: WebSession,
    session: Database,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot edit profiles")

    user_id = context.user.id
    page = _render_web_userpage(payload.body)
    context.user.page = Page(raw=page["raw"], html=page["html"])
    session.add(context.user)
    await session.commit()
    await _invalidate_profile_caches(cache_service, user_id, include_v1=False)
    _emit_profile_event(
        UserPageUpdatedEvent(
            user_id=user_id,
            raw_length=len(page["raw"]),
            html_length=len(page["html"]),
        ),
        user_id,
    )
    return {"profile_text": page["raw"], "profile_html": page["html"]}


async def _verify_web_security_password(
    session: Database,
    redis: Redis,
    context: WebContext,
    password: str,
    action: str,
) -> None:
    attempts = await _rate_increment(
        redis,
        f"web-site:security-rate:{context.user.id}:{action}",
        SECURITY_WINDOW_SECONDS,
    )
    if attempts > SECURITY_ATTEMPTS:
        raise HTTPException(status_code=429, detail="Too many security changes; try again later")
    authenticated = await authenticate_user(session, str(context.user.id), password)
    if authenticated is None or authenticated.id != context.user.id:
        raise HTTPException(status_code=400, detail="Current password is incorrect")


@router.patch(f"{WEB_API_PREFIX}/me/security/email", tags=["Website"], include_in_schema=False)
async def update_web_email(
    payload: WebEmailChangeRequest,
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    await _verify_web_security_password(session, redis, context, payload.current_password, "email")
    user_id = context.user.id
    email = str(payload.email).strip().casefold()
    duplicate = (
        await session.exec(select(User.id).where(func.lower(User.email) == email, col(User.id) != user_id).limit(1))
    ).first()
    if duplicate is not None:
        raise HTTPException(status_code=409, detail="Email is already registered")

    context.user.email = email
    session.add(context.user)
    await session.commit()
    await _invalidate_profile_caches(cache_service, user_id, include_v1=False)
    return {"email": email}


@router.patch(
    f"{WEB_API_PREFIX}/me/security/password",
    tags=["Website"],
    include_in_schema=False,
)
async def update_web_password(
    payload: WebPasswordChangeRequest,
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
):
    _require_csrf(request, context)
    await _verify_web_security_password(session, redis, context, payload.current_password, "password")
    if errors := validate_password(payload.new_password):
        raise HTTPException(status_code=422, detail={"message": "Invalid new password", "errors": errors})

    user_id = context.user.id
    context.user.pw_bcrypt = get_password_hash(payload.new_password)
    session.add(context.user)
    await session.execute(delete(TrustedDevice).where(col(TrustedDevice.user_id) == user_id))
    await session.execute(delete(LoginSession).where(col(LoginSession.user_id) == user_id))
    await session.execute(delete(OAuthToken).where(col(OAuthToken.user_id) == user_id))
    await session.commit()
    await invalidate_web_sessions(redis, user_id)
    return {"changed": True, "reauthenticate": True}


@router.patch(f"{WEB_API_PREFIX}/me/security/codeword", tags=["Website"], include_in_schema=False)
async def update_web_codeword(
    payload: WebCodewordUpdateRequest,
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
):
    _require_csrf(request, context)
    await _verify_web_security_password(session, redis, context, payload.current_password, "codeword")

    record = await session.get(UserRecoveryWord, context.user.id)
    if record is None:
        record = UserRecoveryWord(
            user_id=context.user.id,
            word_hash=get_password_hash(payload.codeword),
        )
    else:
        record.word_hash = get_password_hash(payload.codeword)
        record.updated_at = utcnow()
    session.add(record)
    await session.commit()
    return {"configured": True}


def _prepare_avatar(content: bytes) -> bytes:
    if not content:
        raise HTTPException(status_code=422, detail="Avatar file is empty")
    if len(content) > 5 * 1024 * 1024:
        raise HTTPException(status_code=413, detail="Avatar must be smaller than 5 MB")
    try:
        with Image.open(io.BytesIO(content)) as source:
            if source.format not in {"PNG", "JPEG", "GIF", "WEBP"}:
                raise HTTPException(status_code=422, detail="Use PNG, JPEG, GIF, or WebP")
            if source.width > 2048 or source.height > 2048 or source.width * source.height > 2048 * 2048:
                raise HTTPException(status_code=422, detail="Avatar dimensions must not exceed 2048x2048")
            source.load()
            image = ImageOps.exif_transpose(source).convert("RGBA")
            side = min(image.size)
            left = (image.width - side) // 2
            top = (image.height - side) // 2
            image = image.crop((left, top, left + side, top + side)).resize((256, 256), Image.Resampling.LANCZOS)
            output = io.BytesIO()
            image.save(output, format="PNG", optimize=True)
            return output.getvalue()
    except (Image.DecompressionBombError, UnidentifiedImageError, OSError) as exc:
        raise HTTPException(status_code=422, detail="The uploaded file is not a valid image") from exc


async def _invalidate_avatar_caches(cache_service: UserCacheService, user_id: int) -> None:
    """Best-effort cleanup after an avatar transaction is already durable."""

    for invalidator in (cache_service.invalidate_user_all_cache, cache_service.invalidate_v1_user_cache):
        try:
            await invalidator(user_id)
        except Exception:
            logger.exception("Post-avatar cache invalidation failed for user {}", user_id)


@router.post(f"{WEB_API_PREFIX}/me/avatar", tags=["Website"], include_in_schema=False)
async def upload_web_avatar(
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
    storage: StorageService,
    cache_service: UserCacheService,
    avatar: Annotated[UploadFile, File(...)],
):
    _require_csrf(request, context)
    user_id = context.user.id
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot change avatars")
    rate_key = f"web-site:avatar-rate:{user_id}"
    attempts = await _rate_increment(redis, rate_key, AVATAR_WINDOW_SECONDS)
    if attempts > AVATAR_ATTEMPTS:
        retry_after = max(1, int(await redis.ttl(rate_key)))
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many avatar uploads",
            headers={"Retry-After": str(retry_after)},
        )
    content = await avatar.read(5 * 1024 * 1024 + 1)
    image = await run_in_threadpool(_prepare_avatar, content)
    old_url = context.user.avatar_url
    old_path = storage.get_file_name_by_url(old_url) if old_url else None
    avatar_prefix = f"avatars/{user_id}_"
    if old_path is not None and not old_path.startswith(avatar_prefix):
        old_path = None
    digest = hashlib.sha256(image).hexdigest()
    path = f"avatars/{user_id}_{digest}.png"
    if old_path == path:
        return {"url": f"/users/{user_id}/avatar?v={digest[:12]}", "has_custom_avatar": True}
    await storage.write_file(path, image, "image/png")
    try:
        context.user.avatar_url = await storage.get_file_url(path)
        session.add(context.user)
        await session.commit()
    except Exception:
        await session.rollback()
        await storage.delete_file(path)
        raise
    if old_path and old_path != path:
        try:
            await storage.delete_file(old_path)
        except RuntimeError:
            logger.warning("Could not delete the previous avatar for user {}", user_id)
    # The image and database row are already committed. Cache expiry is
    # preferable to reporting a false failure for a successful upload.
    await _invalidate_avatar_caches(cache_service, user_id)
    return {"url": f"/users/{user_id}/avatar?v={digest[:12]}", "has_custom_avatar": True}


@router.delete(f"{WEB_API_PREFIX}/me/avatar", tags=["Website"], include_in_schema=False)
async def delete_web_avatar(
    request: Request,
    context: WebSession,
    session: Database,
    storage: StorageService,
    cache_service: UserCacheService,
):
    _require_csrf(request, context)
    user_id = context.user.id
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot change avatars")
    old_url = context.user.avatar_url
    old_path = storage.get_file_name_by_url(old_url) if old_url else None
    # Only remove this user's uploaded file after the database change succeeds.
    # Empty avatar_url is rendered as the bundled default by both avatar APIs.
    try:
        context.user.avatar_url = ""
        session.add(context.user)
        await session.commit()
    except Exception:
        await session.rollback()
        raise
    if old_path and old_path.startswith(f"avatars/{user_id}_"):
        try:
            await storage.delete_file(old_path)
        except Exception:
            logger.warning("Could not delete the previous avatar for user {}", user_id)
    await _invalidate_avatar_caches(cache_service, user_id)
    return {"url": "/site/soms-default-avatar.png", "has_custom_avatar": False}


def _search_item(item: dict[str, Any], local_policy: dict[str, Any] | None = None) -> dict[str, Any]:
    covers_value = item.get("covers")
    covers: dict[str, Any] = covers_value if isinstance(covers_value, dict) else {}
    beatmaps_value = item.get("beatmaps")
    beatmaps: list[Any] = beatmaps_value if isinstance(beatmaps_value, list) else []
    upstream_status_date = item.get("ranked_date")
    status_changed_at = local_policy.get("status_changed_at") if local_policy else upstream_status_date
    return {
        "id": item.get("id"),
        "artist": item.get("artist"),
        "title": item.get("title"),
        "creator": item.get("creator"),
        "status": item.get("status"),
        "ranked": item.get("ranked"),
        "bpm": item.get("bpm"),
        "play_count": item.get("play_count"),
        "favourite_count": item.get("favourite_count"),
        "cover_url": covers.get("cover@2x") or covers.get("cover") or covers.get("list@2x") or covers.get("list"),
        "preview_url": item.get("preview_url"),
        "ranked_date": upstream_status_date,
        "last_updated": item.get("last_updated"),
        "submitted_date": item.get("submitted_date"),
        "status_changed_at": status_changed_at,
        "beatmaps": [
            {
                "id": beatmap.get("id"),
                "version": beatmap.get("version"),
                "mode": beatmap.get("mode"),
                "difficulty_rating": beatmap.get("difficulty_rating"),
                "status": beatmap.get("status"),
                "ranked": beatmap.get("ranked"),
            }
            for beatmap in beatmaps
            if isinstance(beatmap, dict)
        ],
        "local_policy": local_policy,
    }


def _exact_beatmap_reference(value: str) -> tuple[Literal["set", "beatmap", "auto"], int] | None:
    cleaned = value.strip()
    if cleaned.isdigit():
        return "auto", int(cleaned)
    try:
        parsed = urlsplit(cleaned)
    except ValueError:
        return None
    if parsed.scheme not in {"http", "https"} or parsed.hostname not in {"osu.ppy.sh", "www.osu.ppy.sh"}:
        return None
    set_match = re.fullmatch(r"/beatmapsets/(\d+)(?:/)?", parsed.path)
    if set_match:
        return "set", int(set_match.group(1))
    beatmap_match = re.fullmatch(r"/(?:beatmaps|b)/(\d+)(?:/)?", parsed.path)
    if beatmap_match:
        return "beatmap", int(beatmap_match.group(1))
    return None


async def _fetch_exact_beatmapset(fetcher: Fetcher, value: str) -> dict[str, Any] | None:
    reference = _exact_beatmap_reference(value)
    if reference is None:
        return None
    kind, identifier = reference
    if identifier <= 0:
        return None

    async def fetch_set(beatmapset_id: int) -> dict[str, Any]:
        return dict(await fetcher.get_beatmapset(beatmapset_id))

    try:
        if kind in {"set", "auto"}:
            try:
                return await fetch_set(identifier)
            except HTTPStatusError as exc:
                if exc.response.status_code != 404 or kind == "set":
                    raise
        beatmap = await fetcher.get_beatmap(beatmap_id=identifier)
        beatmapset_value = beatmap.get("beatmapset")
        beatmapset_id = beatmap.get("beatmapset_id")
        if beatmapset_id is None and isinstance(beatmapset_value, dict):
            beatmapset_id = beatmapset_value.get("id")
        if not isinstance(beatmapset_id, int) or beatmapset_id <= 0:
            return None
        return await fetch_set(beatmapset_id)
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            return None
        raise HTTPException(status_code=503, detail="Beatmap search is temporarily unavailable") from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Beatmap search is temporarily unavailable") from exc


def _cursor_for_search_cache(cursor_string: str | None) -> dict[str, int | float]:
    if cursor_string is None:
        return {}
    try:
        decoded = json.loads(base64.b64decode(cursor_string, altchars=b"-_", validate=True))
    except (ValueError, TypeError, json.JSONDecodeError):
        decoded = None
    if isinstance(decoded, dict) and 0 < len(decoded) <= 16:
        cursor: dict[str, int | float] = {}
        for key, value in decoded.items():
            if (
                not isinstance(key, str)
                or not key
                or len(key) > 64
                or isinstance(value, bool)
                or not isinstance(value, (int, float))
                or not math.isfinite(value)
            ):
                break
            cursor[key] = value
        else:
            return cursor
    # CursorString is opaque by contract.  If it is not one of the legacy
    # base64-encoded numeric cursor dictionaries emitted by this fetcher, a
    # stable numeric marker still keeps its cache entry separate.
    return {"_web_cursor": int(hashlib.sha256(cursor_string.encode()).hexdigest()[:15], 16)}


@router.get(f"{WEB_API_PREFIX}/beatmapsets", tags=["Website"], include_in_schema=False)
async def search_web_beatmapsets(
    session: Database,
    redis: Redis,
    fetcher: Fetcher,
    q: Annotated[str, Query(max_length=120)] = "",
    mode: Literal["all", "osu", "taiko", "fruits", "mania", "osurx", "osuap"] = "all",
    status_filter: Annotated[
        Literal["any", "leaderboard", "ranked", "qualified", "loved", "pending", "wip", "graveyard"],
        Query(alias="status"),
    ] = "leaderboard",
    sort: Literal[
        "relevance_desc",
        "plays_desc",
        "favourites_desc",
        "ranked_desc",
        "updated_desc",
        "difficulty_desc",
    ] = "relevance_desc",
    cursor_string: Annotated[
        str | None,
        Query(min_length=1, max_length=512, pattern=r"^[A-Za-z0-9+/=_-]+$"),
    ] = None,
):
    mode_id = None if mode == "all" else int(GameMode(mode))
    query = SearchQueryModel(
        q=q.strip(),
        m=mode_id,
        g=Genre.ANY,
        s=status_filter,
        l=Language.ANY,
        sort=sort,
        nsfw=False,
    )
    exact_item = await _fetch_exact_beatmapset(fetcher, q)
    result: SearchBeatmapsetsResp | None = None
    source_items: list[dict[str, Any]] = []
    result_total = 0
    result_cursor: str | None = None
    cache_service = get_beatmapset_cache_service(redis)
    query_hash = generate_hash({"schema": 2, "query": query.model_dump()})
    # The shared fetcher deliberately excludes ``query.cursor_string`` from
    # its cache key.  Supply a numeric fingerprint as its cache cursor while
    # still forwarding the opaque cursor string to osu!, otherwise every
    # cursor-based page would collide with page one in the fetcher cache.
    cursor = _cursor_for_search_cache(cursor_string)
    cursor_hash = generate_hash(cursor)
    cached = None if exact_item is not None else await cache_service.get_search_from_cache(query_hash, cursor_hash)
    if exact_item is not None:
        source_items = [exact_item]
        result_total = 1
        result_cursor = None
    elif cached:
        result = SearchBeatmapsetsResp(**cached)
    else:
        fetch_query = query.model_copy(update={"cursor_string": cursor_string})
        try:
            result = await fetcher.search_beatmapset(fetch_query, cursor, redis)
        except HTTPError as exc:
            raise HTTPException(status_code=503, detail="Beatmap search is temporarily unavailable") from exc
        finally:
            # The fetcher schedules delayed prefetching and closes over this
            # model.  Clearing the incoming cursor makes that task advance
            # with the newly returned cursor instead of replaying this page.
            fetch_query.cursor_string = None
        await cache_service.cache_search_result(query_hash, cursor_hash, result.model_dump())
    if exact_item is None:
        assert result is not None
        result = await overlay_local_ranked_beatmapsets(
            session,
            query,
            result,
            current_user=None,
            first_page=cursor_string is None,
        )
        payload = result.model_dump(mode="json")
        source_items = [item for item in payload.get("beatmapsets", []) if isinstance(item, dict)]
        result_total = payload.get("total", 0)
        result_cursor = payload.get("cursor_string")
    item_ids = {int(item["id"]) for item in source_items if str(item.get("id", "")).isdigit()}
    local_policies: dict[int, dict[str, Any]] = {}
    if item_ids:
        set_policies = list(
            (
                await session.exec(
                    select(BeatmapsetRankingPolicy).where(
                        col(BeatmapsetRankingPolicy.beatmapset_id).in_(item_ids),
                        col(BeatmapsetRankingPolicy.is_active).is_(True),
                    )
                )
            ).all()
        )
        difficulty_policies = list(
            (
                await session.exec(
                    select(BeatmapRankingPolicy).where(
                        col(BeatmapRankingPolicy.beatmapset_id).in_(item_ids),
                        col(BeatmapRankingPolicy.is_active).is_(True),
                        col(BeatmapRankingPolicy.blocks_set_policy).is_(False),
                    )
                )
            ).all()
        )
        set_policy_ids = {policy.beatmapset_id for policy in set_policies}
        difficulty_policy_ids = {policy.beatmapset_id for policy in difficulty_policies}
        candidate_ids = set_policy_ids | difficulty_policy_ids
        local_sets = (
            (await session.exec(select(Beatmapset).where(col(Beatmapset.id).in_(candidate_ids)))).all()
            if candidate_ids
            else []
        )
        for beatmapset in local_sets:
            policy = await get_effective_beatmapset_policy(session, beatmapset)
            if policy.source.value != "upstream":
                changed_candidates = [
                    row.updated_at
                    for row in (*set_policies, *difficulty_policies)
                    if row.beatmapset_id == beatmapset.id
                ]
                local_policies[beatmapset.id] = {
                    "source": policy.source.value,
                    "status": policy.status.name.lower(),
                    "leaderboard_enabled": policy.leaderboard_enabled,
                    "pp_enabled": policy.pp_enabled,
                    "status_changed_at": max(changed_candidates) if changed_candidates else None,
                }
    return {
        "items": [_search_item(item, local_policies.get(int(item["id"]))) for item in source_items],
        "total": result_total,
        "cursor_string": result_cursor,
    }


@router.get(f"{WEB_API_PREFIX}/beatmapsets/{{beatmapset_id}}", tags=["Website"], include_in_schema=False)
async def get_web_beatmapset(
    beatmapset_id: int,
    session: Database,
    fetcher: Fetcher,
):
    if beatmapset_id <= 0:
        raise HTTPException(status_code=422, detail="Beatmapset ID must be positive")
    try:
        upstream = await fetcher.get_beatmapset(beatmapset_id)
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            raise HTTPException(status_code=404, detail="Beatmapset not found") from exc
        raise HTTPException(status_code=503, detail="Beatmap details are temporarily unavailable") from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Beatmap details are temporarily unavailable") from exc

    payload: dict[str, Any] = dict(upstream)
    result = _search_item(payload)
    result["description"] = payload.get("description")
    result["source"] = payload.get("source")
    result["tags"] = payload.get("tags")
    for key in ("user_id", "genre", "language", "ratings", "current_nominations", "availability"):
        value = payload.get(key)
        result[key] = value.model_dump() if hasattr(value, "model_dump") else value
    upstream_maps = {item["id"]: item for item in payload.get("beatmaps", [])}
    for item in result["beatmaps"]:
        source = upstream_maps.get(item["id"], {})
        for key in (
            "total_length",
            "hit_length",
            "max_combo",
            "bpm",
            "ar",
            "cs",
            "drain",
            "accuracy",
            "count_circles",
            "count_sliders",
            "count_spinners",
            "playcount",
            "passcount",
            "owners",
        ):
            value = source.get(key)
            item[key] = (
                [owner.model_dump() if hasattr(owner, "model_dump") else owner for owner in value]
                if key == "owners" and value
                else value
            )

    # Details are fetched read-only from osu!.  Existing local rows are used only
    # to overlay this server's ranking decisions; visiting a page never grows DB.
    beatmapset = await session.get(Beatmapset, beatmapset_id)
    if beatmapset is not None:
        beatmaps = list((await session.exec(select(Beatmap).where(Beatmap.beatmapset_id == beatmapset_id))).all())
        policies = await get_effective_beatmap_policies(session, beatmaps)
        set_policy = await get_effective_beatmapset_policy(session, beatmapset)
        if set_policy.source.value != "upstream":
            result["local_policy"] = {
                "status": set_policy.status.name.lower(),
                "leaderboard_enabled": set_policy.leaderboard_enabled,
                "pp_enabled": set_policy.pp_enabled,
                "source": set_policy.source.value,
            }
        local_beatmaps = {beatmap.id: beatmap for beatmap in beatmaps}
        for item in result["beatmaps"]:
            beatmap = local_beatmaps.get(item["id"])
            if beatmap is None:
                continue
            policy = policies[beatmap.id]
            item.update(
                {
                    "total_length": beatmap.total_length,
                    "max_combo": beatmap.max_combo,
                    "status": policy.status.name.lower(),
                    "leaderboard_enabled": policy.leaderboard_enabled,
                    "pp_enabled": policy.pp_enabled,
                    "policy_source": policy.source.value,
                }
            )
    return result


async def _refresh_web_ranking_target(beatmapset_id: int) -> None:
    try:
        await get_beatmapset_update_service().refresh_beatmapset(beatmapset_id)
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            raise HTTPException(status_code=404, detail="Beatmapset not found") from exc
        raise HTTPException(status_code=503, detail="Beatmap details are temporarily unavailable") from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Beatmap details are temporarily unavailable") from exc


@router.post(
    f"{WEB_API_PREFIX}/beatmapsets/{{beatmapset_id}}/moderation",
    tags=["Website"],
    include_in_schema=False,
)
async def moderate_web_beatmapset(
    beatmapset_id: int,
    payload: WebBeatmapModerationRequest,
    request: Request,
    context: WebSession,
    session: Database,
):
    """Apply a set-wide local status from the community beatmap page."""

    _require_csrf(request, context)
    _require_web_permission(context, "beatmap_moderation")
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot moderate beatmaps")
    if beatmapset_id <= 0:
        raise HTTPException(status_code=422, detail="Beatmapset ID must be positive")

    await _refresh_web_ranking_target(beatmapset_id)
    actor_user_id = context.user.id
    reason = f"Website {payload.action} action by {context.user.username}"
    response_status = BeatmapRankStatus.GRAVEYARD
    leaderboard_enabled = False
    pp_enabled = False
    try:
        async with with_db() as mutation_session:
            if payload.action == "unrank":
                await apply_local_unrank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=beatmapset_id,
                    replace_difficulty_overrides=True,
                    reason=reason,
                )
            else:
                rank_status = BeatmapRankStatus.LOVED if payload.action == "love" else BeatmapRankStatus.RANKED
                await apply_local_rank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=beatmapset_id,
                    status=rank_status,
                    leaderboard_enabled=True,
                    pp_enabled=rank_status.has_pp(),
                    replace_difficulty_overrides=True,
                    reason=reason,
                )
                response_status = rank_status
                leaderboard_enabled = True
                pp_enabled = rank_status.has_pp()
    except LookupError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc

    if payload.action == "unrank":
        async with with_db() as state_session:
            beatmapset = await state_session.get(Beatmapset, beatmapset_id)
            if beatmapset is None:
                raise HTTPException(status_code=404, detail="Beatmapset not found")
            effective = await get_effective_beatmapset_policy(state_session, beatmapset)
            response_status = effective.status
            leaderboard_enabled = effective.leaderboard_enabled
            pp_enabled = effective.pp_enabled
    return {
        "ok": True,
        "action": payload.action,
        "beatmapset_id": beatmapset_id,
        "status": int(response_status),
        "leaderboard_enabled": leaderboard_enabled,
        "pp_enabled": pp_enabled,
    }


@router.get(f"{WEB_API_PREFIX}/beatmaps/{{beatmap_id}}/scores", tags=["Website"], include_in_schema=False)
async def get_web_beatmap_scores(
    beatmap_id: int,
    session: Database,
    request: Request,
    redis: Redis,
    mode: ModeName | None = None,
    page: Annotated[int, Query(ge=1)] = 1,
    page_size: Annotated[int, Query(ge=5, le=100)] = 50,
    scope: Literal["global", "country", "friends"] = "global",
    mods: Annotated[str | None, Query(max_length=100)] = None,
):
    from app.service.negative_pp_service import negative_map_ids
    from app.service.web_beatmap_leaderboard_service import leaderboard_page

    viewer = await _optional_web_user(request, session, redis)
    board = await leaderboard_page(session, beatmap_id, mode, page, page_size, viewer, scope, mods)
    negative_maps = await negative_map_ids(session, [beatmap_id])

    async def serialize(row):
        if row is None:
            return None
        score, user, position = row
        negative = beatmap_id in negative_maps and score.ranked and score.passed and score.pp > 0
        return {
            "rank": int(position),
            "user": await _user_payload(session, user),
            "score": {
                "id": score.id,
                "pp": round(float(score.pp) * (-1 if negative else 1), 2),
                "negative_pp": negative,
                "total_score": score.total_score,
                "accuracy": score.accuracy,
                "max_combo": score.max_combo,
                "rank": score.rank.value,
                "ruleset": board["mode"],
                "statistics": {
                    "great": score.n300,
                    "ok": score.n100,
                    "meh": score.n50,
                    "miss": score.nmiss,
                    "perfect": score.ngeki,
                    "good": score.nkatu,
                },
                "mods": score.mods,
                "ended_at": _as_utc(score.ended_at),
                "has_replay": score.has_replay,
                "replay_url": f"/api/v2/scores/{score.id}/download" if score.has_replay else None,
            },
        }

    return {
        "beatmap_id": beatmap_id,
        "mode": board["mode"],
        "scope": scope,
        "total": board["total"],
        "page": page,
        "page_size": page_size,
        "pages": max(1, (board["total"] + page_size - 1) // page_size),
        "items": [await serialize(row) for row in board["rows"]],
        "top_score": await serialize(board["top"]),
        "user_score": await serialize(board["personal"]),
    }


async def _invalidate_deleted_score_caches(
    cache_service: UserCacheService,
    redis: Redis,
    user_id: int,
) -> None:
    ranking_cache = get_ranking_cache_service(redis)
    invalidators = [
        (cache_service.invalidate_user_all_cache, (user_id,)),
        (cache_service.invalidate_v1_user_cache, (user_id,)),
        (ranking_cache.invalidate_cache, ()),
        (ranking_cache.invalidate_country_cache, ()),
        (ranking_cache.invalidate_team_cache, ()),
        (ranking_cache.invalidate_top_scores_cache, ()),
    ]
    for invalidator, arguments in invalidators:
        try:
            await invalidator(*arguments)
        except Exception:
            logger.exception("Post-deletion cache invalidation failed for user {}", user_id)


@router.delete(f"{WEB_API_PREFIX}/scores/{{score_id}}", tags=["Website"], include_in_schema=False)
async def delete_web_score(
    score_id: int,
    request: Request,
    context: WebSession,
    session: Database,
    redis: Redis,
    storage: StorageService,
    cache_service: UserCacheService,
):
    """Delete any score as an administrator and rebuild the affected profile."""

    _require_csrf(request, context)
    _require_web_permission(context, "score_delete")
    if await context.user.is_restricted(session):
        raise HTTPException(status_code=403, detail="Restricted accounts cannot delete scores")

    score = await session.get(Score, score_id)
    if score is None:
        raise HTTPException(status_code=404, detail="Score not found")
    target = await session.get(User, score.user_id)
    if target is None:
        raise HTTPException(status_code=404, detail="Score owner not found")
    target_user_id = target.id

    result = await delete_user_score(session, redis, target, score_id)
    if result is None:
        raise HTTPException(status_code=404, detail="Score not found")
    session.add(
        AdminAuditEvent(
            actor_user_id=context.user.id,
            actor_username=context.user.username,
            action="score.delete",
            target_type="score",
            target_id=str(score_id),
            reason="Deleted from the community site score menu",
            before={
                "user_id": target_user_id,
                "ruleset": result.ruleset.value,
                "has_replay": bool(result.replay_paths),
            },
            after=None,
            ip_address=_request_ip(request),
        )
    )
    await session.commit()

    for replay_path in result.replay_paths:
        try:
            await storage.delete_file(replay_path)
        except Exception:
            logger.exception("Could not remove stored replay {} after score deletion", replay_path)
    await _invalidate_deleted_score_caches(cache_service, redis, target_user_id)
    try:
        hub.emit(ScoreDeletedEvent(score=result.score_data))
    except Exception:
        logger.exception("Post-delete score event failed for score {}", score_id)
    return {"deleted_score_id": score_id}


@router.get(f"{WEB_API_PREFIX}/beatmapsets/{{beatmapset_id}}/download", tags=["Website"], include_in_schema=False)
async def download_web_beatmapset(
    beatmapset_id: int,
    request: Request,
    context: WebSession,
    download_service: DownloadService,
    fetcher: Fetcher,
    no_video: bool = False,
):
    if beatmapset_id <= 0:
        raise HTTPException(status_code=422, detail="Beatmapset ID must be positive")
    try:
        upstream = await fetcher.get_beatmapset(beatmapset_id)
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            raise HTTPException(status_code=404, detail="Beatmapset not found") from exc
        raise HTTPException(status_code=503, detail="Could not verify beatmap download") from exc
    except HTTPError as exc:
        raise HTTPException(status_code=503, detail="Could not verify beatmap download") from exc
    availability = upstream.get("availability")
    download_disabled = (
        availability.get("download_disabled")
        if isinstance(availability, dict)
        else getattr(availability, "download_disabled", None)
    )
    if download_disabled is None:
        raise HTTPException(status_code=503, detail="Could not verify beatmap download")
    if download_disabled:
        raise HTTPException(status_code=403, detail="Beatmap download is disabled upstream")

    geo = get_geoip_helper().lookup(_request_ip(request))
    country = geo.get("country_iso", "")
    is_china = country == "CN" or (not country and context.user.country_code == "CN")
    url = download_service.get_download_url(beatmapset_id, no_video, is_china)
    return RedirectResponse(url)


__all__ = [
    "WebCodewordUpdateRequest",
    "WebContext",
    "WebEmailChangeRequest",
    "WebLoginRequest",
    "WebPasswordChangeRequest",
    "WebProfileLayoutRequest",
    "WebProfileUpdateRequest",
    "WebRegisterRequest",
    "WebUserpageUpdateRequest",
    "require_web_session",
]
