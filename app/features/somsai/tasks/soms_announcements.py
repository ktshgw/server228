"""Announce new official Ranked/Loved releases without replaying the historic catalogue."""

from datetime import UTC, datetime, timedelta

from app.features.somsai.database.soms_activity import SomsActivity
from app.dependencies.database import with_db
from app.dependencies.scheduler import get_scheduler
from app.features.somsai.services.soms_activity_service import chat_link, deliver_announcements

from sqlmodel import select


async def stage_releases(session, fetcher, category: str, start: datetime):
    from app.service.ranked_catalogue_service import SEARCH_URL

    params = {"s": category, "sort": "ranked_desc", "nsfw": "true"}
    for _ in range(100):
        page = await fetcher.request_api(SEARCH_URL, params=params, timeout=30)
        reached_start = False
        for item in page.get("beatmapsets", []):
            timestamp = item.get("ranked_date")
            if not timestamp:
                continue
            ranked_at = datetime.fromisoformat(timestamp)
            if ranked_at.tzinfo is None:
                ranked_at = ranked_at.replace(tzinfo=UTC)
            if ranked_at < start:
                reached_start = True
                continue
            key = f"{category}-map:{item['id']}"
            if (await session.exec(select(SomsActivity.id).where(SomsActivity.event_key == key))).first():
                continue
            title = f"{item['artist']} — {item['title']}"
            prefix = "Новая Loved-карта" if category == "loved" else "Новая рейтинговая карта"
            session.add(
                SomsActivity(
                    event_key=key,
                    kind="new_map",
                    payload={"beatmapset_id": item["id"], "status": category},
                    announcement=f"{prefix}: {chat_link(f'beatmapsets/{item["id"]}', title[:300])} "
                    f"от {str(item.get('creator', ''))[:80]}!",
                )
            )
        await session.commit()
        cursor = page.get("cursor_string")
        if reached_start or not cursor:
            break
        params["cursor_string"] = cursor


@get_scheduler().scheduled_job("interval", id="soms_announce_outbox", seconds=3, max_instances=1, coalesce=True)
async def announce_outbox_job():
    await deliver_announcements()


@get_scheduler().scheduled_job(
    "interval",
    id="soms_announce_new_maps",
    minutes=5,
    next_run_time=datetime.now(UTC) + timedelta(minutes=1),
    max_instances=1,
    coalesce=True,
)
async def announce_maps_job():
    from app.dependencies.fetcher import get_fetcher

    async with with_db() as session:
        activation = (
            await session.exec(select(SomsActivity).where(SomsActivity.event_key == "announce:enabled"))
        ).first()
        if activation is None:
            return
        fetcher = await get_fetcher()
        loved_activation = (
            await session.exec(select(SomsActivity).where(SomsActivity.event_key == "announce:loved:enabled"))
        ).first()
        if loved_activation is None:
            loved_activation = SomsActivity(event_key="announce:loved:enabled", kind="activation")
            session.add(loved_activation)
            await session.commit()
            await session.refresh(loved_activation)
        # Independent watermark: installing Loved support must not replay past releases.
        for category, start in (
            ("loved", loved_activation.created_at.replace(tzinfo=UTC)),
            ("ranked", activation.created_at.replace(tzinfo=UTC)),
        ):
            try:
                await stage_releases(session, fetcher, category, start)
            except Exception:
                from app.log import task_logger

                await session.rollback()
                task_logger("SomsAnnouncements").exception(f"Failed to refresh {category} releases")
