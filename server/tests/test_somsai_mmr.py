"""Real SQL rating edits, website placement and administrative boundaries."""

# ruff: noqa: PT009, PT027, S106
from copy import deepcopy
import unittest
from unittest.mock import AsyncMock, patch

from app.database import AdminAuditEvent
from app.database.somsai import SomsaiRating
from app.models.score import GameMode
from app.router.private.admin_panel import AdminContext, SomsaiMmrUpdateRequest, update_admin_somsai_mmr
from app.service.somsai_mmr_service import change_somsai_mmr, list_somsai_mmr, somsai_profile_payload
from app.service.somsai_service import join_queue
from tests import test_somsai_core as core
from tests.test_admin_panel import staff

from fastapi import HTTPException
from pydantic import ValidationError
from sqlmodel import select
from starlette.requests import Request


class SomsaiMmrTests(unittest.IsolatedAsyncioTestCase):
    setUp = core.SomsaiCoreTests.setUp
    asyncSetUp = core.SomsaiCoreTests.asyncSetUp

    async def selected(self):
        return next(item for item in await list_somsai_mmr(self.session, 10) if item["key"] == "0:0:1v1")

    async def test_profile_reads_actual_rating_without_seeding_and_keeps_formats_separate(self):
        before = len(self.db.exec(select(SomsaiRating)).all())
        missing = await somsai_profile_payload(self.session, 10, GameMode.TAIKO)
        self.assertIsNone(missing["mmr"])
        self.assertIsNone(missing["global_rank"])
        self.assertEqual(len(self.db.exec(select(SomsaiRating)).all()), before)
        item = await self.selected()
        await change_somsai_mmr(self.session, 10, 0, 0, "1v1", 2222, item["version"])
        profile = await somsai_profile_payload(self.session, 10, GameMode.OSU)
        self.assertEqual((profile["mmr"], profile["global_rank"]), (2222, 1))
        self.assertEqual((await somsai_profile_payload(self.session, 10, GameMode.OSU, format="2v2"))["mmr"], 1500)
        self.assertEqual((await somsai_profile_payload(self.session, 11, GameMode.OSU))["global_rank"], 2)

    async def test_edit_preserves_other_ratings_and_rejects_stale_form(self):
        item = await self.selected()
        old = {row.id: deepcopy(row.model_dump()) for row in self.db.exec(select(SomsaiRating)).all()}
        _, result = await change_somsai_mmr(self.session, 10, 0, 0, "1v1", 2500, item["version"])
        self.db.commit()
        self.assertEqual(result["version"], (await self.selected())["version"])
        for row in self.db.exec(select(SomsaiRating)).all():
            if (row.user_id, row.format) != (10, "1v1"):
                self.assertEqual(row.model_dump(), old[row.id])
        with self.assertRaises(HTTPException) as error:
            await change_somsai_mmr(self.session, 10, 0, 0, "1v1", 2000, item["version"])
        self.assertEqual(error.exception.status_code, 409)

    async def test_active_queue_blocks_edit(self):
        item = await self.selected()
        await join_queue(self.session, 10, "1v1", 0, 0)
        with self.assertRaises(HTTPException) as error:
            await change_somsai_mmr(self.session, 10, 0, 0, "1v1", 2000, item["version"])
        self.assertEqual(error.exception.status_code, 409)

    async def call_route(self, actor=None, target=None, csrf="fixture-csrf"):
        item = await self.selected()
        request = Request(
            {"type": "http", "headers": [(b"origin", b"http://admin.test"), (b"x-csrf-token", csrf.encode())]}
        )
        context = AdminContext(
            user=actor or staff(id=17, is_admin=True), csrf_token="fixture-csrf", session_digest="test"
        )
        payload = SomsaiMmrUpdateRequest(mmr=2000, expected_version=item["version"], reason="correct rating")
        with (
            patch("app.router.private.admin_panel._expected_origin", return_value="http://admin.test"),
            patch("app.router.private.admin_panel.resolve_human_user", AsyncMock(return_value=target or staff(id=10))),
        ):
            return await update_admin_somsai_mmr(7, 0, 0, "1v1", payload, request, context, self.session)

    async def test_route_commits_canonical_user_rating_and_audit_together(self):
        result = await self.call_route()
        self.assertEqual(result["mmr"], 2000)
        audit = self.db.exec(select(AdminAuditEvent)).one()
        self.assertEqual((audit.target_id, audit.actor_user_id, audit.action), ("10", 17, "user.somsai_mmr.update"))
        self.assertEqual((audit.before["mmr"], audit.after["mmr"]), (1500, 2000))
        self.assertNotIn("version", audit.after)

    async def test_permissions_csrf_and_protected_accounts(self):
        for args in (
            {"actor": staff(id=17)},
            {"actor": staff(id=17, is_bng=True)},
            {"csrf": "wrong"},
            {"target": staff(id=10, is_owner=True)},
            {"target": staff(id=10, is_admin=True)},
        ):
            with self.subTest(args=args), self.assertRaises(HTTPException) as error:
                await self.call_route(**args)
            self.assertEqual(error.exception.status_code, 403)
        self.assertEqual((await self.selected())["mmr"], 1500)
        self.assertEqual(
            (await self.call_route(actor=staff(id=17, is_owner=True), target=staff(id=10, is_admin=True)))["mmr"], 2000
        )

    async def test_audit_commit_failure_rolls_back_rating(self):
        from sqlalchemy.exc import IntegrityError

        async def fail():
            self.db.flush()
            raise IntegrityError("test", {}, Exception("synthetic"))

        with patch.object(self.session, "commit", fail), self.assertRaises(HTTPException) as error:
            await self.call_route()
        self.assertEqual(error.exception.status_code, 409)
        self.assertEqual((await self.selected())["mmr"], 1500)
        self.assertFalse(self.db.exec(select(AdminAuditEvent)).all())

    async def test_http_ranking_accepts_string_query_variants_and_separates_formats(self):
        from app.dependencies.database import get_db, get_redis
        from app.router.private.web_site import get_web_rankings

        from fastapi import FastAPI
        import httpx

        app = FastAPI()
        app.add_api_route("/rankings", get_web_rankings)
        app.dependency_overrides[get_db] = lambda: self.session
        app.dependency_overrides[get_redis] = lambda: None
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
            for format in ("1v1", "2v2"):
                response = await client.get(
                    "/rankings", params={"section": "somsai", "somsai_variant": "4", "somsai_format": format}
                )
                self.assertEqual(response.status_code, 200, response.text)
                self.assertEqual(response.json()["total"], 8)
                self.assertTrue(all(item["mmr"] == 1500 for item in response.json()["items"]))
            response = await client.get("/rankings", params={"section": "somsai", "somsai_variant": "5"})
            self.assertEqual(response.status_code, 422)

    def test_request_bounds_match_rating_engine(self):
        valid = {"mmr": 2000, "expected_version": "0" * 64, "reason": "correct"}
        for value in (True, "2000", 1.5, -1, 5001, None):
            with self.subTest(value=value), self.assertRaises(ValidationError):
                SomsaiMmrUpdateRequest.model_validate({**valid, "mmr": value})
