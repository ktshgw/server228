"""Expire abandoned searches and advance tournament deadlines without a browser."""

from datetime import UTC, datetime, timedelta

from app.dependencies.scheduler import get_scheduler
from app.log import task_logger
from app.service.somsai_party_service import somsai_transaction
from app.service.somsai_service import tick


@get_scheduler().scheduled_job(
    "interval",
    id="somsai_tick",
    seconds=2,
    next_run_time=datetime.now(UTC) + timedelta(seconds=10),
    max_instances=1,
    coalesce=True,
)
async def somsai_tick_job() -> None:
    try:
        async with somsai_transaction() as session:
            await tick(session)
    except Exception:
        task_logger("SOMSAI").exception("Tournament tick failed; next tick will retry")
