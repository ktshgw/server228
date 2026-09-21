"""Bounded, deterministic estimates from rank and observed scores, not an AI impersonation."""

import math
import random


def history_candidates(best: list[dict], recent: list[dict]) -> list[dict]:
    """Cover mod/style groups across the top 100, keeping recent failures too."""
    groups: dict[tuple, list[dict]] = {}
    for score in best:
        target = map_features(score.get("beatmap") or {}, score.get("mods") or [])
        group = (
            tuple(target["mods"]),
            round(target["aim_ratio"] * 4),
            round(target["bpm"] / 60),
            round(target["slider_ratio"] * 3),
            target["length"] >= 180,
        )
        groups.setdefault(group, []).append(score)
    choices = []
    for index in range(16):
        for scores in groups.values():
            if index < len(scores) and len(choices) < 16:
                choices.append(scores[index])
    seen = set()
    result = []
    for score in choices + recent[:8]:
        identity = score.get("id") or (
            (score.get("beatmap") or {}).get("id"),
            str(score.get("mods")),
            score.get("accuracy"),
        )
        if identity not in seen:
            result.append(score)
            seen.add(identity)
    return result


def normalize_level(level: str) -> str:
    return "top1000" if level in {"expert", "impossible"} else level


def rank_skill(rank: int | None, level: str) -> float:
    rank = rank or {"easy": 350000, "medium": 35000, "hard": 3500, "top1000": 350, "mrekk": 1}.get(
        normalize_level(level), 35000
    )
    # Continuous across digit boundaries; rank is a prior, not a fixed score.
    return max(2.5, min(11.0, 11.6 - 1.4 * math.log10(max(1, rank))))


def mod_names(mods: list) -> list[str]:
    names = {m if isinstance(m, str) else m.get("acronym", "") for m in mods}
    if "NC" in names:
        names.discard("NC")
        names.add("DT")
    if "DC" in names:
        names.discard("DC")
        names.add("HT")
    return sorted(names - {"NF", "CL", "SD", "PF", "MR", "SO"})


def map_features(beatmap: dict, mods: list, attributes: dict | None = None) -> dict:
    names = mod_names(mods)
    rate = 1.5 if "DT" in names else 0.75 if "HT" in names else 1.0
    for mod in mods:
        if isinstance(mod, dict):
            rate = float(mod.get("settings", {}).get("speed_change", rate))
    rate = max(0.5, min(2, rate))
    length = max(1, float(beatmap.get("hit_length") or beatmap.get("total_length") or 120))
    circles = int(beatmap.get("count_circles") or 0)
    sliders = int(beatmap.get("count_sliders") or 0)
    count = max(1, circles + sliders + int(beatmap.get("count_spinners") or 0))
    ar = float(beatmap.get("ar") or 0)
    cs = float(beatmap.get("cs") or 0)
    if "HR" in names:
        ar, cs = min(10, ar * 1.4), min(10, cs * 1.3)
    if "EZ" in names:
        ar, cs = ar / 2, cs / 2
    preempt = (1800 - 120 * ar if ar < 5 else 1200 - 150 * (ar - 5)) / rate
    ar = (1800 - preempt) / 120 if preempt > 1200 else 5 + (1200 - preempt) / 150
    # Draft fallback only. Gameplay always uses the native modded star calculator;
    # modded historical scores are admitted only with upstream attributes.
    stars = float(beatmap.get("difficulty_rating") or 0) * rate**0.55
    if "HR" in names:
        stars += 0.35
    if "EZ" in names:
        stars *= 0.8
    bpm = float(beatmap.get("bpm") or 0) * rate
    nps = count / length * rate
    aim = float((attributes or {}).get("aim_difficulty") or 0)
    speed = float((attributes or {}).get("speed_difficulty") or 0)
    speed_ratio = speed / (aim + speed) if aim + speed > 0 else min(0.8, nps / max(1, bpm / 60) / 6)
    return {
        "stars": float((attributes or {}).get("star_rating", stars)),
        **((attributes or {}).get("technical") or {}),
        "slider_control": 1 - float(attributes["slider_factor"])
        if attributes and "slider_factor" in attributes
        else None,
        "mods": names,
        "bpm": bpm,
        "nps": nps,
        "aim_ratio": 1 - speed_ratio,
        "stamina": speed_ratio * math.sqrt(length / rate / 120),
        "ar": ar,
        "cs": cs,
        "length": length / rate,
        "slider_ratio": sliders / count,
    }


def score_sample(score: dict, attributes: dict | None = None) -> dict | None:
    beatmap = score.get("beatmap") or {}
    mods = score.get("mods") or []
    names = mod_names(mods)
    if not beatmap.get("difficulty_rating") or any(m in names for m in ("AT", "RX", "AP")):
        return None
    if attributes is None and any(m in names for m in ("DT", "HT", "HR", "EZ", "FL")):
        return None
    sample = map_features(beatmap, mods, attributes)
    acc = max(0, min(1, float(score.get("accuracy") or 0)))
    stats = score.get("statistics") or {}
    misses = stats.get("miss", stats.get("count_miss", 0)) or 0
    count = max(1, sum(int(beatmap.get(key) or 0) for key in ("count_circles", "count_sliders", "count_spinners")))
    passed = score.get("passed", True)
    sample.update(
        ability=sample["stars"] + 0.35 - 8 * (1 - acc) - min(2, 8 * misses / count) - (1.2 if not passed else 0),
        quality=max(0.1, acc * (1 if passed else 0.35)),
        accuracy=acc,
        passed=passed,
    )
    return sample


def skill_profile(rank: int | None, level: str, samples: list[dict]) -> dict:
    prior = rank_skill(rank, level)
    successes = sorted(s["ability"] for s in samples if s.get("passed", True) and s["accuracy"] >= 0.9)
    observed = successes[round((len(successes) - 1) * 0.7)] if successes else prior
    weight = min(0.4, len(successes) / 40)
    comfort = prior + weight * max(-1.2, min(1.2, observed - prior))
    return {"version": 1, "rank": rank, "comfort": comfort, "samples": samples[:24]}


def comfort_for(profile: dict, target: dict, level: str) -> float:
    base = float(profile.get("comfort", rank_skill(profile.get("rank"), level)))
    samples = profile.get("samples") or []
    if not samples:
        return base
    # A missing mod/skillset gives a small uncertain penalty, never an automatic fail.
    target_mods = set(target.get("mods") or [])
    distances = []
    for sample in samples:
        distance = len(set(sample.get("mods") or []) ^ target_mods) * 0.9
        for feature, scale in (
            ("bpm", 90),
            ("nps", 5),
            ("ar", 3),
            ("cs", 3),
            ("slider_ratio", 0.6),
            ("aim_ratio", 0.15),
            ("stamina", 0.5),
            ("rhythm", 0.2),
            ("angles", 0.25),
            ("slider_tech", 0.2),
            ("slider_control", 0.2),
        ):
            if sample.get(feature) is None or target.get(feature) is None:
                continue
            distance += min(2, abs(float(sample.get(feature, 0)) - float(target.get(feature, 0))) / scale)
        distance += abs(math.log(max(1, sample["length"]) / max(1, target["length"]))) * 0.5
        distances.append((distance, sample))
    nearest = sorted(distances, key=lambda item: item[0])[:6]
    weights = [math.exp(-distance) * sample["quality"] for distance, sample in nearest]
    total = sum(weights)
    observed = sum(sample["ability"] * weight for (_, sample), weight in zip(nearest, weights)) / max(1e-9, total)
    reference = sorted(s["ability"] for s in samples)[len(samples) // 2]
    confidence = min(1, total / 2)
    adjustment = max(-0.65, min(0.65, observed - reference)) * confidence
    unfamiliar = min(0.3, nearest[0][0] * 0.07)
    return base + adjustment + min(0.25, total / 12) - unfamiliar


def choose_draft_slot(bots: list[dict], slots: list[dict], *, banning: bool, seed: int) -> dict:
    rng = random.Random(seed)
    scored = []
    for slot in slots:
        target = map_features(slot, slot.get("mods") or [], slot.get("bot_attributes"))
        margins = [
            comfort_for(
                bot.get("skill_profile") or skill_profile(bot.get("global_rank"), bot["level"], []),
                target,
                bot["level"],
            )
            - target["stars"]
            for bot in bots
        ]
        # A team protects its weaker players as well as its average skill.
        margin = sum(margins) / max(1, len(margins)) * 0.7 + min(margins, default=0) * 0.3
        scored.append((margin + rng.uniform(-0.04, 0.04), slot))
    return (min if banning else max)(scored, key=lambda item: item[0])[1]
