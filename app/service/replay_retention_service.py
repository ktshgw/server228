"""Replay retention rules for solo leaderboard scores."""

from __future__ import annotations

from collections.abc import Iterable
import json
from typing import Any

from app.database.score import Score
from app.models.mods import API_MODS, APIMod
from app.models.score import GameMode

from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


async def lock_replay_candidates(session: AsyncSession, user_id: int, beatmap_id: int) -> list[Score]:
    """Serialize client uploads and imports competing for the same replay slots."""

    return list(
        (
            await session.exec(
                select(Score)
                .where(Score.user_id == user_id, Score.beatmap_id == beatmap_id)
                .order_by(col(Score.id))
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        ).all()
    )


def _normalise_setting_value(value: Any, setting_type: str | None) -> Any:
    if setting_type == "number" and isinstance(value, (int, float)) and not isinstance(value, bool):
        return float(value)
    return value


def replay_mod_combination_key(gamemode: GameMode, mods: list[APIMod]) -> str:
    """Return an order-independent key which includes every lazer mod setting.

    Missing settings are expanded to their ruleset defaults. This makes, for
    example, an implicit 1.5x DT equal to an explicitly configured 1.5x DT,
    while keeping 1.2x DT as a separate replay category.
    """

    ruleset_mods = API_MODS.get(int(gamemode.to_base_ruleset()), {})
    normalised: list[dict[str, Any]] = []
    for raw_mod in mods or []:
        acronym = str(raw_mod.get("acronym", "")).upper()
        raw_settings = raw_mod.get("settings")
        supplied_settings = raw_settings if isinstance(raw_settings, dict) else {}
        definition = ruleset_mods.get(acronym)
        setting_definitions = (
            {str(item["Name"]): item for item in definition.get("Settings", [])} if definition is not None else {}
        )

        settings: dict[str, Any] = {}
        for name, item in setting_definitions.items():
            value = supplied_settings.get(name, item.get("DefaultValue"))
            settings[name] = _normalise_setting_value(value, str(item.get("Type", "")))
        for name, value in supplied_settings.items():
            if name not in settings:
                settings[str(name)] = value

        mod: dict[str, Any] = {"acronym": acronym}
        if settings:
            mod["settings"] = settings
        normalised.append(mod)

    normalised.sort(
        key=lambda mod: (
            str(mod["acronym"]),
            json.dumps(mod.get("settings", {}), ensure_ascii=True, sort_keys=True, separators=(",", ":")),
        )
    )
    return json.dumps(normalised, ensure_ascii=True, sort_keys=True, separators=(",", ":"))


def replay_retention_eligible(score: Score) -> bool:
    """Whether a score is allowed to own a long-lived leaderboard replay."""

    return bool(
        score.passed and score.leaderboard_eligible and score.room_id is None and score.playlist_item_id is None
    )


def replay_combination_scores(scores: Iterable[Score], target: Score) -> list[Score]:
    """Return eligible scores competing for the target's replay slot."""

    if not replay_retention_eligible(target):
        return []
    target_key = replay_mod_combination_key(target.gamemode, target.mods)
    return [
        score
        for score in scores
        if replay_retention_eligible(score)
        and score.gamemode == target.gamemode
        and replay_mod_combination_key(score.gamemode, score.mods) == target_key
    ]


def best_replay_score(scores: Iterable[Score], target: Score) -> Score | None:
    """Choose the score which owns one user/map/mode/mod replay slot."""

    matching = replay_combination_scores(scores, target)
    if not matching:
        return None
    # An exact tie keeps the score submitted first, avoiding replay churn.
    return max(matching, key=lambda score: (int(score.total_score), -int(score.id)))


__all__ = [
    "best_replay_score",
    "lock_replay_candidates",
    "replay_combination_scores",
    "replay_mod_combination_key",
    "replay_retention_eligible",
]
