"""Collect client concurrency independently of visits to the website."""

from datetime import UTC, datetime

from app.dependencies.database import get_redis, with_db
from app.dependencies.scheduler import get_scheduler
from app.service.home_activity_service import record_online_sample


@get_scheduler().scheduled_job(
    "cron",
    id="soms_online_history",
    minute="*/10",
    next_run_time=datetime.now(UTC),
    misfire_grace_time=300,
    max_instances=1,
    coalesce=True,
)
async def online_history_job():
    async with with_db() as session:
        await record_online_sample(session, get_redis())
