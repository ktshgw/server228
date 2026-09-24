"""Canonical SOMSAI divisions and the Sheet 7 pool matrix."""

from __future__ import annotations

from dataclasses import dataclass
import math

ROMANS = ("I", "II", "III", "IV", "V")
BANDS = ("BRONZE", "SILVER", "GOLD", "PLATINUM", "DIAMOND")
RANKS = (*tuple(f"{band} {roman}" for band in BANDS for roman in ROMANS), "ARCHSOM")


def rank_midpoint_rating(rank: str) -> float:
    """Representative rating for a rank — midpoint of its 100pt band, used for
    continuous pool-mismatch comparisons instead of discrete division gaps."""
    rank = normalise_rank(rank)
    if rank == "ARCHSOM":
        return 3500.0
    band, roman = rank.split()
    return 500 + BANDS.index(band) * 500 + ROMANS.index(roman) * 100 + 50


@dataclass(frozen=True)
class RankPoolRule:
    best_of: int
    bans_per_team: int
    slots: dict[str, tuple[float | None, float | None]]


def _range(value: str) -> tuple[float | None, float | None]:
    if value.endswith("+"):
        return float(value[:-1]), None
    if value.startswith("<"):
        return None, math.nextafter(float(value[1:]), -math.inf)
    low, high = value.split("-")
    return float(low), float(high)


def _slots(values: dict[str, str]) -> dict[str, tuple[float | None, float | None]]:
    return {slot: _range(value) for slot, value in values.items()}


RULES_BY_BAND = {
    "ARCHSOM": RankPoolRule(13, 2, _slots({
        **{f"NM{i}": "8+" for i in range(1, 6)}, "NM6": "6.01+",
        "HD1": "8+", "HD2": "7.6+", "HD3": "7.8+",
        "HR1": "8+", "HR2": "7.6+", "HR3": "7.8+",
        **{f"DT{i}": "8+" for i in range(1, 5)},
        **{f"FM{i}": "7.6+" for i in range(1, 4)}, "TB": "8.3+",
    })),
    "DIAMOND": RankPoolRule(13, 2, _slots({
        "NM1": "7.6-7.99", "NM2": "7.6-7.99", "NM3": "7.6-7.99", "NM4": "7.4-7.99",
        "NM5": "7.6-7.99", "NM6": "5-6", "HD1": "7.6-7.99", "HD2": "7.2-7.59",
        "HD3": "7.4-7.79", "HR1": "7.6-7.99", "HR2": "7.2-7.59", "HR3": "7.4-7.79",
        "DT1": "7.6-7.99", "DT2": "7.6-7.99", "DT3": "7.6-7.99", "DT4": "7.6-7.99",
        "FM1": "7.2-7.59", "FM2": "7.2-7.59", "FM3": "7.2-7.59", "TB": "8-8.29",
    })),
    "PLATINUM": RankPoolRule(11, 2, _slots({
        "NM1": "7.2-7.59", "NM2": "7-7.59", "NM3": "7-7.59", "NM4": "6.8-7.39",
        "NM5": "7-7.59", "HD1": "7.2-7.59", "HD2": "6.8-7.19", "HD3": "6.8-7.39",
        "HR1": "7.2-7.59", "HR2": "6.6-7.19", "HR3": "6.8-7.39",
        "DT1": "7.2-7.59", "DT2": "7.2-7.59", "DT3": "7.2-7.59",
        "FM1": "6.6-7.19", "FM2": "6.6-7.19", "FM3": "6.6-7.19", "TB": "7.6-7.99",
    })),
    "GOLD": RankPoolRule(9, 1, _slots({
        "NM1": "6.7-7.19", "NM2": "6.5-6.99", "NM3": "6.5-6.99", "NM4": "6.4-6.79",
        "NM5": "6.5-6.99", "HD1": "6.7-7.19", "HD2": "6.4-6.79", "HD3": "6.3-6.79",
        "HR1": "6.7-7.19", "HR2": "6-6.59", "HR3": "6.3-6.79",
        "DT1": "6.7-7.19", "DT2": "6.7-7.19", "DT3": "6.7-7.19", "TB": "7.2-7.59",
    })),
    "SILVER": RankPoolRule(7, 1, _slots({
        "NM1": "5.81-6.69", "NM2": "5.51-6.49", "NM3": "5.51-6.49", "NM4": "5.51-6.39", "NM5": "5.5-6.49",
        "HD1": "5.81-6.69", "HD2": "5.41-6.39", "HR1": "5.81-6.69", "HR2": "5.61-5.99",
        "DT1": "5.81-6.69", "DT2": "5.81-6.69", "DT3": "5.81-6.69", "TB": "6.8-7.19",
    })),
    "BRONZE": RankPoolRule(7, 1, _slots({
        "NM1": "<5.8", "NM2": "<5.5", "NM3": "<5.5", "NM4": "<5.5", "HD1": "<5.8",
        "HD2": "<5.4", "HR1": "<5.8", "HR2": "<5.6", "DT1": "<5.8", "DT2": "<5.8",
        "DT3": "<5.8", "TB": "6.6-6.99",
    })),
}


def normalise_rank(value: str) -> str:
    rank = " ".join(value.upper().split())
    if rank not in RANKS:
        raise ValueError("Неизвестный ранг SOMSAI")
    return rank


def rank_position(rank: str) -> int:
    return RANKS.index(normalise_rank(rank))


def rank_from_rating(rating: float) -> str:
    if rating >= 3000:
        return "ARCHSOM"
    if rating < 600:
        return "BRONZE I"
    band_index = min(4, max(0, int(rating // 500) - 1))
    roman_index = min(4, max(0, int((rating - (500 + band_index * 500)) // 100)))
    return f"{BANDS[band_index]} {ROMANS[roman_index]}"


def average_rank(ratings: list[float]) -> str:
    if not ratings:
        return "SILVER I"
    # Floor is deliberate: with ascending ranks it rounds towards the lower pool.
    return RANKS[math.floor(sum(rank_position(rank_from_rating(value)) for value in ratings) / len(ratings))]


def rule_for(rank: str) -> RankPoolRule:
    rank = normalise_rank(rank)
    return RULES_BY_BAND["ARCHSOM" if rank == "ARCHSOM" else rank.split()[0]]


def eligible_ranks(slot: str, stars: float) -> list[str]:
    result = []
    for rank in RANKS:
        limits = rule_for(rank).slots.get(slot)
        if limits is None:
            continue
        low, high = limits
        if (low is None or stars >= low) and (high is None or stars <= high):
            result.append(rank)
    return result


def eligibility_label(ranks: list[str]) -> str:
    if not ranks:
        return "Не используется"
    groups: list[str] = []
    for band in BANDS:
        present = [rank.split()[1] for rank in ranks if rank.startswith(f"{band} ")]
        if present:
            groups.append(f"{band} {present[0]}–{present[-1]}" if len(present) > 1 else f"{band} {present[0]}")
    if "ARCHSOM" in ranks:
        groups.append("ARCHSOM")
    return ", ".join(groups)
