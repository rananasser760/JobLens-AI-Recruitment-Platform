from __future__ import annotations

from apscheduler.schedulers.asyncio import AsyncIOScheduler

from .config import get_recruitment_settings
from .scraper_service import run_scraper

scheduler = AsyncIOScheduler()


async def scheduled_scrape() -> None:
    await run_scraper()


def start_scheduler() -> dict:
    settings = get_recruitment_settings()

    if not settings.scheduled_scrape_enabled:
        return {
            "enabled": False,
            "running": False,
            "interval_hours": settings.scheduled_scrape_hours,
        }

    if not scheduler.running:
        scheduler.add_job(
            scheduled_scrape,
            "interval",
            hours=settings.scheduled_scrape_hours,
            id="joblens-recruitment-scrape",
            replace_existing=True,
            max_instances=1,
            coalesce=True,
        )
        scheduler.start()

    return {
        "enabled": True,
        "running": scheduler.running,
        "interval_hours": settings.scheduled_scrape_hours,
    }


def stop_scheduler() -> None:
    if scheduler.running:
        scheduler.shutdown(wait=False)


def scheduler_status() -> dict:
    settings = get_recruitment_settings()
    return {
        "enabled": settings.scheduled_scrape_enabled,
        "running": scheduler.running,
        "interval_hours": settings.scheduled_scrape_hours,
    }
