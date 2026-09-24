"""File serving router for local storage.

This module provides endpoints for serving files from local storage.
Only active when using LocalStorageService backend.
"""

import mimetypes

from app.dependencies.storage import StorageService as StorageServiceDep
from app.models.error import ErrorType, RequestError
from app.storage import LocalStorageService

from fastapi import APIRouter
from fastapi.responses import FileResponse

file_router = APIRouter(prefix="/file", include_in_schema=False)


@file_router.get("/{path:path}")
async def get_file(path: str, storage: StorageServiceDep):
    """Serve a file from local storage.

    Only works when LocalStorageService is configured. For other storage
    backends (e.g., S3), clients should access files directly via their URLs.

    Args:
        path: Relative file path within the storage.
        storage: Storage service dependency.

    Returns:
        FileResponse with the requested file.

    Raises:
        RequestError: If file not found or storage is not local.
    """
    if not isinstance(storage, LocalStorageService):
        raise RequestError(ErrorType.NOT_FOUND)
    if not await storage.is_exists(path):
        raise RequestError(ErrorType.NOT_FOUND)

    try:
        media_type = mimetypes.guess_type(path)[0] or "application/octet-stream"
        if media_type in {"image/avif", "image/gif", "image/jpeg", "image/png", "image/webp"}:
            return FileResponse(
                path=storage._get_file_path(path),
                media_type=media_type,
                headers={
                    "Cache-Control": "public, max-age=31536000, immutable",
                    "X-Content-Type-Options": "nosniff",
                },
            )
        return FileResponse(
            path=storage._get_file_path(path),
            media_type=media_type,
            filename=path.split("/")[-1],
            headers={"X-Content-Type-Options": "nosniff"},
        )
    except FileNotFoundError:
        raise RequestError(ErrorType.NOT_FOUND)
