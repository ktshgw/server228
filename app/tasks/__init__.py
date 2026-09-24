"""Application tasks module.

This module provides scheduled background tasks and startup/shutdown tasks
for the application, including cache management, database cleanup,
rank calculation, and various data synchronization jobs.
"""

# ruff: noqa: F401

from . import (
    beatmapset_update,
    database_cleanup,
    online_history,
    ranked_catalogue,
    recalculate_failed_score,
    update_client_version,
)
from app.features.somsai.tasks import soms_announcements, somsai  # noqa: F401
from .cache import start_cache_tasks, stop_cache_tasks
from .calculate_all_user_rank import calculate_user_rank
from .create_banchobot import create_banchobot
from .daily_challenge import daily_challenge_job, process_daily_challenge_top
from .geoip import init_geoip
from .load_achievements import load_achievements
from .special_statistics import create_custom_ruleset_statistics, create_rx_statistics

__all__ = [
    "calculate_user_rank",
    "create_banchobot",
    "create_custom_ruleset_statistics",
    "create_rx_statistics",
    "daily_challenge_job",
    "init_geoip",
    "load_achievements",
    "process_daily_challenge_top",
    "start_cache_tasks",
    "stop_cache_tasks",
]
