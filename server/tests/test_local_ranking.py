from collections.abc import Callable
from dataclasses import dataclass
from types import SimpleNamespace
from typing import Any
import unittest
from unittest.mock import AsyncMock, patch

from app.config import Settings
from app.database.beatmap_ranking import BeatmapRankingPolicy, BeatmapsetRankingPolicy
from app.models.beatmap import BeatmapRankStatus, Genre, Language, SearchQueryModel
from app.router.private.beatmap_ranking_admin import (
    ApplyLocalRankRequest,
    ClientBeatmapModerationRequest,
    _client_moderation_state,
)
from app.service.beatmap_ranking_command_service import parse_ranking_target
from app.service.beatmap_ranking_reconciliation_service import plan_score_policy_transitions
from app.service.beatmap_ranking_service import (
    EffectiveBeatmapPolicy,
    EffectiveBeatmapsetPolicy,
    EffectivePolicySource,
    _aggregate_effective_difficulties,
    _difficulty_policy_applies,
    _is_effective_local_rank,
    _resolve_flags,
    _set_policy_applies_to_difficulty,
)
from app.service.beatmap_search_overlay_service import (
    _category_matches,
    _sort_merged_results,
    _supports_local_injection,
)
from app.service.beatmapset_update_service import BeatmapChangeType, ChangedBeatmap, _metadata_only_beatmaps

from pydantic import ValidationError


@dataclass(frozen=True)
class Policy:
    status: BeatmapRankStatus
    leaderboard_enabled: bool
    pp_enabled: bool


def assert_raises(expected: type[BaseException], action: Callable[[], Any]) -> None:
    try:
        action()
    except expected:
        return
    raise AssertionError(f"Expected {expected.__name__} to be raised")


class ScorePolicyTransitionTests(unittest.TestCase):
    def test_ranked_to_loved_only_retires_pp(self) -> None:
        before = {1: Policy(BeatmapRankStatus.RANKED, True, True)}
        after = {1: Policy(BeatmapRankStatus.LOVED, True, False)}

        (transition,) = plan_score_policy_transitions(before, after)

        assert not transition.disable_leaderboard
        assert transition.disable_pp
        assert not transition.disable_ranked_score

    def test_ranked_to_qualified_retires_pp_and_ranked_score(self) -> None:
        before = {1: Policy(BeatmapRankStatus.RANKED, True, True)}
        after = {1: Policy(BeatmapRankStatus.QUALIFIED, True, False)}

        (transition,) = plan_score_policy_transitions(before, after)

        assert not transition.disable_leaderboard
        assert transition.disable_pp
        assert transition.disable_ranked_score

    def test_disabled_to_enabled_starts_a_fresh_ranked_score_epoch(self) -> None:
        before = {1: Policy(BeatmapRankStatus.PENDING, False, False)}
        after = {1: Policy(BeatmapRankStatus.RANKED, True, True)}

        (transition,) = plan_score_policy_transitions(before, after)

        assert not transition.disable_leaderboard
        assert not transition.disable_pp
        assert not transition.disable_ranked_score
        assert transition.reset_ranked_score_epoch

    def test_partial_snapshots_are_rejected(self) -> None:
        assert_raises(
            ValueError,
            lambda: plan_score_policy_transitions(
                {1: Policy(BeatmapRankStatus.RANKED, True, True)},
                {2: Policy(BeatmapRankStatus.PENDING, False, False)},
            ),
        )


class RankingCommandParserTests(unittest.TestCase):
    def test_bare_id_defaults_to_whole_set(self) -> None:
        target, reason = parse_ranking_target(["123", "manual", "review"])

        assert target.beatmapset_id == 123
        assert target.beatmap_id is None
        assert reason == ["manual", "review"]

    def test_difficulty_url_selects_one_beatmap(self) -> None:
        target, reason = parse_ranking_target(["https://osu.ppy.sh/beatmapsets/123#osu/456", "good", "chart"])

        assert target.beatmap_id == 456
        assert target.beatmapset_id is None
        assert reason == ["good", "chart"]

    def test_ambiguous_text_target_is_rejected(self) -> None:
        assert_raises(ValueError, lambda: parse_ranking_target(["not-an-id"]))


class RankingApiValidationTests(unittest.TestCase):
    def test_native_client_only_accepts_rank_unrank_and_love(self) -> None:
        for action in ("rank", "unrank", "love"):
            assert ClientBeatmapModerationRequest.model_validate({"action": action}).action == action
        selected = ClientBeatmapModerationRequest.model_validate({"action": "rank", "beatmap_id": 456})
        assert selected.beatmap_id == 456
        assert_raises(
            ValidationError,
            lambda: ClientBeatmapModerationRequest.model_validate({"action": "inherit"}),
        )
        assert_raises(
            ValidationError,
            lambda: ClientBeatmapModerationRequest.model_validate({"action": "rank", "beatmap_id": 0}),
        )

    def test_ranked_and_loved_are_accepted(self) -> None:
        ranked = ApplyLocalRankRequest.model_validate({"beatmapset_id": 1, "status": 1, "reason": "reviewed"})
        loved = ApplyLocalRankRequest.model_validate({"beatmapset_id": 1, "status": 4, "reason": "reviewed"})

        assert ranked.status == BeatmapRankStatus.RANKED
        assert loved.status == BeatmapRankStatus.LOVED

    def test_other_status_is_rejected(self) -> None:
        assert_raises(
            ValidationError,
            lambda: ApplyLocalRankRequest.model_validate({"beatmapset_id": 1, "status": 3, "reason": "not rankable"}),
        )

    def test_bool_ids_status_and_string_flags_are_rejected(self) -> None:
        invalid_payloads = [
            {"beatmapset_id": True, "reason": "invalid id"},
            {"beatmapset_id": 1, "status": True, "reason": "invalid status"},
            {"beatmapset_id": 1, "leaderboard_enabled": "yes", "reason": "invalid flag"},
        ]
        for payload in invalid_payloads:
            with self.subTest(payload=payload):
                assert_raises(ValidationError, lambda payload=payload: ApplyLocalRankRequest.model_validate(payload))

    def test_unknown_request_fields_are_rejected(self) -> None:
        assert_raises(
            ValidationError,
            lambda: ApplyLocalRankRequest.model_validate({"beatmapset_id": 1, "pp_enable": True, "reason": "typo"}),
        )

    def test_loved_cannot_enable_pp(self) -> None:
        assert_raises(ValueError, lambda: _resolve_flags(BeatmapRankStatus.LOVED, True, True))

    def test_tombstone_survives_upstream_checksum_change(self) -> None:
        tombstone = BeatmapRankingPolicy(
            beatmap_id=1,
            beatmapset_id=2,
            status=BeatmapRankStatus.PENDING,
            leaderboard_enabled=False,
            pp_enabled=False,
            blocks_set_policy=True,
            ranked_checksum="a" * 32,
            reason="keep this difficulty on upstream status",
        )
        override = tombstone.model_copy(update={"blocks_set_policy": False})

        assert _difficulty_policy_applies(tombstone, "b" * 32)
        assert not _difficulty_policy_applies(override, "b" * 32)

    def test_forced_unrank_survives_difficulty_revision_change(self) -> None:
        policy = BeatmapRankingPolicy(
            beatmap_id=1,
            beatmapset_id=2,
            status=BeatmapRankStatus.PENDING,
            leaderboard_enabled=False,
            pp_enabled=False,
            force_unranked=True,
            blocks_set_policy=False,
            ranked_checksum="a" * 32,
            reason="keep disabled locally",
        )

        assert _difficulty_policy_applies(policy, "b" * 32)
        assert not _difficulty_policy_applies(policy.model_copy(update={"is_active": False}), "b" * 32)

    def test_only_visible_pulse_rank_is_restored_by_derank(self) -> None:
        upstream_rank = EffectiveBeatmapPolicy(
            status=BeatmapRankStatus.RANKED,
            leaderboard_enabled=True,
            pp_enabled=True,
            source=EffectivePolicySource.UPSTREAM,
        )
        pulse_rank = EffectiveBeatmapPolicy(
            status=BeatmapRankStatus.LOVED,
            leaderboard_enabled=True,
            pp_enabled=False,
            source=EffectivePolicySource.BEATMAPSET,
        )
        pulse_graveyard = EffectiveBeatmapPolicy(
            status=BeatmapRankStatus.GRAVEYARD,
            leaderboard_enabled=False,
            pp_enabled=False,
            source=EffectivePolicySource.BEATMAP,
        )

        assert not _is_effective_local_rank(upstream_rank)
        assert _is_effective_local_rank(pulse_rank)
        assert not _is_effective_local_rank(pulse_graveyard)

    def test_graveyard_is_a_valid_restrictive_status(self) -> None:
        assert _resolve_flags(BeatmapRankStatus.GRAVEYARD, False, False) == (False, False)


class ClientModerationStateTests(unittest.IsolatedAsyncioTestCase):
    async def test_upstream_ranked_map_can_be_unranked(self) -> None:
        session = SimpleNamespace(get=AsyncMock(return_value=SimpleNamespace(id=123)))
        policy = EffectiveBeatmapsetPolicy(
            status=BeatmapRankStatus.RANKED,
            leaderboard_enabled=True,
            pp_enabled=True,
            source=EffectivePolicySource.UPSTREAM,
            locally_ranked_difficulty_count=0,
        )
        with patch(
            "app.router.private.beatmap_ranking_admin.get_effective_beatmapset_policy",
            new=AsyncMock(return_value=policy),
        ):
            state = await _client_moderation_state(session, 123)  # type: ignore[arg-type]

        assert state.can_unrank
        assert not state.can_rank
        assert state.can_love
        assert state.source == "upstream"
        assert state.scope == "beatmapset"
        assert state.beatmap_id is None

    async def test_loved_map_does_not_offer_redundant_love_action(self) -> None:
        session = SimpleNamespace(get=AsyncMock(return_value=SimpleNamespace(id=123)))
        policy = EffectiveBeatmapsetPolicy(
            status=BeatmapRankStatus.LOVED,
            leaderboard_enabled=True,
            pp_enabled=False,
            source=EffectivePolicySource.BEATMAPSET,
            locally_ranked_difficulty_count=2,
        )
        with patch(
            "app.router.private.beatmap_ranking_admin.get_effective_beatmapset_policy",
            new=AsyncMock(return_value=policy),
        ):
            state = await _client_moderation_state(session, 123)  # type: ignore[arg-type]

        assert state.can_unrank
        assert not state.can_rank
        assert not state.can_love
        assert not state.pp_enabled

    async def test_selected_difficulty_uses_its_own_effective_policy(self) -> None:
        beatmapset = SimpleNamespace(id=123)
        beatmap = SimpleNamespace(id=456, beatmapset_id=123)
        session = SimpleNamespace(get=AsyncMock(side_effect=[beatmapset, beatmap]))
        policy = EffectiveBeatmapPolicy(
            status=BeatmapRankStatus.PENDING,
            leaderboard_enabled=False,
            pp_enabled=False,
            source=EffectivePolicySource.BEATMAP,
        )
        with patch(
            "app.router.private.beatmap_ranking_admin.get_effective_beatmap_policy",
            new=AsyncMock(return_value=policy),
        ):
            state = await _client_moderation_state(session, 123, beatmap_id=456)  # type: ignore[arg-type]

        assert state.scope == "beatmap"
        assert state.beatmap_id == 456
        assert state.can_rank
        assert not state.can_unrank


class RankingPolicyValidationTests(unittest.TestCase):
    def test_forced_set_unrank_covers_changed_and_new_difficulties(self) -> None:
        policy = BeatmapsetRankingPolicy(
            beatmapset_id=2,
            status=BeatmapRankStatus.PENDING,
            leaderboard_enabled=False,
            pp_enabled=False,
            force_unranked=True,
            revision_manifest={"1": "a" * 32},
            reason="keep the complete set disabled locally",
        )

        assert _set_policy_applies_to_difficulty(
            policy,
            beatmap_id=1,
            current_checksum="b" * 32,
            has_pending_revision_event=True,
        )
        assert _set_policy_applies_to_difficulty(
            policy,
            beatmap_id=99,
            current_checksum="c" * 32,
            has_pending_revision_event=False,
        )
        assert not _set_policy_applies_to_difficulty(
            policy.model_copy(update={"is_active": False}),
            beatmap_id=99,
            current_checksum="c" * 32,
            has_pending_revision_event=False,
        )

    def test_rank_policy_remains_revision_pinned(self) -> None:
        policy = BeatmapsetRankingPolicy(
            beatmapset_id=2,
            status=BeatmapRankStatus.RANKED,
            leaderboard_enabled=True,
            pp_enabled=True,
            revision_manifest={"1": "a" * 32},
            reason="reviewed chart",
        )

        assert not _set_policy_applies_to_difficulty(
            policy,
            beatmap_id=1,
            current_checksum="b" * 32,
            has_pending_revision_event=False,
        )

    def test_difficulty_unrank_does_not_hide_other_ranked_difficulties(self) -> None:
        status, leaderboard_enabled, pp_enabled = _aggregate_effective_difficulties(
            (
                EffectiveBeatmapPolicy(
                    status=BeatmapRankStatus.PENDING,
                    leaderboard_enabled=False,
                    pp_enabled=False,
                    source=EffectivePolicySource.BEATMAP,
                ),
                EffectiveBeatmapPolicy(
                    status=BeatmapRankStatus.RANKED,
                    leaderboard_enabled=True,
                    pp_enabled=True,
                    source=EffectivePolicySource.UPSTREAM,
                ),
            )
        )

        assert status == BeatmapRankStatus.RANKED
        assert leaderboard_enabled is True
        assert pp_enabled is True


class ServerConfigurationTests(unittest.TestCase):
    def test_global_pp_requires_global_leaderboards(self) -> None:
        assert_raises(
            ValidationError,
            lambda: Settings.model_validate(
                {
                    "enable_all_beatmap_pp": True,
                    "enable_all_beatmap_leaderboard": False,
                }
            ),
        )


class SearchOverlayTests(unittest.TestCase):
    def test_ranked_category_includes_approved(self) -> None:
        policy = EffectiveBeatmapsetPolicy(
            status=BeatmapRankStatus.APPROVED,
            leaderboard_enabled=True,
            pp_enabled=True,
            source=EffectivePolicySource.BEATMAP,
            locally_ranked_difficulty_count=1,
        )

        assert _category_matches("ranked", policy)

    def test_injected_rows_follow_requested_sort(self) -> None:
        items = [
            {"id": 2, "artist": "Zulu", "title": "B"},
            {"id": 1, "artist": "Alpha", "title": "A"},
        ]

        _sort_merged_results(items, "artist_asc")

        assert [item["id"] for item in items] == [1, 2]

    def test_personalised_filters_do_not_inject_from_incomplete_local_state(self) -> None:
        base = {"sort": "relevance_desc"}

        assert not _supports_local_injection(SearchQueryModel.model_validate({**base, "s": "favourites"}))
        assert not _supports_local_injection(SearchQueryModel.model_validate({**base, "played": "played"}))
        assert not _supports_local_injection(SearchQueryModel.model_validate({**base, "c": ["follows"]}))
        assert _supports_local_injection(SearchQueryModel.model_validate(base))

    def test_pinned_lazer_search_parameters_follow_numeric_contract(self) -> None:
        query = SearchQueryModel.model_validate(
            {
                "sort": "relevance_desc",
                "g": "2",
                "l": "10",
                "played": "unplayed",
                "c": "converts.featured_artists",
                "e": "video.storyboard",
            }
        )

        assert query.g == Genre.VIDEO_GAME
        assert query.l == Language.SPANISH
        assert query.played == "unplayed"
        assert query.model_dump(exclude_none=True, exclude_unset=True, exclude_defaults=True) == {
            "c": "converts.featured_artists",
            "g": Genre.VIDEO_GAME,
            "l": Language.SPANISH,
            "sort": "relevance_desc",
            "e": "video.storyboard",
            "played": "unplayed",
        }


class BeatmapsetMetadataRefreshTests(unittest.TestCase):
    def test_only_unchanged_difficulties_use_metadata_only_path(self) -> None:
        beatmaps = [{"id": 1}, {"id": 2}, {"id": 3}]
        changed = [
            ChangedBeatmap(1, BeatmapChangeType.MAP_UPDATED),
            ChangedBeatmap(3, BeatmapChangeType.STATUS_CHANGED),
        ]

        metadata_only = _metadata_only_beatmaps(beatmaps, changed)  # pyright: ignore[reportArgumentType]

        assert [beatmap["id"] for beatmap in metadata_only] == [2]


if __name__ == "__main__":
    unittest.main()
