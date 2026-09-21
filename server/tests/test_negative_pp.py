"""PP penalty integration: real SQL, ownership, rankings, website and staff boundaries."""

# ruff: noqa: PT009, PT027
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from app.database import (
    Beatmap,
    BeatmapMapperCredit,
    Beatmapset,
    BestScore,
    NegativePPRule,
    Score,
    User,
    UserStatistics,
)
from app.database.score import calculate_user_pp, get_user_best_pp
from app.database.statistics import public_ranking_conditions
from app.helpers import utcnow
from app.models.negative_pp import NegativePPDelete, NegativePPTarget, NegativePPWrite
from app.models.score import GameMode, Rank
from app.router.private import negative_pp_admin as admin
from app.router.private.web_site import _score_payloads, _user_payload
from app.service.negative_pp_service import (
    negative_map_ids,
    negative_score_counts,
    negative_score_users,
    recalculate,
    reference_id,
)
from tests.test_admin_panel import staff
from tests.test_somsai_core import AsyncSql, SomsaiCoreTests

from fastapi import HTTPException
from sqlalchemy import BigInteger, Integer, MetaData
from sqlmodel import SQLModel, select
from starlette.requests import Request


class PenaltySession(AsyncSql):
    @property
    def info(self):
        return self.db.info

    async def refresh(self, item, **kwargs):
        self.db.refresh(item, **kwargs)


class NegativePPTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        SomsaiCoreTests.setUp(self)
        metadata = MetaData()
        for table in SQLModel.metadata.tables.values():
            table.to_metadata(metadata)
        for table in metadata.tables.values():
            for column in table.columns:
                if column.primary_key and isinstance(column.type, BigInteger):
                    column.type = Integer()
        wanted = {"negative_pp_rules", "beatmap_mapper_credits", "best_scores", "score_imports", "beatmap_failtimes"}
        wanted.discard("beatmap_failtimes")
        wanted.update(name for name in metadata.tables if "failtime" in name)
        while True:
            expanded = wanted | {fk.column.table.name for name in wanted for fk in metadata.tables[name].foreign_keys}
            if expanded == wanted:
                break
            wanted = expanded
        metadata.create_all(self.engine, tables=[metadata.tables[name] for name in wanted])
        self.session = PenaltySession(self.db)
        now = utcnow()
        for sid, bid, pp in [(1, 101, 300), (2, 102, 200), (3, 103, 100)]:
            self.db.add(
                Score(
                    id=sid,
                    beatmap_id=bid,
                    user_id=10,
                    gamemode=GameMode.OSU,
                    pp=pp,
                    rank=Rank.A,
                    type="solo_score",
                    accuracy=0.95,
                    ended_at=now,
                    started_at=now,
                    has_replay=True,
                    max_combo=100,
                    passed=True,
                    ranked=True,
                    processed=True,
                    mods=[],
                    n300=95,
                    n100=5,
                    n50=0,
                    nmiss=0,
                    ngeki=0,
                    nkatu=0,
                    map_md5=f"{bid:032x}",
                )
            )
        self.db.flush()
        self.db.add_all(
            [
                BestScore(user_id=10, beatmap_id=bid, score_id=sid, pp=pp, acc=0.95, gamemode=GameMode.OSU)
                for sid, bid, pp in [(1, 101, 300), (2, 102, 200), (3, 103, 100)]
            ]
        )
        self.db.add_all(
            [
                BeatmapMapperCredit(beatmap_id=101, mapper_id=500, username="Host"),
                BeatmapMapperCredit(beatmap_id=102, mapper_id=600, username="Guest"),
                BeatmapMapperCredit(beatmap_id=103, mapper_id=500, username="Host"),
            ]
        )
        self.db.commit()

    def rule(self, kind="beatmap", target_id=102):
        rule = NegativePPRule(kind=kind, target_id=target_id, label="Fixture", reason="Test rule", created_by=10)
        self.db.add(rule)
        self.db.flush()
        return rule

    async def test_subtracts_same_weight_without_changing_raw_pp_or_order(self):
        before, accuracy = await calculate_user_pp(self.session, 10, GameMode.OSU)
        self.rule()
        after, new_accuracy = await calculate_user_pp(self.session, 10, GameMode.OSU)
        self.assertAlmostEqual(before, 300 + 200 * 0.95 + 100 * 0.95**2)
        self.assertAlmostEqual(after, 300 - 200 * 0.95 + 100 * 0.95**2)
        self.assertEqual(accuracy, new_accuracy)
        self.assertEqual([s.score_id for s in await get_user_best_pp(self.session, 10, GameMode.OSU)], [1, 2, 3])
        self.assertEqual(self.db.get(Score, 2).pp, 200)

    async def test_overlaps_only_subtract_once_and_removal_restores(self):
        first = self.rule()
        second = self.rule("mapper", 600)
        negative, _ = await calculate_user_pp(self.session, 10, GameMode.OSU)
        self.db.delete(first)
        self.db.flush()
        self.assertEqual((await calculate_user_pp(self.session, 10, GameMode.OSU))[0], negative)
        self.db.delete(second)
        self.db.flush()
        self.assertAlmostEqual((await calculate_user_pp(self.session, 10, GameMode.OSU))[0], 580.25)

    async def test_guest_does_not_penalize_other_difficulties_or_set_host(self):
        self.rule("mapper", 600)
        self.assertEqual(await negative_map_ids(self.session, [101, 102, 103]), {102})
        self.db.add(BeatmapMapperCredit(beatmap_id=104, mapper_id=600, username="Renamed guest"))
        self.db.flush()
        self.assertEqual(await negative_map_ids(self.session, [101, 102, 103, 104]), {102, 104})

    async def test_set_rule_covers_all_difficulties(self):
        self.rule("beatmapset", 100)
        self.assertEqual(await negative_map_ids(self.session, [101, 102, 103]), {101, 102, 103})
        await recalculate(self.session, [101, 102, 103])
        statistics = self.db.exec(
            select(UserStatistics).where(UserStatistics.user_id == 10, UserStatistics.mode == GameMode.OSU)
        ).one()
        self.assertAlmostEqual(statistics.pp, -580.25)
        ranked = self.db.exec(select(UserStatistics.user_id).where(*public_ranking_conditions(GameMode.OSU))).all()
        self.assertIn(10, ranked)

    async def test_badge_includes_non_best_score_but_not_failed_or_unranked(self):
        self.rule()
        self.db.delete(self.db.get(BestScore, 2))
        self.db.flush()
        self.assertIn(10, await negative_score_users(self.session))
        score = self.db.get(Score, 2)
        for field in ["passed", "ranked", "processed"]:
            setattr(score, field, False)
            self.db.flush()
            self.session.info.clear()
            self.assertNotIn(10, await negative_score_users(self.session))
            setattr(score, field, True)
        score.pp = 0
        self.db.flush()
        self.session.info.clear()
        self.assertNotIn(10, await negative_score_users(self.session))

    async def test_site_payload_keeps_replay_and_signed_score(self):
        self.rule()
        scores = self.db.exec(select(Score).order_by(Score.id)).all()
        payload = await _score_payloads(self.session, scores)
        self.assertEqual([s["pp"] for s in payload], [300, -200, 100])
        self.assertEqual([s["negative_pp"] for s in payload], [False, True, False])
        self.assertEqual(payload[1]["raw_pp"], 200)
        self.assertEqual(payload[1]["replay_url"], "/api/v2/scores/2/download")
        self.assertTrue((await _user_payload(self.session, self.db.get(User, 10)))["negative_pp_badge"])

    async def test_title_promotions_count_all_unique_scores_across_modes(self):
        self.rule()
        self.rule("mapper", 600)
        template = self.db.get(Score, 2)
        values = {column.key: getattr(template, column.key) for column in Score.__table__.columns}
        next_id = 4
        current_count = 1
        player = self.db.get(User, 10)
        for count, tier, name in (
            (1, 1, "Говноед"),
            (99, 1, "Говноед"),
            (100, 2, "Сомелье дристни"),
            (999, 2, "Сомелье дристни"),
            (1000, 3, "Верховный копрогастроном"),
            (1001, 3, "Верховный копрогастроном"),
        ):
            with self.subTest(count=count):
                while current_count < count:
                    self.db.add(Score(**{**values, "id": next_id, "gamemode": GameMode.MANIA}))
                    next_id += 1
                    current_count += 1
                self.db.flush()
                self.session.info.clear()  # Next HTTP request.
                payload = await _user_payload(self.session, player)
                self.assertEqual(payload["negative_pp_score_count"], count)
                self.assertEqual(payload["negative_pp_title"], {"tier": tier, "name": name})
        # Only three records are indexed as bests; the other 1000 plays still count.
        self.assertEqual(len(self.db.exec(select(BestScore)).all()), 3)

    async def test_counts_and_title_disappear_when_last_rule_is_removed(self):
        rule = self.rule()
        self.assertEqual(await negative_score_counts(self.session), {10: 1})
        self.db.delete(rule)
        self.db.flush()
        await recalculate(self.session, [102])
        payload = await _user_payload(self.session, self.db.get(User, 10))
        self.assertFalse(payload["negative_pp_badge"])
        self.assertEqual(payload["negative_pp_score_count"], 0)
        self.assertIsNone(payload["negative_pp_title"])

    async def test_count_query_is_batched_and_overlapping_rules_do_not_multiply_scores(self):
        self.rule()
        self.rule("beatmapset", 100)
        self.rule("mapper", 600)
        first, second = self.db.get(User, 10), self.db.get(User, 11)
        with patch.object(self.session, "exec", AsyncMock(wraps=self.session.exec)) as execute:
            first_payload = await _user_payload(self.session, first)
            second_payload = await _user_payload(self.session, second)
        self.assertEqual(execute.await_count, 1)
        self.assertEqual(first_payload["negative_pp_score_count"], 3)
        self.assertIsNone(second_payload["negative_pp_title"])

    async def test_mapper_credits_survive_fetcher_validation_and_merge(self):
        from app.fetcher.beatmap import adapter

        current = self.db.get(Beatmap, 102)
        response = {
            **current.model_dump(),
            "ranked": 1,
            "owners": [{"id": 600, "username": "Guest"}],
            "mode_int": 0,
            "convert": False,
            "is_scoreable": True,
            "status": "ranked",
            "beatmapset": {
                **self.db.get(Beatmapset, 100).model_dump(),
                "status": "ranked",
                "play_count": 0,
                "favourite_count": 0,
                "discussion_enabled": True,
                "ranked": 1,
                "genre_id": 0,
                "language_id": 0,
            },
            "url": "https://osu.ppy.sh/beatmaps/102",
        }
        validated = adapter.validate_python(response)
        self.assertEqual(validated["owners"][0].id, 600)
        merged = self.db.merge(await Beatmap.from_resp_no_save(self.session, validated))
        self.db.flush()
        self.assertTrue(merged.owners_known)
        self.assertEqual([c.mapper_id for c in merged.mapper_credits], [600])
        validated.pop("owners")
        self.db.merge(await Beatmap.from_resp_no_save(self.session, validated))
        self.db.flush()
        self.assertTrue(merged.owners_known)
        self.assertEqual([c.mapper_id for c in merged.mapper_credits], [600])

    async def test_admin_add_remove_recalculates_and_audits(self):
        from app.database import AdminAuditEvent

        request = Request({"type": "http", "headers": []})
        context = SimpleNamespace(user=staff(id=10, is_admin=True))
        with (
            patch.object(admin, "_require_csrf"),
            patch.object(admin, "_resolve", AsyncMock(return_value=(102, "Fixture"))),
            patch.object(admin, "invalidate_score_reconciliation_caches", AsyncMock()),
        ):
            added = await admin.add_negative_pp(
                NegativePPWrite(kind="beatmap", reference="102", reason="Test add"), request, context, self.session
            )
            self.assertEqual(added["user_modes"], 1)
            await admin.remove_negative_pp(
                added["rule"]["id"], NegativePPDelete(reason="Test remove"), request, context, self.session
            )
        self.assertEqual(len(self.db.exec(select(AdminAuditEvent)).all()), 2)
        self.assertAlmostEqual((await calculate_user_pp(self.session, 10, GameMode.OSU))[0], 580.25)

    async def test_owner_refresh_repairs_old_guest_pp_without_affecting_host(self):
        from app.service.negative_pp_service import refresh_owners

        self.rule("mapper", 600)
        fetcher = SimpleNamespace(
            request_api=AsyncMock(
                return_value={
                    "beatmaps": [
                        {"id": 101, "owners": [{"id": 500, "username": "Host"}]},
                        {"id": 102, "owners": [{"id": 600, "username": "Guest"}]},
                        {"id": 103, "owners": [{"id": 500, "username": "Host"}]},
                    ]
                }
            )
        )
        with patch(
            "app.service.beatmap_ranking_reconciliation_service.invalidate_score_reconciliation_caches", AsyncMock()
        ):
            await refresh_owners(self.session, fetcher, [101, 102, 103])
        fetcher.request_api.assert_awaited_once_with("https://osu.ppy.sh/api/v2/beatmapsets/100")
        self.assertEqual(await negative_map_ids(self.session, [101, 102, 103]), {102})
        self.assertTrue(all(self.db.get(Beatmap, bid).owners_known for bid in [101, 102, 103]))

    async def test_unavailable_guest_attribution_prevents_mapper_rule(self):
        from app.service.negative_pp_service import refresh_owners

        fetcher = SimpleNamespace(request_api=AsyncMock(return_value={"beatmaps": [{"id": 102, "user_id": 500}]}))
        with (
            patch(
                "app.service.beatmap_ranking_reconciliation_service.invalidate_score_reconciliation_caches", AsyncMock()
            ),
            self.assertRaises(HTTPException) as caught,
        ):
            await refresh_owners(self.session, fetcher, [102])
        self.assertEqual(caught.exception.status_code, 409)
        self.assertFalse(self.db.get(Beatmap, 102).owners_known)

    async def test_every_admin_endpoint_rejects_non_admin_roles(self):
        request = Request({"type": "http", "headers": []})
        for role in ({}, {"is_bng": True}, {"is_gmt": True}, {"is_qat": True}):
            context = SimpleNamespace(user=staff(**role))
            with patch.object(admin, "_require_csrf"):
                for action in [
                    lambda: admin.list_negative_pp(context, self.session),
                    lambda: admin.preview_negative_pp(
                        NegativePPTarget(kind="beatmap", reference="102"), request, context, self.session
                    ),
                    lambda: admin.add_negative_pp(
                        NegativePPWrite(kind="beatmap", reference="102", reason="Test"), request, context, self.session
                    ),
                    lambda: admin.remove_negative_pp(
                        1, NegativePPDelete(reason="Test"), request, context, self.session
                    ),
                ]:
                    with self.assertRaises(HTTPException) as caught:
                        await action()
                    self.assertEqual(caught.exception.status_code, 403)

    async def test_mutations_require_csrf_before_fetch_or_write(self):
        request = Request({"type": "http", "headers": []})
        context = SimpleNamespace(user=staff(is_owner=True), csrf_token="required")  # noqa: S106
        with self.assertRaises(HTTPException) as caught:
            await admin.add_negative_pp(
                NegativePPWrite(kind="beatmap", reference="102", reason="Test"), request, context, self.session
            )
        self.assertEqual(caught.exception.status_code, 403)

    def test_target_url_selects_difficulty_and_rejects_wrong_kind_or_host(self):
        self.assertEqual(reference_id("beatmap", "https://osu.ppy.sh/beatmapsets/100#osu/102"), "102")
        self.assertEqual(reference_id("beatmapset", "https://osu.ppy.sh/beatmapsets/100#osu/102"), "100")
        for reference in ["https://example.com/beatmaps/102", "https://osu.ppy.sh/users/102"]:
            with self.assertRaises(HTTPException):
                reference_id("beatmap", reference)
