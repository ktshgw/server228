"""BanchoBot-facing command adapter for local beatmap ranking."""

from dataclasses import dataclass
import re

from app.database import User
from app.database.beatmap import Beatmap
from app.dependencies.database import with_db
from app.dependencies.fetcher import get_fetcher
from app.dependencies.staff import StaffRole, has_staff_role
from app.models.beatmap import BeatmapRankStatus
from app.service.beatmap_ranking_service import apply_local_rank, clear_local_rank
from app.service.beatmapset_update_service import get_beatmapset_update_service

from httpx import HTTPStatusError
from sqlmodel.ext.asyncio.session import AsyncSession


@dataclass(frozen=True, slots=True)
class RankingCommandTarget:
    """A set or individual difficulty parsed from a chat command."""

    beatmapset_id: int | None = None
    beatmap_id: int | None = None


_SET_TOKEN = re.compile(r"^(?:set|s)(?P<id>\d+)$", re.IGNORECASE)
_BEATMAP_TOKEN = re.compile(r"^(?:beatmap|diff|b)(?P<id>\d+)$", re.IGNORECASE)
_SET_URL = re.compile(r"/beatmapsets/(?P<set_id>\d+)(?:#[^/\s]+/(?P<beatmap_id>\d+))?", re.IGNORECASE)
_BEATMAP_URL = re.compile(r"/beatmaps/(?P<beatmap_id>\d+)", re.IGNORECASE)

_SET_SELECTORS = {"set", "s", "beatmapset"}
_BEATMAP_SELECTORS = {"beatmap", "b", "diff", "difficulty"}


def _positive_id(value: str) -> int:
    target_id = int(value)
    if target_id <= 0:
        raise ValueError("target ID must be positive")
    return target_id


def parse_ranking_target(args: list[str]) -> tuple[RankingCommandTarget, list[str]]:
    """Parse a target and leave the remaining arguments as an audit reason.

    A bare numeric ID means a beatmapset, matching the server's default of
    ranking the full set.  Individual difficulties must be explicit (for
    example ``diff 123`` or ``b123``) or supplied as a difficulty URL.
    """

    if not args:
        raise ValueError("missing target")

    first = args[0]
    lowered = first.lower()
    if lowered in _SET_SELECTORS | _BEATMAP_SELECTORS:
        if len(args) < 2 or not args[1].isdigit() or int(args[1]) <= 0:
            raise ValueError(f"{first} must be followed by a positive numeric ID")
        target_id = _positive_id(args[1])
        target = (
            RankingCommandTarget(beatmapset_id=target_id)
            if lowered in _SET_SELECTORS
            else RankingCommandTarget(beatmap_id=target_id)
        )
        return target, args[2:]

    match = _SET_TOKEN.fullmatch(first)
    if match:
        return RankingCommandTarget(beatmapset_id=_positive_id(match.group("id"))), args[1:]

    match = _BEATMAP_TOKEN.fullmatch(first)
    if match:
        return RankingCommandTarget(beatmap_id=_positive_id(match.group("id"))), args[1:]

    match = _SET_URL.search(first)
    if match:
        beatmap_id = match.group("beatmap_id")
        if beatmap_id is not None:
            return RankingCommandTarget(beatmap_id=_positive_id(beatmap_id)), args[1:]
        return RankingCommandTarget(beatmapset_id=_positive_id(match.group("set_id"))), args[1:]

    match = _BEATMAP_URL.search(first)
    if match:
        return RankingCommandTarget(beatmap_id=_positive_id(match.group("beatmap_id"))), args[1:]

    if first.isdigit() and int(first) > 0:
        return RankingCommandTarget(beatmapset_id=_positive_id(first)), args[1:]

    raise ValueError("target must be a set/difficulty ID or beatmap URL")


async def _resolve_target(
    target: RankingCommandTarget,
    session: AsyncSession,
    *,
    force_refresh: bool,
) -> tuple[int, int | None]:
    if not force_refresh:
        if target.beatmap_id is None:
            assert target.beatmapset_id is not None
            return target.beatmapset_id, None
        beatmap = await session.get(Beatmap, target.beatmap_id)
        if beatmap is None:
            raise LookupError(f"Beatmap {target.beatmap_id} is not cached on this server")
        return beatmap.beatmapset_id, beatmap.id

    fetcher = await get_fetcher()
    if target.beatmap_id is not None:
        beatmap = await fetcher.get_beatmap(target.beatmap_id)
        beatmapset_id = beatmap.get("beatmapset_id")
        if beatmapset_id is None:
            raise ValueError("upstream beatmap response has no beatmapset_id")
        snapshot = await get_beatmapset_update_service().refresh_beatmapset(beatmapset_id)
        if not any(item["id"] == target.beatmap_id for item in snapshot["beatmaps"]):
            raise LookupError(f"Beatmap {target.beatmap_id} is not present in beatmapset {beatmapset_id}")
        return beatmapset_id, target.beatmap_id

    assert target.beatmapset_id is not None
    snapshot = await get_beatmapset_update_service().refresh_beatmapset(target.beatmapset_id)
    return snapshot["id"], None


async def execute_ranking_command(
    command: str,
    user: User,
    args: list[str],
    session: AsyncSession,
) -> str:
    """Authorize and execute ``rank``, ``love``, or ``unrank``."""

    if not user.is_active or await user.is_restricted(session):
        return "Permission denied: restricted or inactive accounts cannot use ranking commands."
    if not has_staff_role(user, StaffRole.OWNER):
        return "Permission denied: this command is restricted to the server owner."
    if command not in {"rank", "love", "unrank"}:
        raise ValueError(f"Unsupported ranking command: {command}")

    usage = f"Usage: !{command} <set ID | set <ID> | diff <ID> | beatmap URL> [reason]"
    try:
        target, reason_parts = parse_ranking_target(args)
    except ValueError as exc:
        return f"{exc}. {usage}"

    actor_user_id = user.id
    actor_username = user.username
    reason = " ".join(reason_parts).strip() or f"BanchoBot !{command} by {actor_username}"
    if len(reason) > 1000:
        return "Reason is too long (maximum 1000 characters)."

    try:
        beatmapset_id, beatmap_id = await _resolve_target(
            target,
            session,
            force_refresh=command != "unrank",
        )
        async with with_db() as mutation_session:
            if command == "unrank":
                await clear_local_rank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=beatmapset_id,
                    beatmap_id=beatmap_id,
                    reason=reason,
                )
                action = "Cleared local rank"
            else:
                status = BeatmapRankStatus.LOVED if command == "love" else BeatmapRankStatus.RANKED
                await apply_local_rank(
                    mutation_session,
                    actor_user_id=actor_user_id,
                    beatmapset_id=beatmapset_id,
                    beatmap_id=beatmap_id,
                    status=status,
                    leaderboard_enabled=True,
                    pp_enabled=status.has_pp(),
                    reason=reason,
                )
                action = f"Applied local {status.name.lower()}"
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            return "The requested beatmap or beatmapset was not found upstream."
        raise
    except LookupError as exc:
        return f"Ranking target not found: {exc}"
    except ValueError as exc:
        return f"Ranking request rejected: {exc}"

    scope = f"difficulty {beatmap_id}" if beatmap_id is not None else f"beatmapset {beatmapset_id}"
    return f"{action} to {scope}."


__all__ = ["RankingCommandTarget", "execute_ranking_command", "parse_ranking_target"]
