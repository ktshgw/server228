from datetime import UTC, datetime
from types import SimpleNamespace as Obj
import unittest
from unittest.mock import AsyncMock, Mock

from app.models.beatmap import BeatmapRankStatus
from app.features.somsai.services.soms_activity_service import stage_local_map_release
from app.features.somsai.tasks.soms_announcements import stage_releases


class LovedAnnouncementTests(unittest.IsolatedAsyncioTestCase):
    def test_local_set_and_difficulty_announce_once_per_status_transition(self):
        for beatmap_id in (None, 71):
            for status in (BeatmapRankStatus.LOVED, BeatmapRankStatus.RANKED):
                session = Obj(add=Mock(), commit=Mock())
                args = {
                    "status": status,
                    "beatmapset_id": 14,
                    "beatmap_id": beatmap_id,
                    "label": "Artist — Title",
                    "actor_id": 8,
                    "changed_at": datetime(2026, 9, 13),
                }
                stage_local_map_release(session, previous_statuses=[BeatmapRankStatus.PENDING], **args)
                event = session.add.call_args.args[0]
                assert ("Loved" if status == BeatmapRankStatus.LOVED else "рейтинговая") in event.announcement
                assert ("beatmaps/71" if beatmap_id else "beatmapsets/14") in event.announcement
                assert event.actor_id == 8
                session.commit.assert_not_called()  # outbox participates in the ranking transaction
                session.add.reset_mock()
                stage_local_map_release(session, previous_statuses=[status, status], **args)
                session.add.assert_not_called()

    async def test_loved_release_uses_separate_status_key_and_link(self):
        added = []
        session = Obj(
            exec=AsyncMock(return_value=Obj(first=lambda: None)), add=Mock(side_effect=added.append), commit=AsyncMock()
        )
        fetcher = Obj(
            request_api=AsyncMock(
                return_value={
                    "beatmapsets": [
                        {
                            "id": 14,
                            "artist": "Artist",
                            "title": "Title",
                            "creator": "Mapper",
                            "ranked_date": "2026-09-13T12:00:00Z",
                        },
                        {"id": 13, "artist": "Old", "title": "Old", "ranked_date": "2026-09-11T12:00:00Z"},
                        {"id": 12, "ranked_date": None},
                    ],
                    "cursor_string": "older-page",
                }
            )
        )
        await stage_releases(session, fetcher, "loved", datetime(2026, 9, 12, tzinfo=UTC))
        assert len(added) == 1
        assert added[0].event_key == "loved-map:14"
        assert added[0].payload["status"] == "loved"
        assert "Loved" in added[0].announcement
        assert "beatmapsets/14" in added[0].announcement
        assert "Mapper" in added[0].announcement
        assert fetcher.request_api.await_args.kwargs["params"]["s"] == "loved"
        assert fetcher.request_api.await_count == 1  # stop at the activation watermark

    async def test_retry_does_not_duplicate_and_ranked_still_works(self):
        item = {"id": 14, "artist": "Artist", "title": "Title", "ranked_date": "2026-09-13T12:00:00+00:00"}
        session = Obj(exec=AsyncMock(return_value=Obj(first=lambda: 99)), add=Mock(), commit=AsyncMock())
        fetcher = Obj(request_api=AsyncMock(return_value={"beatmapsets": [item]}))
        await stage_releases(session, fetcher, "loved", datetime(2026, 9, 12, tzinfo=UTC))
        session.add.assert_not_called()
        session.exec.return_value = Obj(first=lambda: None)
        await stage_releases(session, fetcher, "ranked", datetime(2026, 9, 12, tzinfo=UTC))
        event = session.add.call_args.args[0]
        assert event.event_key == "ranked-map:14"
        assert "рейтинговая" in event.announcement
