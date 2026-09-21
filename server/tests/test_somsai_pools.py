"""Catalogue invariants against isolated SQL; no external service or production DB."""

# ruff: noqa: PT009, PT027, SIM117, S106
from copy import deepcopy
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch

from app.database import AdminAuditEvent, Beatmap, Beatmapset, SomsaiPool
from app.dependencies.database import get_db
from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus
from app.models.score import GameMode
from app.models.somsai_admin import SomsaiImportRequest, SomsaiPoolSpec
from app.service.somsai_collector_service import collector_preview, collector_source, fetch_collector_source
from app.service.somsai_pool_service import delete_pool, get_playable_pool_slots, pool_payload, preview_pool, save_pool
from tests.test_admin_panel import staff
from tests.test_ranked_elo_admin import AsyncTestSession

from fastapi import FastAPI, HTTPException
import httpx
from pydantic import ValidationError
from sqlalchemy.orm import lazyload
from sqlmodel import Session, SQLModel, create_engine, select


class SomsaiPoolTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.engine = create_engine("sqlite://")
        SQLModel.metadata.create_all(
            self.engine,
            tables=[
                SQLModel.metadata.tables[model.__tablename__]
                for model in (Beatmapset, Beatmap, SomsaiPool, AdminAuditEvent)
            ],
        )
        self.db = Session(self.engine, expire_on_commit=False)
        self.session: Any = AsyncTestSession(self.db)
        self.addCleanup(self.engine.dispose)
        self.addCleanup(self.db.close)
        now = utcnow()
        self.db.add(
            Beatmapset(
                id=1,
                artist="Test",
                artist_unicode="Test",
                creator="test",
                title="Pool fixture",
                title_unicode="Pool fixture",
                user_id=2,
                preview_url="",
                video=False,
                covers=None,
                last_updated=now,
                submitted_date=now,
                beatmap_status=BeatmapRankStatus.GRAVEYARD,
            )
        )
        self.db.add_all(
            [
                Beatmap(
                    id=i,
                    beatmapset_id=1,
                    user_id=2,
                    mode=GameMode.OSU,
                    version=f"Map {i}",
                    difficulty_rating=5.0,
                    url=f"https://osu.ppy.sh/beatmaps/{i}",
                    total_length=120,
                    hit_length=100,
                    checksum=f"{i:032x}",
                    beatmap_status=BeatmapRankStatus.GRAVEYARD,
                    last_updated=now,
                    cs=4,
                )
                for i in range(1, 22)
            ]
        )
        self.db.commit()

    def spec(self, **changes: Any) -> SomsaiPoolSpec:
        slots = [{"id": f"NM{i}", "beatmap_id": i} for i in range(1, 9)] + [{"id": "TB", "beatmap_id": 9}]
        return SomsaiPoolSpec.model_validate({"name": "Test pool", "slots": slots, **changes})

    async def test_unranked_maps_activate_and_snapshot_has_real_metadata(self):
        before, pool = await save_pool(self.session, self.spec(active=True), None, None)
        self.assertIsNone(before)
        slots = await get_playable_pool_slots(self.session, pool)
        self.assertEqual(len(slots), 9)
        self.assertEqual(slots[0]["checksum"], f"{1:032x}")
        self.assertEqual(slots[0]["name"], "Test - Pool fixture [Map 1]")
        self.assertEqual(pool_payload(pool)["average_stars"], 5)
        self.assertEqual(self.beatmap(1).beatmap_status, BeatmapRankStatus.GRAVEYARD)

    def beatmap(self, beatmap_id: int) -> Beatmap:
        return self.db.exec(select(Beatmap).where(Beatmap.id == beatmap_id).options(lazyload("*"))).one()

    async def test_missing_maps_draft_allowed_activation_rejected(self):
        spec = self.spec(slots=[{"id": "NM1", "beatmap_id": 100}])
        _, pool = await save_pool(self.session, spec, None, None)
        self.assertFalse(pool.active)
        with self.assertRaises(HTTPException) as caught:
            await save_pool(self.session, self.spec(active=True, slots=spec.slots), pool.id, 1)
        self.assertEqual(caught.exception.status_code, 422)

    async def test_bo7_9_11_require_enough_distinct_normal_maps_and_tb(self):
        for best_of in (7, 9, 11):
            normal = best_of + 1
            slots = [{"id": f"NM{i}", "beatmap_id": i} for i in range(1, normal + 1)]
            slots.append({"id": "TB", "beatmap_id": 21})
            self.assertTrue((await preview_pool(self.session, self.spec(best_of=best_of, slots=slots)))["playable"])
            self.assertFalse(
                (await preview_pool(self.session, self.spec(best_of=best_of, slots=slots[1:])))["playable"]
            )

    async def test_checksum_change_and_mode_change_block_new_matches(self):
        _, pool = await save_pool(self.session, self.spec(active=True), None, None)
        beatmap = self.beatmap(1)
        beatmap.checksum = "f" * 32
        self.db.flush()
        with self.assertRaises(HTTPException):
            await get_playable_pool_slots(self.session, pool)
        beatmap.checksum = f"{1:032x}"
        beatmap.mode = GameMode.MANIA
        with self.assertRaises(HTTPException):
            await get_playable_pool_slots(self.session, pool)

    async def test_deleted_map_and_mania_variant_are_checked(self):
        beatmap = self.beatmap(1)
        beatmap.deleted_at = utcnow()
        self.assertFalse((await preview_pool(self.session, self.spec()))["playable"])
        beatmap.deleted_at = None
        for item in self.db.exec(select(Beatmap).options(lazyload("*"))).all():
            item.mode = GameMode.MANIA
        self.db.flush()
        self.assertTrue((await preview_pool(self.session, self.spec(ruleset_id=3, variant_id=4)))["playable"])
        self.assertFalse((await preview_pool(self.session, self.spec(ruleset_id=3, variant_id=7)))["playable"])

    async def test_download_disabled_source_cannot_activate(self):
        self.db.get(Beatmapset, 1).download_disabled = True
        result = await preview_pool(self.session, self.spec())
        self.assertFalse(result["playable"])
        self.assertTrue(any("скачивание" in error for error in result["errors"]))

    async def test_stale_edit_cannot_overwrite_and_snapshot_independent(self):
        _, pool = await save_pool(self.session, self.spec(), None, None)
        snapshot = deepcopy(pool.slots)
        await save_pool(self.session, self.spec(name="Changed"), pool.id, 1)
        with self.assertRaises(HTTPException) as caught:
            await save_pool(self.session, self.spec(name="Stale"), pool.id, 1)
        self.assertEqual(caught.exception.status_code, 409)
        self.assertEqual(pool.name, "Changed")
        self.assertEqual(snapshot[0]["beatmap_id"], 1)

    async def test_append_renumbers_and_rejects_duplicate_maps(self):
        _, pool = await save_pool(self.session, self.spec(), None, None)
        await save_pool(self.session, self.spec(slots=[{"id": "NM1", "beatmap_id": 10}]), pool.id, 1, append=True)
        self.assertEqual(pool.slots[-1]["id"], "NM9")
        with self.assertRaises(HTTPException):
            await save_pool(self.session, self.spec(slots=[{"id": "HR1", "beatmap_id": 1}]), pool.id, 2, append=True)

    async def test_delete_requires_current_revision(self):
        _, pool = await save_pool(self.session, self.spec(), None, None)
        with self.assertRaises(HTTPException):
            await delete_pool(self.session, int(pool.id or 0), 2)
        before = await delete_pool(self.session, int(pool.id or 0), 1)
        self.assertEqual(before["name"], "Test pool")
        self.assertEqual(len(self.db.exec(select(SomsaiPool)).all()), 0)

    async def test_http_import_refetches_source_forces_draft_and_audits(self):
        from app.router.private.admin_panel import AdminContext, require_admin_session
        from app.router.private.somsai_admin import create_somsai_import

        app = FastAPI()
        app.add_api_route("/import", create_somsai_import, methods=["POST"])
        app.dependency_overrides[get_db] = lambda: self.session
        context = AdminContext(user=staff(is_admin=True), csrf_token="fixture-csrf", session_digest="fixture")
        app.dependency_overrides[require_admin_session] = lambda: context
        imported = {
            "source_kind": "collector_tournament",
            "source_id": 123,
            "source_round": "Finals",
            "source_url": "https://osucollector.com/tournaments/123",
            "source_metadata": {"sha256": "source"},
            "slots": [{"id": "HD1", "beatmap_id": 1, "checksum": f"{1:032x}"}],
        }
        payload = {
            "url": imported["source_url"],
            "round": "Finals",
            "pool": {**self.spec(active=True).model_dump(), "reason": "HTTP import regression"},
        }
        with (
            patch("app.router.private.admin_panel._expected_origin", return_value="http://test"),
            patch("app.router.private.somsai_admin.preview_collector", AsyncMock(return_value=imported)) as source,
            patch("app.router.private.somsai_admin.get_fetcher", AsyncMock(return_value=None)),
        ):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                denied = await client.post("/import", json=payload)
                self.assertEqual(denied.status_code, 403)
                source.assert_not_awaited()
                context.user.is_admin = False
                denied = await client.post(
                    "/import", json=payload, headers={"origin": "http://test", "x-csrf-token": "fixture-csrf"}
                )
                self.assertEqual(denied.status_code, 403)
                source.assert_not_awaited()
                context.user.is_admin = True
                response = await client.post(
                    "/import", json=payload, headers={"origin": "http://test", "x-csrf-token": "fixture-csrf"}
                )
                self.assertEqual(response.status_code, 200, response.text)
        saved = response.json()
        self.assertFalse(saved["active"])
        self.assertEqual(saved["slots"][0]["id"], "HD1")
        self.assertEqual(saved["slots"][0]["mods"], [{"acronym": "HD"}])
        self.assertEqual(len(saved["slots"]), 1)
        self.assertEqual(saved["source_metadata"]["sha256"], "source")
        self.assertEqual(self.db.exec(select(AdminAuditEvent)).one().action, "somsai.pool.create")

    async def test_failed_import_rolls_back_pool_and_audit(self):
        from app.router.private.admin_panel import AdminContext, require_admin_session
        from app.router.private.somsai_admin import create_somsai_import

        app = FastAPI()
        app.add_api_route("/import", create_somsai_import, methods=["POST"])
        app.dependency_overrides[get_db] = lambda: self.session
        context = AdminContext(user=staff(is_owner=True), csrf_token="csrf", session_digest="test")
        app.dependency_overrides[require_admin_session] = lambda: context
        with (
            patch("app.router.private.admin_panel._expected_origin", return_value="http://test"),
            patch(
                "app.router.private.somsai_admin.preview_collector",
                AsyncMock(return_value={"slots": [{"id": "NM1", "beatmap_id": 1, "checksum": None}]}),
            ),
        ):
            async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
                response = await client.post(
                    "/import",
                    json={
                        "url": "https://osucollector.com/tournaments/123",
                        "pool": {**self.spec().model_dump(), "reason": "bad source"},
                    },
                    headers={"origin": "http://test", "x-csrf-token": "csrf"},
                )
        self.assertEqual(response.status_code, 422)
        self.assertEqual(len(self.db.exec(select(SomsaiPool)).all()), 0)
        self.assertEqual(len(self.db.exec(select(AdminAuditEvent)).all()), 0)

    def test_schema_rejects_duplicate_slots_maps_invalid_bo_and_variant(self):
        for changes in (
            {"best_of": 8},
            {"ruleset_id": 3},
            {"variant_id": 4},
            {"rating_min": 5001},
            {"slots": [{"id": "NM1", "beatmap_id": 1}, {"id": "HR1", "beatmap_id": 1}]},
            {"slots": [{"id": "NM1", "beatmap_id": 1}, {"id": "NM1", "beatmap_id": 2}]},
        ):
            with self.assertRaises(ValidationError):
                self.spec(**changes)


class CollectorTests(unittest.IsolatedAsyncioTestCase):
    def provenance(self, kind="tournament"):
        return {
            "source_kind": f"collector_{kind}",
            "source_id": 123,
            "source_round": None,
            "source_url": f"https://osucollector.com/{kind}s/123",
            "source_metadata": {"sha256": "fixture"},
        }

    def tournament(self):
        return {
            "id": 123,
            "name": "Fixture",
            "uploader": {"rank": 999},
            "rounds": [
                {"round": "Finals", "mods": [{"mod": category, "maps": [{"id": i, "checksum": f"{i:032x}"}]}]}
                for i, category in [(1, "NM")]
            ],
        }

    def test_only_explicit_collector_https_source_accepted(self):
        self.assertEqual(collector_source("https://osucollector.com/tournaments/123/name")[1], 123)
        for url in (
            "https://osucollector.com.evil/tournaments/123",
            "https://user@osucollector.com/tournaments/123",
            "http://osucollector.com/tournaments/123",
            "https://127.0.0.1/tournaments/123",
            "https://osucollector.com/api/tournaments/123",
            "https://osucollector.com/tournaments/123?url=x",
        ):
            with self.assertRaises(HTTPException):
                collector_source(url)

    def test_round_selection_preserves_source_and_never_uses_uploader_rank(self):
        request = SomsaiImportRequest(url="https://osucollector.com/tournaments/123")
        result = collector_preview(self.tournament(), self.provenance(), request)
        self.assertEqual(result["rounds"], ["Finals"])
        self.assertEqual(result["slots"], [])
        result = collector_preview(self.tournament(), self.provenance(), request.model_copy(update={"round": "Finals"}))
        self.assertEqual(result["slots"][0]["id"], "NM1")
        self.assertNotIn("rating_min", result)
        self.assertNotIn("source_rank_min", result)

    def test_collections_use_explicit_category_not_inferred_name(self):
        result = collector_preview(
            {"name": "DT misleading", "beatmapsets": [{"beatmaps": [{"id": 1}]}]},
            self.provenance("collection"),
            SomsaiImportRequest(url="https://osucollector.com/collections/123", category="HR"),
        )
        self.assertEqual(result["slots"][0]["id"], "HR1")
        self.assertTrue(result["warnings"])

    def test_unknown_mod_and_oversized_pool_are_not_silently_truncated(self):
        data = self.tournament()
        data["rounds"][0]["mods"][0]["mod"] = "HDDT"
        request = SomsaiImportRequest(url="https://osucollector.com/tournaments/123", round="Finals")
        with self.assertRaises(HTTPException):
            collector_preview(data, self.provenance(), request)
        data["rounds"][0]["mods"] = [{"mod": "NM", "maps": [{"id": i} for i in range(1, 66)]}]
        with self.assertRaises(HTTPException):
            collector_preview(data, self.provenance(), request)

    async def test_redirects_rejected_and_body_size_bounded(self):
        for response in (
            httpx.Response(302, headers={"location": "http://127.0.0.1/private"}),
            httpx.Response(200, content=b" " * 2_000_001),
        ):
            real_client = httpx.AsyncClient
            transport = httpx.MockTransport(lambda _: response)
            with patch(
                "app.service.somsai_collector_service.httpx.AsyncClient",
                side_effect=lambda **kw: real_client(transport=transport, **kw),
            ):
                with self.assertRaises(HTTPException):
                    await fetch_collector_source("https://osucollector.com/tournaments/123")

    async def test_route_authorization_precedes_upstream_requests(self):
        from app.router.private.somsai_admin import preview_somsai_import

        request = SomsaiImportRequest(url="https://osucollector.com/tournaments/123")
        with (
            patch("app.router.private.somsai_admin._require_csrf", side_effect=HTTPException(403)),
            patch("app.router.private.somsai_admin.preview_collector", new_callable=AsyncMock) as upstream,
        ):
            with self.assertRaises(HTTPException):
                await preview_somsai_import(request, None, None)  # type: ignore[arg-type]
            upstream.assert_not_awaited()
