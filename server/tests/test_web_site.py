from datetime import UTC, datetime
import hashlib
import io
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.database.statistics import UserStatistics
from app.database.user import User
from app.models.beatmap import BeatmapRankStatus
from app.models.events.user import UserPageUpdatedEvent
from app.models.mods import RANKED_MODS, init_mods
from app.models.mods.performance import DEFAULT_RANKED_MODS
from app.models.score import GameMode
from app.router.private.web_site import (
    WebBeatmapModerationRequest,
    WebEmailChangeRequest,
    WebLoginRequest,
    WebProfileLayoutRequest,
    WebProfileUpdateRequest,
    WebRegisterRequest,
    WebScorePinReorderRequest,
    WebUserpageUpdateRequest,
    _emit_profile_event,
    _exact_beatmap_reference,
    _invalidate_avatar_caches,
    _invalidate_profile_caches,
    _normalise_profile_order,
    _prepare_avatar,
    _ranking_pages,
    _ranking_response,
    _recent_scores,
    _render_web_userpage,
    _reordered_pin_ids,
    _request_ip,
    _score_page_window,
    _statistics_payload,
    _web_mod_catalog_payload,
    _web_permissions,
    update_web_email,
    update_web_profile_layout,
    update_web_userpage,
)
from app.service.web_session_service import web_session_digest

from fastapi import HTTPException, Request
from PIL import Image
from pydantic import ValidationError


def assert_validation_error(action) -> None:
    try:
        action()
    except ValidationError:
        return
    raise AssertionError("Expected ValidationError to be raised")


def assert_http_error(action, status_code: int) -> HTTPException:
    caught: HTTPException | None = None
    try:
        action()
    except HTTPException as exc:
        caught = exc
    if caught is None:
        raise AssertionError("Expected HTTPException to be raised")
    assert caught.status_code == status_code
    return caught


def encoded_image(format_name: str = "PNG", size: tuple[int, int] = (64, 64)) -> bytes:
    image = Image.new("RGB", size, "#ff66aa")
    output = io.BytesIO()
    image.save(output, format=format_name)
    return output.getvalue()


class WebSiteModCatalogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        init_mods()

    def test_catalog_exposes_lazer_categories_for_each_site_mode(self) -> None:
        catalog = _web_mod_catalog_payload()

        assert set(catalog) == {"osu", "taiko", "fruits", "mania", "osurx", "osuap"}
        assert catalog["osurx"] == catalog["osuap"] == catalog["osu"]
        for mode in catalog.values():
            assert mode["EZ"]["type"] == "DifficultyReduction"
            assert mode["NF"]["type"] == "DifficultyReduction"
            assert mode["HT"]["type"] == "DifficultyReduction"
            assert mode["DC"]["type"] == "DifficultyReduction"
            assert mode["SD"]["type"] == "DifficultyIncrease"
            assert mode["PF"]["type"] == "DifficultyIncrease"

    def test_catalog_exposes_custom_rate_defaults_and_all_setting_descriptors(self) -> None:
        osu_mods = _web_mod_catalog_payload()["osu"]
        double_time_settings = {setting["name"]: setting for setting in osu_mods["DT"]["settings"]}

        assert double_time_settings["speed_change"]["default"] == 1.5
        assert double_time_settings["adjust_pitch"]["default"] is False
        assert {setting["name"] for setting in osu_mods["DA"]["settings"]} == {
            "circle_size",
            "approach_rate",
            "drain_rate",
            "overall_difficulty",
            "extended_limits",
        }


class ExpireOnCommitRecord(SimpleNamespace):
    """Small ORM stand-in that rejects reads after a simulated commit."""

    def __init__(self, *, expire_fields: set[str], **values: object) -> None:
        super().__init__(**values)
        self._expire_fields = frozenset(expire_fields)
        self._expired = False

    def expire(self) -> None:
        self._expired = True

    def __getattribute__(self, name: str):
        state = object.__getattribute__(self, "__dict__")
        if state.get("_expired", False) and name in state.get("_expire_fields", ()):
            raise AssertionError(f"expired ORM attribute read after commit: {name}")
        return super().__getattribute__(name)


class WebSiteRequestModelTests(unittest.TestCase):
    def test_registration_normalises_user_input(self) -> None:
        payload = WebRegisterRequest.model_validate(
            {
                "username": "  player  ",
                "email": "  player@example.test  ",
                "password": "correct-horse-battery-staple",
                "country_code": " ru ",
            }
        )

        assert payload.username == "player"
        assert payload.email == "player@example.test"
        assert payload.country_code == "RU"

    def test_registration_rejects_bad_country_and_extra_fields(self) -> None:
        base = {
            "username": "player",
            "email": "player@example.test",
            "password": "valid-password",
        }

        assert_validation_error(lambda: WebRegisterRequest.model_validate({**base, "country_code": "RUS"}))
        assert_validation_error(lambda: WebRegisterRequest.model_validate({**base, "is_owner": True}))

    def test_login_rejects_empty_values_and_extra_fields(self) -> None:
        assert_validation_error(lambda: WebLoginRequest.model_validate({"username": "", "password": "secret"}))
        assert_validation_error(
            lambda: WebLoginRequest.model_validate({"username": "player", "password": "secret", "admin": True})
        )

    def test_profile_normalises_country_and_trims_text(self) -> None:
        payload = WebProfileUpdateRequest.model_validate(
            {"country_code": " jp ", "playmode": "mania", "location": "  Tokyo  ", "website": " example.test "}
        )

        assert payload.country_code == "JP"
        assert payload.playmode == "mania"
        assert payload.location == "Tokyo"
        assert payload.website == "example.test"

    def test_profile_rejects_unknown_mode_and_extra_fields(self) -> None:
        assert_validation_error(lambda: WebProfileUpdateRequest.model_validate({"playmode": "relax"}))
        assert_validation_error(lambda: WebProfileUpdateRequest.model_validate({"username": "renamed"}))

    def test_profile_layout_accepts_dragged_core_sections_and_appends_the_rest(self) -> None:
        payload = WebProfileLayoutRequest.model_validate({"order": ["scores", "medals", "about"]})

        assert payload.order[:3] == ["top_ranks", "medals", "me"]
        assert len(payload.order) == len(set(payload.order))
        assert "historical" in payload.order
        assert "beatmaps" in payload.order

    def test_profile_layout_rejects_duplicates_unknown_and_missing_core_sections(self) -> None:
        assert_validation_error(
            lambda: WebProfileLayoutRequest.model_validate({"order": ["top_ranks", "top_ranks", "me", "medals"]})
        )
        assert_validation_error(
            lambda: WebProfileLayoutRequest.model_validate({"order": ["top_ranks", "me", "medals", "secrets"]})
        )
        assert_validation_error(lambda: WebProfileLayoutRequest.model_validate({"order": ["top_ranks", "me"]}))

    def test_userpage_request_keeps_bbcode_formatting(self) -> None:
        payload = WebUserpageUpdateRequest.model_validate(
            {"body": "[center][img]https://example.test/a.png[/img][/center]"}
        )

        assert payload.body.startswith("[center]")

    def test_score_pin_reorder_requires_exactly_one_positive_reference(self) -> None:
        before = WebScorePinReorderRequest.model_validate({"before_score_id": 12})
        after = WebScorePinReorderRequest.model_validate({"after_score_id": 13})

        assert before.before_score_id == 12
        assert after.after_score_id == 13
        assert_validation_error(lambda: WebScorePinReorderRequest.model_validate({}))
        assert_validation_error(
            lambda: WebScorePinReorderRequest.model_validate({"before_score_id": 12, "after_score_id": 13})
        )
        assert_validation_error(lambda: WebScorePinReorderRequest.model_validate({"before_score_id": 0}))

    def test_beatmap_moderation_accepts_only_the_three_site_actions(self) -> None:
        assert WebBeatmapModerationRequest.model_validate({"action": "rank"}).action == "rank"
        assert WebBeatmapModerationRequest.model_validate({"action": "unrank"}).action == "unrank"
        assert WebBeatmapModerationRequest.model_validate({"action": "love"}).action == "love"
        assert_validation_error(lambda: WebBeatmapModerationRequest.model_validate({"action": "inherit"}))
        assert_validation_error(lambda: WebBeatmapModerationRequest.model_validate({"action": "rank", "admin": True}))


class WebSiteHelperTests(unittest.TestCase):
    def test_website_permissions_keep_bng_separate_from_administration(self) -> None:
        base = {
            "is_owner": False,
            "is_admin": False,
            "is_bng": False,
        }
        bng = _web_permissions(SimpleNamespace(**{**base, "is_bng": True}))  # type: ignore[arg-type]
        administrator = _web_permissions(SimpleNamespace(**{**base, "is_admin": True}))  # type: ignore[arg-type]
        owner = _web_permissions(SimpleNamespace(**{**base, "is_owner": True}))  # type: ignore[arg-type]

        assert bng == {"admin_panel": False, "beatmap_moderation": True, "score_delete": False}
        assert administrator == {"admin_panel": True, "beatmap_moderation": True, "score_delete": True}
        assert owner == administrator

    def test_website_default_profile_order_starts_with_records_and_keeps_maps_last(self) -> None:
        order = _normalise_profile_order(None)

        assert order[:3] == ["top_ranks", "me", "medals"]
        assert order[-1] == "beatmaps"

    def test_ranking_response_keeps_selected_view_and_bounded_pagination(self) -> None:
        response = _ranking_response(
            items=[{"rank": 51}],
            section="countries",
            sort="score",
            mode=GameMode.MANIA,
            page=2,
            page_size=50,
            total=51,
        )

        assert response == {
            "items": [{"rank": 51}],
            "section": "countries",
            "sort": "score",
            "mode": "mania",
            "page": 2,
            "page_size": 50,
            "pages": 2,
            "total": 51,
        }
        assert _ranking_pages(0, 50) == 1

    def test_userpage_renderer_supports_images_and_sanitises_unsafe_urls(self) -> None:
        rendered = _render_web_userpage(
            "[b]hello[/b]\n[img]https://example.test/avatar.png[/img]\n[url=javascript:alert(1)]unsafe[/url]"
        )

        assert "<strong>hello</strong>" in rendered["html"]
        assert 'src="https://example.test/avatar.png"' in rendered["html"]
        assert "javascript:" not in rendered["html"]

    def test_userpage_renderer_allows_clearing_content(self) -> None:
        assert _render_web_userpage("  \n") == {"raw": "", "html": ""}

    def test_userpage_regex_timeout_is_a_validation_error(self) -> None:
        with patch("app.router.private.web_site.bbcode_service.validate_bbcode", side_effect=TimeoutError):
            error = assert_http_error(lambda: _render_web_userpage("[b]hello[/b]"), 422)

        assert error.detail == "BBCode validation timed out"

    def test_exact_beatmap_references_support_site_links_and_ids(self) -> None:
        assert _exact_beatmap_reference("2488899") == ("auto", 2488899)
        assert _exact_beatmap_reference("https://osu.ppy.sh/beatmapsets/2488899#osu/5466136") == (
            "set",
            2488899,
        )
        assert _exact_beatmap_reference("https://osu.ppy.sh/beatmaps/5466136") == ("beatmap", 5466136)
        assert _exact_beatmap_reference("Pow Intro") is None

    def test_session_digest_is_deterministic_and_does_not_store_token(self) -> None:
        opaque_value = "opaque-cookie-token"

        digest = web_session_digest(opaque_value)

        assert digest == hashlib.sha256(opaque_value.encode()).hexdigest()
        assert opaque_value not in digest
        assert len(digest) == 64

    def test_best_score_window_never_exposes_more_than_top_200(self) -> None:
        assert _score_page_window("best", 1, 200, 999) == (200, 0, 200)
        assert _score_page_window("best", 4, 55, 999) == (200, 165, 35)
        assert _score_page_window("best", 5, 55, 999) == (200, 220, 0)
        assert _score_page_window("recent", 2, 50, 999) == (999, 50, 50)

    def test_pinned_scores_reorder_before_and_after_with_compact_positions(self) -> None:
        assert _reordered_pin_ids(
            [10, 20, 30, 40],
            40,
            before_score_id=20,
            after_score_id=None,
        ) == [10, 40, 20, 30]
        assert _reordered_pin_ids(
            [10, 20, 30, 40],
            10,
            before_score_id=None,
            after_score_id=30,
        ) == [20, 30, 10, 40]
        with self.assertRaisesRegex(ValueError, "relative to itself"):  # noqa: PT027
            _reordered_pin_ids([10, 20], 10, before_score_id=10, after_score_id=None)

    def test_request_ip_prefers_first_forwarded_address(self) -> None:
        request = Request(
            {
                "type": "http",
                "headers": [(b"x-forwarded-for", b"198.51.100.7, 10.0.0.1")],
                "client": ("127.0.0.1", 12345),
            }
        )

        assert _request_ip(request) == "198.51.100.7"

    def test_avatar_is_center_cropped_resized_and_encoded_as_png(self) -> None:
        source = Image.new("RGB", (400, 200), "red")
        source.paste("green", (100, 0, 300, 200))
        source_bytes = io.BytesIO()
        source.save(source_bytes, format="JPEG", quality=100, subsampling=0)

        result = _prepare_avatar(source_bytes.getvalue())

        with Image.open(io.BytesIO(result)) as avatar:
            assert avatar.format == "PNG"
            assert avatar.mode == "RGBA"
            assert avatar.size == (256, 256)
            pixel = avatar.getpixel((128, 128))
            assert isinstance(pixel, tuple)
            assert len(pixel) == 4
            red, green, blue, alpha = pixel
            assert green > red
            assert green > blue
            assert alpha == 255

    def test_avatar_rejects_empty_oversized_invalid_and_unsupported_files(self) -> None:
        assert_http_error(lambda: _prepare_avatar(b""), 422)
        assert_http_error(lambda: _prepare_avatar(b"x" * (5 * 1024 * 1024 + 1)), 413)
        assert_http_error(lambda: _prepare_avatar(b"not an image"), 422)
        assert_http_error(lambda: _prepare_avatar(encoded_image("BMP")), 422)

    def test_avatar_rejects_excessive_dimensions(self) -> None:
        assert_http_error(lambda: _prepare_avatar(encoded_image(size=(2049, 1))), 422)


class WebSiteAsyncHelperTests(unittest.IsolatedAsyncioTestCase):
    async def test_ranked_statistics_use_canonical_window_ranks(self) -> None:
        statistics = UserStatistics(user_id=1, mode=GameMode.OSU, pp=747.941, is_ranked=True)
        query_result = SimpleNamespace(first=lambda: statistics)
        session = SimpleNamespace(exec=AsyncMock(return_value=query_result))
        user = User(
            id=1,
            username="ranked",
            email="ranked@example.invalid",
            pw_bcrypt="x",
            country_code="DE",
        )

        with patch("app.router.private.web_site.get_rank", new=AsyncMock(side_effect=[2, 1])) as rank:
            payload = await _statistics_payload(
                session,  # pyright: ignore[reportArgumentType]
                user,
                GameMode.OSU,
            )

        assert payload["global_rank"] == 2
        assert payload["country_rank"] == 1
        assert rank.await_args_list[0].args == (session, statistics)
        assert rank.await_args_list[1].args == (session, statistics, "DE")

    async def test_unranked_statistics_do_not_receive_a_public_rank(self) -> None:
        statistics = UserStatistics(user_id=1, mode=GameMode.OSU, pp=123.0, is_ranked=False)
        query_result = SimpleNamespace(first=lambda: statistics)
        session = SimpleNamespace(exec=AsyncMock(return_value=query_result))
        user = User(id=1, username="unranked", email="unranked@example.invalid", pw_bcrypt="x")

        payload = await _statistics_payload(
            session,  # pyright: ignore[reportArgumentType]
            user,
            GameMode.OSU,
        )

        assert payload["global_rank"] is None
        assert payload["country_rank"] is None
        session.exec.assert_awaited_once()

    async def test_committed_avatar_does_not_fail_when_cache_cleanup_fails(self) -> None:
        cache = SimpleNamespace(
            invalidate_user_all_cache=AsyncMock(side_effect=RuntimeError("cache unavailable")),
            invalidate_v1_user_cache=AsyncMock(),
        )

        await _invalidate_avatar_caches(cache, 1500000000)  # type: ignore[arg-type]

        cache.invalidate_user_all_cache.assert_awaited_once_with(1500000000)
        cache.invalidate_v1_user_cache.assert_awaited_once_with(1500000000)

    async def test_committed_profile_does_not_fail_when_post_commit_work_fails(self) -> None:
        cache = SimpleNamespace(
            invalidate_user_all_cache=AsyncMock(side_effect=RuntimeError("cache unavailable")),
            invalidate_v1_user_cache=AsyncMock(side_effect=RuntimeError("v1 cache unavailable")),
        )

        await _invalidate_profile_caches(cache, 1500000000, include_v1=True)  # type: ignore[arg-type]
        with patch("app.router.private.web_site.hub.emit", side_effect=RuntimeError("event bus unavailable")) as emit:
            _emit_profile_event(UserPageUpdatedEvent(user_id=1500000000, raw_length=1, html_length=1), 1500000000)

        cache.invalidate_user_all_cache.assert_awaited_once_with(1500000000)
        cache.invalidate_v1_user_cache.assert_awaited_once_with(1500000000)
        emit.assert_called_once()

    async def test_profile_layout_uses_captured_values_after_expire_on_commit(self) -> None:
        user = ExpireOnCommitRecord(
            expire_fields={"id"},
            id=7,
            is_restricted=AsyncMock(return_value=False),
        )
        preference = ExpireOnCommitRecord(expire_fields={"extras_order"}, extras_order=[])

        async def commit() -> None:
            user.expire()
            preference.expire()

        session = SimpleNamespace(
            get=AsyncMock(return_value=preference),
            add=Mock(),
            commit=AsyncMock(side_effect=commit),
        )
        cache = SimpleNamespace(
            invalidate_user_all_cache=AsyncMock(),
            invalidate_v1_user_cache=AsyncMock(),
        )
        payload = WebProfileLayoutRequest(order=["top_ranks", "me", "medals"])
        with (
            patch("app.router.private.web_site._require_csrf"),
            patch("app.router.private.web_site._emit_profile_event"),
        ):
            result = await update_web_profile_layout(
                payload,
                SimpleNamespace(),  # type: ignore[arg-type]
                SimpleNamespace(user=user),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert result == {"profile_order": payload.order}

    async def test_userpage_uses_captured_user_id_after_expire_on_commit(self) -> None:
        user = ExpireOnCommitRecord(
            expire_fields={"id"},
            id=7,
            is_restricted=AsyncMock(return_value=False),
        )

        async def commit() -> None:
            user.expire()

        session = SimpleNamespace(add=Mock(), commit=AsyncMock(side_effect=commit))
        cache = SimpleNamespace(
            invalidate_user_all_cache=AsyncMock(),
            invalidate_v1_user_cache=AsyncMock(),
        )
        payload = WebUserpageUpdateRequest(body="[b]hello[/b]")
        with (
            patch("app.router.private.web_site._require_csrf"),
            patch("app.router.private.web_site._emit_profile_event"),
        ):
            result = await update_web_userpage(
                payload,
                SimpleNamespace(),  # type: ignore[arg-type]
                SimpleNamespace(user=user),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert result["profile_text"] == "[b]hello[/b]"
        assert "<strong>hello</strong>" in result["profile_html"]

    async def test_email_change_uses_captured_user_id_after_expire_on_commit(self) -> None:
        user = ExpireOnCommitRecord(expire_fields={"id"}, id=7, email="old@example.test")

        async def commit() -> None:
            user.expire()

        session = SimpleNamespace(
            exec=AsyncMock(return_value=SimpleNamespace(first=lambda: None)),
            add=Mock(),
            commit=AsyncMock(side_effect=commit),
        )
        cache = SimpleNamespace(
            invalidate_user_all_cache=AsyncMock(),
            invalidate_v1_user_cache=AsyncMock(),
        )
        payload = WebEmailChangeRequest(
            current_password="password",  # noqa: S106 - inert request-model fixture
            email="new@example.com",
        )
        with (
            patch("app.router.private.web_site._require_csrf"),
            patch("app.router.private.web_site._verify_web_security_password", new=AsyncMock()),
        ):
            result = await update_web_email(
                payload,
                SimpleNamespace(),  # type: ignore[arg-type]
                SimpleNamespace(user=user),  # type: ignore[arg-type]
                session,  # type: ignore[arg-type]
                SimpleNamespace(),  # type: ignore[arg-type]
                cache,  # type: ignore[arg-type]
            )

        assert result == {"email": "new@example.com"}


class WebRecentScoreTests(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def score(score_id, *, mods=None, pp=100, status=BeatmapRankStatus.RANKED, leaderboard=True):
        return SimpleNamespace(
            id=score_id,
            beatmap_id=score_id,
            beatmap=SimpleNamespace(id=score_id, effective_status=status),
            mods=mods or [],
            pp=pp,
            leaderboard_eligible=leaderboard,
            ended_at=datetime(2026, 9, 13, tzinfo=UTC),
        )

    @staticmethod
    async def policies(_session, beatmaps):
        return {beatmap.id: SimpleNamespace(status=beatmap.effective_status) for beatmap in beatmaps}

    async def test_recent_feed_checks_mod_settings_and_preserves_loved(self):
        candidates = [
            self.score(12, mods=[{"acronym": "DA"}], pp=0),
            self.score(11, mods=[{"acronym": "DA"}], pp=250),  # old cached PP is not permission
            self.score(10, mods=[{"acronym": "AT"}]),
            self.score(9, mods=[{"acronym": "HD", "settings": {"only_fade_approach_circles": True}}]),
            self.score(8, mods=[{"acronym": "DT", "settings": {"speed_change": 2.5}}]),
            self.score(7, mods=[{"acronym": "DT", "settings": {"speed_change": 1.3}}]),
            self.score(6, mods=[{"acronym": "DA"}], pp=0, status=BeatmapRankStatus.LOVED),
            self.score(5, pp=0, status=BeatmapRankStatus.LOVED),
            self.score(4, pp=0),
            self.score(3, pp=0, status=BeatmapRankStatus.PENDING),
            self.score(2, pp=0, status=BeatmapRankStatus.LOVED, leaderboard=False),
            self.score(1),
        ]
        session = SimpleNamespace(exec=AsyncMock(return_value=SimpleNamespace(all=lambda: candidates)))
        with (
            patch.dict(RANKED_MODS, DEFAULT_RANKED_MODS, clear=True),
            patch("app.router.private.web_site.get_effective_beatmap_policies", side_effect=self.policies),
        ):
            results = await _recent_scores(session, GameMode.OSU)
        assert [score.id for score in results] == [7, 5, 1]

    async def test_recent_feed_refills_after_a_full_batch_of_unranked_scores(self):
        rejected = [self.score(score_id, mods=[{"acronym": "DA"}]) for score_id in range(40, 8, -1)]
        accepted = [self.score(score_id) for score_id in range(8, 0, -1)]
        session = SimpleNamespace(
            exec=AsyncMock(side_effect=[SimpleNamespace(all=lambda: rejected), SimpleNamespace(all=lambda: accepted)])
        )
        with (
            patch.dict(RANKED_MODS, DEFAULT_RANKED_MODS, clear=True),
            patch("app.router.private.web_site.get_effective_beatmap_policies", side_effect=self.policies),
        ):
            results = await _recent_scores(session, GameMode.OSU)
        assert [score.id for score in results] == list(range(8, 0, -1))
        assert session.exec.await_count == 2
        refill = session.exec.await_args_list[1].args[0].compile(compile_kwargs={"literal_binds": True})
        assert "scores.id < 9" in str(refill)
        assert "scores.ended_at <" in str(refill)


if __name__ == "__main__":
    unittest.main()
