"""Overlay server-local ranking decisions on official beatmap search results.

The official osu! search remains the source for the public catalogue.  It
cannot, however, return a Graveyard/Pending map under its Ranked filter merely
because this server ranked it.  This module replaces official rows whose local
status differs and injects locally-ranked cached sets on the first result page.
"""

from typing import Any, cast

from app.database.beatmap_ranking import BeatmapRankingPolicy, BeatmapsetRankingPolicy
from app.database.beatmapset import Beatmapset, BeatmapsetModel
from app.database.search_beatmapset import SearchBeatmapsetsResp
from app.database.user import User
from app.models.beatmap import BeatmapRankStatus, Genre, Language, SearchQueryModel
from app.service.beatmap_ranking_service import (
    EffectiveBeatmapsetPolicy,
    EffectivePolicySource,
    get_effective_beatmapset_policy,
)

from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession

SEARCH_RESULT_INCLUDES = [
    *BeatmapsetModel.BEATMAPSET_TRANSFORMER_INCLUDES,
    "beatmaps.max_combo",
    "favourite_count",
    "nominations",
    "pack_tags",
    "play_count",
    "ratings",
]


def _category_matches(category: str, policy: EffectiveBeatmapsetPolicy) -> bool:
    if category == "any":
        return True
    if category == "leaderboard":
        return policy.leaderboard_enabled
    if category == "ranked":
        return policy.status in {BeatmapRankStatus.RANKED, BeatmapRankStatus.APPROVED}
    return policy.status.name.lower() == category


def _supports_local_injection(query: SearchQueryModel) -> bool:
    """Return whether every active filter can be evaluated from local data.

    The official API owns personalised membership such as favourites, mapper
    ownership, follows, recommendations and played ranks.  For those queries
    we may safely restyle/remove rows already returned upstream, but must not
    invent additional rows or adjust the total from incomplete local state.
    """

    if query.s in {"favourites", "mine"} or query.played is not None or query.r:
        return False
    return not ({"recommended", "follows"} & set(query.c))


async def _query_matches(beatmapset: Beatmapset, query: SearchQueryModel) -> bool:
    if not query.nsfw and beatmapset.nsfw:
        return False
    if "spotlights" in query.c and not beatmapset.spotlight:
        return False
    if "featured_artists" in query.c and beatmapset.track_id is None:
        return False
    if query.g != Genre.ANY and beatmapset.beatmap_genre != query.g:
        return False
    if query.l != Language.ANY and beatmapset.beatmap_language != query.l:
        return False
    if "video" in query.e and not beatmapset.video:
        return False
    if "storyboard" in query.e and not beatmapset.storyboard:
        return False

    beatmaps = [beatmap for beatmap in await beatmapset.awaitable_attrs.beatmaps if beatmap.deleted_at is None]
    if query.m is not None:
        has_native_mode = any(int(beatmap.mode) == query.m for beatmap in beatmaps)
        has_convertible_mode = (
            "converts" in query.c and query.m in {1, 2, 3} and any(int(beatmap.mode) == 0 for beatmap in beatmaps)
        )
        if not has_native_mode and not has_convertible_mode:
            return False

    words = [word.casefold() for word in query.q.split() if word.strip()]
    if not words:
        return True
    searchable = " ".join(
        [
            str(beatmapset.id),
            beatmapset.artist,
            beatmapset.artist_unicode,
            beatmapset.title,
            beatmapset.title_unicode,
            beatmapset.creator,
            beatmapset.source,
            beatmapset.tags,
            *(beatmap.version for beatmap in beatmaps),
            *(str(beatmap.id) for beatmap in beatmaps),
        ]
    ).casefold()
    return all(word in searchable for word in words)


def _rating(item: dict[str, Any]) -> float:
    ratings = item.get("ratings")
    if not isinstance(ratings, list):
        return 0.0
    votes = sum(int(count) for count in ratings)
    if votes == 0:
        return 0.0
    return sum(index * int(count) for index, count in enumerate(ratings)) / votes


def _sort_value(item: dict[str, Any], field: str) -> str | float | int:
    if field in {"title", "artist"}:
        return str(item.get(field, "")).casefold()
    if field == "difficulty":
        beatmaps = item.get("beatmaps")
        if isinstance(beatmaps, list):
            return max(
                (float(beatmap.get("difficulty_rating", 0)) for beatmap in beatmaps if isinstance(beatmap, dict)),
                default=0.0,
            )
        return 0.0
    if field in {"updated", "ranked"}:
        return str(item.get("last_updated" if field == "updated" else "ranked_date") or "")
    if field == "rating":
        return _rating(item)
    if field == "plays":
        return int(item.get("play_count") or 0)
    if field == "favourites":
        return int(item.get("favourite_count") or 0)
    if field == "nominations":
        nominations = item.get("nominations")
        if isinstance(nominations, dict):
            return int(nominations.get("current") or 0)
        return 0
    return 0


def _sort_merged_results(items: list[dict[str, Any]], sort: str) -> None:
    field, direction = sort.rsplit("_", 1)
    if field == "relevance":
        # The official relevance score is not exposed by the API. Keep its
        # ordering and leave newly-promoted local results at the front.
        return
    items.sort(
        key=lambda item: (_sort_value(item, field), int(item["id"])),
        reverse=direction == "desc",
    )


async def overlay_local_ranked_beatmapsets(
    session: AsyncSession,
    query: SearchQueryModel,
    upstream: SearchBeatmapsetsResp,
    *,
    current_user: User | None,
    first_page: bool,
) -> SearchBeatmapsetsResp:
    """Merge valid local ranking policies into an official search response.

    Injection is intentionally limited to the first page so the official
    cursor remains usable.  Rows already present on any page are still replaced
    (or removed from a now-incompatible category) using their effective local
    status.
    """

    active_set_ids = set(
        (
            await session.exec(
                select(BeatmapsetRankingPolicy.beatmapset_id).where(col(BeatmapsetRankingPolicy.is_active).is_(True))
            )
        ).all()
    )
    active_difficulty_set_ids = set(
        (
            await session.exec(
                select(BeatmapRankingPolicy.beatmapset_id).where(
                    col(BeatmapRankingPolicy.is_active).is_(True),
                    col(BeatmapRankingPolicy.blocks_set_policy).is_(False),
                )
            )
        ).all()
    )
    candidate_ids = active_set_ids | active_difficulty_set_ids
    if not candidate_ids:
        return upstream

    supports_injection = _supports_local_injection(query)

    beatmapsets = (await session.exec(select(Beatmapset).where(col(Beatmapset.id).in_(candidate_ids)))).all()

    overridden_ids: set[int] = set()
    injectable_ids: set[int] = set()
    matching_local: dict[int, dict[str, Any]] = {}
    total_delta = 0
    for beatmapset in beatmapsets:
        policy = await get_effective_beatmapset_policy(session, beatmapset)
        if policy.source == EffectivePolicySource.UPSTREAM:
            # A stale checksum makes an otherwise active policy ineffective.
            continue
        overridden_ids.add(beatmapset.id)
        # The official result set is authoritative for filters backed by
        # private account state. Existing rows are handled below by ID; only
        # fully local-evaluable searches may gain injected rows.
        metadata_matches = await _query_matches(beatmapset, query) if supports_injection else True
        upstream_status = BeatmapRankStatus(beatmapset.beatmap_status)
        upstream_policy = EffectiveBeatmapsetPolicy(
            status=upstream_status,
            leaderboard_enabled=upstream_status.has_leaderboard(),
            pp_enabled=upstream_status.has_pp(),
            source=EffectivePolicySource.UPSTREAM,
            locally_ranked_difficulty_count=0,
        )
        if query.s in {"favourites", "mine"}:
            upstream_matches = local_matches = metadata_matches
        else:
            upstream_matches = metadata_matches and _category_matches(query.s, upstream_policy)
            local_matches = metadata_matches and _category_matches(query.s, policy)
        if supports_injection:
            total_delta += int(local_matches) - int(upstream_matches)
        if not local_matches:
            continue
        transformed = await BeatmapsetModel.transform(
            beatmapset,
            session=session,
            includes=SEARCH_RESULT_INCLUDES,
            user=current_user,
        )
        matching_local[beatmapset.id] = cast(dict[str, Any], transformed)
        if supports_injection and not upstream_matches:
            injectable_ids.add(beatmapset.id)

    merged: list[dict[str, Any]] = []
    seen: set[int] = set()
    for upstream_item in upstream.beatmapsets:
        item = cast(dict[str, Any], upstream_item)
        beatmapset_id = int(item["id"])
        if beatmapset_id in overridden_ids:
            replacement = matching_local.get(beatmapset_id)
            if replacement is not None:
                merged.append(replacement)
                seen.add(beatmapset_id)
            continue
        merged.append(item)
        seen.add(beatmapset_id)

    added = 0
    if first_page:
        injected = [
            item
            for beatmapset_id, item in matching_local.items()
            if beatmapset_id in injectable_ids and beatmapset_id not in seen
        ]
        injected.sort(key=lambda item: (str(item.get("artist", "")).casefold(), str(item.get("title", "")).casefold()))
        added = len(injected)
        merged = [*injected, *merged]
        if added:
            _sort_merged_results(merged, query.sort)

    payload = upstream.model_dump()
    payload["beatmapsets"] = merged
    payload["total"] = max(len(merged), upstream.total + total_delta)
    return SearchBeatmapsetsResp.model_validate(payload)


__all__ = ["overlay_local_ranked_beatmapsets"]
