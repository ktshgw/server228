"""Opaque same-origin browser sessions for the public community website."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
import hashlib
import json
import secrets

from app.helpers import utcnow

from fastapi import Request
from redis.asyncio import Redis

WEB_SESSION_COOKIE = "__Host-private_osu_web"
WEB_SESSION_KEY_PREFIX = "web-site:session:"
WEB_USER_SESSIONS_PREFIX = "web-site:user-sessions:"
WEB_SESSION_TTL_SECONDS = 7 * 24 * 60 * 60


@dataclass(slots=True, frozen=True)
class WebSessionData:
    user_id: int
    csrf_token: str
    digest: str


class InvalidWebSessionError(ValueError):
    """Raised when a browser session is absent, expired, or browser-bound elsewhere."""


def web_session_digest(token: str) -> str:
    return hashlib.sha256(token.encode()).hexdigest()


def _session_key(digest: str) -> str:
    return f"{WEB_SESSION_KEY_PREFIX}{digest}"


def _user_sessions_key(user_id: int) -> str:
    return f"{WEB_USER_SESSIONS_PREFIX}{user_id}"


def _user_agent_digest(request: Request) -> str:
    return hashlib.sha256(request.headers.get("user-agent", "").encode()).hexdigest()


async def create_web_session(redis: Redis, user_id: int, request: Request) -> tuple[str, WebSessionData]:
    token = secrets.token_urlsafe(48)
    digest = web_session_digest(token)
    csrf_token = secrets.token_urlsafe(32)
    payload = {
        "user_id": user_id,
        "csrf_token": csrf_token,
        "created_at": utcnow().isoformat(),
        "user_agent_hash": _user_agent_digest(request),
    }
    await redis.setex(_session_key(digest), WEB_SESSION_TTL_SECONDS, json.dumps(payload))
    membership_key = _user_sessions_key(user_id)
    await redis.sadd(membership_key, digest)
    await redis.expire(membership_key, WEB_SESSION_TTL_SECONDS)
    return token, WebSessionData(user_id=user_id, csrf_token=csrf_token, digest=digest)


async def read_web_session(redis: Redis, request: Request) -> WebSessionData:
    token = request.cookies.get(WEB_SESSION_COOKIE)
    if not token:
        raise InvalidWebSessionError("login_required")

    digest = web_session_digest(token)
    raw = await redis.get(_session_key(digest))
    if not raw:
        raise InvalidWebSessionError("expired")

    try:
        payload = json.loads(raw)
        user_id = int(payload["user_id"])
        csrf_token = str(payload["csrf_token"])
        created_at = datetime.fromisoformat(str(payload["created_at"]))
        expected_user_agent = str(payload["user_agent_hash"])
    except (KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        await redis.delete(_session_key(digest))
        raise InvalidWebSessionError("invalid") from exc

    if created_at.tzinfo is None or utcnow() - created_at >= timedelta(seconds=WEB_SESSION_TTL_SECONDS):
        await redis.delete(_session_key(digest))
        await redis.srem(_user_sessions_key(user_id), digest)
        raise InvalidWebSessionError("expired")

    if not secrets.compare_digest(expected_user_agent, _user_agent_digest(request)):
        await redis.delete(_session_key(digest))
        await redis.srem(_user_sessions_key(user_id), digest)
        raise InvalidWebSessionError("browser_changed")

    return WebSessionData(user_id=user_id, csrf_token=csrf_token, digest=digest)


async def delete_web_session(redis: Redis, data: WebSessionData) -> None:
    await redis.delete(_session_key(data.digest))
    await redis.srem(_user_sessions_key(data.user_id), data.digest)


async def invalidate_web_sessions(redis: Redis, user_id: int) -> None:
    membership_key = _user_sessions_key(user_id)
    digests = await redis.smembers(membership_key)
    if digests:
        await redis.delete(*[_session_key(str(digest)) for digest in digests])
    await redis.delete(membership_key)


__all__ = [
    "WEB_SESSION_COOKIE",
    "WEB_SESSION_TTL_SECONDS",
    "InvalidWebSessionError",
    "WebSessionData",
    "create_web_session",
    "delete_web_session",
    "invalidate_web_sessions",
    "read_web_session",
    "web_session_digest",
]
