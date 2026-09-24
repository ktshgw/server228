"""Discover Ranked maps independently of clients; retry interrupted scans."""

from datetime import UTC, datetime, timedelta

from app.config import settings
from app.dependencies.scheduler import get_scheduler
from app.service.ranked_catalogue_service import sync_official_catalogue

if settings.enable_ranked_catalogue_sync:

    @get_scheduler().scheduled_job(
        "interval",
        id="ranked_official_catalogue",
        minutes=5,
        next_run_time=datetime.now(UTC) + timedelta(seconds=30),
        max_instances=1,
        coalesce=True,
    )
    async def ranked_catalogue_job() -> None:
        await sync_official_catalogue()
