"""Authenticate new spectator endpoints using its existing signed-URL protocol."""

import hashlib
import hmac
import time

from app.config import settings

from fastapi import HTTPException, Request


async def require_somsai_interop(request: Request) -> None:
    if not settings.shared_interop_secret:
        raise HTTPException(503, "Spectator interop secret is not configured")
    timestamps = request.query_params.getlist("timestamp")
    try:
        valid_time = len(timestamps) == 1 and abs(time.time() - int(timestamps[0])) <= 60
    except ValueError:
        valid_time = False
    signature = request.headers.get("x-lio-signature", "")
    # SharedInterop signs the ASCII absolute URL, including timestamp/query order.
    expected = hmac.new(
        settings.shared_interop_secret.encode(), str(request.url).encode("ascii", errors="replace"), hashlib.sha1
    ).hexdigest()
    if not valid_time or not hmac.compare_digest(signature.encode(), expected.encode()):
        raise HTTPException(403, "Invalid spectator interop signature")
