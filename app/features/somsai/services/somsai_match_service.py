"""Authoritative tournament state machine; clients never report their own score."""

from copy import deepcopy
from datetime import datetime, timedelta
import secrets
from typing import Any

from app.const import BANCHOBOT_ID
from app.database import Beatmap, ChannelType, ChatChannel, Playlist, Room, Score
from app.features.somsai.database.somsai import SomsaiMatch
from app.helpers import utcnow
from app.models.room import MatchType, QueueMode, RoomCategory, RoomStatus
from app.models.score import GameMode
from app.features.somsai.services.somsai_bot_skill import choose_draft_slot
from app.features.somsai.services.somsai_party_service import activity, aware, reject, release_reservation, user_payload
from app.features.somsai.services.somsai_rating_service import ensure_rating, performance_impacts, settle_ratings

from sqlalchemy.orm import lazyload
from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

FINAL_STAGES = {"ended", "cancelled"}
PICK_SECONDS = 60
READY_SECONDS = 120
RESULT_SECONDS = 45


def deadline(seconds: int) -> str:
    return (utcnow() + timedelta(seconds=seconds)).isoformat()


def members_of(match: SomsaiMatch) -> list[int]:
    return [uid for team in match.state["teams"] for uid in team]


def touch(match: SomsaiMatch, state: dict, stage: str | None = None) -> None:
    # Queries below may autoflush. Keep a distinct value so later JSON updates
    # (especially rating_changes after settlement) remain visible to SQLAlchemy.
    match.state = deepcopy(state)
    match.stage = stage or match.stage
    match.revision += 1
    match.updated_at = utcnow()


def find_slot(state: dict, slot_id: str) -> dict:
    slot = next((slot for slot in state["slots"] if slot["id"] == slot_id), None)
    if slot is None:
        reject("Слот не найден.", 404)
    return slot


async def create_match(
    session: AsyncSession,
    teams: list[list[int]],
    format: str,
    ruleset_id: int,
    variant_id: int,
    reservations: list[str],
    *,
    ranked: bool = True,
    owner_id: int | None = None,
    pool_id: int | None = None,
    name: str = "",
    target_mmr: int | None = None,
    target_rank: str | None = None,
    bots: list[dict] | None = None,
) -> SomsaiMatch:
    bots = bots or []
    if bots and ranked:
        reject("Боты доступны только в кастомных матчах.", 422)
    bot_ids = {bot["user_id"] for bot in bots}
    owner_id = owner_id or teams[0][0]
    ratings = [
        await ensure_rating(session, uid, ruleset_id, variant_id, format if ranked else "2v2")
        for team in teams
        for uid in team
        if uid not in bot_ids
    ]
    average = sum(row.rating for row in ratings) / len(ratings)
    match_rating = average if ranked or target_mmr is None else target_mmr
    from app.features.somsai.services.somsai_mixed_pool_service import mixed_pool
    from app.features.somsai.services.somsai_rank_pool import average_rank, rank_from_rating

    if pool_id is not None:
        reject("Выбор отдельного турнирного пула больше недоступен. Используйте автоматический пул.", 422)
    # All new matches, including custom rooms, use automatic slot mixing.
    pool_rank = (
        average_rank([row.rating for row in ratings])
        if ranked
        else (target_rank or rank_from_rating(match_rating))
    )
    pool = await mixed_pool(session, ruleset_id, variant_id, match_rating, rank=pool_rank)
    slots = deepcopy(pool["slots"])
    first_team = secrets.randbelow(2)
    for slot in slots:
        slot["status"] = "tiebreaker" if slot["id"] == "TB" else "available"
        slot["title"] = slot.get("name", "")
        slot["stars"] = slot.get("difficulty_rating", 0)
    state = {
        "teams": teams,
        "bots": bots,
        "bot_scores": {},
        "captains": [team[0] if team else None for team in teams],
        "wins": [0, 0],
        "slots": slots,
        "best_of": pool["best_of"],
        "bans_per_team": pool["bans_per_team"],
        "pool_name": pool["name"],
        "pool_revision": pool["revision"],
        "source_url": pool["source_url"],
        "pool_candidates": [],
        "pool_votes": {},
        "pool_selected": True,
        "selection_kind": pool["selection_kind"],
        "source_pools": pool["source_pools"],
        "mmr_window": pool["mmr_window"],
        "match_rating": round(match_rating),
        "pool_rank": pool["pool_rank"],
        "accepted": [],
        "ready": [],
        "bans": [0, 0],
        "turn_team": first_team,
        "next_pick_team": first_team,
        "deadline": deadline(90 if ranked else 1800),
        "history": [],
        "reservations": reservations,
        "current_slot": None,
        "playlist_item_id": 0,
        "settled": False,
        "winner_team_id": None,
        "offline": {},
        "connected": [],
        "server_seen_at": utcnow().isoformat(),
        "results_notified": False,
    }
    match = SomsaiMatch(
        name=(name.strip() or f"SOMSAI {format}")[:100],
        format=format,
        ruleset_id=ruleset_id,
        variant_id=variant_id,
        ranked=ranked,
        owner_id=owner_id,
        pool_id=pool["id"],
        state=state,
        password=secrets.token_urlsafe(24),
    )
    session.add(match)
    await session.flush()
    room = Room(
        name=f"SOMSAI · {match.id} · {match.name}",
        category=RoomCategory.REALTIME,
        type=MatchType.TEAM_VERSUS,
        status=RoomStatus.IDLE,
        host_id=BANCHOBOT_ID,
        password=match.password,
        queue_mode=QueueMode.HOST_ONLY,
        auto_skip=False,
        auto_start_duration=0,
        participant_count=0,
        tournament_mode=True,
        ends_at=utcnow() + timedelta(hours=3),
    )
    session.add(room)
    await session.flush()
    channel = ChatChannel(
        channel_name=f"mp_{room.id}", type=ChannelType.MULTIPLAYER, description=f"SOMSAI match {match.id}"
    )
    session.add(channel)
    await session.flush()
    room.channel_id = channel.channel_id
    session.add(room)
    match.room_id = room.id
    first = slots[0]
    session.add(
        Playlist(
            room_id=room.id,
            id=0,
            beatmap_id=first["beatmap_id"],
            ruleset_id=ruleset_id,
            owner_id=BANCHOBOT_ID,
            required_mods=deepcopy(first["mods"]),
            allowed_mods=[],
            freestyle=False,
            playlist_order=0,
        )
    )
    for uid in members_of(match):
        (await activity(session, uid)).match_id = match.id
    start_filled_custom(match)
    session.add(match)
    await session.flush()
    return match


async def finish_match(
    session: AsyncSession, match: SomsaiMatch, winner: int | None, *, cancelled: bool = False, reason: str = ""
) -> None:
    if match.stage in FINAL_STAGES:
        return
    state = deepcopy(match.state)
    state.update(winner_team_id=winner, reason=reason, deadline=None, settled=True)
    touch(match, state, "cancelled" if cancelled else "ended")
    match.ended_at = utcnow()
    room = (await session.exec(select(Room).where(Room.id == match.room_id).options(lazyload("*")))).first()
    if room is not None:
        room.ends_at = match.ended_at
        room.status = RoomStatus.IDLE
        session.add(room)
    if match.ranked and not cancelled:
        state["rating_changes"] = await settle_ratings(session, match, state["teams"], winner)
    else:
        state["rating_changes"] = []
    state["impacts"] = performance_impacts(state["teams"], state["history"], winner)
    match.state = deepcopy(state)
    for reservation_id in state["reservations"]:
        await release_reservation(session, reservation_id)
    # Keep a last-match pointer so the native screen can display final results.
    for uid in members_of(match):
        (await activity(session, uid)).match_id = match.id
    session.add(match)


def start_filled_custom(match: SomsaiMatch) -> bool:
    if (
        match.ranked
        or match.stage != "waiting"
        or any(len(team) != int(match.format[0]) for team in match.state["teams"])
    ):
        return False
    start_draft(match)
    return True


def start_draft(match: SomsaiMatch) -> None:
    state = deepcopy(match.state)
    if match.stage == "waiting":
        # A custom lobby can remain open before its roster is complete and the
        # native room can start. Give its first connection a fresh grace period.
        state["server_seen_at"] = utcnow().isoformat()
    state["deadline"] = deadline(PICK_SECONDS)
    if not state.get("pool_selected", True):
        touch(match, state, "pool_select")
        return
    state["turn_team"] = state["next_pick_team"]
    touch(match, state, "banning" if state["bans_per_team"] else "picking")


def finish_pool_vote(match: SomsaiMatch) -> None:
    state = deepcopy(match.state)
    choices = state["pool_candidates"]
    votes = list(state["pool_votes"].values())
    selected_id = secrets.choice(votes or [candidate["id"] for candidate in choices])
    selected = next(candidate for candidate in choices if candidate["id"] == selected_id)
    match.pool_id = selected_id
    state.update(
        pool_selected=True,
        pool_name=selected["name"],
        pool_revision=selected["revision"],
        source_url=selected["source_url"],
        slots=deepcopy(selected["slots"]),
        best_of=selected["best_of"],
        bans_per_team=selected["bans_per_team"],
    )
    touch(match, state)
    start_draft(match)


async def select_map(session: AsyncSession, match: SomsaiMatch, slot_id: str) -> None:
    state = deepcopy(match.state)
    slot = find_slot(state, slot_id)
    beatmap = (
        await session.exec(select(Beatmap).options(lazyload("*")).where(Beatmap.id == slot["beatmap_id"]))
    ).first()
    if beatmap is None or beatmap.deleted_at:
        await finish_match(
            session, match, None, cancelled=True, reason="Карта пула больше недоступна. Матч отменён без рейтинга."
        )
        return
    # A pool stores the revision visible when it was created. Serve the current
    # server revision when the mapper updates it, so every player downloads and
    # submits against the same chart instead of being tied to the creator's copy.
    slot["checksum"] = beatmap.checksum
    slot["status"] = "picked"
    slot["selected_by_team"] = state["turn_team"] if slot_id != "TB" else None
    state["current_slot"] = slot_id
    state["playlist_item_id"] += 1
    state["ready"] = [bot["user_id"] for bot in state.get("bots", [])]
    state["available"] = []
    state["force_start"] = False
    state["bot_scores"] = {}
    state["deadline"] = deadline(READY_SECONDS)
    state["results_notified"] = False
    state["round_started_at"] = None
    required = deepcopy(slot["mods"])
    allowed = [{"acronym": "HD"}, {"acronym": "HR"}] if slot["category"] in ("FM", "TB") else []
    session.add(
        Playlist(
            room_id=match.room_id,
            id=state["playlist_item_id"],
            beatmap_id=slot["beatmap_id"],
            ruleset_id=match.ruleset_id,
            owner_id=BANCHOBOT_ID,
            playlist_order=state["playlist_item_id"],
            required_mods=required,
            allowed_mods=allowed,
            freestyle=False,
        )
    )
    state["next_pick_team"] = 1 - state["turn_team"]
    touch(match, state, "ready")
    session.add(match)


async def draft_action(session: AsyncSession, match: SomsaiMatch, user_id: int, action: str, slot_id: str) -> None:
    state = deepcopy(match.state)
    team = state["turn_team"]
    if state["captains"][team] != user_id:
        reject("Сейчас ход капитана другой команды.", 403)
    if (action == "ban" and match.stage != "banning") or (action == "pick" and match.stage != "picking"):
        reject("Фаза драфта уже изменилась.")
    slot = find_slot(state, slot_id)
    if slot["status"] != "available":
        reject("Этот слот уже недоступен.")
    if action == "ban":
        slot["status"] = "banned"
        slot["selected_by_team"] = team
        state.setdefault("draft_history", []).append({"action": "ban", "slot_id": slot_id, "team_id": team})
        state["bans"][team] += 1
        state["turn_team"] = 1 - team
        stage = "banning"
        if min(state["bans"]) >= state["bans_per_team"]:
            stage = "picking"
            state["turn_team"] = state["next_pick_team"]
        state["deadline"] = deadline(PICK_SECONDS)
        touch(match, state, stage)
        session.add(match)
    else:
        await select_map(session, match, slot_id)


async def settle_round(session: AsyncSession, match: SomsaiMatch, *, force: bool = False) -> bool:
    if match.stage != "results":
        return False
    state = deepcopy(match.state)
    slot = find_slot(state, state["current_slot"])
    scores = (
        await session.exec(
            select(Score)
            .options(lazyload("*"))
            .where(
                Score.room_id == match.room_id,
                Score.playlist_item_id == state["playlist_item_id"],
                col(Score.user_id).in_(members_of(match)),
                Score.beatmap_id == slot["beatmap_id"],
                Score.map_md5 == slot["checksum"],
                Score.gamemode == (GameMode.OSU, GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA)[match.ruleset_id],
            )
            .order_by(col(Score.id))
        )
    ).all()
    # One authoritative submission per roster member and round. A replay import
    # cannot claim a room/playlist token. Do not let duplicate attempts inflate it.
    by_user = {}
    for score in scores:
        by_user.setdefault(score.user_id, score)
    bot_scores = state.get("bot_scores", {})
    if not force and set(by_user) | {int(uid) for uid in bot_scores} != set(members_of(match)):
        return False
    players = [
        {
            "user_id": uid,
            "score": bot_scores[str(uid)]["score"]
            if str(uid) in bot_scores
            else max(0, int(by_user[uid].total_score))
            if uid in by_user
            else 0,
            "accuracy": bot_scores[str(uid)]["accuracy"]
            if str(uid) in bot_scores
            else by_user[uid].accuracy
            if uid in by_user
            else 0,
            "score_id": by_user[uid].id if uid in by_user else None,
            "missing": uid not in by_user and str(uid) not in bot_scores,
            "is_bot": any(bot["user_id"] == uid for bot in state.get("bots", [])),
            "max_combo": bot_scores[str(uid)]["max_combo"]
            if str(uid) in bot_scores
            else by_user[uid].max_combo
            if uid in by_user
            else 0,
            "statistics": bot_scores[str(uid)].get("statistics", {})
            if str(uid) in bot_scores
            else {
                "miss": by_user[uid].nmiss,
                "meh": by_user[uid].n50,
                "ok": by_user[uid].n100,
                "great": by_user[uid].n300,
                "perfect": by_user[uid].ngeki,
                "good": by_user[uid].nkatu,
            }
            if uid in by_user
            else {},
            "maximum_statistics": bot_scores[str(uid)].get("maximum_statistics", {})
            if str(uid) in bot_scores
            else {},
            "rank": bot_scores[str(uid)].get("rank", "A")
            if str(uid) in bot_scores
            else by_user[uid].rank
            if uid in by_user
            else "F",
            "passed": bot_scores[str(uid)].get("passed", True)
            if str(uid) in bot_scores
            else by_user[uid].passed
            if uid in by_user
            else False,
        }
        for uid in members_of(match)
    ]
    totals = [sum(player["score"] for player in players if player["user_id"] in team) for team in state["teams"]]
    winner = 0 if totals[0] > totals[1] else 1 if totals[1] > totals[0] else None
    await complete_round(session, match, winner, totals, players)
    return True


async def complete_round(
    session: AsyncSession,
    match: SomsaiMatch,
    winner: int | None,
    totals: list[int],
    players: list[dict],
    *,
    forfeit: bool = False,
    reason: str = "",
) -> None:
    """A missed map costs a round. Only the target number of wins ends the match."""
    state = deepcopy(match.state)
    slot = find_slot(state, state["current_slot"])
    state["history"].append(
        {
            "round": len(state["history"]) + 1,
            "slot_id": slot["id"],
            "playlist_item_id": state["playlist_item_id"],
            "team_scores": totals,
            "winner_team_id": winner,
            "players": players,
            "picked_by_team": slot.get("selected_by_team"),
            "forfeit": forfeit,
            "reason": reason,
        }
    )
    if winner is not None:
        state["wins"][winner] += 1
    slot["status"] = "played"
    touch(match, state)
    if max(state["wins"]) >= (state["best_of"] + 1) // 2:
        await finish_match(session, match, winner)
    elif winner is None and not forfeit:
        if sum(item["winner_team_id"] is None for item in state["history"]) >= 3:
            await finish_match(session, match, None, reason="Три ничьи: матч завершён вничью.")
        else:
            await select_map(session, match, slot["id"])
    elif min(state["wins"]) == state["best_of"] // 2:
        await select_map(session, match, "TB")
    else:
        state = deepcopy(match.state)
        state["turn_team"] = state["next_pick_team"]
        state["deadline"] = deadline(PICK_SECONDS)
        touch(match, state, "picking")
    session.add(match)


async def match_action(
    session: AsyncSession,
    match: SomsaiMatch,
    user_id: int,
    action: str,
    *,
    slot_id: str | None = None,
    expected_revision: int | None = None,
    pool_id: int | None = None,
) -> None:
    if user_id not in members_of(match):
        reject("Вы не участвуете в этом матче.", 403)
    if match.stage in FINAL_STAGES:
        if action == "leave_match":
            (await activity(session, user_id)).match_id = None
            return
        reject("Матч уже завершён.")
    if expected_revision is not None and expected_revision != match.revision:
        reject("Состояние матча обновилось. Повторите действие.")
    if action == "pool_vote":
        if match.stage != "pool_select" or user_id not in match.state["captains"]:
            reject("Пул выбирают капитаны во время голосования.", 403)
        if not any(candidate["id"] == pool_id for candidate in match.state["pool_candidates"]):
            reject("Выберите один из предложенных турниров.", 422)
        state = deepcopy(match.state)
        team = str(state["captains"].index(user_id))
        state["pool_votes"][team] = pool_id
        touch(match, state)
        if len(state["pool_votes"]) == 2:
            finish_pool_vote(match)
    elif action in {"ban", "pick"}:
        await draft_action(session, match, user_id, action, slot_id or "")
    elif action == "leave_match":
        team = next(i for i, members in enumerate(match.state["teams"]) if user_id in members)
        await finish_match(session, match, 1 - team, cancelled=match.stage == "waiting", reason="Игрок покинул матч.")
    elif action == "custom_start":
        if match.ranked or match.owner_id != user_id:
            reject("Запустить кастом может его создатель.", 403)
        if match.stage != "waiting":
            return  # Older clients may still send this after automatic start.
        size = int(match.format[0])
        if any(len(team) != size for team in match.state["teams"]):
            reject("Сначала заполните обе команды.")
        start_draft(match)
    elif action in {"ready", "unready"}:
        if match.stage not in {"waiting", "ready"}:
            reject("Сейчас нельзя подтвердить готовность.")
        state = deepcopy(match.state)
        if action == "unready" and match.stage != "ready":
            reject("Снять готовность можно только перед картой.")
        key = "accepted" if match.stage == "waiting" else "ready"
        state[key] = sorted(set(state[key]) | {user_id}) if action == "ready" else sorted(set(state[key]) - {user_id})
        touch(match, state)
        if match.ranked and match.stage == "waiting" and set(state["accepted"]) == set(members_of(match)):
            start_draft(match)
    else:
        reject("Неизвестное действие.", 422)
    session.add(match)


async def match_payload(session: AsyncSession, match: SomsaiMatch, *, internal: bool = False) -> dict[str, Any]:
    state = match.state
    teams = []
    for team_id, members in enumerate(state["teams"]):
        players = []
        for uid in members:
            player = await user_payload(session, uid)
            rating = await ensure_rating(
                session, uid, match.ruleset_id, match.variant_id, match.format if match.ranked else "2v2"
            )
            player.update(rating=rating.rating, ready=uid in state["ready"], accepted=uid in state["accepted"])
            bot = next((b for b in state.get("bots", []) if b["user_id"] == uid), None)
            if bot:
                player.update(
                    is_bot=True,
                    bot_level=bot["level"],
                    official_id=bot.get("official_id"),
                    official_username=bot.get("username"),
                    official_rank=bot.get("global_rank"),
                )
            players.append(player)
        teams.append(
            {
                "id": team_id,
                "name": "Красная команда" if team_id == 0 else "Синяя команда",
                "captain_id": state["captains"][team_id],
                "members": players,
            }
        )
    payload = {
        "id": match.id,
        "name": match.name,
        "format": match.format,
        "ruleset_id": match.ruleset_id,
        "variant_id": match.variant_id,
        "ranked": match.ranked,
        "with_bots": bool(state.get("bots")),
        "stage": match.stage,
        "revision": match.revision,
        "owner_id": match.owner_id,
        "room_id": match.room_id,
        "password": match.password,
        "teams": teams,
        "wins": state["wins"],
        "best_of": state["best_of"],
        "turn_user_id": state["captains"][state["turn_team"]] if match.stage in {"banning", "picking"} else None,
        "deadline": state["deadline"],
        "slots": state["slots"],
        "map_slot": state["current_slot"],
        "history": state["history"],
        "draft_history": state.get("draft_history", []),
        "pool_name": state["pool_name"],
        "pool_selected": state.get("pool_selected", True),
        "pool_candidates": [
            {key: value for key, value in candidate.items() if key != "slots"} | {"map_count": len(candidate["slots"])}
            for candidate in state.get("pool_candidates", [])
        ],
        "pool_votes": state.get("pool_votes", {}),
        "target_mmr": state.get("match_rating"),
        "winner_team_id": state["winner_team_id"],
        "rating_changes": state.get("rating_changes", []),
        "reason": state.get("reason", ""),
        "source_url": state.get("source_url"),
    }
    if internal:
        payload.update(
            managed=True,
            roster=state["teams"],
            playlist_item_id=state["playlist_item_id"],
            ready=state["ready"],
            bots=state.get("bots", []),
            force_start=state.get("force_start", False),
            start_allowed=match.stage == "ready"
            and (state.get("force_start", False) or set(state["ready"]) == set(members_of(match)))
            and set(state.get("available", state["ready"])) == set(members_of(match)),
        )
    return payload


async def room_event(
    session: AsyncSession,
    room_id: int,
    event: str,
    playlist_item_id: int,
    connected: list[int],
    *,
    ready: list[int] | None = None,
    available: list[int] | None = None,
) -> dict:
    match = (await session.exec(select(SomsaiMatch).where(SomsaiMatch.room_id == room_id))).first()
    if match is None:
        return {"managed": False}
    if match.stage in FINAL_STAGES:
        return await match_payload(session, match, internal=True)
    if (
        event == "aborted"
        and match.stage in {"ready", "playing", "results"}
        and playlist_item_id == match.state["playlist_item_id"]
    ):
        await complete_round(
            session,
            match,
            None,
            [0, 0],
            [],
            forfeit=True,
            reason="Игровой сервер не смог запустить карту: раунд пропущен без очков.",
        )
        return await match_payload(session, match, internal=True)
    state = deepcopy(match.state)
    state["connected"] = [uid for uid in connected if uid in members_of(match)]
    state["server_seen_at"] = utcnow().isoformat()
    if playlist_item_id == state["playlist_item_id"]:
        state["available"] = [
            uid
            for uid in (available if available is not None else ready or [])
            if uid in connected and uid in members_of(match)
        ]
    for uid in members_of(match):
        if uid in connected:
            state["offline"].pop(str(uid), None)
        elif match.stage != "waiting":
            state["offline"].setdefault(str(uid), deadline(120))
    if event == "started" and match.stage == "ready" and playlist_item_id == state["playlist_item_id"]:
        if (
            set(connected) != set(members_of(match))
            or set(ready or []) != set(members_of(match))
            or (not state.get("force_start") and set(state["ready"]) != set(members_of(match)))
            or set(state["available"]) != set(members_of(match))
        ):
            reject("Не все участники готовы.")
        state["round_started_at"] = utcnow().isoformat()
        state["deadline"] = deadline(1800)
        touch(match, state, "playing")
    elif event == "completed" and match.stage == "playing" and playlist_item_id == state["playlist_item_id"]:
        state["results_notified"] = True
        state["deadline"] = deadline(RESULT_SECONDS)
        touch(match, state, "results")
    else:
        # Heartbeats should not invalidate a user's draft revision every 2s.
        match.state = state
    session.add(match)
    return await match_payload(session, match, internal=True)


async def tick_match(session: AsyncSession, match: SomsaiMatch) -> None:
    if start_filled_custom(match):
        session.add(match)
    if match.stage in FINAL_STAGES:
        return
    state = match.state
    now = utcnow()
    if match.stage != "waiting" and aware(datetime.fromisoformat(state["server_seen_at"])) < now - timedelta(
        seconds=90
    ):
        await finish_match(
            session, match, None, cancelled=True, reason="Потеряна связь с игровым сервером. Рейтинг не изменён."
        )
        return
    offline_teams = [
        i
        for i, team in enumerate(state["teams"])
        if any(
            str(uid) in state["offline"] and aware(datetime.fromisoformat(state["offline"][str(uid)])) <= now
            for uid in team
        )
    ]
    if offline_teams:
        await finish_match(
            session,
            match,
            1 - offline_teams[0] if len(offline_teams) == 1 else None,
            cancelled=len(offline_teams) == 2,
            reason="Время на переподключение истекло.",
        )
        return
    expired = state["deadline"] is not None and aware(datetime.fromisoformat(state["deadline"])) <= now
    if (
        match.stage in {"banning", "picking"}
        and any(bot["user_id"] == state["captains"][state["turn_team"]] for bot in state.get("bots", []))
        and aware(match.updated_at) <= now - timedelta(seconds=2)
    ):
        available = [slot for slot in state["slots"] if slot["status"] == "available"]
        if available:
            await draft_action(
                session,
                match,
                state["captains"][state["turn_team"]],
                "ban" if match.stage == "banning" else "pick",
                choose_draft_slot(
                    [bot for bot in state.get("bots", []) if bot["user_id"] in state["teams"][state["turn_team"]]],
                    available,
                    banning=match.stage == "banning",
                    seed=int(match.id or 0) * 10000 + match.revision,
                )["id"],
            )
            return
    if match.stage == "results":
        await settle_round(session, match, force=expired)
    elif expired:
        if match.stage == "waiting":
            await finish_match(session, match, None, cancelled=True, reason="Не все игроки подтвердили участие.")
        elif match.stage == "pool_select":
            finish_pool_vote(match)
            session.add(match)
        elif match.stage in {"banning", "picking"}:
            available = [slot for slot in state["slots"] if slot["status"] == "available"]
            if not available:
                await finish_match(session, match, None, cancelled=True, reason="В пуле закончились доступные карты.")
                return
            captain = state["captains"][state["turn_team"]]
            await draft_action(
                session, match, captain, "ban" if match.stage == "banning" else "pick", available[0]["id"]
            )
        elif match.stage == "ready":
            available = set(state.get("available", state["ready"])) & set(state.get("connected", []))
            missing = [i for i, team in enumerate(state["teams"]) if not set(team).issubset(available)]
            if missing or state.get("force_start"):
                await complete_round(
                    session,
                    match,
                    1 - missing[0] if len(missing) == 1 else None,
                    [0, 0],
                    [],
                    forfeit=True,
                    reason="Карта не скачана вовремя: техническое поражение в раунде."
                    if missing
                    else "Игровой сервер не подтвердил запуск: раунд пропущен без очков.",
                )
            else:
                next_state = deepcopy(state)
                next_state["force_start"] = True
                next_state["deadline"] = deadline(30)
                touch(match, next_state)
                session.add(match)
        elif match.stage == "playing":
            await finish_match(
                session, match, None, cancelled=True, reason="Матч не завершился вовремя. Рейтинг не изменён."
            )
