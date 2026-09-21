from collections import Counter
from datetime import UTC, datetime
from importlib import import_module
from pathlib import Path
from types import SimpleNamespace
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch

from app.database import User, UserAchievement
from app.models.achievement_catalog import AchievementCatalogEntry, AchievementMode, get_achievement_catalogue
from app.router.private.web_site import (
    WebContext,
    _achievement_collection_payload,
    get_web_own_achievements,
    get_web_user_achievements,
)
from app.service.web_session_service import WebSessionData

from fastapi import Response


def achievement(
    achievement_id: int,
    slug: str,
    *,
    mode: AchievementMode | None = None,
    grouping: str = "Skill & Dedication",
) -> AchievementCatalogEntry:
    return AchievementCatalogEntry(
        id=achievement_id,
        name=f"Medal {achievement_id}",
        description=f"Description {achievement_id}",
        slug=slug,
        mode=mode,
        grouping=grouping,
        ordering=0,
        icon_url=f"https://assets.ppy.sh/medals/web/{slug}.png",
    )


class WebSiteAchievementPayloadTests(unittest.TestCase):
    def test_catalog_is_grouped_and_contains_locked_and_unlocked_medals(self) -> None:
        skill = achievement(55, "osu-skill-pass-1", mode="osu")
        intro = achievement(122, "all-intro-doubletime", grouping="Mod Introduction")
        secret = achievement(353, "all-secret-hotshot", grouping="Hush-Hush")
        unlocked_at = datetime(2026, 9, 4, 5, 7, 30, tzinfo=UTC)
        records = [
            UserAchievement(id=2, user_id=123, achievement_id=intro.id, achieved_at=unlocked_at),
            UserAchievement(id=1, user_id=123, achievement_id=skill.id, achieved_at=unlocked_at),
        ]

        with patch(
            "app.router.private.web_site.get_achievement_catalogue",
            return_value=(skill, intro, secret),
        ):
            payload = _achievement_collection_payload(records, mode="osu", latest_limit=1, include_locked=True)

        assert payload["total"] == 3
        assert payload["unlocked_count"] == 2
        assert payload["catalog_visible"] is True
        assert [item["id"] for item in payload["latest"]] == [intro.id]
        groups = {group["key"]: group for group in payload["groups"]}
        assert groups["skill-dedication"]["unlocked_count"] == 1
        assert groups["mod-introduction"]["unlocked_count"] == 1
        assert groups["hush-hush"]["unlocked_count"] == 0
        assert groups["hush-hush"]["achievements"][0]["is_secret"] is True
        assert groups["hush-hush"]["achievements"][0]["unlocked"] is False
        assert groups["hush-hush"]["achievements"][0]["achieved_at"] is None

    def test_unknown_legacy_unlock_is_not_silently_lost(self) -> None:
        record = UserAchievement(
            id=1,
            user_id=123,
            achievement_id=9999,
            achieved_at=datetime(2026, 9, 4, tzinfo=UTC),
        )

        with patch("app.router.private.web_site.get_achievement_catalogue", return_value=()):
            payload = _achievement_collection_payload([record], mode="osu", latest_limit=8, include_locked=False)

        assert payload["total"] == 1
        assert payload["unlocked_count"] == 1
        assert payload["catalog_visible"] is False
        assert payload["groups"][0]["key"] == "legacy"
        assert payload["groups"][0]["achievements"][0]["image_url"] is None

    def test_public_projection_keeps_every_unlock_and_removes_every_locked_definition(self) -> None:
        unlocked = achievement(55, "osu-skill-pass-1", mode="osu")
        locked = achievement(122, "all-intro-doubletime", grouping="Mod Introduction")
        records = [
            UserAchievement(
                id=2,
                user_id=123,
                achievement_id=9999,
                achieved_at=datetime(2026, 9, 4, 6, tzinfo=UTC),
            ),
            UserAchievement(
                id=1,
                user_id=123,
                achievement_id=unlocked.id,
                achieved_at=datetime(2026, 9, 4, 5, tzinfo=UTC),
            ),
        ]

        with patch(
            "app.router.private.web_site.get_achievement_catalogue",
            return_value=(unlocked, locked),
        ):
            payload = _achievement_collection_payload(records, mode="osu", latest_limit=8, include_locked=False)

        visible = [item for group in payload["groups"] for item in group["achievements"]]
        assert {item["id"] for item in visible} == {unlocked.id, 9999}
        assert all(item["unlocked"] for item in visible)
        assert payload["total"] == payload["unlocked_count"] == 2
        assert [item["id"] for item in payload["latest"]] == [9999, unlocked.id]

    def test_selected_ruleset_gets_common_and_its_own_medals_only(self) -> None:
        catalogue = get_achievement_catalogue()
        by_slug = {entry.slug: entry for entry in catalogue}
        records = [
            UserAchievement(
                id=index,
                user_id=123,
                achievement_id=by_slug[slug].id,
                achieved_at=datetime(2026, 9, 4, index, tzinfo=UTC),
            )
            for index, slug in enumerate(
                (
                    "all-intro-doubletime",
                    "osu-skill-pass-1",
                    "taiko-skill-pass-1",
                    "fruits-skill-pass-1",
                    "mania-skill-pass-1",
                ),
                start=1,
            )
        ]

        payload = _achievement_collection_payload(records, mode="taiko", latest_limit=8, include_locked=False)
        visible = [item for group in payload["groups"] for item in group["achievements"]]

        assert {item["slug"] for item in visible} == {"all-intro-doubletime", "taiko-skill-pass-1"}
        assert payload["total"] == payload["unlocked_count"] == 2
        assert {item["ruleset"] for item in payload["latest"]} == {"all", "taiko"}


class AchievementCatalogueTests(unittest.TestCase):
    def test_catalogue_is_complete_unique_and_mode_partitioned(self) -> None:
        catalogue = get_achievement_catalogue()

        assert len(catalogue) == 352
        assert len({entry.id for entry in catalogue}) == 352
        assert len({entry.slug for entry in catalogue}) == 352
        assert Counter(entry.mode for entry in catalogue) == Counter(
            {None: 197, "osu": 83, "taiko": 24, "fruits": 24, "mania": 24}
        )
        assert Counter(entry.grouping for entry in catalogue) == Counter(
            {
                "Beatmap Challenge Packs": 7,
                "Beatmap Packs": 80,
                "Beatmap Spotlights": 22,
                "Hush-Hush": 70,
                "Hush-Hush (Expert)": 64,
                "Mod Introduction": 13,
                "Skill & Dedication": 96,
            }
        )

    def test_owner_catalogue_count_tracks_selected_ruleset(self) -> None:
        expected: dict[AchievementMode, int] = {"osu": 280, "taiko": 221, "fruits": 221, "mania": 221}

        for mode, total in expected.items():
            with self.subTest(mode=mode):
                payload = _achievement_collection_payload(
                    [],
                    mode=mode,
                    latest_limit=8,
                    include_locked=True,
                )
                assert payload["total"] == total
                assert payload["unlocked_count"] == 0

    def test_every_implemented_processor_uses_the_official_id_for_its_slug(self) -> None:
        catalogue_by_slug = {entry.slug: entry for entry in get_achievement_catalogue()}
        modules = (
            "clientside",
            "daily_challenge",
            "hush_hush",
            "mods",
            "osu_combo",
            "osu_playcount",
            "skill",
            "total_hits",
        )
        implemented = [
            achievement
            for module_name in modules
            for achievement in import_module(f"app.achievements.{module_name}").MEDALS
        ]

        assert len(implemented) == 136
        assert len({achievement.id for achievement in implemented}) == 136
        for medal in implemented:
            with self.subTest(slug=medal.assets_id):
                assert catalogue_by_slug[medal.assets_id].id == medal.id


class WebSiteAchievementEndpointTests(unittest.IsolatedAsyncioTestCase):
    async def test_public_endpoint_returns_real_unlocks_without_private_user_fields(self) -> None:
        medal = achievement(55, "osu-skill-pass-1", mode="osu")
        locked = achievement(122, "all-intro-doubletime", grouping="Mod Introduction")
        user = User(
            id=1500000000,
            server_id=1,
            username="player",
            email="private@example.test",
            pw_bcrypt="x" * 60,
            country_code="RU",
        )
        records = [
            UserAchievement(
                id=1,
                user_id=user.id,
                achievement_id=medal.id,
                achieved_at=datetime(2026, 9, 4, 5, 7, 30, tzinfo=UTC),
            )
        ]
        session = SimpleNamespace(
            exec=AsyncMock(return_value=SimpleNamespace(all=lambda: records)), info={"negative_pp_score_counts": {}}
        )

        with (
            patch(
                "app.router.private.web_site.get_achievement_catalogue",
                return_value=(medal, locked),
            ),
            patch(
                "app.router.private.web_site._find_public_user",
                new=AsyncMock(return_value=user),
            ) as find_user,
        ):
            payload = await get_web_user_achievements("1", session, latest_limit=8)  # type: ignore[arg-type]

        find_user.assert_awaited_once_with(session, "1")
        session.exec.assert_awaited_once()
        assert payload["user"]["server_id"] == 1
        assert "email" not in payload["user"]
        assert payload["latest"][0]["id"] == medal.id
        assert payload["latest"][0]["image_url"].endswith("/osu-skill-pass-1.png")
        assert payload["latest"][0]["achieved_at"] == records[0].achieved_at
        assert payload["catalog_visible"] is False
        visible = [item for group in payload["groups"] for item in group["achievements"]]
        assert [item["id"] for item in visible] == [medal.id]

    async def test_owner_endpoint_returns_the_full_catalog_and_disables_caching(self) -> None:
        unlocked = achievement(55, "osu-skill-pass-1", mode="osu")
        locked = achievement(122, "all-intro-doubletime", grouping="Mod Introduction")
        user = User(
            id=1500000000,
            server_id=1,
            username="player",
            email="private@example.test",
            pw_bcrypt="x" * 60,
            country_code="RU",
        )
        records = [
            UserAchievement(
                id=1,
                user_id=user.id,
                achievement_id=unlocked.id,
                achieved_at=datetime(2026, 9, 4, 5, 7, 30, tzinfo=UTC),
            )
        ]
        session: Any = SimpleNamespace(
            exec=AsyncMock(return_value=SimpleNamespace(all=lambda: records)), info={"negative_pp_score_counts": {}}
        )
        context = WebContext(
            user=user,
            session=WebSessionData(user_id=user.id, csrf_token=f"csrf-{user.id}", digest="digest"),
        )
        response = Response()

        with patch(
            "app.router.private.web_site.get_achievement_catalogue",
            return_value=(unlocked, locked),
        ):
            payload = await get_web_own_achievements(  # type: ignore[arg-type]
                context=context,
                session=session,
                response=response,
                latest_limit=8,
            )

        session.exec.assert_awaited_once()
        visible = [item for group in payload["groups"] for item in group["achievements"]]
        assert {item["id"] for item in visible} == {unlocked.id, locked.id}
        assert payload["catalog_visible"] is True
        assert payload["total"] == 2
        assert payload["unlocked_count"] == 1
        assert response.headers["cache-control"] == "private, no-store"


class WebSiteAchievementFrontendTests(unittest.TestCase):
    def test_frontend_uses_owner_endpoint_and_defensively_filters_public_catalog(self) -> None:
        source = (Path(__file__).resolve().parents[1] / "static" / "site" / "app.js").read_text(encoding="utf-8")

        assert "const achievementsPath = ownProfile" in source
        assert 'new URLSearchParams({ latest_limit: "8", mode: state.mode })' in source
        assert "`/me/achievements?${achievementParams}`" in source
        assert ".filter((item) => catalogVisible || item.unlocked)" in source
        assert "[item.image_url_2x, item.image_url].map(safeAssetUrl)" in source


if __name__ == "__main__":
    unittest.main()
