"""SOMSAI matchmaking, public state and the native-screen action gateway."""

from copy import deepcopy
from datetime import timedelta
from itertools import combinations

from app.database import SomsaiPool, User
from app.features.somsai.database.somsai import SomsaiMatch, SomsaiQueue, SomsaiReservation
from app.helpers import utcnow
from app.features.somsai.services.somsai_match_service import (
    FINAL_STAGES,
    create_match,
    finish_match,
    match_action,
    match_payload,
    members_of,
    start_filled_custom,
    tick_match,
    touch,
)
from app.features.somsai.services.somsai_mixed_pool_service import mixed_pool
from app.features.somsai.services.somsai_party_service import (
    active_user,
    activity,
    aware,
    get_party,
    party_action,
    party_snapshot,
    reject,
    release_reservation,
    reserve_members,
)
from app.features.somsai.services.somsai_pool_service import pool_payload
from app.features.somsai.services.somsai_rank_pool import RANKS
from app.features.somsai.services.somsai_rating_service import ensure_rating, rating_payload

from fastapi import HTTPException
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


SEARCH_RADIUS_BASE = 150.0
SEARCH_RADIUS_CAP = 500.0
SEARCH_RADIUS_HOLD_SECONDS = 5 * 60.0
SEARCH_RADIUS_RAMP_SECONDS = 5 * 60.0
EXTREME_ARCHSOM_RATING = 3500.0
DIAMOND_V_MIN = 2900.0
ELITE_FALLBACK_SECONDS = SEARCH_RADIUS_HOLD_SECONDS + SEARCH_RADIUS_RAMP_SECONDS


def search_radius(waited_seconds: float) -> float:
    """Keep close matches preferred and only reach the 500 MMR limit after ten minutes."""
    waited = max(0.0, waited_seconds)
    if waited <= SEARCH_RADIUS_HOLD_SECONDS:
        return SEARCH_RADIUS_BASE
    progress = min(1.0, (waited - SEARCH_RADIUS_HOLD_SECONDS) / SEARCH_RADIUS_RAMP_SECONDS)
    return SEARCH_RADIUS_BASE + progress * (SEARCH_RADIUS_CAP - SEARCH_RADIUS_BASE)


def queue_entries_compatible(left: SomsaiQueue, right: SomsaiQueue, now) -> bool:
    difference = abs(left.rating - right.rating)
    left_wait = (now - aware(left.joined_at)).total_seconds()
    right_wait = (now - aware(right.joined_at)).total_seconds()
    if difference <= min(search_radius(left_wait), search_radius(right_wait)):
        return True

    # Once a 3500+ player has exhausted the normal ten-minute expansion, the
    # open-ended ARCHSOM division needs its own fallback. They may meet any
    # other ARCHSOM or the adjacent DIAMOND V division. The candidate does not
    # have to wait another ten minutes; lower divisions retain the 500-MMR cap.
    if left.rating >= right.rating:
        high, high_wait, low = left.rating, left_wait, right.rating
    else:
        high, high_wait, low = right.rating, right_wait, left.rating
    if high < EXTREME_ARCHSOM_RATING or high_wait < ELITE_FALLBACK_SECONDS:
        return False
    return low >= DIAMOND_V_MIN


def validate_mode(ruleset_id: int, variant_id: int) -> None:
    if ruleset_id not in range(4) or variant_id not in ({4, 7} if ruleset_id == 3 else {0}):
        reject("Неизвестный режим или вариант игры.", 422)


def balance_units(entries: list[SomsaiQueue], size: int) -> tuple[list[int], list[int]] | None:
    """Whole parties are indivisible; minimise team mean-rating difference."""
    if sum(len(entry.members) for entry in entries) != size * 2:
        return None
    best = None
    best_difference = float("inf")
    for mask in range(1, 1 << len(entries)):
        if not mask & 1:
            continue
        left = [entry for i, entry in enumerate(entries) if mask & (1 << i)]
        right = [entry for i, entry in enumerate(entries) if not mask & (1 << i)]
        if sum(len(entry.members) for entry in left) != size:
            continue
        delta = (
            abs(
                sum(entry.rating * len(entry.members) for entry in left)
                - sum(entry.rating * len(entry.members) for entry in right)
            )
            / size
        )
        if delta < best_difference:
            best_difference = delta
            best = ([uid for entry in left for uid in entry.members], [uid for entry in right for uid in entry.members])
    return best


async def join_queue(session: AsyncSession, user_id: int, format: str, ruleset_id: int, variant_id: int) -> None:
    if format not in {"1v1", "2v2"}:
        reject("Рейтинговый поиск доступен для 1v1 и 2v2.", 422)
    await ensure_rating(session, user_id, ruleset_id, variant_id, format)
    party = await get_party(session, user_id)
    members = [user_id]
    if format == "2v2" and party:
        if party.captain_id != user_id:
            reject("Поиск запускает капитан пати.", 403)
        members = list(party.members)
    ratings = [await ensure_rating(session, uid, ruleset_id, variant_id, format) for uid in members]
    rating = sum(row.rating for row in ratings) / len(ratings)
    await mixed_pool(session, ruleset_id, variant_id, rating)
    reservation = await reserve_members(session, user_id, members, "somsai:queue", party.id if party else None)
    session.add(
        SomsaiQueue(
            reservation_id=reservation.id,
            captain_id=user_id,
            members=members,
            format=format,
            ruleset_id=ruleset_id,
            variant_id=variant_id,
            rating=rating,
            expires_at=utcnow() + timedelta(seconds=90),
        )
    )
    for uid in members:
        (await activity(session, uid)).match_id = None
    await session.flush()
    await match_queues(session)


async def leave_queue(session: AsyncSession, user_id: int) -> None:
    state = await activity(session, user_id)
    if not state.reservation_id:
        return
    entry = (await session.exec(select(SomsaiQueue).where(SomsaiQueue.reservation_id == state.reservation_id))).first()
    if entry is None:
        match = await session.get(SomsaiMatch, state.match_id) if state.match_id else None
        if match and match.ranked and match.stage == "waiting" and user_id in members_of(match):
            await finish_match(
                session, match, None, cancelled=True, reason="Игрок отменил поиск до подтверждения матча."
            )
            return
        reject("Используйте выход из активного матча или очереди Ranked.")
    await release_reservation(session, entry.reservation_id)
    await session.delete(entry)
    await session.flush()


async def match_queues(session: AsyncSession) -> None:
    all_entries = list(
        (await session.exec(select(SomsaiQueue).order_by(col(SomsaiQueue.joined_at), col(SomsaiQueue.id)))).all()
    )
    for entry in all_entries[:]:
        if aware(entry.expires_at) <= utcnow():
            await release_reservation(session, entry.reservation_id)
            await session.delete(entry)
            all_entries.remove(entry)
    groups = {(entry.ruleset_id, entry.variant_id, entry.format) for entry in all_entries}
    for ruleset_id, variant_id, format in sorted(groups):
        entries = [e for e in all_entries if (e.ruleset_id, e.variant_id, e.format) == (ruleset_id, variant_id, format)]
        size = int(format[0])
        while entries:
            anchor = entries[0]
            now = utcnow()
            nearby = [e for e in entries[1:33] if queue_entries_compatible(anchor, e, now)]
            selected = None
            teams = None
            for count in range(1, size * 2):
                for others in combinations(nearby, count):
                    candidate = [anchor, *others]
                    if any(
                        not queue_entries_compatible(left, right, now)
                        for left, right in combinations(candidate, 2)
                    ):
                        continue
                    balanced = balance_units(candidate, size)
                    if balanced:
                        selected, teams = candidate, balanced
                        break
                if selected:
                    break
            if not selected or not teams:
                entries.pop(0)
                continue
            # Revalidate the players under the same coordinator lock as party
            # and queue changes. Pool updates cannot alter the captured match.
            invalid = False
            for entry in selected:
                for uid in entry.members:
                    try:
                        await active_user(session, uid)
                    except HTTPException:
                        invalid = True
                if invalid:
                    await release_reservation(session, entry.reservation_id)
                    await session.delete(entry)
                    if entry in entries:
                        entries.remove(entry)
                    break
            if invalid:
                continue
            try:
                await create_match(
                    session, list(teams), format, ruleset_id, variant_id, [entry.reservation_id for entry in selected]
                )
            except HTTPException:
                # An admin may disable the last pool while people are queued.
                # Return them to idle instead of leaving an impossible search.
                for entry in selected:
                    await release_reservation(session, entry.reservation_id)
            for entry in selected:
                await session.delete(entry)
                entries.remove(entry)
    await session.flush()


async def create_custom(
    session: AsyncSession,
    user_id: int,
    format: str,
    ruleset_id: int,
    variant_id: int,
    pool_id: int | None,
    name: str,
    target_mmr: int | None = None,
    target_rank: str | None = None,
    bot_level: str | None = None,
    bot_profiles: list[dict] | None = None,
) -> None:
    if format not in {"1v1", "2v2", "3v3", "4v4"}:
        reject("Неверный формат кастома.", 422)
    party = await get_party(session, user_id)
    members = list(party.members) if party else [user_id]
    if party and party.captain_id != user_id:
        reject("Кастом создаёт капитан пати.", 403)
    if len(members) > int(format[0]):
        reject("Размер пати больше размера команды.")
    reservation = await reserve_members(session, user_id, members, "somsai:custom", party.id if party else None)
    from app.features.somsai.services.somsai_bot_service import create_bot_team

    bots = await create_bot_team(session, int(format[0]), bot_level, bot_profiles) if bot_level and bot_profiles else []
    await create_match(
        session,
        [members, [bot["user_id"] for bot in bots]],
        format,
        ruleset_id,
        variant_id,
        [reservation.id],
        ranked=False,
        owner_id=user_id,
        pool_id=pool_id,
        name=name,
        target_mmr=target_mmr,
        target_rank=target_rank,
        bots=bots,
    )


async def join_custom(session: AsyncSession, user_id: int, match_id: int, team: int) -> None:
    match = await session.get(SomsaiMatch, match_id)
    if match is None or match.ranked or match.stage != "waiting" or team not in (0, 1):
        reject("Кастом недоступен для входа.")
    if match.state.get("bots") and team == 1:
        reject("Эта команда занята ботами. Вступите в красную команду.")
    party = await get_party(session, user_id)
    members = list(party.members) if party else [user_id]
    if party and party.captain_id != user_id:
        reject("Пати присоединяет капитан.", 403)
    user_state = await activity(session, user_id)
    already_joined = user_id in members_of(match)
    reservation = None
    if already_joined:
        reservation = await session.get(SomsaiReservation, user_state.reservation_id)
        if (
            user_state.match_id != match.id
            or reservation is None
            or reservation.captain_id != user_id
            or reservation.kind != "somsai:custom"
            or reservation.members != members
        ):
            reject("Команду пати меняет её капитан.", 403)
    remaining = [uid for uid in match.state["teams"][team] if uid not in members]
    if len(remaining) + len(members) > int(match.format[0]):
        reject("В выбранной команде не хватает места.")
    if reservation is None:
        reservation = await reserve_members(session, user_id, members, "somsai:custom", party.id if party else None)
    state = deepcopy(match.state)
    state["teams"] = [[uid for uid in group if uid not in members] for group in state["teams"]]
    state["teams"][team].extend(members)
    state["captains"] = [group[0] if group else None for group in state["teams"]]
    state["accepted"] = []
    state["ready"] = []
    if not already_joined:
        state["reservations"].append(reservation.id)
    touch(match, state)
    start_filled_custom(match)
    session.add(match)
    for uid in members:
        (await activity(session, uid)).match_id = match.id


async def handle_action(
    session: AsyncSession, user_id: int, payload, *, bot_profiles: list[dict] | None = None
) -> None:
    validate_mode(payload.ruleset_id, payload.variant_id)
    await active_user(session, user_id)
    action = payload.action
    if action.startswith("party_"):
        await party_action(
            session,
            user_id,
            action,
            target_id=payload.target_user_id,
            invitation_id=payload.invitation_id,
            target_username=payload.target_username,
        )
    elif action == "queue_join":
        await join_queue(session, user_id, payload.format, payload.ruleset_id, payload.variant_id)
    elif action == "queue_leave":
        await leave_queue(session, user_id)
    elif action == "custom_create":
        await create_custom(
            session,
            user_id,
            payload.format,
            payload.ruleset_id,
            payload.variant_id,
            payload.pool_id,
            payload.name,
            payload.target_mmr,
            payload.target_rank,
            payload.bot_level if payload.with_bots else None,
            bot_profiles,
        )
    elif action == "custom_join":
        await join_custom(session, user_id, payload.match_id, payload.team)
    else:
        match = await session.get(SomsaiMatch, payload.match_id)
        if match is None:
            reject("Матч не найден.", 404)
        await match_action(
            session,
            match,
            user_id,
            action,
            slot_id=payload.slot_id,
            expected_revision=payload.expected_revision,
            pool_id=payload.pool_id,
        )
    await session.flush()


async def public_state(session: AsyncSession, user_id: int, ruleset_id: int, variant_id: int) -> dict:
    validate_mode(ruleset_id, variant_id)
    await active_user(session, user_id)
    ratings = {
        format: await rating_payload(session, await ensure_rating(session, user_id, ruleset_id, variant_id, format))
        for format in ("1v1", "2v2")
    }
    state = await activity(session, user_id)
    entry = (
        (await session.exec(select(SomsaiQueue).where(SomsaiQueue.reservation_id == state.reservation_id))).first()
        if state.reservation_id
        else None
    )
    queue = None
    if entry and aware(entry.expires_at) <= utcnow():
        await release_reservation(session, entry.reservation_id)
        await session.delete(entry)
        entry = None
    if entry:
        entry.expires_at = utcnow() + timedelta(seconds=90)
        session.add(entry)
        queue = {
            "format": entry.format,
            "state": "searching",
            "joined_at": aware(entry.joined_at).isoformat(),
            "members": entry.members,
        }
    match = await session.get(SomsaiMatch, state.match_id) if state.match_id else None
    pools = (
        await session.exec(
            select(SomsaiPool).where(
                SomsaiPool.ruleset_id == ruleset_id,
                SomsaiPool.variant_id == variant_id,
                col(SomsaiPool.active).is_(True),
            )
        )
    ).all()
    customs = (
        await session.exec(
            select(SomsaiMatch)
            .where(
                SomsaiMatch.ruleset_id == ruleset_id,
                SomsaiMatch.variant_id == variant_id,
                col(SomsaiMatch.ranked).is_(False),
                SomsaiMatch.stage == "waiting",
            )
            .order_by(col(SomsaiMatch.id).desc())
            .limit(30)
        )
    ).all()
    recent = (
        await session.exec(
            select(SomsaiMatch)
            .where(col(SomsaiMatch.stage).in_(FINAL_STAGES))
            .order_by(col(SomsaiMatch.id).desc())
            .limit(100)
        )
    ).all()
    user_recent = [item for item in recent if user_id in members_of(item)][:20]
    recent_user_ids = {
        member_id
        for item in user_recent
        for team in item.state.get("teams", [])
        for member_id in team
    }
    recent_users = (
        await session.exec(select(User).where(col(User.id).in_(recent_user_ids)))
        if recent_user_ids
        else []
    )
    recent_usernames = {user.id: user.username for user in recent_users}

    def recent_payload(item: SomsaiMatch) -> dict:
        item_state = item.state
        teams = item_state.get("teams", [[], []])
        local_team = next((index for index, team in enumerate(teams) if user_id in team), None)
        winner = item_state.get("winner_team_id")
        if item.stage == "cancelled":
            outcome = "cancelled"
        elif winner is None:
            outcome = "draw"
        else:
            outcome = "win" if winner == local_team else "loss"
        rating_delta = next(
            (
                change.get("delta", 0)
                for change in item_state.get("rating_changes", [])
                if change.get("user_id") == user_id
            ),
            0,
        )
        return {
            "id": item.id,
            "format": item.format,
            "ranked": item.ranked,
            "outcome": outcome,
            "wins": item_state.get("wins", [0, 0]),
            "local_team_id": local_team,
            "opponents": [
                recent_usernames.get(member_id, f"#{member_id}")
                for index, team in enumerate(teams)
                if index != local_team
                for member_id in team
            ],
            "rating_delta": rating_delta,
            "ended_at": aware(item.ended_at).isoformat() if item.ended_at else None,
        }
    party = await party_snapshot(session, user_id, public=True)
    for member in party["members"]:
        member["ratings"] = {
            format: await rating_payload(
                session, await ensure_rating(session, member["id"], ruleset_id, variant_id, format)
            )
            for format in ("1v1", "2v2")
        }
    return {
        "user_id": user_id,
        "ruleset_id": ruleset_id,
        "variant_id": variant_id,
        "ratings": ratings,
        "formats": ["1v1", "2v2", "3v3", "4v4"],
        "party": party,
        "queue": queue,
        "match": await match_payload(session, match) if match and user_id in members_of(match) else None,
        "pools": [
            {
                key: value
                for key, value in pool_payload(pool).items()
                if key in {"id", "name", "best_of", "average_stars"}
            }
            for pool in pools
        ],
        "customs": [
            {
                "id": m.id,
                "name": m.name,
                "format": m.format,
                "participants": len(members_of(m)),
                "capacity": int(m.format[0]) * 2,
                "teams": [len(team) for team in m.state["teams"]],
                "owner_id": m.owner_id,
                "target_mmr": m.state.get("match_rating"),
                "target_rank": m.state.get("pool_rank"),
            }
            for m in customs
        ],
        "recent_matches": [recent_payload(item) for item in user_recent],
        "pool_ranks": list(reversed(RANKS)),
    }


async def tick(session: AsyncSession) -> None:
    await match_queues(session)
    matches = (await session.exec(select(SomsaiMatch).where(~col(SomsaiMatch.stage).in_(FINAL_STAGES)))).all()
    for match in matches:
        await tick_match(session, match)
    await session.flush()
