"""Private API router module.

This module exports the private router and conditionally imports sub-modules
based on application settings (e.g., TOTP verification).
"""

from app.config import settings

from . import (  # noqa: F401
    admin,
    admin_panel,
    api_keys,
    audio_proxy,
    avatar,
    beatmap_ranking_admin,
    beatmapset,
    cover,
    gamemodes,
    marathon,
    negative_pp_admin,
    oauth,
    official_profiles,
    password,
    ranked_admin,
    relationship,
    score,
    stealth,
    somsai,
    somsai_admin,
    team,
    user,
    web_beatmap_community,
    web_notifications,
    web_site,
)
from .router import router as private_router

if settings.enable_totp_verification:
    from . import totp  # noqa: F401

__all__ = [
    "private_router",
]
