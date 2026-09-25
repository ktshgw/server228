"""Displayed pool difficulty: exact calculator stars and clock-adjusted map stats."""

from contextlib import suppress


def display_stats(raw: str, slot: dict, attributes: dict, ruleset_id: int) -> dict:
    # Warehouse slots already contain clock-rate and difficulty-mod adjusted
    # values. Re-applying DT here used to turn a correct 255 BPM into 382.5.
    if slot.get("stats_are_modded"):
        return {
            "stars": attributes.get("star_rating", slot.get("difficulty_rating", 0)),
            "bpm": slot.get("bpm", 0),
            "cs": slot.get("cs", 0),
            "ar": slot.get("ar", 0),
            "od": slot.get("od", 0),
            "hp": slot.get("hp", 0),
            "length": slot.get("total_length", slot.get("hit_length", 0)),
        }
    difficulty = {}
    section = ""
    for line in raw.splitlines():
        line = line.strip()
        if line.startswith("["):
            section = line
        elif section == "[Difficulty]" and ":" in line:
            key, value = line.split(":", 1)
            with suppress(ValueError):
                difficulty[key] = float(value)
    # FM is catalogued against plain HardRock; players may still choose their
    # permitted mods during the match. TB remains nomod for pool balancing.
    mods = [] if slot.get("category") == "TB" else slot.get("mods", [])
    names = {mod["acronym"] for mod in mods}
    rate = 1.0
    for mod in mods:
        if mod["acronym"] in {"DT", "NC", "HT", "DC"}:
            rate *= mod.get("settings", {}).get("speed_change", 1.5 if mod["acronym"] in {"DT", "NC"} else 0.75)
    cs = difficulty.get("CircleSize", slot.get("cs", 5))
    od = difficulty.get("OverallDifficulty", slot.get("od", 5))
    ar = difficulty.get("ApproachRate", difficulty.get("OverallDifficulty", slot.get("ar", 5)))
    hp = difficulty.get("HPDrainRate", slot.get("hp", 5))
    if "HR" in names:
        cs, ar, od, hp = min(10, cs * 1.3), min(10, ar * 1.4), min(10, od * 1.4), min(10, hp * 1.4)
    if "EZ" in names:
        cs, ar, od, hp = cs / 2, ar / 2, od / 2, hp / 2
    if ruleset_id in {0, 2}:
        preempt = (1800 - 120 * ar if ar < 5 else 1200 - 150 * (ar - 5)) / rate
        ar = (1800 - preempt) / 120 if preempt > 1200 else 5 + (1200 - preempt) / 150
    if ruleset_id == 0:
        od = (80 - (80 - 6 * od) / rate) / 6
    elif ruleset_id == 1:
        od = (50 - (50 - 3 * od) / rate) / 3
    return {
        "stars": attributes["star_rating"],
        "bpm": slot.get("bpm", 0) * rate,
        "cs": cs,
        "ar": ar,
        "od": od,
        "hp": hp,
        "length": slot.get("total_length", slot.get("hit_length", 0)) / rate,
    }
