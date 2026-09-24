"""Custom bot rosters, draft, authoritative settlement, and public history."""

# The project uses unittest fixtures and exception assertions.
# ruff: noqa: PT027

from contextlib import asynccontextmanager
from datetime import timedelta
import unittest
from unittest.mock import AsyncMock, patch

from app.database import Score, User, UserStatistics
from app.database.beatmap_ranking import (
    BeatmapRankingAudit,
    BeatmapRankingEvent,
    BeatmapRankingPolicy,
    BeatmapsetRankingPolicy,
    RankingPolicyAction,
    RankingPolicyScope,
)
from app.features.somsai.database.somsai import SomsaiMatch
from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus
from app.features.somsai.models.somsai import SomsaiBotResults, SomsaiBotScore
from app.features.somsai.routers.somsai_lio import internal_bot_results
from app.service.home_activity_service import local_releases, online_history, real_online_count
from app.features.somsai.services.somsai_match_service import (
    finish_match,
    match_action,
    members_of,
    room_event,
    settle_round,
    tick_match,
)
from app.features.somsai.services.somsai_party_service import active_user, activity
from app.features.somsai.services.somsai_service import create_custom, join_custom
from tests.test_somsai_core import SomsaiCoreTests

from fastapi import HTTPException
from sqlmodel import SQLModel, select


class BotCustomTests(unittest.IsolatedAsyncioTestCase):
    setUp = SomsaiCoreTests.setUp
    asyncSetUp = SomsaiCoreTests.asyncSetUp
    add_score = SomsaiCoreTests.add_score

    async def create(self, size=1):
        await create_custom(
            self.session,
            10,
            f"{size}v{size}",
            0,
            0,
            None,
            "Bots",
            1500,
            "mrekk",
            [
                {
                    "username": "mrekk",
                    "official_id": 7562902,
                    "avatar_url": "https://a.ppy.sh/7562902",
                    "country_code": "AU",
                }
            ],
        )
        match = await self.session.get(SomsaiMatch, (await activity(self.session, 10)).match_id)
        for uid in range(11, 10 + size):
            await join_custom(self.session, uid, match.id, 0)
        return match

    async def test_all_custom_sizes_fill_only_opponent_team_and_keep_profiles(self):
        for size in range(1, 5):
            match = await self.create(size)
            assert [len(team) for team in match.state["teams"]] == [size, size]
            assert not match.ranked
            assert match.stage != "waiting", "A full custom roster starts without an owner action"
            for uid in match.state["teams"][1]:
                user = await self.session.get(User, uid)
                assert user.is_bot
                assert user.avatar_url == "https://a.ppy.sh/7562902"
                with self.assertRaises(HTTPException):
                    await active_user(self.session, uid)
                assert all(
                    not row.is_ranked
                    for row in self.db.exec(select(UserStatistics).where(UserStatistics.user_id == uid)).all()
                )
            with self.assertRaises(HTTPException):
                await join_custom(self.session, 17, match.id, 1)
            await finish_match(self.session, match, None, cancelled=True)
            await match_action(self.session, match, 10, "leave_match")
            self.db.commit()

    async def test_custom_waits_for_last_human_slot_before_auto_start(self):
        await create_custom(
            self.session,
            10,
            "2v2",
            0,
            0,
            None,
            "Auto start",
            1500,
            "mrekk",
            [
                {
                    "username": "mrekk",
                    "official_id": 7562902,
                    "avatar_url": "https://a.ppy.sh/7562902",
                    "country_code": "AU",
                }
            ],
        )
        match = await self.session.get(SomsaiMatch, (await activity(self.session, 10)).match_id)
        assert match.stage == "waiting"
        await join_custom(self.session, 11, match.id, 0)
        assert match.stage == "banning"
        revision = match.revision
        await match_action(self.session, match, 10, "custom_start")
        assert match.revision == revision, "Legacy start must not reset the draft"

    async def test_bot_drafts_readies_and_result_cannot_be_overwritten_or_claim_human(self):
        match = await self.create()
        await match_action(self.session, match, 10, "custom_start")
        while match.stage in {"banning", "picking"}:
            captain = match.state["captains"][match.state["turn_team"]]
            if captain != 10:
                match.updated_at = utcnow() - timedelta(seconds=3)
                await tick_match(self.session, match)
            else:
                slot = next(s for s in match.state["slots"] if s["status"] == "available")
                await match_action(
                    self.session, match, 10, "ban" if match.stage == "banning" else "pick", slot_id=slot["id"]
                )
        bot_id = match.state["bots"][0]["user_id"]
        assert match.state["ready"] == [bot_id]
        await match_action(self.session, match, 10, "ready")
        await room_event(
            self.session,
            match.room_id,
            "started",
            match.state["playlist_item_id"],
            members_of(match),
            ready=members_of(match),
        )
        self.add_score(match, 10, 800000)
        item_id = match.state["playlist_item_id"]
        result = SomsaiBotResults(
            playlist_item_id=item_id,
            scores=[
                SomsaiBotScore(
                    user_id=bot_id,
                    score=900000,
                    accuracy=0.997,
                    max_combo=100,
                    statistics={"Great": 98, "Ok": 2},
                    maximum_statistics={"Great": 100},
                    rank="S",
                )
            ],
        )

        @asynccontextmanager
        async def transaction():
            yield self.session

        with patch("app.features.somsai.routers.somsai_lio.somsai_transaction", transaction):
            await internal_bot_results(match.room_id, result)
            await internal_bot_results(match.room_id, result)
            with self.assertRaises(HTTPException):
                await internal_bot_results(
                    match.room_id,
                    result.model_copy(
                        update={"scores": [SomsaiBotScore(user_id=10, score=1, accuracy=1, max_combo=1)]}
                    ),
                )
            with self.assertRaises(HTTPException):
                changed = result.model_copy(deep=True)
                changed.scores[0].score = 1
                await internal_bot_results(match.room_id, changed)
        await room_event(self.session, match.room_id, "completed", item_id, members_of(match))
        assert await settle_round(self.session, match)
        assert match.state["history"][0]["team_scores"] == [800000, 900000]
        assert match.state["history"][0]["players"][1]["is_bot"]
        assert match.state["history"][0]["players"][1]["statistics"] == {"Great": 98, "Ok": 2}
        assert match.state["history"][0]["players"][1]["max_combo"] == 100
        assert not self.db.exec(select(Score).where(Score.user_id == bot_id)).all()
        await finish_match(self.session, match, 1)
        assert match.state["rating_changes"] == []

    async def test_presence_excludes_bots_and_history_preserves_real_zero_and_gaps(self):
        match = await self.create()
        redis = AsyncMock()
        with patch("app.service.home_activity_service.get_online_user_ids", AsyncMock(return_value=members_of(match))):
            assert await real_online_count(self.session, redis) == 1
        now = utcnow()
        stamp = int(now.timestamp())
        redis.hgetall.return_value = {str(stamp - 600): "0", str(stamp - 1800): "3", str(stamp - 90000): "10"}
        data = await online_history(redis, now=now)
        assert [point["users"] for point in data["points"]] == [3, 0]
        assert len(data["points"]) == 2

    async def test_home_only_shows_current_local_ranked_or_loved_releases(self):
        for model in (BeatmapRankingAudit, BeatmapRankingPolicy, BeatmapsetRankingPolicy, BeatmapRankingEvent):
            SQLModel.metadata.tables[model.__tablename__].create(self.engine, checkfirst=True)
        assert await local_releases(self.session) == []
        policy = BeatmapRankingPolicy(
            beatmap_id=101,
            beatmapset_id=100,
            status=BeatmapRankStatus.LOVED,
            leaderboard_enabled=True,
            pp_enabled=False,
            ranked_checksum=f"{101:032x}",
            reason="Test",
        )
        audit = BeatmapRankingAudit(
            action=RankingPolicyAction.APPLY,
            scope=RankingPolicyScope.BEATMAP,
            beatmapset_id=100,
            beatmap_id=101,
            reason="Test",
            after={"status": 4, "is_active": True},
        )
        self.db.add_all([policy, audit])
        self.db.flush()
        feed = await local_releases(self.session)
        assert len(feed) == 1
        assert feed[0]["status"] == "loved"
        assert [item["id"] for item in feed[0]["beatmaps"]] == [101]
        policy.ranked_checksum = "changed-revision"
        self.db.add(policy)
        self.db.flush()
        assert await local_releases(self.session) == []
