"""Match-scoped SOMSAI bot identities, strengths and drafting preferences.

The Bancho account used as a bot avatar is deliberately absent from this
module.  A new competitive profile is rolled for every match and remains
stable only for the lifetime of that match.
"""

from __future__ import annotations

from dataclasses import dataclass
import random


@dataclass(frozen=True)
class Division:
    name: str
    low_mmr: int
    high_mmr: int | None
    rank_low: int
    rank_high: int
    minimum_score: int
    minimum_accuracy: float


DIVISIONS = (
    Division("ARCHSOM", 3000, None, 1, 250, 800_000, 95.0),
    Division("DIAMOND V", 2900, 2999, 251, 350, 800_000, 94.5),
    Division("DIAMOND IV", 2800, 2899, 351, 450, 775_000, 94.0),
    Division("DIAMOND III", 2700, 2799, 451, 550, 750_000, 93.5),
    Division("DIAMOND II", 2600, 2699, 551, 750, 725_000, 93.0),
    Division("DIAMOND I", 2500, 2599, 751, 999, 700_000, 92.5),
    Division("PLATINUM V", 2400, 2499, 1000, 1500, 800_000, 94.5),
    Division("PLATINUM IV", 2300, 2399, 1501, 2500, 775_000, 94.0),
    Division("PLATINUM III", 2200, 2299, 2501, 3250, 750_000, 93.5),
    Division("PLATINUM II", 2100, 2199, 3251, 4250, 725_000, 93.0),
    Division("PLATINUM I", 2000, 2099, 4251, 5000, 700_000, 92.5),
    Division("GOLD V", 1900, 1999, 5001, 6000, 800_000, 93.0),
    Division("GOLD IV", 1800, 1899, 6001, 6500, 775_000, 92.5),
    Division("GOLD III", 1700, 1799, 6501, 7000, 750_000, 92.0),
    Division("GOLD II", 1600, 1699, 7001, 8000, 725_000, 91.5),
    Division("GOLD I", 1500, 1599, 8001, 9999, 700_000, 91.0),
    Division("SILVER V", 1400, 1499, 10000, 15000, 800_000, 91.5),
    Division("SILVER IV", 1300, 1399, 15001, 25000, 775_000, 90.5),
    Division("SILVER III", 1200, 1299, 25001, 55000, 750_000, 89.5),
    Division("SILVER II", 1100, 1199, 55001, 75000, 725_000, 88.5),
    Division("SILVER I", 1000, 1099, 75001, 99999, 700_000, 87.5),
    Division("BRONZE V", 900, 999, 100000, 150000, 800_000, 89.0),
    Division("BRONZE IV", 800, 899, 150001, 250000, 775_000, 87.0),
    Division("BRONZE III", 700, 799, 250001, 350000, 750_000, 85.0),
    Division("BRONZE II", 600, 699, 350001, 450000, 725_000, 83.0),
    Division("BRONZE I", 0, 599, 450001, 600000, 700_000, 81.0),
)

STANDARD_ARCHETYPES = (
    ("precision", 10),
    ("nomod", 5),
    ("flow_aim", 10),
    ("tapping", 20),
    ("aimer", 20),
    ("hidden", 10),
    ("dt_aim", 10),
    ("dt_tapping", 15),
)
ARCHSOM_ARCHETYPES = (("allrounder", 10), ("consistency", 45), ("high_bpm", 45))
WEAKNESSES = (
    ("anti_hidden", 10),
    ("technical", 15),
    ("anti_nomod", 5),
    ("low_ar", 15),
    ("singletapper", 20),
    ("anti_stream", 15),
    ("anti_aim", 15),
    ("magnifying_glass", 5),
)

ARCHETYPE_MODIFIERS: dict[str, dict[str, float]] = {
    "precision": {
        "NM1": 0.07,
        "HD1": 0.10,
        "HR1": 0.16,
        "HR2": 0.20,
        "DT1": 0.08,
        "FM1": 0.12,
        "FM2": 0.16,
        "NM2": -0.07,
        "NM5": -0.10,
        "DT2": -0.12,
        "DT3": -0.12,
    },
    "nomod": {
        "NM1": 0.14,
        "NM2": 0.14,
        "NM3": 0.12,
        "NM4": 0.04,
        "NM5": 0.12,
        "NM6": 0.10,
        "TB": 0.04,
        "HD1": -0.05,
        "HD2": -0.05,
        "HD3": -0.05,
        "DT1": -0.08,
        "DT2": -0.08,
        "DT3": -0.08,
        "DT4": -0.08,
    },
    "flow_aim": {
        "NM2": 0.18,
        "NM3": 0.14,
        "HD3": 0.14,
        "HR3": 0.10,
        "FM3": 0.12,
        "TB": 0.10,
        "NM1": -0.08,
        "HD1": -0.08,
        "HR1": -0.08,
        "DT1": -0.08,
    },
    "tapping": {
        "NM2": 0.12,
        "NM5": 0.20,
        "DT2": 0.14,
        "DT3": 0.16,
        "DT4": 0.12,
        "NM1": -0.10,
        "HD1": -0.10,
        "HR1": -0.10,
        "DT1": -0.10,
        "FM1": -0.10,
    },
    "aimer": {
        "NM1": 0.18,
        "HD1": 0.14,
        "HR1": 0.16,
        "DT1": 0.12,
        "FM1": 0.16,
        "NM2": -0.12,
        "NM5": -0.14,
        "DT2": -0.12,
        "DT3": -0.14,
        "DT4": -0.12,
    },
    "hidden": {
        "HD1": 0.16,
        "HD2": 0.20,
        "HD3": 0.16,
        "HR1": -0.08,
        "HR2": -0.08,
        "HR3": -0.08,
        "DT1": -0.07,
        "DT2": -0.07,
        "DT3": -0.07,
        "DT4": -0.07,
    },
    "dt_aim": {
        "DT1": 0.22,
        "DT2": 0.05,
        "DT3": 0.03,
        "DT4": 0.08,
        "NM1": 0.10,
        "HD1": 0.08,
        "HR1": 0.07,
        "NM2": -0.10,
        "NM5": -0.10,
    },
    "dt_tapping": {
        "DT1": 0.08,
        "DT2": 0.20,
        "DT3": 0.22,
        "DT4": 0.18,
        "NM5": 0.12,
        "NM2": 0.08,
        "NM1": -0.10,
        "HD1": -0.10,
        "HR1": -0.10,
        "FM1": -0.10,
    },
    "allrounder": {},
    "consistency": {
        "NM1": 0.12,
        "NM2": 0.10,
        "NM3": 0.10,
        "NM4": 0.08,
        "NM6": 0.12,
        "TB": 0.12,
        "HD1": 0.06,
        "HD2": 0.06,
        "HD3": 0.06,
        "HR1": 0.05,
        "NM5": -0.16,
        "DT2": -0.20,
        "DT3": -0.18,
        "DT4": -0.16,
    },
    "high_bpm": {
        "NM5": 0.18,
        "DT1": 0.16,
        "DT2": 0.22,
        "DT3": 0.22,
        "DT4": 0.20,
        "HR1": 0.08,
        "HR2": 0.08,
        "HR3": 0.08,
        "NM6": -0.08,
        "HD2": -0.08,
    },
}

WEAKNESS_MODIFIERS: dict[str, dict[str, float]] = {
    "anti_hidden": {"HD1": -0.34, "HD2": -0.34, "HD3": -0.34},
    "technical": {
        "NM3": 0.12,
        "NM4": 0.20,
        "TB": 0.16,
        "NM1": -0.08,
        "NM2": -0.08,
        "NM5": -0.10,
        "NM6": -0.08,
        "HD1": -0.08,
        "HD2": -0.08,
        "HD3": -0.08,
        "HR1": -0.08,
        "HR2": -0.08,
        "HR3": -0.08,
        "DT1": -0.10,
        "DT2": -0.10,
        "DT3": -0.10,
        "DT4": -0.10,
        "FM1": -0.08,
        "FM2": -0.08,
        "FM3": -0.08,
    },
    "anti_nomod": {f"NM{i}": -0.16 for i in range(1, 7)},
    "low_ar": {
        "DT1": -0.20,
        "DT2": -0.20,
        "DT3": -0.20,
        "DT4": -0.20,
        "NM5": -0.14,
        "NM3": 0.12,
        "NM6": 0.16,
        "HD2": 0.22,
    },
    "singletapper": {"NM3": -0.14, "NM4": -0.14, "HR3": -0.14, "FM3": -0.14, "DT4": -0.18},
    "anti_stream": {"NM2": -0.18, "NM5": -0.18, "HD3": -0.18, "DT2": -0.18, "DT3": -0.20, "DT4": -0.18},
    "anti_aim": {"NM1": -0.18, "HR1": -0.18, "HD1": -0.18, "FM1": -0.18, "HR2": -0.14, "FM2": -0.14},
    "magnifying_glass": {"HR2": -0.38},
}

PREFERENCES = {
    "precision": "HR",
    "nomod": "HR",
    "flow_aim": "HD",
    "tapping": "HD",
    "aimer": "HR",
    "hidden": "HD",
    "dt_aim": "HR",
    "dt_tapping": "HD",
    "consistency": "HD",
    "high_bpm": "HR",
}


def division_for(*, rating: int | None = None, rank: str | None = None) -> Division:
    if rank:
        wanted = rank.strip().upper().replace("ARCHSOM I", "ARCHSOM")
        for division in DIVISIONS:
            if division.name == wanted:
                return division
    value = 1500 if rating is None else max(0, rating)
    return next(d for d in DIVISIONS if value >= d.low_mmr and (d.high_mmr is None or value <= d.high_mmr))


def _weighted(rng: random.Random, values: tuple[tuple[str, int], ...]) -> str:
    return rng.choices([name for name, _ in values], weights=[weight for _, weight in values], k=1)[0]


def _weakness_allowed(archetype: str, weakness: str) -> bool:
    return not (
        (weakness == "anti_hidden" and archetype == "hidden")
        or (weakness == "anti_nomod" and archetype == "nomod")
        or (weakness == "low_ar" and archetype in {"dt_aim", "dt_tapping", "high_bpm"})
        or (weakness == "anti_stream" and archetype in {"tapping", "dt_tapping"})
        or (weakness == "anti_aim" and archetype in {"aimer", "dt_aim"})
        or (weakness == "magnifying_glass" and archetype == "precision")
    )


def roll_ai_profile(*, rating: int | None = None, rank: str | None = None, seed: int | None = None) -> dict:
    """Roll a fresh skillset. Call exactly once for each bot in each new match."""
    rng = random.Random(seed) if seed is not None else random.SystemRandom()
    division = division_for(rating=rating, rank=rank)
    bot_rating = rng.randint(division.low_mmr, division.high_mmr or division.low_mmr + 199)
    archetype = _weighted(rng, ARCHSOM_ARCHETYPES if division.name == "ARCHSOM" else STANDARD_ARCHETYPES)
    weakness = None
    if rng.random() < 0.10:
        allowed = tuple(item for item in WEAKNESSES if _weakness_allowed(archetype, item[0]))
        weakness = _weighted(rng, allowed)
    preference = PREFERENCES.get(archetype)
    if archetype == "allrounder":
        preference = rng.choices(("HD", "HR", "HDHR"), weights=(47.5, 47.5, 5), k=1)[0]
    modifiers = dict(ARCHETYPE_MODIFIERS[archetype])
    if weakness:
        for slot, value in WEAKNESS_MODIFIERS[weakness].items():
            modifiers[slot] = round(max(-0.45, min(0.25, modifiers.get(slot, 0) + value)), 4)
    return {
        "version": 2,
        "rating": bot_rating,
        "division": division.name,
        "minimum_score": division.minimum_score,
        "minimum_accuracy": division.minimum_accuracy,
        "archetype": archetype,
        "weakness": weakness,
        "match_form": round(rng.uniform(0.72, 0.88), 4),
        "consistency": 0.025 if archetype == "allrounder" else 0.04,
        "freemod_preference": preference,
        # TB is optional. Only the hidden archetype sometimes elects to use HD.
        "tiebreaker_preference": "HD" if archetype == "hidden" and rng.random() < 0.60 else None,
        "slot_modifiers": modifiers,
    }


def slot_modifier(profile: dict, slot_id: str) -> float:
    return float((profile.get("slot_modifiers") or {}).get(slot_id.upper(), 0))


def choose_draft_slot(bots: list[dict], slots: list[dict], *, banning: bool, seed: int) -> dict:
    rng = random.Random(seed)
    scored = []
    for slot in slots:
        slot_id = str(slot.get("id") or "").upper()
        values = [slot_modifier(bot.get("ai_profile") or {}, slot_id) for bot in bots]
        # Picks favour team average without sacrificing the least comfortable bot.
        strength = 0.7 * sum(values) / max(1, len(values)) + 0.3 * min(values, default=0)
        scored.append((strength + rng.uniform(-0.025, 0.025), slot))
    return (min if banning else max)(scored, key=lambda item: item[0])[1]
