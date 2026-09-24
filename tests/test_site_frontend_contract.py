"""Static contracts for the bundled community-site frontend."""

from html.parser import HTMLParser
from pathlib import Path
from typing import ClassVar
import unittest

ROOT = Path(__file__).resolve().parents[1]
INDEX_SOURCE = (ROOT / "static" / "site" / "index.html").read_text(encoding="utf-8")
APP_SOURCE = (ROOT / "static" / "site" / "app.js").read_text(encoding="utf-8")
STYLE_SOURCE = (ROOT / "static" / "site" / "styles.css").read_text(encoding="utf-8")
LEGACY_BRIDGE_SOURCE = (ROOT / "static" / "site" / "legacy-beatmap-bridge.js").read_text(encoding="utf-8")


class _Node:
    def __init__(self, tag: str, attributes: list[tuple[str, str | None]], parent: "_Node | None") -> None:
        self.tag = tag
        self.attributes = dict(attributes)
        self.parent = parent
        self.children: list[_Node] = []
        self.text: list[str] = []

    def all_text(self) -> str:
        return "".join([*self.text, *(child.all_text() for child in self.children)])


class _DocumentParser(HTMLParser):
    _void_elements: ClassVar[set[str]] = {
        "area",
        "base",
        "br",
        "col",
        "embed",
        "hr",
        "img",
        "input",
        "link",
        "meta",
        "source",
    }

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.root = _Node("document", [], None)
        self.stack = [self.root]
        self.ids: dict[str, _Node] = {}

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        node = _Node(tag, attrs, self.stack[-1])
        self.stack[-1].children.append(node)
        if element_id := node.attributes.get("id"):
            self.ids[element_id] = node
        if tag not in self._void_elements:
            self.stack.append(node)

    def handle_startendtag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        self.handle_starttag(tag, attrs)
        if tag not in self._void_elements:
            self.stack.pop()

    def handle_endtag(self, tag: str) -> None:
        for index in range(len(self.stack) - 1, 0, -1):
            if self.stack[index].tag == tag:
                del self.stack[index:]
                return

    def handle_data(self, data: str) -> None:
        self.stack[-1].text.append(data)


class SiteFrontendContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.document = _DocumentParser()
        cls.document.feed(INDEX_SOURCE)

    def test_profile_metadata_is_below_level_and_contains_only_compact_contacts(self) -> None:
        level = self.document.ids["profile-level"]
        metadata = self.document.ids["profile-meta"]
        metadata_parent = metadata.parent
        level_parent = level.parent
        assert metadata_parent is not None
        assert level_parent is not None
        level_row = level_parent.parent
        assert level_row is not None
        profile_root = level_row.parent
        assert profile_root is not None
        anchors = next(
            node for node in metadata_parent.children if node.attributes.get("class") == "profile-anchor-nav"
        )

        assert profile_root is metadata_parent
        siblings = metadata_parent.children
        assert siblings.index(level_row) < siblings.index(metadata)
        assert siblings.index(metadata) < siblings.index(anchors)

        renderer = APP_SOURCE.split("function renderProfileMetadata", 1)[1].split(
            "function renderProfileScores",
            1,
        )[0]
        assert "user.interests" not in renderer
        assert "user.location" not in renderer
        assert 'makeProfileMetaIcon("discord"' in renderer
        assert 'makeProfileMetaIcon("website"' in renderer
        assert 'makeProfileMetaIcon("occupation"' in renderer

    def test_game_history_is_a_reorderable_profile_block_without_caret_labels(self) -> None:
        history = self.document.ids["profile-activity-section"]
        assert history.attributes.get("data-profile-block") == "historical"
        assert "movable-profile-block" in (history.attributes.get("class") or "")
        history_parent = history.parent
        assert history_parent is not None
        assert history_parent.attributes.get("id") == "profile-blocks"
        assert 'historical: "historical"' in APP_SOURCE
        assert "profileOrderQueue.then" in APP_SOURCE
        assert "arrangeProfileBlocks(draggedBlockStartOrder)" in APP_SOURCE

        metadata_parent = self.document.ids["profile-meta"].parent
        assert metadata_parent is not None
        anchor_nav = next(
            node for node in metadata_parent.children if node.attributes.get("class") == "profile-anchor-nav"
        )
        assert "⌃" not in anchor_nav.all_text()
        assert "⌃" not in history.all_text()

    def test_user_disclosure_has_supported_actions_and_accessibility_hooks(self) -> None:
        trigger = self.document.ids["user-menu-button"]
        popover = self.document.ids["user-menu-popover"]
        assert trigger.attributes.get("aria-controls") == "user-menu-popover"
        assert trigger.attributes.get("aria-expanded") == "false"
        assert popover.attributes.get("role") == "dialog"
        action_ids = {"user-menu-profile", "user-menu-settings", "user-menu-logout"}
        assert action_ids <= set(self.document.ids)
        assert "closeUserMenu(true)" in APP_SOURCE
        assert 'event.target.closest(".user-menu-shell")' in APP_SOURCE

    def test_admin_panel_entry_is_permission_gated_below_settings(self) -> None:
        settings = self.document.ids["user-menu-settings"]
        admin = self.document.ids["user-menu-admin"]
        navigation = settings.parent

        assert navigation is not None
        assert navigation is admin.parent
        assert navigation.children.index(admin) == navigation.children.index(settings) + 1
        assert "hidden" in admin.attributes
        assert '$("#user-menu-admin").hidden = !hasSitePermission("admin_panel")' in APP_SOURCE
        assert 'window.location.assign("/admin/")' in APP_SOURCE

    def test_discord_contact_copies_with_an_accessible_fallback(self) -> None:
        clipboard = APP_SOURCE.split("async function copyTextToClipboard", 1)[1].split(
            "function formatNumber",
            1,
        )[0]
        icon_factory = APP_SOURCE.split("function makeProfileMetaIcon", 1)[1].split(
            "function renderProfileMetadata",
            1,
        )[0]
        metadata = APP_SOURCE.split("function renderProfileMetadata", 1)[1].split(
            "function renderProfileScores",
            1,
        )[0]

        assert "navigator.clipboard?.writeText" in clipboard
        assert 'document.execCommand("copy")' in clipboard
        assert 'field.setAttribute("aria-hidden", "true")' in clipboard
        assert "item.dataset.tooltip = label" in icon_factory
        assert "item.title = label" not in icon_factory
        assert 'item.setAttribute("aria-label", label)' in icon_factory
        assert "item.tabIndex = 0" in icon_factory
        assert "copyTextToClipboard(discord)" in metadata
        assert "`Discord: ${discord}`" in metadata
        assert 'makeProfileMetaIcon("website", user.website, safeUrl)' in metadata
        assert 'makeProfileMetaIcon("occupation", occupation)' in metadata
        assert "Скопировать Discord:" not in metadata
        assert "Сайт:" not in metadata
        assert "Занятие:" not in metadata
        assert 'makeProfileMetaIcon("discord"' in metadata

        tooltip_rule = STYLE_SOURCE.split("\n.profile-meta-icon::after {", 1)[1].split("}", 1)[0]
        assert "content:attr(data-tooltip)" in tooltip_rule
        assert "max-width:min(280px,calc(100vw - 32px))" in tooltip_rule
        assert "transition:opacity .055s ease-out" in STYLE_SOURCE
        assert ".profile-meta-icon:focus-visible::after" in STYLE_SOURCE

    def test_profile_friend_button_and_supporter_heart_follow_bancho_states(self) -> None:
        friend_slot = self.document.ids["profile-friend-slot"]
        friend_button = self.document.ids["profile-friend-button"]
        supporter_heart = self.document.ids["profile-supporter-heart"]
        level = self.document.ids["profile-level"]

        assert friend_slot.parent is not None
        assert friend_slot.parent is level.parent.parent
        assert friend_button.attributes.get("data-state") == "none"
        assert supporter_heart.attributes.get("role") == "img"
        assert supporter_heart.attributes.get("aria-label") == "osu!supporter"

        friendship = APP_SOURCE.split("function profileFriendIcon", 1)[1].split(
            "function formatPlayTime",
            1,
        )[0]
        profile_renderer = APP_SOURCE.split("function renderProfile(profile, user", 1)[1].split(
            "function renderProfileFriendship",
            1,
        )[0]
        assert 'friendship.mutual ? "mutual" : friendship.is_following ? "following" : "none"' in friendship
        assert 'method: wasFollowing ? "DELETE" : "PUT"' in friendship
        assert "follower_count" in friendship
        assert 'openAuth("login")' in friendship
        assert '.filter((role) => role !== "supporter")' in profile_renderer
        assert 'profileTitle.replaceChildren(...roleLabels.map((label) => make("span", "profile-role", label)))' in profile_renderer
        assert 'profileTitle.hidden = roleLabels.length === 0' in profile_renderer
        assert 'owner: "Владелец"' in APP_SOURCE
        assert "Игрок сервера" not in APP_SOURCE
        assert '$("#profile-supporter-heart").hidden = !supporter' in APP_SOURCE
        assert '.profile-friend-button[data-state="following"] { background:#78a80f; }' in STYLE_SOURCE
        assert '.profile-friend-button[data-state="mutual"] { background:#cf578e; }' in STYLE_SOURCE
        assert 'button.classList.add("is-own")' in friendship
        assert 'button.dataset.tooltip = "Друзья"' in friendship
        assert 'button.setAttribute("aria-disabled", "true")' in friendship
        assert ".profile-friend-button.is-own { position:relative; cursor:default; }" in STYLE_SOURCE
        assert ".profile-supporter-heart" in STYLE_SOURCE

    def test_rank_lost_notification_links_to_beatmap(self) -> None:
        rank_lost = APP_SOURCE.split('if (item.kind === "rank_lost") {', 1)[1].split("row.append(map);", 1)[0]
        assert "map.href = beatmapSiteUrl(item.data.beatmapset_id, item.data.beatmap_id)" in rank_lost
        assert "score_id" not in rank_lost

    def test_location_field_is_absent_from_profile_settings(self) -> None:
        assert "settings-location" not in self.document.ids
        settings_population = APP_SOURCE.split("function populateSettings", 1)[1].split(
            "async function saveProfile",
            1,
        )[0]
        profile_save = APP_SOURCE.split("async function saveProfile", 1)[1].split(
            "function selectSettingsTab",
            1,
        )[0]

        assert "settings-location" not in settings_population
        assert "settings-location" not in profile_save
        assert "Город или место" not in self.document.root.all_text()

    def test_toasts_share_site_styling_and_use_a_fast_entrance(self) -> None:
        toast = APP_SOURCE.split("function toast", 1)[1].split(
            "async function copyTextToClipboard",
            1,
        )[0]
        toast_rule = STYLE_SOURCE.split(".toast {", 1)[1].split("}", 1)[0]

        assert 'const kind = type === "error" ? "error" : "success"' in toast
        assert 'item.setAttribute("role", kind === "error" ? "alert" : "status")' in toast
        assert 'item.classList.add("is-visible")' in toast
        assert "transition:opacity .09s ease-out" in toast_rule
        assert "background:linear-gradient(135deg,var(--b3),var(--b4))" in toast_rule
        assert ".toast.is-error" in STYLE_SOURCE

    def test_most_played_cards_are_larger_and_counts_have_no_suffix(self) -> None:
        renderer = APP_SOURCE.split("function renderMostPlayed", 1)[1].split(
            "function renderProfileActivity",
            1,
        )[0]
        card_rule = STYLE_SOURCE.split(".most-played-item {", 1)[1].split("}", 1)[0]
        art_rule = STYLE_SOURCE.split(".most-played-art {", 1)[1].split("}", 1)[0]

        assert 'make("span", "most-played-count", formatNumber(count))' in renderer
        assert "formatNumber(count)}×" not in renderer
        assert "min-height: 62px" in card_rule
        assert "grid-template-columns: 72px" in card_rule
        assert "width: 72px" in art_rule
        assert ".most-played-copy strong { font-size: 15px" in STYLE_SOURCE

    def test_map_status_is_one_symbolic_badge_and_does_not_shift_difficulties(self) -> None:
        card_renderer = APP_SOURCE.split("function createMapCard", 1)[1].split(
            "function renderMapCards",
            1,
        )[0]
        assert "map-local-badge" not in APP_SOURCE + STYLE_SOURCE
        assert "map-local-icon" not in APP_SOURCE + STYLE_SOURCE
        assert "mapStatusSymbol(map.status, map.local)" in card_renderer
        assert 'if (local) return "◆"' in APP_SOURCE
        assert 'status.setAttribute("aria-label", statusLabel)' in card_renderer
        assert "cover.append(status)" in card_renderer

        status_rule = STYLE_SOURCE.split("\n.map-card-status {", 1)[1].split("}", 1)[0]
        assert "width: 24px" in status_rule
        assert "height: 24px" in status_rule
        assert "position: absolute" in STYLE_SOURCE.split(".map-status {", 1)[1].split("}", 1)[0]

    def test_map_cards_and_profile_records_use_real_stretched_links(self) -> None:
        card_renderer = APP_SOURCE.split("function createMapCard", 1)[1].split(
            "function renderMapCards",
            1,
        )[0]
        score_renderer = APP_SOURCE.split("function createScoreRow", 1)[1].split(
            "function renderSettingsGate",
            1,
        )[0]
        assert 'make("a", "map-card-link")' in card_renderer
        assert "primaryLink.href = beatmapSiteUrl(map.id)" in card_renderer
        assert "primaryLink.title = `${statusLabel}:" in card_renderer
        assert 'primaryLink.setAttribute("aria-label"' in card_renderer
        assert "card.append(primaryLink, cover, body)" in card_renderer
        assert "primaryLink.click()" not in card_renderer
        assert 'make("a", "score-map-link")' in score_renderer
        assert "mapLink.href = mapUrl" in score_renderer
        assert "beatmapSiteUrl(map.beatmapsetId, map.id)" in score_renderer

        card_link_rule = STYLE_SOURCE.split(".map-card-link {", 1)[1].split("}", 1)[0]
        score_link_rule = STYLE_SOURCE.split(".score-map-link::after {", 1)[1].split("}", 1)[0]
        map_body_rule = STYLE_SOURCE.split(".map-body {", 1)[1].split("}", 1)[0]
        download_rule = STYLE_SOURCE.split(".map-download {", 1)[1].split("}", 1)[0]
        assert "position: absolute" in card_link_rule
        assert "inset: 0" in card_link_rule
        assert "z-index: 5" in card_link_rule
        assert "z-index" not in map_body_rule
        assert "position: absolute" in score_link_rule
        assert "inset: 0" in score_link_rule
        assert "z-index: 6" in download_rule
        assert "pointer-events: auto" in download_rule
        assert ".home-map-grid .map-download { position: relative; z-index: 6; }" in STYLE_SOURCE

    def test_beatmap_leaderboard_offers_only_available_replay_downloads(self) -> None:
        renderer = APP_SOURCE.split("function renderBeatmapLeaderboard", 1)[1].split(
            "async function loadBeatmapScores",
            1,
        )[0]

        assert "const replayUrl = replayUrlForScore(item.score)" in renderer
        assert "if (replayUrl)" in renderer
        assert 'make("a", "beatmap-score-replay")' in renderer
        assert 'replay.dataset.tooltip = "Скачать реплей"' in renderer
        assert ".beatmap-score-replay svg" in STYLE_SOURCE

    def test_beatmap_moderation_and_admin_score_deletion_are_permission_gated(self) -> None:
        moderation = self.document.ids["beatmap-moderation-actions"]
        assert "hidden" in moderation.attributes
        assert {"beatmap-rank", "beatmap-unrank", "beatmap-love"} <= set(self.document.ids)
        assert 'hasSitePermission("beatmap_moderation")' in APP_SOURCE
        assert "data: { action }" in APP_SOURCE
        assert 'hasSitePermission("score_delete")' in APP_SOURCE
        assert 'make("button", "is-danger", "Удалить с сервера")' in APP_SOURCE
        assert 'api(`/scores/${Number(score.id)}`, { method: "DELETE" })' in APP_SOURCE
        assert "openScoreActionMenu(menu, actionableScore, { allowPin: false })" in APP_SOURCE

    def test_internal_and_lazer_beatmap_links_keep_exact_difficulty(self) -> None:
        url_helper = APP_SOURCE.split("function beatmapSiteUrl", 1)[1].split(
            "function openBeatmap",
            1,
        )[0]
        assert "Number.isSafeInteger(beatmapsetId)" in url_helper
        assert "`/site/#beatmap/${beatmapsetId}${exactDifficulty}`" in url_helper
        assert "#(?:osu|taiko|fruits|mania)" in LEGACY_BRIDGE_SOURCE
        assert "`/site/#beatmap/${beatmapsetId}/${difficultyId}`" in LEGACY_BRIDGE_SOURCE

    def test_ranking_tabs_are_controls_backed_by_real_queries_and_renderers(self) -> None:
        ranking_page = self.document.ids["page-rankings"]
        section_buttons = [
            node
            for child in ranking_page.children
            for node in child.children
            if node.attributes.get("data-ranking-section") is not None
        ]
        assert {button.attributes["data-ranking-section"] for button in section_buttons} == {
            "world",
            "countries",
            "scores",
            "teams",
            "playlists",
            "ranked",
            "daily",
        }
        assert all(button.tag == "button" for button in section_buttons)
        ranking_loader = APP_SOURCE.split("async function loadRankings", 1)[1].split(
            "function renderPodium",
            1,
        )[0]
        ranking_renderer = APP_SOURCE.split("function renderRankingTable", 1)[1].split(
            "async function loadBeatmaps",
            1,
        )[0]
        assert "section: rankings.section" in ranking_loader
        assert "sort: rankings.sort" in ranking_loader
        assert "scope: rankings.scope" in ranking_loader
        assert 'params.set("country", rankings.country)' in ranking_loader
        for section in ("countries", "scores", "teams", "playlists", "ranked", "daily"):
            assert f'section === "{section}"' in ranking_renderer or f'"{section}"' in ranking_renderer


if __name__ == "__main__":
    unittest.main()
