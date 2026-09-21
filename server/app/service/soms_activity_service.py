"""Transaction-bound notifications and a persistent chat outbox."""

from app.config import settings
from app.const import BANCHOBOT_ID
from app.database.soms_activity import SomsActivity
from app.database.user_preference import UserPreference
from app.helpers import utcnow

from sqlmodel import col, select

DEFAULT_PREFERENCES = {"rank_lost": True, "friend_added": True, "friend_removed": True}
ANNOUNCE_CHANNEL = "#announce"


def stage_local_map_release(
    session, *, status, previous_statuses, beatmapset_id, beatmap_id, label, actor_id, changed_at
):
    from app.models.beatmap import BeatmapRankStatus

    if status not in (BeatmapRankStatus.RANKED, BeatmapRankStatus.LOVED):
        return
    if not any(previous != status for previous in previous_statuses):
        return
    category = "loved" if status == BeatmapRankStatus.LOVED else "ranked"
    target = f"beatmaps/{beatmap_id}" if beatmap_id is not None else f"beatmapsets/{beatmapset_id}"
    prefix = "Новая Loved-карта на SOMS!" if category == "loved" else "Новая рейтинговая карта на SOMS!"
    session.add(
        SomsActivity(
            event_key=f"local-{category}:{beatmapset_id}:{beatmap_id or 0}:{changed_at.isoformat()}",
            kind="new_map",
            actor_id=actor_id,
            payload={"beatmapset_id": beatmapset_id, "beatmap_id": beatmap_id, "status": int(status)},
            announcement=f"{prefix}: {chat_link(target, label[:350])}!",
        )
    )


async def notification_preferences(session, user_id):
    preference = await session.get(UserPreference, user_id)
    saved = (preference.extra or {}).get("soms_notifications", {}) if preference else {}
    if not isinstance(saved, dict):
        saved = {}
    return {key: saved.get(key, default) is not False for key, default in DEFAULT_PREFERENCES.items()}


async def eligible_recipient(session, user_id, kind):
    from app.database.user import User

    user = await session.get(User, user_id)
    if not user or not user.is_active or not user.is_supporter or user.is_bot:
        return None
    if await user.is_restricted(session):
        return None
    return user_id if (await notification_preferences(session, user_id))[kind] else None


async def stage_friend_notification(session, actor, target_id, relationship_id, *, removed=False):
    kind = "friend_removed" if removed else "friend_added"
    recipient = await eligible_recipient(session, target_id, kind)
    if recipient is None:
        return
    key = f"{'unfriend' if removed else 'friend'}:{relationship_id}"
    if (await session.exec(select(SomsActivity.id).where(SomsActivity.event_key == key))).first():
        return
    session.add(
        SomsActivity(
            event_key=key,
            kind=kind,
            actor_id=actor.id,
            recipient_id=recipient,
            payload={"username": actor.username, "user_id": actor.id},
            delivered=True,
        )
    )


async def current_winner(session, beatmap_id, mode):
    from app.database import Score, TotalScoreBestScore, User
    from app.service.web_beatmap_leaderboard_service import visible_score_conditions

    # Select scalar values: the best-score ORM object will be mutated in this transaction.
    return (
        await session.exec(
            select(TotalScoreBestScore.score_id, TotalScoreBestScore.user_id, User.username)
            .join(Score, col(Score.id) == col(TotalScoreBestScore.score_id))
            .join(User, col(User.id) == col(TotalScoreBestScore.user_id))
            .where(
                TotalScoreBestScore.beatmap_id == beatmap_id,
                TotalScoreBestScore.gamemode == mode,
                *visible_score_conditions(),
            )
            .order_by(col(TotalScoreBestScore.total_score).desc(), col(TotalScoreBestScore.score_id).desc())
            .limit(1)
            .with_for_update()
        )
    ).first()


def chat_link(path, label):
    # Bancho link syntax; metadata must not be able to close the label or inject a mention line.
    clean = str(label).replace("[", "(").replace("]", ")").replace("\n", " ").replace("\r", " ")
    return f"[{str(settings.web_url).rstrip('/')}/{path.lstrip('/')} {clean}]"


async def stage_first_place(session, score, previous):
    winner = await current_winner(session, score.beatmap_id, score.gamemode)
    if not winner or winner[0] != score.id:
        return
    key = f"first:{score.id}"
    if (await session.exec(select(SomsActivity.id).where(SomsActivity.event_key == key))).first():
        return
    room_id = getattr(score, "room_id", None)
    playlist_id = getattr(score, "playlist_item_id", None)
    if room_id is not None:
        pending = (
            await session.exec(
                select(SomsActivity)
                .where(
                    SomsActivity.kind == "rank_lost",
                    col(SomsActivity.delivered).is_(False),
                    col(SomsActivity.chat_message_id).is_(None),
                    SomsActivity.payload["room_id"].as_integer() == room_id,
                    SomsActivity.payload["playlist_item_id"].as_integer() == playlist_id,
                    SomsActivity.payload["beatmap_id"].as_integer() == score.beatmap_id,
                    SomsActivity.payload["mode"].as_string() == score.gamemode.readable(),
                )
                .order_by(col(SomsActivity.id))
                .with_for_update()
            )
        ).all()
        if pending:
            baseline = pending[0].payload
            previous = (
                (0, baseline["previous_user_id"], baseline.get("previous_username", ""))
                if baseline.get("previous_user_id")
                else None
            )
            for event in pending:
                # Intermediate leaders were never announced. They must not get
                # a notification claiming that they lost an announced #1 either.
                event.delivered = True
                event.recipient_id = None
                event.announcement = None
                session.add(event)
    if previous and previous[1] == score.user_id:
        return
    beatmap = score.beatmap
    title = f"{beatmap.beatmapset.artist} — {beatmap.beatmapset.title} [{beatmap.version}]"
    # APIMod is stored as JSON dictionaries, including after ORM reloads.
    mods = ", ".join(str(mod.get("acronym", "?")) for mod in score.mods) or "NM"
    message = (
        f"{chat_link(f'users/{score.user_id}', score.user.username)} занял #1 на "
        f"{chat_link(f'beatmaps/{score.beatmap_id}', title[:250])} "
        f"({score.gamemode.readable()}, {mods}, {score.accuracy * 100:.2f}%, {score.pp or 0:.2f}pp)."
    )
    if previous:
        message += f" Первое место забрано у {previous[2]}."
    recipient = await eligible_recipient(session, previous[1], "rank_lost") if previous else None
    session.add(
        SomsActivity(
            event_key=key,
            kind="rank_lost",
            actor_id=score.user_id,
            recipient_id=recipient,
            announcement=message[:1000],
            payload={
                "username": score.user.username,
                "user_id": score.user_id,
                "beatmap_id": score.beatmap_id,
                "beatmapset_id": beatmap.beatmapset_id,
                "title": title,
                "score_id": score.id,
                "mode": score.gamemode.readable(),
                "previous_user_id": previous[1] if previous else None,
                "previous_username": previous[2] if previous else None,
                "room_id": room_id,
                "playlist_item_id": playlist_id,
            },
        )
    )


async def ensure_announce_channel(session):
    from app.database.chat import ChannelType, ChatChannel

    channel = (await session.exec(select(ChatChannel).where(ChatChannel.channel_name == ANNOUNCE_CHANNEL))).first()
    if channel is None:
        channel = ChatChannel(
            channel_name=ANNOUNCE_CHANNEL,
            type=ChannelType.PUBLIC,
            description="SOMSBot: новые Ranked/Loved-карты и первые места SOMS!",
        )
        session.add(channel)
        await session.flush()
    return channel


async def announcement_ready(session, event, now=None):
    if event.kind != "rank_lost" or event.payload.get("room_id") is None:
        return True
    from app.database.somsai import SomsaiMatch
    from app.service.somsai_party_service import aware

    age = ((now or utcnow()) - aware(event.created_at)).total_seconds()
    if age < 5:
        return False
    match = (await session.exec(select(SomsaiMatch).where(SomsaiMatch.room_id == event.payload["room_id"]))).first()
    if (
        age < 120
        and match
        and match.stage in {"playing", "results"}
        and match.state.get("playlist_item_id") == event.payload.get("playlist_item_id")
    ):
        return False
    if age < 120:
        # Round settlement can precede PP/statistics processing of the last human.
        # Do not announce a provisional leaderboard while those scores are pending.
        from app.database.score import Score

        pending_score = (
            await session.exec(
                select(Score.id)
                .where(
                    Score.room_id == event.payload["room_id"],
                    Score.playlist_item_id == event.payload.get("playlist_item_id"),
                    col(Score.processed).is_(False),
                )
                .limit(1)
            )
        ).first()
        if pending_score is not None:
            return False
    return True


async def deliver_announcements():
    from app.database.chat import ChatMessage, ChatMessageModel
    from app.dependencies.database import with_db
    from app.router.notification.server import server
    from app.service.redis_message_system import redis_message_system

    async with with_db() as session:
        channel = await ensure_announce_channel(session)
        channel_id = channel.channel_id
        await session.commit()
        # A crash before commit leaves no message; a crash after broadcast reuses the same ID.
        # Clients deduplicate message IDs, and history always contains exactly one entry.
        last_seen = 0
        for _ in range(50):
            event = (
                await session.exec(
                    select(SomsActivity)
                    .where(
                        col(SomsActivity.delivered).is_(False),
                        col(SomsActivity.announcement).is_not(None),
                        SomsActivity.id > last_seen,
                    )
                    .order_by(col(SomsActivity.id))
                    .limit(1)
                    .with_for_update(skip_locked=True)
                )
            ).first()
            if event is None:
                break
            last_seen = event.id
            if not await announcement_ready(session, event):
                continue
            message = await session.get(ChatMessage, event.chat_message_id) if event.chat_message_id else None
            if message is None:
                message_id = await redis_message_system._generate_message_id(channel_id)
                message = ChatMessage(
                    message_id=message_id, channel_id=channel_id, sender_id=BANCHOBOT_ID, content=event.announcement
                )
                session.add(message)
                await session.flush()
                event.chat_message_id = message.message_id
                session.add(event)
                await session.commit()
            await session.refresh(message)
            response = await ChatMessageModel.transform(message, includes=["sender"])
            await redis_message_system._store_to_redis(message.message_id, channel_id, response)
            await server.send_message_to_channel(response)
            event.delivered = True
            session.add(event)
            await session.commit()
