"""Small chart descriptors for pattern familiarity, not a replacement star calculator."""

from itertools import pairwise
import math


def _mean(values):
    return sum(values) / len(values) if values else 0.0


def _variation(values):
    return _mean([min(1, abs(math.log2(b / a))) for a, b in pairwise(values) if min(a, b) > 0])


def _turns(points):
    result = []
    for a, b, c in zip(points, points[1:], points[2:]):
        u, v = (b[0] - a[0], b[1] - a[1]), (c[0] - b[0], c[1] - b[1])
        length = math.hypot(*u) * math.hypot(*v)
        if length > 1:
            result.append(math.acos(max(-1, min(1, (u[0] * v[0] + u[1] * v[1]) / length))))
    return result


def technical_features(raw: str) -> dict:
    section = ""
    timing, hits = [], []
    multiplier = 1.4
    for line in raw.splitlines():
        line = line.strip()
        if not line or line.startswith("//"):
            continue
        if line.startswith("["):
            section = line
            continue
        if section == "[Difficulty]" and line.startswith("SliderMultiplier:"):
            multiplier = float(line.split(":", 1)[1])
        elif section == "[TimingPoints]" and "," in line:
            parts = line.split(",")
            timing.append((float(parts[0]), float(parts[1])))
        elif section == "[HitObjects]" and "," in line:
            parts = line.split(",")
            if not int(parts[3]) & 8:
                hits.append(parts)
    if len(hits) < 3:
        return {"rhythm": 0.0, "angles": 0.0, "slider_tech": 0.0}
    hits.sort(key=lambda p: float(p[2]))
    timing.sort()
    times = [float(p[2]) for p in hits]
    points = [(float(p[0]), float(p[1])) for p in hits]
    deltas = [b - a for a, b in pairwise(times) if 25 <= b - a <= 1000]
    distances = [math.dist(a, b) for a, b in pairwise(points) if math.dist(a, b) > 1]
    turns = _turns(points)
    angle_variation = _mean([abs(b - a) / math.pi for a, b in pairwise(turns)])
    shapes, speeds, repeats = [], [], []
    cursor, beat_length, sv = 0, 500.0, 1.0
    for parts in hits:
        while cursor < len(timing) and timing[cursor][0] <= float(parts[2]):
            value = timing[cursor][1]
            if value > 0:
                beat_length, sv = value, 1.0
            elif value < 0:
                sv = max(0.1, min(10, -100 / value))
            cursor += 1
        if int(parts[3]) & 2 and len(parts) >= 8:
            path = [(float(parts[0]), float(parts[1]))]
            for point in parts[5].split("|")[1:]:
                coordinates = point.split(":")
                if len(coordinates) >= 2:
                    path.append((float(coordinates[0]), float(coordinates[1])))
            shapes.append(_mean(_turns(path)) / math.pi)
            speeds.append(multiplier * 100 * sv / beat_length)
            repeats.append(min(1, max(0, int(parts[6]) - 1) / 3))
    return {
        "rhythm": _variation(deltas),
        "angles": min(1, angle_variation * 1.5 + _variation(distances) * 0.3),
        "slider_tech": min(1, _mean(shapes) * 0.45 + _variation(speeds) * 0.4 + _mean(repeats) * 0.15),
    }
