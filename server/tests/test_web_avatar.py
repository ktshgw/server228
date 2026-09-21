# ruff: noqa: PT027
import io
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.router.private.web_site import delete_web_avatar, upload_web_avatar

from fastapi import HTTPException, Request, UploadFile
from PIL import Image


class WebAvatarTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.user = SimpleNamespace(
            id=7, avatar_url="https://soms.invalid/file/avatars/7_old.png", is_restricted=AsyncMock(return_value=False)
        )
        self.context = SimpleNamespace(user=self.user, session=SimpleNamespace(csrf_token="test-csrf"))  # noqa: S106 -- fixture
        self.db = SimpleNamespace(add=Mock(), commit=AsyncMock(), rollback=AsyncMock())
        self.storage = SimpleNamespace(
            get_file_name_by_url=Mock(return_value="avatars/7_old.png"),
            delete_file=AsyncMock(),
            write_file=AsyncMock(),
            get_file_url=AsyncMock(return_value="https://soms.invalid/file/avatars/7_new.png"),
        )
        self.cache = SimpleNamespace(invalidate_user_all_cache=AsyncMock(), invalidate_v1_user_cache=AsyncMock())
        self.redis = SimpleNamespace(eval=AsyncMock(return_value=1))
        self.origin = patch("app.router.private.web_site._expected_origin", return_value="https://soms.invalid")
        self.origin.start()
        self.addCleanup(self.origin.stop)

    def request(self, csrf="test-csrf", origin="https://soms.invalid"):
        return Request({"type": "http", "headers": [(b"origin", origin.encode()), (b"x-csrf-token", csrf.encode())]})

    async def delete(self, request=None):
        return await delete_web_avatar(request or self.request(), self.context, self.db, self.storage, self.cache)

    async def test_delete_commits_default_before_removing_owned_file(self):
        async def remove(_path):
            self.db.commit.assert_awaited_once()
            assert self.user.avatar_url == ""

        self.storage.delete_file.side_effect = remove
        result = await self.delete()
        assert result == {"url": "/site/soms-default-avatar.png", "has_custom_avatar": False}
        self.storage.delete_file.assert_awaited_once_with("avatars/7_old.png")
        self.cache.invalidate_user_all_cache.assert_awaited_once_with(7)
        self.cache.invalidate_v1_user_cache.assert_awaited_once_with(7)

    async def test_delete_never_removes_another_users_file_or_shared_asset(self):
        for path in (None, "avatars/8_old.png", "shared/default.png"):
            self.storage.get_file_name_by_url.return_value = path
            await self.delete()
        self.storage.delete_file.assert_not_awaited()

    async def test_failed_commit_rolls_back_and_keeps_image(self):
        self.db.commit.side_effect = RuntimeError("database unavailable")
        with self.assertRaises(RuntimeError):
            await self.delete()
        self.db.rollback.assert_awaited_once()
        self.storage.delete_file.assert_not_awaited()
        self.cache.invalidate_user_all_cache.assert_not_awaited()

    async def test_file_cleanup_failure_does_not_report_failed_deletion(self):
        self.storage.delete_file.side_effect = OSError("storage unavailable")
        result = await self.delete()
        assert result["has_custom_avatar"] is False
        self.cache.invalidate_user_all_cache.assert_awaited_once_with(7)

    async def test_csrf_origin_and_restricted_account_cannot_delete(self):
        for request in (self.request(csrf="wrong"), self.request(origin="https://other.invalid")):
            with self.assertRaises(HTTPException) as caught:
                await self.delete(request)
            assert caught.exception.status_code == 403
        self.user.is_restricted.return_value = True
        with self.assertRaises(HTTPException) as caught:
            await self.delete()
        assert caught.exception.status_code == 403
        self.db.commit.assert_not_awaited()
        self.storage.delete_file.assert_not_awaited()

    async def test_upload_prepares_image_and_returns_cache_version(self):
        source = io.BytesIO()
        Image.new("RGB", (80, 40), "#ff66aa").save(source, format="PNG")
        source.seek(0)
        result = await upload_web_avatar(
            self.request(),
            self.context,
            self.db,
            self.redis,
            self.storage,
            self.cache,
            UploadFile(source, filename="avatar.png"),
        )
        assert result["has_custom_avatar"] is True
        assert result["url"].startswith("/users/7/avatar?v=")
        uploaded = self.storage.write_file.await_args.args
        assert uploaded[0].startswith("avatars/7_")
        with Image.open(io.BytesIO(uploaded[1])) as avatar:
            assert avatar.size == (256, 256)
            assert avatar.format == "PNG"
        self.storage.delete_file.assert_awaited_once_with("avatars/7_old.png")

    async def test_invalid_upload_keeps_previous_avatar(self):
        with self.assertRaises(HTTPException) as caught:
            await upload_web_avatar(
                self.request(),
                self.context,
                self.db,
                self.redis,
                self.storage,
                self.cache,
                UploadFile(io.BytesIO(b"invalid"), filename="avatar.png"),
            )
        assert caught.exception.status_code == 422
        self.db.commit.assert_not_awaited()
        self.storage.delete_file.assert_not_awaited()
        self.storage.write_file.assert_not_awaited()
