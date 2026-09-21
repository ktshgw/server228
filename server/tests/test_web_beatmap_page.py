"""Real SQL checks for supporter boards and local discussions."""

# ruff: noqa: PT009, PT027
from datetime import timedelta
import json
from types import SimpleNamespace
import unittest

from app.database import (
    BeatmapComment,
    BeatmapCommentVote,
    Relationship,
    RelationshipType,
    Score,
    TotalScoreBestScore,
    User,
)
from app.helpers import utcnow
from app.models.mods import init_mods
from app.models.score import GameMode, Rank
from app.router.private.web_beatmap_community import CommentBody, beatmap_activity
from app.service.web_beatmap_community_service import add_comment, comment_page, remove_comment, vote_comment
from app.service.web_beatmap_leaderboard_service import first_place_score_ids, leaderboard_page, parse_mod_filter
from tests.test_negative_pp import NegativePPTests

from fastapi import HTTPException
from pydantic import ValidationError
from sqlmodel import select


class WebBeatmapPageTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        init_mods()
        NegativePPTests.setUp(self)
        for model in (TotalScoreBestScore, BeatmapComment, BeatmapCommentVote):
            model.__table__.create(self.engine, checkfirst=True)
        connection = self.db.connection().connection.driver_connection
        connection.create_function("json_contains", 2, lambda a, b: int(set(json.loads(b)).issubset(json.loads(a))))
        self.viewer = self.db.get(User, 10)
        self.viewer.is_supporter = True
        self.viewer.country_code = "RU"
        self.db.get(User, 11).country_code = "RU"
        self.db.get(User, 12).country_code = "US"
        self.db.add(Relationship(user_id=10, target_id=12, type=RelationshipType.FOLLOW))
        self.db.flush()
        template = self.db.get(Score, 1)
        values = {column.key: getattr(template, column.key) for column in Score.__table__.columns}
        for sid, uid, total, mods in [
            (501, 10, 900000, []),
            (502, 10, 700000, ["DT"]),
            (503, 10, 950000, ["HD", "DT"]),
            (504, 11, 920000, []),
            (505, 12, 960000, []),
        ]:
            self.db.add(
                Score(
                    **{
                        **values,
                        "id": sid,
                        "user_id": uid,
                        "total_score": total,
                        "leaderboard_eligible": True,
                        "mods": [{"acronym": mod} for mod in mods],
                    }
                )
            )
            self.db.flush()
            self.db.add(
                TotalScoreBestScore(
                    score_id=sid,
                    user_id=uid,
                    beatmap_id=101,
                    gamemode=GameMode.OSU,
                    total_score=total,
                    mods=mods,
                    rank=Rank.A,
                )
            )
        self.db.commit()

    def tearDown(self):
        self.db.close()
        self.engine.dispose()

    async def board(self, **kwargs):
        return await leaderboard_page(
            self.session,
            101,
            kwargs.pop("mode", None),
            kwargs.pop("page", 1),
            kwargs.pop("page_size", 50),
            kwargs.pop("viewer", self.viewer),
            **kwargs,
        )

    async def test_one_best_per_player_and_personal_rank_outside_page(self):
        result = await self.board(page_size=1, page=3)
        self.assertEqual(result["total"], 3)
        self.assertEqual(result["top"][0].id, 505)
        self.assertEqual(result["rows"][0][0].id, 504)
        self.assertEqual(result["personal"][0].id, 503)
        self.assertEqual(result["personal"][2], 2)

    async def test_converted_scores_use_requested_mode_on_original_beatmap(self):
        template = self.db.get(Score, 501)
        values = {column.key: getattr(template, column.key) for column in Score.__table__.columns}
        for mode in (GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
            sid = 600 + int(mode)
            self.db.add(Score(**{**values, "id": sid, "gamemode": mode}))
            self.db.flush()
            self.db.add(
                TotalScoreBestScore(
                    score_id=sid,
                    user_id=10,
                    beatmap_id=101,
                    gamemode=mode,
                    total_score=template.total_score,
                    mods=[],
                    rank=Rank.A,
                )
            )
        self.db.commit()

        for mode in (GameMode.TAIKO, GameMode.FRUITS, GameMode.MANIA):
            result = await self.board(mode=mode.value, mods="NM")
            self.assertEqual(result["mode"], mode.value)
            self.assertEqual([row[0].id for row in result["rows"]], [600 + int(mode)])
            self.assertEqual(result["top"][0].id, 600 + int(mode))
            self.assertEqual(result["personal"][2], 1)
            self.assertEqual(await beatmap_activity(101, self.session, mode.value), {"plays": 1, "passes": 1})
        native = await self.board(mode="osu")
        self.assertEqual(native["total"], 3)
        self.assertEqual([row[0].id for row in native["rows"]], [505, 503, 504])

    async def test_mod_filter_precedes_best_selection_and_nm_is_exact(self):
        result = await self.board(mods="DT")
        self.assertEqual([row[0].id for row in result["rows"]], [502])
        result = await self.board(mods="NM")
        self.assertEqual([row[0].id for row in result["rows"]], [505, 504, 501])
        result = await self.board(mods="dt,hd,DT")
        self.assertEqual([row[0].id for row in result["rows"]], [503])

    async def test_country_and_friends_apply_to_rows_totals_and_highlights(self):
        result = await self.board(scope="country")
        self.assertEqual([row[1].id for row in result["rows"]], [10, 11])
        self.assertEqual(result["total"], 2)
        self.assertEqual(result["top"][1].id, 10)
        result = await self.board(scope="friends")
        self.assertEqual([row[1].id for row in result["rows"]], [12, 10])

    async def test_supporter_access_is_enforced_without_trusting_ui(self):
        for viewer, status in [(None, 401), (SimpleNamespace(id=10, is_supporter=False), 403)]:
            for options in [{"scope": "country"}, {"scope": "friends"}, {"mods": "DT"}, {"mods": "NM"}]:
                with self.assertRaises(HTTPException) as error:
                    await self.board(viewer=viewer, **options)
                self.assertEqual(error.exception.status_code, status)
        self.assertEqual((await self.board(viewer=None))["total"], 3)

    async def test_hidden_and_ineligible_scores_do_not_leak(self):
        self.db.get(Score, 505).leaderboard_eligible = False
        self.db.get(User, 11).is_active = False
        self.db.flush()
        self.assertEqual((await self.board())["total"], 1)

    async def test_profile_first_places_follow_global_board_and_ties(self):
        self.assertEqual(list(self.db.exec(first_place_score_ids(12, GameMode.OSU)).all()), [505])
        self.assertEqual(list(self.db.exec(first_place_score_ids(10, GameMode.OSU)).all()), [])
        # Equal totals use the same newer-score tie-breaker as the board.
        self.db.get(TotalScoreBestScore, 503).total_score = 960000
        self.db.flush()
        self.assertEqual(list(self.db.exec(first_place_score_ids(12, GameMode.OSU)).all()), [505])
        self.db.get(Score, 505).leaderboard_eligible = False
        self.db.flush()
        self.assertEqual(list(self.db.exec(first_place_score_ids(10, GameMode.OSU)).all()), [503])
        self.assertEqual((await self.board())["top"][0].id, 503)
        self.db.get(User, 10).is_active = False
        self.db.flush()
        self.assertEqual(list(self.db.exec(first_place_score_ids(11, GameMode.OSU)).all()), [504])
        self.assertEqual(list(self.db.exec(first_place_score_ids(11, GameMode.TAIKO)).all()), [])

    async def test_activity_counts_every_pass_instead_of_boolean_sum(self):
        self.db.get(Score, 501).passed = False
        self.db.flush()
        self.assertEqual(await beatmap_activity(101, self.session), {"plays": 6, "passes": 5})
        self.assertEqual(await beatmap_activity(99999, self.session), {"plays": 0, "passes": 0})

    def test_invalid_mods_and_empty_comments_are_rejected(self):
        for mods in ["", "NM,DT", "FAKE", "DT); DROP TABLE scores"]:
            with self.assertRaises(HTTPException):
                parse_mod_filter(mods, GameMode.OSU)
        for body in ["   ", "x" * 2001]:
            with self.assertRaises(ValidationError):
                CommentBody(body=body)

    async def test_comments_stay_local_vote_idempotency_and_delete_permissions(self):
        await add_comment(self.session, self.viewer, 99999, "<script>alert(1)</script> Обсуждение")
        rows, _, total = await comment_page(self.session, 99999, self.viewer)
        self.assertEqual(total, 1)
        comment_id = rows[0][0].id
        with self.assertRaises(HTTPException) as error:
            await add_comment(self.session, self.viewer, 99999, "Повтор")
        self.assertEqual(error.exception.status_code, 429)
        await vote_comment(self.session, self.viewer, comment_id, True)
        await vote_comment(self.session, self.viewer, comment_id, True)
        rows, liked, _ = await comment_page(self.session, 99999, self.viewer, sort="top")
        self.assertEqual(rows[0][2], 1)
        self.assertEqual(liked, {comment_id})
        with self.assertRaises(HTTPException) as error:
            await remove_comment(self.session, self.db.get(User, 11), comment_id)
        self.assertEqual(error.exception.status_code, 403)
        await remove_comment(self.session, self.viewer, comment_id)
        self.assertEqual((await comment_page(self.session, 99999, self.viewer))[2], 0)
        self.assertEqual(self.db.exec(select(BeatmapCommentVote)).all(), [])

    async def test_comments_sort_and_unlike(self):
        first = BeatmapComment(beatmapset_id=100, user_id=10, body="Первый", created_at=utcnow() - timedelta(minutes=1))
        second = BeatmapComment(beatmapset_id=100, user_id=11, body="Второй")
        self.db.add_all([first, second])
        self.db.commit()
        await vote_comment(self.session, self.viewer, first.id, True)
        self.assertEqual((await comment_page(self.session, 100, self.viewer, sort="new"))[0][0][0].id, second.id)
        self.assertEqual((await comment_page(self.session, 100, self.viewer, sort="old"))[0][0][0].id, first.id)
        self.assertEqual((await comment_page(self.session, 100, self.viewer, sort="top"))[0][0][0].id, first.id)
        await vote_comment(self.session, self.viewer, first.id, False)
        self.assertEqual((await comment_page(self.session, 100, self.viewer))[1], set())
