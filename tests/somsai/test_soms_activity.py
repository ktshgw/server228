from datetime import UTC, datetime, timedelta
from types import SimpleNamespace as Obj
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.features.somsai.database.soms_activity import SomsActivity
from app.database.user_preference import UserPreference
from app.models.score import GameMode
from app.router.private.web_notifications import NotificationPreferences, inbox, mark_read, read_one, save_preferences
from app.features.somsai.services.soms_activity_service import (
    announcement_ready,
    chat_link,
    eligible_recipient,
    notification_preferences,
    stage_first_place,
    stage_friend_notification,
)

from fastapi import HTTPException, Response
from sqlalchemy.dialects import mysql


class ActivityTests(unittest.IsolatedAsyncioTestCase):
    async def test_multiplayer_intermediate_winner_is_replaced_and_original_holder_preserved(self):
        for previous_id in (None, 9, 7):
            db = self.session()
            pending = SomsActivity(
                id=14,
                event_key="first:14",
                kind="rank_lost",
                recipient_id=9,
                announcement="interim",
                payload={"previous_user_id": previous_id, "previous_username": "original"},
            )
            db.exec.side_effect = [Obj(first=lambda: None), Obj(all=lambda: [pending])]
            score = self.score()
            score.room_id, score.playlist_item_id = 42, 2
            with (
                patch(
                    "app.features.somsai.services.soms_activity_service.current_winner", new=AsyncMock(return_value=(15, 7, "winner"))
                ),
                patch("app.features.somsai.services.soms_activity_service.eligible_recipient", new=AsyncMock(return_value=previous_id)),
            ):
                await stage_first_place(db, score, (14, 8, "interim"))
            assert pending.delivered
            assert pending.announcement is None
            assert pending.recipient_id is None
            events = [call.args[0] for call in db.add.call_args_list if call.args[0] is not pending]
            if previous_id == 7:
                assert not events, "The original holder retained #1; no lost-rank notification"
            else:
                assert len(events) == 1
                assert events[0].payload["previous_user_id"] == previous_id
                assert "interim" not in events[0].announcement

    async def test_multiplayer_announcement_waits_for_settlement_and_solo_does_not(self):
        db = self.session()
        now = datetime.now(UTC)
        event = SomsActivity(
            kind="rank_lost", event_key="first:15", created_at=now, payload={"room_id": 42, "playlist_item_id": 2}
        )
        assert not await announcement_ready(db, event, now + timedelta(seconds=4))
        match = Obj(stage="results", state={"playlist_item_id": 2})
        db.exec.return_value = Obj(first=lambda: match)
        assert not await announcement_ready(db, event, now + timedelta(seconds=6))
        match.stage = "picking"
        db.exec.side_effect = [Obj(first=lambda: match), Obj(first=lambda: 123)]
        assert not await announcement_ready(db, event, now + timedelta(seconds=6))
        db.exec.side_effect = [Obj(first=lambda: match), Obj(first=lambda: None)]
        assert await announcement_ready(db, event, now + timedelta(seconds=6))
        db.exec.side_effect = None
        match.stage = "results"
        assert await announcement_ready(db, event, now + timedelta(seconds=121))
        event.payload = {}
        assert await announcement_ready(db, event, now)

    def session(self):
        return Obj(
            get=AsyncMock(),
            exec=AsyncMock(return_value=Obj(first=lambda: None)),
            add=Mock(),
            commit=AsyncMock(),
            flush=AsyncMock(),
        )

    def user(self, supporter=True):
        return Obj(
            id=9,
            username="previous",
            is_active=True,
            is_supporter=supporter,
            is_bot=False,
            is_restricted=AsyncMock(return_value=False),
        )

    def score(self):
        return Obj(
            id=15,
            user_id=7,
            beatmap_id=88,
            gamemode=GameMode.OSU,
            user=Obj(id=7, username="winner"),
            mods=[{"acronym": "DT", "settings": {"speed_change": 1.2}}],
            accuracy=0.98,
            pp=321,
            beatmap=Obj(version="Insane", beatmapset_id=77, beatmapset=Obj(artist="Artist", title="Title")),
        )

    async def test_defaults_and_explicit_opt_out(self):
        db = self.session()
        db.get.return_value = None
        assert await notification_preferences(db, 9) == {
            "rank_lost": True,
            "friend_added": True,
            "friend_removed": True,
        }
        db.get.return_value = UserPreference(user_id=9, extra={"soms_notifications": {"rank_lost": False}})
        assert await notification_preferences(db, 9) == {
            "rank_lost": False,
            "friend_added": True,
            "friend_removed": True,
        }

    async def test_supporter_and_preference_required_for_recipient(self):
        db = self.session()
        db.get.return_value = self.user(False)
        assert await eligible_recipient(db, 9, "rank_lost") is None
        db.get.side_effect = [self.user(), None]
        assert await eligible_recipient(db, 9, "rank_lost") == 9
        db.get.side_effect = [
            self.user(),
            UserPreference(user_id=9, extra={"soms_notifications": {"friend_added": False}}),
        ]
        assert await eligible_recipient(db, 9, "friend_added") is None

    async def test_new_first_place_announced_with_previous_holder_at_end(self):
        db = self.session()
        with (
            patch("app.features.somsai.services.soms_activity_service.current_winner", new=AsyncMock(return_value=(15, 7, "winner"))),
            patch("app.features.somsai.services.soms_activity_service.eligible_recipient", new=AsyncMock(return_value=9)),
        ):
            await stage_first_place(db, self.score(), (10, 9, "previous"))
        event = db.add.call_args.args[0]
        assert isinstance(event, SomsActivity)
        assert event.announcement.endswith("Первое место забрано у previous.")
        assert "DT" in event.announcement
        assert "98.00%" in event.announcement
        assert event.recipient_id == 9
        assert event.payload["score_id"] == 15
        assert event.payload["beatmap_id"] == 88
        assert event.payload["beatmapset_id"] == 77
        db.commit.assert_not_awaited()  # atomic with the score, no premature commit

    async def test_inbox_backfills_beatmapset_for_existing_rank_notification(self):
        db = self.session()
        db.get.return_value = None
        event = SomsActivity(
            id=4,
            event_key="first:15",
            kind="rank_lost",
            recipient_id=9,
            payload={"beatmap_id": 88, "score_id": 15},
        )
        db.exec.side_effect = [
            Obj(one=lambda: 1),
            Obj(all=lambda: [event]),
            Obj(all=lambda: [(88, 77)]),
        ]
        result = await inbox(Obj(user=self.user()), db, Response())
        assert result["items"][0]["data"]["beatmap_id"] == 88
        assert result["items"][0]["data"]["beatmapset_id"] == 77

    async def test_own_improvement_nonwinner_and_retry_do_not_announce(self):
        for winner, previous, duplicate in [
            ((15, 7, "winner"), (10, 7, "winner"), None),
            ((12, 8, "other"), (12, 8, "other"), None),
            ((15, 7, "winner"), (10, 9, "previous"), 33),
        ]:
            db = self.session()
            db.exec.return_value = Obj(first=lambda: duplicate)
            with patch("app.features.somsai.services.soms_activity_service.current_winner", new=AsyncMock(return_value=winner)):
                await stage_first_place(db, self.score(), previous)
            db.add.assert_not_called()

    async def test_first_score_has_announcement_without_lost_notification(self):
        db = self.session()
        with patch("app.features.somsai.services.soms_activity_service.current_winner", new=AsyncMock(return_value=(15, 7, "winner"))):
            await stage_first_place(db, self.score(), None)
        event = db.add.call_args.args[0]
        assert event.recipient_id is None
        assert "забрано у" not in event.announcement

    async def test_friend_outbox_is_unique_per_relationship(self):
        db = self.session()
        with patch("app.features.somsai.services.soms_activity_service.eligible_recipient", new=AsyncMock(return_value=9)):
            await stage_friend_notification(db, Obj(id=7, username="friend"), 9, 66)
            assert db.add.call_args.args[0].event_key == "friend:66"
            db.add.reset_mock()
            db.exec.return_value = Obj(first=lambda: 1)
            await stage_friend_notification(db, Obj(id=7, username="friend"), 9, 66)
            db.add.assert_not_called()

    async def test_non_supporter_cannot_read_inbox_or_other_users_items(self):
        db = self.session()
        db.get.return_value = None
        result = await inbox(Obj(user=self.user(False)), db, Response())
        assert result["items"] == []
        assert result["unread"] == 0
        db.exec.assert_not_awaited()
        db.get.return_value = SomsActivity(event_key="test", kind="friend_added", recipient_id=8)
        with patch("app.router.private.web_notifications._require_csrf"):
            with self.assertRaises(HTTPException) as caught:  # noqa: PT027
                await read_one(1, Obj(), Obj(user=self.user()), db)
            assert caught.exception.status_code == 404

    async def test_removed_friend_has_independent_preference_and_deduplication(self):
        db = self.session()
        db.get.side_effect = [
            self.user(),
            UserPreference(user_id=9, extra={"soms_notifications": {"friend_added": False, "friend_removed": True}}),
        ]
        await stage_friend_notification(db, Obj(id=7, username="former friend"), 9, 66, removed=True)
        event = db.add.call_args.args[0]
        assert event.kind == "friend_removed"
        assert event.event_key == "unfriend:66"
        assert event.recipient_id == 9
        assert event.payload == {"username": "former friend", "user_id": 7}
        db.commit.assert_not_awaited()
        db.add.reset_mock()
        db.get.side_effect = [self.user(), None]
        db.exec.return_value = Obj(first=lambda: event)
        await stage_friend_notification(db, Obj(id=7), 9, 66, removed=True)
        db.add.assert_not_called()
        db.get.side_effect = [
            self.user(),
            UserPreference(user_id=9, extra={"soms_notifications": {"friend_added": True, "friend_removed": False}}),
        ]
        await stage_friend_notification(db, Obj(id=7), 9, 67, removed=True)
        db.add.assert_not_called()
        db.get.side_effect = [self.user(False)]
        await stage_friend_notification(db, Obj(id=7), 9, 68, removed=True)
        db.add.assert_not_called()

    async def test_native_unfriend_notifies_but_unblock_does_not(self):
        from app.database.relationship import Relationship, RelationshipType
        from app.router.v2.relationship import delete_relationship

        for kind, path in [(RelationshipType.FOLLOW, "/friends/9"), (RelationshipType.BLOCK, "/blocks/9")]:
            db = self.session()
            db.delete = AsyncMock()
            relationship = Relationship(id=66, user_id=7, target_id=9, type=kind)
            db.exec.side_effect = [Obj(first=lambda: True), Obj(), Obj(first=lambda: relationship)]
            actor = Obj(id=7, username="actor", is_restricted=AsyncMock(return_value=False))
            with (
                patch("app.features.somsai.services.soms_activity_service.stage_friend_notification", new=AsyncMock()) as notify,
                patch("app.router.v2.relationship.hub.emit"),
            ):
                await delete_relationship(db, Obj(url=Obj(path=path)), 9, actor)
                if kind == RelationshipType.FOLLOW:
                    notify.assert_awaited_once_with(db, actor, 9, 66, removed=True)
                else:
                    notify.assert_not_awaited()
            db.delete.assert_awaited_once_with(relationship)
            db.commit.assert_awaited_once()

    async def test_preferences_preserve_unrelated_data_and_read_all_is_bounded(self):
        db = self.session()
        db.get.return_value = UserPreference(user_id=9, extra={"other_feature": 42})
        with patch("app.router.private.web_notifications._require_csrf") as csrf:
            await save_preferences(NotificationPreferences(rank_lost=False), Obj(), Obj(user=self.user()), db)
            assert db.add.call_args.args[0].extra == {
                "other_feature": 42,
                "soms_notifications": {"rank_lost": False, "friend_added": True, "friend_removed": True},
            }
            await mark_read(Obj(), Obj(user=self.user()), db, through=123)
        assert csrf.call_count == 2
        statement = str(
            db.exec.call_args.args[0].compile(dialect=mysql.dialect(), compile_kwargs={"literal_binds": True})
        )
        assert "recipient_id = 9" in statement
        assert "id <= 123" in statement

    def test_chat_metadata_cannot_break_links(self):
        link = chat_link("beatmaps/123", "Title]\n[fake")
        assert "\n" not in link
        assert link.count("[") == 1
        assert link.count("]") == 1

    async def test_new_map_poll_skips_history_and_deduplicates(self):
        from app.features.somsai.tasks.soms_announcements import announce_maps_job

        db = self.session()
        db.exec.side_effect = [
            Obj(first=lambda: Obj(created_at=datetime(2026, 9, 9))),
            Obj(first=lambda: Obj(created_at=datetime(2026, 9, 12))),
            Obj(first=lambda: None),
        ]
        context = AsyncMock()
        context.__aenter__.return_value = db
        fetcher = Obj(
            request_api=AsyncMock(
                return_value={
                    "beatmapsets": [
                        {
                            "id": 42,
                            "artist": "A",
                            "title": "New",
                            "creator": "Mapper",
                            "ranked_date": "2026-09-09T01:00:00Z",
                        },
                        {"id": 41, "artist": "A", "title": "Old", "ranked_date": "2026-09-08T01:00:00Z"},
                    ]
                }
            )
        )
        with (
            patch("app.features.somsai.tasks.soms_announcements.with_db", return_value=context),
            patch("app.dependencies.fetcher.get_fetcher", new=AsyncMock(return_value=fetcher)),
        ):
            await announce_maps_job()
        assert db.add.call_count == 1
        assert db.add.call_args.args[0].event_key == "ranked-map:42"

    async def test_outbox_retry_reuses_persisted_message_id_and_populates_chat_cache(self):
        from app.features.somsai.services.soms_activity_service import deliver_announcements

        db = self.session()
        db.refresh = AsyncMock()
        event = SomsActivity(id=1, event_key="first:15", kind="rank_lost", announcement="fixture", chat_message_id=70)
        db.exec.side_effect = [Obj(first=lambda: event), Obj(first=lambda: None)]
        db.get.return_value = Obj(message_id=70)
        context = AsyncMock()
        context.__aenter__.return_value = db
        message = {"message_id": 70, "content": "fixture"}
        with (
            patch("app.dependencies.database.with_db", return_value=context),
            patch(
                "app.features.somsai.services.soms_activity_service.ensure_announce_channel",
                new=AsyncMock(return_value=Obj(channel_id=3)),
            ),
            patch("app.database.chat.ChatMessageModel.transform", new=AsyncMock(return_value=message)),
            patch(
                "app.service.redis_message_system.redis_message_system._generate_message_id", new=AsyncMock()
            ) as allocate,
            patch("app.service.redis_message_system.redis_message_system._store_to_redis", new=AsyncMock()) as cache,
            patch("app.router.notification.server.server.send_message_to_channel", new=AsyncMock()) as broadcast,
        ):
            await deliver_announcements()
        allocate.assert_not_awaited()
        cache.assert_awaited_once_with(70, 3, message)
        broadcast.assert_awaited_once_with(message)
        assert event.delivered
