"""Small, bounded reads of the public osu!Collector API, never beatmap downloads."""

from datetime import UTC, datetime
import hashlib
import json
import re
from typing import Any
from urllib.parse import urlsplit

from app.features.somsai.models.somsai_admin import SomsaiImportRequest, SomsaiSlotSpec

from fastapi import HTTPException
import httpx

MAX_SOURCE_BYTES = 2_000_000


def collector_source(url: str) -> tuple[str, int, str]:
    parsed = urlsplit(url)
    match = re.fullmatch(r"/(tournaments|collections)/([1-9][0-9]*)(?:/[^?#]*)?/?", parsed.path)
    if parsed.scheme != "https" or parsed.netloc != "osucollector.com" or not match or parsed.query or parsed.fragment:
        raise HTTPException(422, "Нужна ссылка https://osucollector.com/tournaments/ID или /collections/ID")
    kind, source_id = match.group(1), int(match.group(2))
    return kind, source_id, f"https://osucollector.com/{kind}/{source_id}"


async def fetch_collector_source(url: str) -> tuple[dict[str, Any], dict[str, Any]]:
    kind, source_id, canonical = collector_source(url)
    try:
        async with (
            httpx.AsyncClient(timeout=20, follow_redirects=False, trust_env=False) as client,
            client.stream("GET", f"https://osucollector.com/api/{kind}/{source_id}") as response,
        ):
            if response.status_code != 200:
                raise HTTPException(502, f"osu!Collector ответил HTTP {response.status_code}")
            body = bytearray()
            async for chunk in response.aiter_bytes():
                body.extend(chunk)
                if len(body) > MAX_SOURCE_BYTES:
                    raise HTTPException(422, "Источник слишком большой; импортируйте меньшую коллекцию")
        data = json.loads(body)
        if not isinstance(data, dict) or data.get("id") != source_id:
            raise ValueError("source identity")
    except (httpx.HTTPError, ValueError) as exc:
        raise HTTPException(502, "Не удалось прочитать публичный API osu!Collector") from exc
    provenance = {
        "source_kind": f"collector_{kind[:-1]}",
        "source_id": source_id,
        "source_url": canonical,
        "source_round": None,
        "source_metadata": {
            "name": str(data.get("name", ""))[:160],
            "sha256": hashlib.sha256(body).hexdigest(),
            "retrieved_at": datetime.now(UTC).isoformat(),
        },
    }
    return data, provenance


def collector_preview(data: dict[str, Any], provenance: dict[str, Any], request: SomsaiImportRequest) -> dict[str, Any]:
    groups: list[dict[str, Any]] = []
    rounds: list[str] = []
    warnings: list[str] = []
    if provenance["source_kind"] == "collector_tournament":
        entries = data.get("rounds", [])
        if not isinstance(entries, list) or any(not isinstance(entry, dict) for entry in entries):
            raise HTTPException(502, "Неизвестный формат раундов osu!Collector")
        rounds = [str(entry.get("round", "")) for entry in entries if isinstance(entry, dict)]
        if request.round is None:
            return {**provenance, "rounds": rounds, "slots": [], "warnings": [], "name": data.get("name", "")}
        selected = [entry for entry in entries if entry.get("round") == request.round]
        if len(selected) != 1:
            raise HTTPException(422, "Выберите один существующий раунд турнира")
        groups = selected[0].get("mods", [])
        provenance = {**provenance, "source_round": request.round}
    else:
        groups = [
            {
                "mod": request.category,
                "maps": [
                    beatmap for beatmapset in data.get("beatmapsets", []) for beatmap in beatmapset.get("beatmaps", [])
                ],
            }
        ]
        if data.get("unknownChecksums") or data.get("unsubmittedBeatmapCount"):
            warnings.append("В коллекции есть неопубликованные карты без ID: они не импортированы")
    slots = []
    counts: dict[str, int] = {}
    seen_beatmaps: set[int] = set()
    skipped = 0
    for group_index, group in enumerate(groups, start=1):
        if not isinstance(group, dict):
            warnings.append(f"Группа #{group_index} имеет неизвестный формат и пропущена")
            skipped += 1
            continue
        category = str(group.get("mod", "")).upper()
        maps = group.get("maps", [])
        if not isinstance(maps, list):
            warnings.append(f"Категория {category or group_index}: список карт повреждён и пропущен")
            skipped += 1
            continue
        if category not in {"NM", "HD", "HR", "DT", "FM", "TB"}:
            warnings.append(f"Категория {category[:40] or group_index} не поддерживается и пропущена")
            skipped += max(1, len(maps))
            continue
        for beatmap in maps:
            counts[category] = counts.get(category, 0) + 1
            label = "TB" if category == "TB" else f"{category}{counts[category]}"
            if category == "TB" and counts[category] > 1:
                warnings.append(f"{label}: лишний тайбрейкер пропущен")
                skipped += 1
                continue
            try:
                if not isinstance(beatmap, dict):
                    raise ValueError("unknown beatmap format")
                checksum = beatmap.get("checksum")
                if checksum is None:
                    warnings.append(f"{label}: исходная контрольная сумма отсутствует; проверьте карту {beatmap.get('id')}")
                slot = SomsaiSlotSpec(
                    id=label, beatmap_id=beatmap.get("id"), checksum=checksum, source_url=provenance["source_url"]
                )
                if slot.beatmap_id in seen_beatmaps:
                    warnings.append(f"{label}: карта {slot.beatmap_id} уже встречалась и пропущена")
                    skipped += 1
                    continue
            except (TypeError, ValueError, AttributeError):
                beatmap_id = beatmap.get("id") if isinstance(beatmap, dict) else "?"
                warnings.append(f"{label}: некорректная карта {beatmap_id} пропущена")
                skipped += 1
                continue
            seen_beatmaps.add(slot.beatmap_id)
            slots.append(slot.model_dump())
    if len(slots) > 64:
        raise HTTPException(422, "В одном пуле не больше 64 карт; выберите меньшую коллекцию")
    return {
        **provenance,
        "rounds": rounds,
        "slots": slots,
        "warnings": warnings,
        "skipped": skipped,
        "name": f"{data.get('name', '')}{' — ' + request.round if request.round else ''}"[:160],
    }


async def preview_collector(request: SomsaiImportRequest) -> dict[str, Any]:
    data, provenance = await fetch_collector_source(request.url)
    return collector_preview(data, provenance, request)
