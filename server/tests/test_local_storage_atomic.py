import asyncio
from pathlib import Path
import secrets
import unittest
from unittest.mock import patch

from app.storage.local import LocalStorageService


class LocalStorageAtomicWriteTests(unittest.IsolatedAsyncioTestCase):
    async def test_successful_write_publishes_only_the_final_file(self) -> None:
        storage = LocalStorageService(str(Path(__file__).parent))
        filename = f".local-storage-test-{secrets.token_hex(8)}.osr"
        try:
            await storage.write_file(filename, b"complete replay")

            assert await storage.read_file(filename) == b"complete replay"
            assert await self._temporary_files_for(filename) == []
        finally:
            await storage.delete_file(filename)

    async def test_failed_publish_preserves_existing_destination_and_removes_temp(self) -> None:
        storage = LocalStorageService(str(Path(__file__).parent))
        filename = f".local-storage-test-{secrets.token_hex(8)}.osr"
        await storage.write_file(filename, b"existing")
        try:
            with patch("app.storage.local.os.replace", side_effect=OSError("publish failed")):
                try:
                    await storage.write_file(filename, b"new replay")
                except RuntimeError:
                    pass
                else:
                    raise AssertionError("Atomic publish failures must be reported")

            assert await storage.read_file(filename) == b"existing"
            assert await self._temporary_files_for(filename) == []
        finally:
            await storage.delete_file(filename)

    @staticmethod
    async def _temporary_files_for(filename: str) -> list[Path]:
        root = Path(__file__).parent
        return await asyncio.to_thread(lambda: list(root.glob(f".{filename}.*.tmp")))


if __name__ == "__main__":
    unittest.main()
