"""Shared parties and exclusive queue leases for SOMSAI and native Ranked."""

from contextlib import asynccontextmanager
from datetime import UTC, datetime, timedelta
import re
from typing import NoReturn
from uuid import uuid4

from app.database import MatchmakingPool, Relationship, RelationshipType, User
from app.features.somsai.database.somsai import (
    SomsaiActivity,
    SomsaiLock,
    SomsaiMatch,
    SomsaiNativeRoom,
    SomsaiParty,
    SomsaiPartyInvite,
    SomsaiReservation,
)
from app.dependencies.database import with_db
from app.helpers import utcnow

from fastapi import HTTPException
from sqlalchemy import func
from sqlalchemy.orm import lazyload
from sqlmodel import col, or_, select
from sqlmodel.ext.asyncio.session import AsyncSession


def aware(value: datetime) -> datetime:
    return value.replace(tzinfo=UTC) if value.tzinfo is None else value.astimezone(UTC)


def reject(message: str, status: int = 409) -> NoReturn:
    raise HTTPException(status, message)


@asynccontextmanager
async def somsai_transaction():
    # A fresh session matters: authentication may have established an older
    # REPEATABLE READ snapshot before this request acquires the coordinator.
    async with with_db() as session:
        try:
            await lock_somsai(session)
            yield session
            await session.commit()
        except BaseException:
            await session.rollback()
            raise


async def lock_somsai(session: AsyncSession) -> None:
    row = (await session.exec(select(SomsaiLock).where(SomsaiLock.id == 1).with_for_update())).first()
    if row is None:
        raise RuntimeError("SOMSAI schema has not been migrated")
    await expire_reservations(session)


async def active_user(session: AsyncSession, user_id: int) -> User:
    user = (
        await session.exec(
            select(User)
            .options(lazyload("*"))
            .where(
                User.id == user_id,
                col(User.is_active).is_(True),
                col(User.is_bot).is_(False),
                ~User.is_restricted_query(col(User.id)),
            )
        )
    ).first()
    if user is None:
        reject("Игрок недоступен для матчей.", 403)
    return user


async def activity(session: AsyncSession, user_id: int) -> SomsaiActivity:
    row = await session.get(SomsaiActivity, user_id)
    if row is None:
        row = SomsaiActivity(user_id=user_id)
        session.add(row)
        await session.flush()
    return row


async def user_payload(session: AsyncSession, user_id: int) -> dict:
    user = (await session.exec(select(User).options(lazyload("*")).where(User.id == user_id))).one()
    display_name = (
        re.sub(r"\s*\[bot [A-Za-z0-9_-]+\]$", " [BOT]", user.username, flags=re.IGNORECASE)
        if user.is_bot
        else user.username
    )
    return {
        "id": user.id,
        "username": display_name,
        "avatar_url": f"/users/{user.id}/avatar",
        "country_code": user.country_code or "XX",
    }


async def get_party(session: AsyncSession, user_id: int) -> SomsaiParty | None:
    state = await activity(session, user_id)
    party = await session.get(SomsaiParty, state.party_id) if state.party_id else None
    return party if party and party.active and user_id in party.members else None


async def party_snapshot(session: AsyncSession, user_id: int, *, public: bool = False) -> dict:
    party = await get_party(session, user_id)
    state = await activity(session, user_id)
    members = list(party.members) if party else [user_id]
    result = {
        "id": party.id if party else None,
        "captain_id": party.captain_id if party else user_id,
        "members": members,
        "busy": bool(state.reservation_id),
        "reservation_id": state.reservation_id,
        "revision": party.revision if party else 0,
    }
    if public:
        result["members"] = [await user_payload(session, uid) for uid in members]
        invites = (
            await session.exec(
                select(SomsaiPartyInvite).where(
                    SomsaiPartyInvite.target_id == user_id,
                    SomsaiPartyInvite.status == "pending",
                    SomsaiPartyInvite.expires_at > utcnow(),
                )
            )
        ).all()
        result["invites"] = []
        for invite in invites:
            invited_party = await session.get(SomsaiParty, invite.party_id)
            if invited_party is not None and invited_party.active:
                result["invites"].append(
                    {"id": invite.id, "captain": await user_payload(session, invited_party.captain_id)}
                )
        result["outgoing_invites"] = []
        if party:
            outgoing = (
                await session.exec(
                    select(SomsaiPartyInvite).where(
                        SomsaiPartyInvite.party_id == party.id,
                        SomsaiPartyInvite.status == "pending",
                        SomsaiPartyInvite.expires_at > utcnow(),
                    )
                )
            ).all()
            for invite in outgoing:
                result["outgoing_invites"].append(
                    {"id": invite.id, "target": await user_payload(session, invite.target_id)}
                )
        result.pop("reservation_id")
    return result


async def assert_party_idle(session: AsyncSession, party: SomsaiParty) -> None:
    for uid in party.members:
        if (await activity(session, uid)).reservation_id:
            reject("Сначала завершите матч или отмените поиск.")


async def leave_party(session: AsyncSession, user_id: int) -> None:
    party = await get_party(session, user_id)
    if party is None:
        return
    await assert_party_idle(session, party)
    state = await activity(session, user_id)
    state.party_id = None
    session.add(state)
    party.members = [uid for uid in party.members if uid != user_id]
    if not party.members:
        party.active = False
    elif party.captain_id == user_id:
        party.captain_id = party.members[0]
    party.revision += 1
    session.add(party)


async def assert_not_blocked(session: AsyncSession, inviter_id: int, target_id: int) -> None:
    blocked = (
        await session.exec(
            select(Relationship.id).where(
                Relationship.type == RelationshipType.BLOCK,
                or_(
                    (Relationship.user_id == inviter_id) & (Relationship.target_id == target_id),
                    (Relationship.user_id == target_id) & (Relationship.target_id == inviter_id),
                ),
            )
        )
    ).first()
    if blocked is not None:
        reject("Нельзя присоединиться к пати этого игрока.", 403)


async def party_action(
    session: AsyncSession,
    user_id: int,
    action: str,
    *,
    target_id: int | None = None,
    invitation_id: int | None = None,
    target_username: str | None = None,
) -> None:
    await active_user(session, user_id)
    if action == "party_invite" and target_username is not None:
        if target_id is not None:
            reject("Укажите ник или ID, но не оба сразу.", 422)
        username = target_username.strip().removeprefix("@").lower()
        if not username:
            reject("Введите ник игрока.", 422)
        target_id = (await session.exec(select(User.id).where(func.lower(User.username) == username))).first()
        if target_id is None:
            reject("Игрок с таким ником не найден.", 404)
    if action == "party_leave":
        await leave_party(session, user_id)
        return
    if action in {"party_accept", "party_decline"}:
        invite = await session.get(SomsaiPartyInvite, invitation_id)
        if (
            invite is None
            or invite.target_id != user_id
            or invite.status != "pending"
            or aware(invite.expires_at) <= utcnow()
        ):
            reject("Приглашение уже недоступно.")
        if action == "party_accept":
            party = await session.get(SomsaiParty, invite.party_id)
            if party is None or not party.active or len(party.members) >= 2:
                reject("В пати больше нет свободного места.")
            if party.captain_id != invite.inviter_id:
                reject("Пригласивший капитан уже покинул пати.")
            await assert_not_blocked(session, invite.inviter_id, user_id)
            await assert_party_idle(session, party)
            await active_user(session, party.captain_id)
            if (await activity(session, user_id)).reservation_id:
                reject("Сначала завершите матч или поиск.")
            await leave_party(session, user_id)
            party.members = [*party.members, user_id]
            party.revision += 1
            (await activity(session, user_id)).party_id = party.id
            session.add(party)
        invite.status = "accepted" if action == "party_accept" else "declined"
        session.add(invite)
        return
    party = await get_party(session, user_id)
    if party is None:
        if (await activity(session, user_id)).reservation_id:
            reject("Сначала завершите матч или поиск.")
        party = SomsaiParty(captain_id=user_id, members=[user_id])
        session.add(party)
        await session.flush()
        (await activity(session, user_id)).party_id = party.id
    if action == "party_create":
        return
    if action != "party_invite" or target_id is None or target_id == user_id:
        reject("Укажите другого игрока.", 422)
    if party.captain_id != user_id:
        reject("Приглашать может капитан пати.", 403)
    await assert_party_idle(session, party)
    if len(party.members) >= 2:
        reject("В пати уже два игрока.")
    target = await active_user(session, target_id)
    blocked = (
        await session.exec(
            select(Relationship.id).where(
                Relationship.type == RelationshipType.BLOCK,
                or_(
                    (Relationship.user_id == user_id) & (Relationship.target_id == target_id),
                    (Relationship.user_id == target_id) & (Relationship.target_id == user_id),
                ),
            )
        )
    ).first()
    if blocked is not None:
        reject("Нельзя пригласить этого игрока.", 403)
    if target.pm_friends_only:
        follows = (
            await session.exec(
                select(Relationship.id).where(
                    Relationship.user_id == target_id,
                    Relationship.target_id == user_id,
                    Relationship.type == RelationshipType.FOLLOW,
                )
            )
        ).first()
        if follows is None:
            reject("Игрок принимает приглашения только от друзей.", 403)
    previous = (
        await session.exec(
            select(SomsaiPartyInvite).where(
                SomsaiPartyInvite.party_id == party.id,
                SomsaiPartyInvite.target_id == target_id,
                SomsaiPartyInvite.status == "pending",
                SomsaiPartyInvite.expires_at > utcnow(),
            )
        )
    ).first()
    if previous is not None:
        return
    if (await activity(session, target_id)).reservation_id:
        reject("Игрок сейчас в матче или поиске.")
    session.add(
        SomsaiPartyInvite(
            party_id=int(party.id or 0),
            inviter_id=user_id,
            target_id=target_id,
            expires_at=utcnow() + timedelta(minutes=5),
        )
    )


async def reserve_members(
    session: AsyncSession,
    captain_id: int,
    members: list[int],
    kind: str,
    party_id: int | None = None,
    reservation_id: str | None = None,
) -> SomsaiReservation:
    if not members or captain_id not in members or len(set(members)) != len(members):
        reject("Некорректный состав команды.", 422)
    for uid in sorted(members):
        await active_user(session, uid)
        native = await session.get(SomsaiNativeRoom, uid)
        if native and aware(native.expires_at) > utcnow():
            reject("Один из игроков находится в обычной комнате мультиплеера.")
        if (await activity(session, uid)).reservation_id:
            reject("Один из игроков уже участвует в поиске или матче.")
    reservation = SomsaiReservation(
        id=reservation_id or str(uuid4()),
        captain_id=captain_id,
        members=members,
        party_id=party_id,
        kind=kind,
        expires_at=utcnow() + timedelta(minutes=10),
    )
    session.add(reservation)
    for uid in members:
        (await activity(session, uid)).reservation_id = reservation.id
    await session.flush()
    return reservation


async def native_reserve(session: AsyncSession, user_id: int, pool_id: int, request_id: str) -> dict:  # noqa: ARG001
    reject("Иди в обычный лазер", 403)


async def _legacy_native_reserve(session: AsyncSession, user_id: int, pool_id: int, request_id: str) -> dict:
    pool = await session.get(MatchmakingPool, pool_id)
    if pool is None or not pool.active or pool.type != "ranked_play" or pool.lobby_size not in (2, 4):
        reject("Очередь Ranked недоступна.")
    party = await get_party(session, user_id)
    members = [user_id]
    if pool.lobby_size == 4 and party:
        if party.captain_id != user_id:
            reject("Поиск запускает капитан пати.", 403)
        members = list(party.members)
    kind = f"ranked:{pool_id}"
    reservation = await session.get(SomsaiReservation, request_id)
    if reservation is not None:
        if reservation.captain_id != user_id or reservation.kind != kind or reservation.members != members:
            reject("Идентификатор запроса уже использован другим составом или очередью.")
        if reservation.released or aware(reservation.expires_at) <= utcnow():
            reject("Резервирование очереди истекло. Начните новый поиск.", 404)
        for uid in members:
            await active_user(session, uid)
            if (await activity(session, uid)).reservation_id != reservation.id:
                reject("Резервирование очереди больше не действует.", 404)
    else:
        reservation = await reserve_members(
            session, user_id, members, kind, party.id if party else None, reservation_id=request_id
        )
    return {"id": reservation.party_id, "captain_id": user_id, "members": members, "reservation_id": reservation.id}


async def release_reservation(session: AsyncSession, reservation_id: str) -> None:
    reservation = await session.get(SomsaiReservation, reservation_id)
    if reservation is None:
        return
    for uid in reservation.members:
        state = await activity(session, uid)
        if state.reservation_id == reservation_id:
            state.reservation_id = None
            state.match_id = None
            session.add(state)
    if reservation.kind.startswith("ranked:"):
        # Tombstones prevent delayed retries from recreating an expired or released lease.
        reservation.released = True
        reservation.expires_at = utcnow()
        session.add(reservation)
    else:
        await session.delete(reservation)
    await session.flush()


async def expire_reservations(session: AsyncSession) -> None:
    expired = (
        await session.exec(
            select(SomsaiReservation).where(
                SomsaiReservation.expires_at <= utcnow(), col(SomsaiReservation.released).is_(False)
            )
        )
    ).all()
    for reservation in expired:
        # SOMSAI's match coordinator owns its match lifetime. Native Ranked
        # leases are renewed by spectator; losing that process releases them.
        if reservation.kind.startswith("ranked:"):
            await release_reservation(session, reservation.id)


async def renew_reservation(session: AsyncSession, reservation_id: str) -> None:
    reservation = await session.get(SomsaiReservation, reservation_id)
    if reservation is None or reservation.released or aware(reservation.expires_at) <= utcnow():
        reject("Резервирование очереди истекло.", 404)
    reservation.expires_at = utcnow() + timedelta(minutes=10)
    session.add(reservation)


async def claim_native_room(session: AsyncSession, user_id: int, room_id: int) -> None:
    await active_user(session, user_id)
    user_activity = await activity(session, user_id)
    if user_activity.reservation_id:
        match = await session.get(SomsaiMatch, user_activity.match_id) if user_activity.match_id else None
        # Lease renewals for the room backing this exact SOMSAI match are not
        # conflicting multiplayer activity.
        if match is None or match.room_id != room_id or match.stage in {"ended", "cancelled"}:
            reject("Игрок уже участвует в очереди или матче SOMSAI/Ranked.")
    row = await session.get(SomsaiNativeRoom, user_id)
    if row and row.room_id != room_id and aware(row.expires_at) > utcnow():
        reject("Игрок уже находится в другой комнате.")
    row = row or SomsaiNativeRoom(user_id=user_id, room_id=room_id, expires_at=utcnow())
    row.room_id = room_id
    row.expires_at = utcnow() + timedelta(seconds=45)
    session.add(row)


async def release_native_room(session: AsyncSession, user_id: int, room_id: int) -> None:
    row = await session.get(SomsaiNativeRoom, user_id)
    if row and row.room_id == room_id:
        await session.delete(row)
