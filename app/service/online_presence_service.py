"""Read live osu! client presence from the spectator metadata heartbeat keys.

The spectator server creates ``metadata:online:{user_id}`` while a metadata
SignalR connection is alive and removes it on disconnect.  The key also has a
TTL, so this is a safer source for dashboards than the denormalised
``lazer_users.is_online`` flag, which can remain stale after an unclean stop.
"""

from collections.abc import AsyncIterator
from typing import Any

from redis.asyncio import Redis

ONLINE_PRESENCE_KEY_PREFIX = "metadata:online:"
ONLINE_PRESENCE_TTL_SECONDS = 2 * 60 * 60


def _user_id_from_presence_key(raw_key: Any) -> int | None:
    """Return a validated user id from a spectator presence key."""
    if isinstance(raw_key, bytes):
        try:
            key = raw_key.decode("ascii")
        except UnicodeDecodeError:
            return None
    elif isinstance(raw_key, str):
        key = raw_key
    else:
        return None

    if not key.startswith(ONLINE_PRESENCE_KEY_PREFIX):
        return None
    raw_user_id = key.removeprefix(ONLINE_PRESENCE_KEY_PREFIX)
    if not raw_user_id.isdecimal():
        return None
    user_id = int(raw_user_id)
    return user_id if user_id > 0 else None


async def _iter_presence_keys(redis: Redis, *, scan_count: int) -> AsyncIterator[Any]:
    async for key in redis.scan_iter(match=f"{ONLINE_PRESENCE_KEY_PREFIX}*", count=scan_count):
        yield key


async def get_online_user_ids(redis: Redis, *, scan_count: int = 100) -> list[int]:
    """Return sorted, unique IDs with a live spectator metadata connection.

    ``SCAN`` is deliberately used instead of ``KEYS`` so this remains safe if
    the server grows.  Malformed or unrelated keys are ignored.
    """
    user_ids: set[int] = set()
    async for raw_key in _iter_presence_keys(redis, scan_count=max(1, scan_count)):
        if (user_id := _user_id_from_presence_key(raw_key)) is not None:
            user_ids.add(user_id)
    return sorted(user_ids)


async def count_online_users(redis: Redis) -> int:
    """Return the number of distinct live spectator metadata connections."""
    return len(await get_online_user_ids(redis))


async def refresh_online_presence(
    redis: Redis,
    user_id: int,
    *,
    ttl_seconds: int = ONLINE_PRESENCE_TTL_SECONDS,
) -> bool:
    """Extend an existing spectator marker after authenticated client traffic.

    ``XX`` prevents a normal website or third-party API login from creating an
    online marker.  Spectator creates the marker; lazer's periodic API traffic
    only keeps it alive during a long uninterrupted connection.
    """
    if user_id <= 0:
        return False
    return bool(
        await redis.expire(
            f"{ONLINE_PRESENCE_KEY_PREFIX}{user_id}",
            max(1, ttl_seconds),
            xx=True,
        )
    )
