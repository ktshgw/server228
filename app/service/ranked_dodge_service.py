"""Ranked dodge penalties. Recovery requires 48 hours after the previous ban ends.

Calendar months/years are used for the final temporary steps. A duel never calls
this service: spectator distinguishes it using the room's unranked pool snapshot.
"""

import calendar
from datetime import UTC, datetime, timedelta

from app.database import (
    AdminAuditEvent,
    LoginSession,
    OAuthToken,
    RankedDodgePenalty,
    Room,
    TrustedDevice,
    User,
)
from app.helpers import utcnow
from app.models.ranked_dodge import RankedDodgeStatus
from app.models.room import MatchType

from sqlalchemy import delete
from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


def as_utc(value: datetime) -> datetime:
    return value.replace(tzinfo=UTC) if value.tzinfo is None else value.astimezone(UTC)


def penalty_end(now: datetime, level: int) -> datetime | None:
    minutes = (5, 15, 30, 60, 90, 120, 1440, 10080, 20160, 43200)
    if 1 <= level <= len(minutes):
        return now + timedelta(minutes=minutes[level - 1])
    if level in (11, 12, 13):
        months = {11: 3, 12: 6, 13: 12}[level]
        year, month = divmod(now.year * 12 + now.month - 1 + months, 12)
        month += 1
        return now.replace(year=year, month=month, day=min(now.day, calendar.monthrange(year, month)[1]))
    if level == 14:
        return None
    raise ValueError("Invalid Ranked dodge penalty level")


def status_for(penalty: RankedDodgePenalty) -> RankedDodgeStatus:
    return RankedDodgeStatus(
        user_id=penalty.user_id,
        level=penalty.level,
        expires_at=as_utc(penalty.expires_at) if penalty.expires_at else None,
        account_banned=penalty.account_banned,
    )


async def latest_penalty(session: AsyncSession, user_id: int) -> RankedDodgePenalty | None:
    return (
        await session.exec(
            select(RankedDodgePenalty)
            .where(RankedDodgePenalty.user_id == user_id)
            .order_by(
                col(RankedDodgePenalty.created_at).desc(),
                col(RankedDodgePenalty.level).desc(),
                col(RankedDodgePenalty.room_id).desc(),
            )
            .limit(1)
        )
    ).first()


async def dodge_status(session: AsyncSession, user_id: int) -> RankedDodgeStatus:
    active = (await session.exec(select(User.is_active).where(User.id == user_id))).first()
    if active is None:
        raise ValueError("Player not found")
    previous = await latest_penalty(session, user_id)
    result = status_for(previous) if previous else RankedDodgeStatus(user_id=user_id)
    # Manual account reinstatement remains authoritative. Temporary queue bans
    # retain their own expiry, but a permanent ban is represented by is_active.
    result.account_banned = not active
    return result


async def register_dodge(
    session: AsyncSession,
    room_id: int,
    user_id: int,
    *,
    now: datetime | None = None,
    allow_missing_room: bool = False,
) -> RankedDodgeStatus:
    """Save the cancellation and penalty atomically; caller owns the commit.

    Serialize the cancellation on the room, then the player's progression on
    the user row. Retried HTTP requests return the original penalty, including
    after room cleanup. Both locks precede snapshot reads (MySQL REPEATABLE READ).
    No score, statistics or Elo-history table is modified.
    """
    room = (await session.exec(select(Room).options(lazyload("*")).where(Room.id == room_id).with_for_update())).first()
    user = (await session.exec(select(User).options(lazyload("*")).where(User.id == user_id).with_for_update())).first()
    existing = await session.get(RankedDodgePenalty, room_id)
    if existing is not None:
        return status_for(existing)
    if user is None:
        raise ValueError("Player not found")
    if (room is None and not allow_missing_room) or (room is not None and room.type != MatchType.RANKED_PLAY):
        raise ValueError("Ranked room not found")

    # MySQL DATETIME stores seconds; return exactly the timestamp a retry reads.
    now = as_utc(now or utcnow()).replace(microsecond=0)

    previous = await latest_penalty(session, user_id)
    level = 1
    if previous and (previous.expires_at is None or now < as_utc(previous.expires_at) + timedelta(hours=48)):
        level = min(14, previous.level + 1)
    penalty = RankedDodgePenalty(
        room_id=room_id,
        user_id=user_id,
        level=level,
        created_at=now,
        expires_at=penalty_end(now, level),
        account_banned=level == 14,
    )
    session.add(penalty)
    if penalty.account_banned:
        user.is_active = False
        session.add(user)
        for model in (LoginSession, TrustedDevice, OAuthToken):
            await session.exec(delete(model).where(col(model.user_id) == user_id))
    session.add(
        AdminAuditEvent(
            actor_user_id=None,
            actor_username="SOMS! Ranked",
            action="ranked.dodge",
            target_type="user",
            target_id=str(user_id),
            reason=f"Left Ranked room {room_id} before confirming the initial hand",
            before={"level": previous.level if previous else 0},
            after={"room_id": room_id, **status_for(penalty).model_dump(mode="json")},
        )
    )
    await session.flush()
    return status_for(penalty)
