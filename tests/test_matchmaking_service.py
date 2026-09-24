from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.models.beatmap import BeatmapRankStatus
from app.models.room import MatchType
from app.router.lio import _parse_room_enums
from app.service.matchmaking_service import ranked_beatmaps


class MatchmakingCatalogueTests(unittest.IsolatedAsyncioTestCase):
    async def test_official_pool_also_honours_local_unrank_policy(self):
        beatmaps = [
            SimpleNamespace(id=i, beatmapset_id=20, checksum="a" * 32, difficulty_rating=4, total_length=90)
            for i in (1, 2, 3)
        ]
        policies = {
            1: SimpleNamespace(status=BeatmapRankStatus.RANKED, leaderboard_enabled=True, pp_enabled=True),
            2: SimpleNamespace(status=BeatmapRankStatus.PENDING, leaderboard_enabled=False, pp_enabled=False),
            3: SimpleNamespace(status=BeatmapRankStatus.LOVED, leaderboard_enabled=True, pp_enabled=False),
        }
        result = Mock()
        result.all.return_value = beatmaps
        session = Mock(exec=AsyncMock(return_value=result))
        with patch(
            "app.service.matchmaking_service.get_effective_beatmap_policies", AsyncMock(return_value=policies)
        ) as resolve:
            maps = await ranked_beatmaps(session, 0)
        assert [item["beatmap_id"] for item in maps] == [1]
        resolve.assert_awaited_once_with(session, beatmaps)
        query = str(session.exec.call_args.args[0])
        assert "beatmaps.deleted_at IS NULL" in query
        assert "beatmapsets.download_disabled IS false" in query
        assert "beatmaps.hit_length BETWEEN" in query
        assert "beatmapsets.track_id IS NOT NULL" in query
        assert "beatmaps.beatmap_status IN" in query

    async def test_invalid_rulesets_and_mania_keymodes_are_not_queues(self):
        session = Mock()
        for ruleset, variant in ((-1, 0), (4, 0), (3, 0), (3, 6)):
            assert await ranked_beatmaps(session, ruleset, variant) == []

    def test_ranked_play_room_type_is_preserved(self):
        assert _parse_room_enums("RankedPlay", "HostOnly")[0] == MatchType.RANKED_PLAY
