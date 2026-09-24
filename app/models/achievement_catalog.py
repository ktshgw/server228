"""Versioned metadata for the complete official osu! medal catalogue.

Medal processors and profile presentation intentionally use separate
registries.  Only medals implemented by this server belong in ``MEDALS``;
the profile still needs the complete official catalogue so locked medals do
not disappear merely because their unlock predicate is not implemented yet.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import cache
import json
from pathlib import Path
from typing import Literal, cast

AchievementMode = Literal["osu", "taiko", "fruits", "mania"]


@dataclass(frozen=True, slots=True)
class AchievementCatalogEntry:
    id: int
    name: str
    description: str
    slug: str
    mode: AchievementMode | None
    grouping: str
    ordering: int
    icon_url: str

    @property
    def icon_url_2x(self) -> str:
        # The official transformer exposes one canonical web asset. Returning
        # the same URL lets the frontend de-duplicate candidates instead of
        # issuing 352 speculative ``@2x`` requests that may all 404.
        return self.icon_url


CATALOGUE_PATH = Path(__file__).parents[1] / "resources" / "achievement_catalog.json"
EXPECTED_CATALOGUE_SIZE = 352
CLIENTSIDE_ACHIEVEMENT_IDS = frozenset({353, 354, 355, 356})


@cache
def get_achievement_catalogue() -> tuple[AchievementCatalogEntry, ...]:
    """Load and validate the pinned official catalogue shipped with the app."""

    document = json.loads(CATALOGUE_PATH.read_text(encoding="utf-8"))
    raw_entries = document.get("achievements", [])
    entries = tuple(
        AchievementCatalogEntry(
            id=int(item["id"]),
            name=str(item["name"]),
            description=str(item["description"]),
            slug=str(item["slug"]),
            mode=cast(AchievementMode | None, item["mode"]),
            grouping=str(item["grouping"]),
            ordering=int(item["ordering"]),
            icon_url=str(item["icon_url"]),
        )
        for item in raw_entries
    )
    ids = {entry.id for entry in entries}
    slugs = {entry.slug for entry in entries}
    if len(entries) != EXPECTED_CATALOGUE_SIZE or len(ids) != len(entries) or len(slugs) != len(entries):
        raise RuntimeError("The bundled achievement catalogue is incomplete or contains duplicate IDs/slugs")
    if document.get("count") != len(entries):
        raise RuntimeError("The bundled achievement catalogue count does not match its contents")
    return entries


def achievement_catalogue_by_slug() -> dict[str, AchievementCatalogEntry]:
    return {entry.slug: entry for entry in get_achievement_catalogue()}
