"""Owner-controlled import of public official osu! score records."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
import hashlib
import json
import lzma
import re
import struct
from typing import Any
from urllib.parse import urlsplit

from app.database import Beatmap, Beatmapset, Score, ScoreImport, ScoreToken, User, UserStatistics
from app.database.score import process_score, process_user
from app.fetcher import Fetcher
from app.fetcher._base import TokenAuthError
from app.helpers import utcnow
from app.log import service_logger
from app.models.mods import APIMod, int_to_mods
from app.models.score import GameMode, HitResult, Rank, SoloScoreSubmissionInfo
from app.service.beatmap_ranking_service import get_effective_beatmap_policy

from httpx import AsyncClient, HTTPError, HTTPStatusError
from redis.asyncio import Redis
from sqlmodel import select
from sqlmodel.ext.asyncio.session import AsyncSession

logger = service_logger("ScoreImport")
OFFICIAL_API_VERSION = "20220705"
MAX_REPLAY_BYTES = 32 * 1024 * 1024
MAX_REPLAY_METADATA_BYTES = 1024 * 1024
MAX_OSR_STRING_BYTES = 64
LAZER_REPLAY_METADATA_VERSION = 30_000_019
OFFICIAL_RULESETS = {"osu": 0, "taiko": 1, "fruits": 2, "mania": 3}
MD5_PATTERN = re.compile(r"^[0-9a-f]{32}$")


class ScoreImportError(ValueError):
    """A safe, user-facing import validation error."""


class ScoreImportConflictError(ScoreImportError):
    """Raised when the same official score is already present locally."""


class ScoreImportNotFoundError(ScoreImportError):
    """Raised when an official score is absent or not publicly readable."""


class ScoreImportUpstreamError(ScoreImportError):
    """Raised when osu! cannot be reached or application OAuth fails."""


@dataclass(slots=True, frozen=True)
class OfficialScoreReference:
    score_id: int
    ruleset: str | None = None

    @property
    def api_path(self) -> str:
        if self.ruleset:
            return f"https://osu.ppy.sh/api/v2/scores/{self.ruleset}/{self.score_id}"
        return f"https://osu.ppy.sh/api/v2/scores/{self.score_id}"

    @property
    def replay_path(self) -> str:
        return f"{self.api_path}/download"


@dataclass(slots=True)
class PreparedOfficialScore:
    reference: OfficialScoreReference
    raw: dict[str, Any]
    info: SoloScoreSubmissionInfo
    beatmap_id: int
    beatmapset_id: int | None
    ruleset: GameMode
    source_user_id: int | None
    source_username: str | None
    started_at: datetime | None
    ended_at: datetime
    build_id: int | None
    classic_total_score: int
    source_total_score: int
    source_total_score_without_mods: int
    source_checksum: str | None
    source_fingerprint: str
    source: str = "official_osu"
    source_score_id: str | None = None
    source_url: str | None = None

    def public_preview(self) -> dict[str, Any]:
        beatmap_value = self.raw.get("beatmap")
        beatmap: dict[str, Any] = beatmap_value if isinstance(beatmap_value, dict) else {}
        beatmapset_value = self.raw.get("beatmapset")
        beatmapset: dict[str, Any] = beatmapset_value if isinstance(beatmapset_value, dict) else {}
        return {
            "source": self.source,
            "source_score_id": self.source_score_id or str(self.reference.score_id),
            "source_fingerprint": self.source_fingerprint,
            "source_url": (
                f"https://osu.ppy.sh/scores/{self.reference.ruleset}/{self.reference.score_id}"
                if self.reference.ruleset
                else f"https://osu.ppy.sh/scores/{self.reference.score_id}"
            )
            if self.source == "official_osu" and self.source_url is None
            else self.source_url,
            "source_user_id": self.source_user_id,
            "source_username": self.source_username,
            "beatmap_id": self.beatmap_id,
            "beatmapset_id": self.beatmapset_id,
            "artist": beatmapset.get("artist"),
            "title": beatmapset.get("title"),
            "version": beatmap.get("version"),
            "ruleset": self.ruleset.value,
            "rank": self.info.rank.value,
            "accuracy": self.info.accuracy,
            "pp_official": self.raw.get("pp"),
            "total_score": self.source_total_score,
            "max_combo": self.info.max_combo,
            "mods": self.info.mods,
            "ended_at": self.ended_at,
            "has_replay": bool(self.raw.get("has_replay")),
        }


@dataclass(slots=True, frozen=True)
class OsrReplayHeader:
    """The security-relevant prefix of an osu! replay file."""

    ruleset: GameMode
    client_version: int
    beatmap_checksum: str
    player_name: str = ""


@dataclass(slots=True, frozen=True)
class ParsedOsrScore:
    """Score metadata stored in the legacy-compatible part of an .osr file."""

    header: OsrReplayHeader
    replay_hash: str
    n300: int
    n100: int
    n50: int
    ngeki: int
    nkatu: int
    nmiss: int
    total_score: int
    max_combo: int
    perfect_combo: bool
    legacy_mods: int
    ended_at: datetime
    online_score_id: int | None


@dataclass(slots=True, frozen=True)
class ServerReplayScoreMetadata:
    """Local identifiers and score fields embedded into a downloaded replay."""

    online_score_id: int
    user_id: int
    client_version: str
    rank: str
    mods: list[APIMod]
    statistics: dict[str, int]
    maximum_statistics: dict[str, int]
    total_score_without_mods: int | None

    @classmethod
    def from_score(cls, score: Score) -> "ServerReplayScoreMetadata":
        return cls(
            online_score_id=score.id,
            user_id=score.user_id,
            client_version=score.client_version or "",
            rank=score.rank.value,
            mods=list(score.mods),
            statistics=_database_score_statistics(score),
            maximum_statistics=_normalise_hit_result_counts(score.maximum_statistics),
            total_score_without_mods=score.total_score_without_mods or None,
        )


@dataclass(slots=True, frozen=True)
class DownloadedOfficialReplay:
    content: bytes
    header: OsrReplayHeader


@dataclass(slots=True, frozen=True)
class ScoreRevisionVerification:
    """Evidence used to decide whether normal ranking is safe."""

    beatmap_id: int
    beatmapset_id: int
    current_checksum: str
    source_checksum: str | None
    replay: DownloadedOfficialReplay | None
    replay_status: str

    @property
    def replay_checksum(self) -> str | None:
        return self.replay.header.beatmap_checksum if self.replay is not None else None

    @property
    def replay_ruleset(self) -> GameMode | None:
        return self.replay.header.ruleset if self.replay is not None else None

    @property
    def current_metadata_matches(self) -> bool:
        return self.source_checksum is not None and self.source_checksum == self.current_checksum

    def revision_verified_for(self, prepared: PreparedOfficialScore) -> bool:
        return (
            self.replay is not None
            and self.replay.header.ruleset == prepared.ruleset
            and self.replay.header.beatmap_checksum == self.current_checksum
        )

    def allows_normal_ranking(self, prepared: PreparedOfficialScore, *, allow_unverified_revision: bool) -> bool:
        return self.revision_verified_for(prepared) or allow_unverified_revision

    def snapshot(self, prepared: PreparedOfficialScore, *, allow_unverified_revision: bool) -> dict[str, Any]:
        verified = self.revision_verified_for(prepared)
        return {
            "beatmap_id": self.beatmap_id,
            "beatmapset_id": self.beatmapset_id,
            "source_checksum": self.source_checksum,
            "current_checksum": self.current_checksum,
            "replay_checksum": self.replay_checksum,
            "replay_ruleset": self.replay_ruleset.value if self.replay_ruleset is not None else None,
            "replay_client_version": self.replay.header.client_version if self.replay is not None else None,
            "replay_size": len(self.replay.content) if self.replay is not None else None,
            "replay_status": self.replay_status,
            "current_metadata_matches": self.current_metadata_matches,
            "replay_ruleset_matches": (
                self.replay.header.ruleset == prepared.ruleset if self.replay is not None else None
            ),
            "revision_verified": verified,
            "allow_unverified_revision": allow_unverified_revision,
            "unverified_override_used": allow_unverified_revision and not verified,
            "ranking_outcome": (
                "normal"
                if self.allows_normal_ranking(
                    prepared,
                    allow_unverified_revision=allow_unverified_revision,
                )
                else "visible_unranked"
            ),
        }


@dataclass(slots=True)
class ImportedOfficialScore:
    score: Score
    provenance: ScoreImport
    replay_content: bytes | None
    replay_unavailable: bool
    pp_pending: bool


def parse_official_score_reference(value: str, ruleset: str | None = None) -> OfficialScoreReference:
    """Accept a numeric ID or an official ``osu.ppy.sh/scores`` URL."""

    cleaned = value.strip()
    requested_ruleset = ruleset.strip().lower() if ruleset else None
    if requested_ruleset == "catch":
        requested_ruleset = "fruits"
    if requested_ruleset and requested_ruleset not in OFFICIAL_RULESETS:
        raise ScoreImportError("Unknown ruleset; use osu, taiko, fruits, or mania")

    if cleaned.isdigit():
        score_id = int(cleaned)
        if score_id <= 0:
            raise ScoreImportError("Score ID must be positive")
        return OfficialScoreReference(score_id=score_id, ruleset=requested_ruleset)

    candidate = cleaned if "://" in cleaned else f"https://{cleaned}"
    parsed = urlsplit(candidate)
    if parsed.scheme != "https" or (parsed.hostname or "").lower() not in {"osu.ppy.sh", "www.osu.ppy.sh"}:
        raise ScoreImportError("Use an official https://osu.ppy.sh/scores/... link or a numeric score ID")
    parts = [part for part in parsed.path.split("/") if part]
    if not parts or parts[0] != "scores":
        raise ScoreImportError("The link is not an official osu! score link")

    url_ruleset: str | None = None
    if len(parts) == 2 and parts[1].isdigit():
        if requested_ruleset is not None:
            raise ScoreImportError("Do not supply a ruleset with a modern /scores/{id} URL")
        score_id = int(parts[1])
    elif len(parts) == 3 and parts[1].lower() in OFFICIAL_RULESETS and parts[2].isdigit():
        url_ruleset = parts[1].lower()
        score_id = int(parts[2])
    else:
        raise ScoreImportError("Could not find a score ID in the official osu! link")
    if score_id <= 0:
        raise ScoreImportError("Score ID must be positive")
    if requested_ruleset and url_ruleset and requested_ruleset != url_ruleset:
        raise ScoreImportError("The supplied ruleset does not match the score URL")
    return OfficialScoreReference(score_id=score_id, ruleset=url_ruleset or requested_ruleset)


def _parse_datetime(value: Any) -> datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError as exc:
        raise ScoreImportError("The official score contains an invalid timestamp") from exc


def _normalise_statistics(value: Any) -> dict[HitResult, int]:
    if not isinstance(value, dict):
        return {}
    legacy_names = {
        "count_300": HitResult.GREAT,
        "count_100": HitResult.OK,
        "count_50": HitResult.MEH,
        "count_miss": HitResult.MISS,
        "count_geki": HitResult.PERFECT,
        "count_katu": HitResult.GOOD,
    }
    result: dict[HitResult, int] = {}
    for key, raw_count in value.items():
        try:
            key_name = str(key)
            hit_result = legacy_names[key_name] if key_name in legacy_names else HitResult(key_name)
            count = int(raw_count)
        except (TypeError, ValueError):
            continue
        if count < 0:
            raise ScoreImportError("The official score contains a negative hit count")
        result[hit_result] = count
    return result


def _normalise_mods(value: Any) -> list[APIMod]:
    if not isinstance(value, list):
        return []
    result: list[APIMod] = []
    for item in value:
        if isinstance(item, str):
            acronym = item.upper()
            settings: dict[str, bool | float | str | int] = {}
        elif isinstance(item, dict):
            acronym = str(item.get("acronym", "")).upper()
            raw_settings = item.get("settings")
            settings = (
                {
                    str(key): setting
                    for key, setting in raw_settings.items()
                    if isinstance(setting, bool | float | str | int)
                }
                if isinstance(raw_settings, dict)
                else {}
            )
        else:
            raise ScoreImportError("The official score contains an invalid mod entry")
        if not acronym:
            raise ScoreImportError("The official score contains an unnamed mod")
        mod: APIMod = {"acronym": acronym}
        if settings:
            mod["settings"] = settings
        result.append(mod)
    return result


def _ruleset_id(raw: dict[str, Any], reference: OfficialScoreReference) -> int:
    value = raw.get("ruleset_id", raw.get("mode_int"))
    if value is None:
        name = str(raw.get("ruleset", raw.get("mode", reference.ruleset or ""))).lower()
        if name == "catch":
            name = "fruits"
        value = OFFICIAL_RULESETS.get(name)
    if value is None:
        raise ScoreImportError("The official score does not contain a supported ruleset")
    try:
        result = int(value)
    except (TypeError, ValueError) as exc:
        raise ScoreImportError("The official score does not contain a supported ruleset") from exc
    if result not in {0, 1, 2, 3}:
        raise ScoreImportError("Only the four official osu! rulesets can be imported")
    if reference.ruleset and OFFICIAL_RULESETS[reference.ruleset] != result:
        raise ScoreImportError("The official response ruleset does not match the supplied link")
    return result


def _source_fingerprint(
    reference: OfficialScoreReference,
    ruleset: GameMode,
    raw: dict[str, Any],
) -> str:
    legacy_value = raw.get("legacy_score_id")
    if legacy_value is not None:
        try:
            legacy_score_id = int(legacy_value)
        except (TypeError, ValueError) as exc:
            raise ScoreImportError("The official score contains an invalid legacy score ID") from exc
        if legacy_score_id <= 0:
            raise ScoreImportError("The official score contains an invalid legacy score ID")
        return f"official_osu:legacy:{ruleset.value}:{legacy_score_id}"
    if reference.ruleset is not None:
        return f"official_osu:legacy:{reference.ruleset}:{reference.score_id}"
    return f"official_osu:lazer:{reference.score_id}"


def prepare_official_score(reference: OfficialScoreReference, raw: dict[str, Any]) -> PreparedOfficialScore:
    try:
        response_score_id = int(raw["id"])
        beatmap_id = int(raw["beatmap_id"])
        source_total_value = raw.get("total_score", raw.get("score"))
        if source_total_value is None:
            raise KeyError("total_score")
        source_total_score = int(source_total_value)
        max_combo = int(raw.get("max_combo") or 0)
        accuracy = float(raw.get("accuracy") or 0)
    except (KeyError, TypeError, ValueError) as exc:
        raise ScoreImportError("The official API returned an incomplete score") from exc
    if reference.ruleset is None:
        response_matches_reference = response_score_id == reference.score_id
    else:
        legacy_value = raw.get("legacy_score_id")
        try:
            response_matches_reference = (
                int(legacy_value) == reference.score_id
                if legacy_value is not None
                else response_score_id == reference.score_id
            )
        except (TypeError, ValueError) as exc:
            raise ScoreImportError("The official score contains an invalid legacy score ID") from exc
    if not response_matches_reference:
        raise ScoreImportError("The official API returned a different score ID")
    if beatmap_id <= 0 or source_total_score < 0 or max_combo < 0 or not 0 <= accuracy <= 1:
        raise ScoreImportError("The official score contains values outside the supported range")
    if not bool(raw.get("passed", True)):
        raise ScoreImportError("Failed plays are not imported into local rankings")

    ruleset_id = _ruleset_id(raw, reference)
    ruleset = GameMode.from_int(ruleset_id)
    statistics = _normalise_statistics(raw.get("statistics"))
    maximum_statistics = _normalise_statistics(raw.get("maximum_statistics"))
    mods = _normalise_mods(raw.get("mods"))
    try:
        source_total_without_mods = int(raw.get("total_score_without_mods") or source_total_score)
    except (TypeError, ValueError) as exc:
        raise ScoreImportError("The official score contains an invalid unmodified total") from exc
    if source_total_without_mods < 0:
        raise ScoreImportError("The official score contains an invalid unmodified total")
    if source_total_score > 2**31 - 1 or source_total_without_mods > 2**31 - 1:
        raise ScoreImportError("The score total is too large for this server build")

    rank_value = str(raw.get("rank") or "F").upper()
    try:
        rank = Rank(rank_value)
    except ValueError as exc:
        raise ScoreImportError("The official score contains an unsupported grade") from exc

    try:
        info = SoloScoreSubmissionInfo(
            rank=rank,
            total_score=source_total_score,
            total_score_without_mods=source_total_without_mods,
            accuracy=accuracy,
            max_combo=max_combo,
            ruleset_id=ruleset_id,
            passed=True,
            mods=mods,
            statistics=statistics,
            maximum_statistics=maximum_statistics,
        )
    except ValueError as exc:
        raise ScoreImportError("The official score contains unsupported score or mod data") from exc
    user_value = raw.get("user")
    user: dict[str, Any] = user_value if isinstance(user_value, dict) else {}
    beatmap_value = raw.get("beatmap")
    beatmap: dict[str, Any] = beatmap_value if isinstance(beatmap_value, dict) else {}
    beatmapset_value = raw.get("beatmapset")
    beatmapset: dict[str, Any] = beatmapset_value if isinstance(beatmapset_value, dict) else {}
    source_user_id_value = raw.get("user_id") or user.get("id")
    beatmapset_id_value = beatmap.get("beatmapset_id") or beatmapset.get("id")
    ended_at = _parse_datetime(raw.get("ended_at") or raw.get("created_at")) or utcnow()
    started_at = _parse_datetime(raw.get("started_at"))
    build_id_value = raw.get("build_id")
    try:
        source_user_id = int(source_user_id_value) if source_user_id_value is not None else None
        beatmapset_id = int(beatmapset_id_value) if beatmapset_id_value is not None else None
        build_id = int(build_id_value) if build_id_value is not None else None
        classic_total = int(raw.get("classic_total_score") or raw.get("legacy_total_score") or 0)
    except (TypeError, ValueError) as exc:
        raise ScoreImportError("The official score contains invalid source metadata") from exc
    if source_user_id is not None and source_user_id <= 0:
        raise ScoreImportError("The official score contains an invalid source user")
    if beatmapset_id is not None and beatmapset_id <= 0:
        raise ScoreImportError("The official score contains an invalid beatmapset ID")
    if build_id is not None and build_id <= 0:
        raise ScoreImportError("The official score contains an invalid build ID")
    if classic_total < 0 or classic_total > 2**63 - 1:
        raise ScoreImportError("The official score contains an invalid classic total")
    checksum_value = beatmap.get("checksum")
    checksum = str(checksum_value).lower() if checksum_value else None
    return PreparedOfficialScore(
        reference=reference,
        raw=raw,
        info=info,
        beatmap_id=beatmap_id,
        beatmapset_id=beatmapset_id,
        ruleset=ruleset,
        source_user_id=source_user_id,
        source_username=str(user.get("username")) if user.get("username") else None,
        started_at=started_at,
        ended_at=ended_at,
        build_id=build_id,
        classic_total_score=classic_total,
        source_total_score=source_total_score,
        source_total_score_without_mods=source_total_without_mods,
        source_checksum=checksum,
        source_fingerprint=_source_fingerprint(reference, ruleset, raw),
    )


async def fetch_official_score(fetcher: Fetcher, reference: OfficialScoreReference) -> PreparedOfficialScore:
    try:
        raw = await fetcher.request_api(
            reference.api_path,
            headers={"x-api-version": OFFICIAL_API_VERSION, "Accept": "application/json"},
        )
    except HTTPStatusError as exc:
        if exc.response.status_code == 404:
            raise ScoreImportNotFoundError("The official score was not found or is not public") from exc
        raise ScoreImportUpstreamError(f"Official osu! returned HTTP {exc.response.status_code}") from exc
    except HTTPError as exc:
        raise ScoreImportUpstreamError("Could not reach the official osu! API") from exc
    except TokenAuthError as exc:
        raise ScoreImportUpstreamError("Official osu! API credentials were rejected") from exc
    if not isinstance(raw, dict):
        raise ScoreImportError("The official API returned an unexpected response")
    return prepare_official_score(reference, raw)


def _read_osr_string(data: bytes, offset: int) -> tuple[str, int]:
    if offset >= len(data):
        raise ScoreImportError("The official replay header is truncated")
    marker = data[offset]
    offset += 1
    if marker == 0:
        return "", offset
    if marker != 0x0B:
        raise ScoreImportError("The official replay contains an invalid string marker")

    length = 0
    for index in range(5):
        if offset >= len(data):
            raise ScoreImportError("The official replay header is truncated")
        value = data[offset]
        offset += 1
        length |= (value & 0x7F) << (index * 7)
        if value & 0x80 == 0:
            break
    else:
        raise ScoreImportError("The official replay contains an invalid string length")

    if length > MAX_OSR_STRING_BYTES or length > len(data) - offset:
        raise ScoreImportError("The official replay contains an invalid string length")
    try:
        return data[offset : offset + length].decode("utf-8"), offset + length
    except UnicodeDecodeError as exc:
        raise ScoreImportError("The official replay header is not valid UTF-8") from exc


def _encode_osr_string(value: str) -> bytes:
    encoded = value.encode("utf-8")
    if len(encoded) > MAX_OSR_STRING_BYTES:
        raise ScoreImportError("The replacement replay username is too long")
    if not encoded:
        return b"\x00"
    length = len(encoded)
    encoded_length = bytearray()
    while length:
        byte = length & 0x7F
        length >>= 7
        encoded_length.append(byte | (0x80 if length else 0))
    return b"\x0b" + bytes(encoded_length) + encoded


def _read_osr_number(data: bytes, offset: int, format_code: str, description: str) -> tuple[int, int]:
    size = struct.calcsize(format_code)
    if offset > len(data) - size:
        raise ScoreImportError(f"The replay is truncated before {description}")
    return int(struct.unpack_from(format_code, data, offset)[0]), offset + size


def parse_osr_header(content: bytes) -> OsrReplayHeader:
    """Parse only the bounded prefix needed to verify a replay revision."""

    if len(content) < 7:
        raise ScoreImportError("The official replay header is truncated")
    mode_id = content[0]
    if mode_id not in {0, 1, 2, 3}:
        raise ScoreImportError("The official replay contains an unsupported ruleset")
    client_version = int.from_bytes(content[1:5], byteorder="little", signed=False)
    beatmap_checksum, offset = _read_osr_string(content, 5)
    normalised_checksum = beatmap_checksum.lower()
    if not MD5_PATTERN.fullmatch(normalised_checksum):
        raise ScoreImportError("The official replay does not contain a valid beatmap checksum")
    player_name = ""
    if offset < len(content):
        player_name, _ = _read_osr_string(content, offset)
    return OsrReplayHeader(
        ruleset=GameMode.from_int(mode_id),
        client_version=client_version,
        beatmap_checksum=normalised_checksum,
        player_name=player_name,
    )


def parse_uploaded_osr(content: bytes) -> ParsedOsrScore:
    """Parse bounded score metadata while leaving compressed replay frames opaque."""

    if not content:
        raise ScoreImportError("The uploaded replay is empty")
    if len(content) > MAX_REPLAY_BYTES:
        raise ScoreImportError("The uploaded replay exceeds the 32 MiB safety limit")

    header = parse_osr_header(content)
    _, offset = _read_osr_string(content, 5)
    player_name, offset = _read_osr_string(content, offset)
    replay_hash, offset = _read_osr_string(content, offset)
    counts: list[int] = []
    for description in ("300 count", "100 count", "50 count", "geki count", "katu count", "miss count"):
        value, offset = _read_osr_number(content, offset, "<H", description)
        counts.append(value)
    total_score, offset = _read_osr_number(content, offset, "<i", "total score")
    max_combo, offset = _read_osr_number(content, offset, "<H", "maximum combo")
    perfect_combo_raw, offset = _read_osr_number(content, offset, "<B", "perfect-combo flag")
    legacy_mods, offset = _read_osr_number(content, offset, "<I", "mods")
    _, offset = _read_osr_string(content, offset)  # life-bar graph
    ticks, offset = _read_osr_number(content, offset, "<q", "timestamp")
    replay_length, offset = _read_osr_number(content, offset, "<i", "replay frame length")

    if total_score < 0:
        raise ScoreImportError("The uploaded replay contains a negative total score")
    if perfect_combo_raw not in {0, 1}:
        raise ScoreImportError("The uploaded replay contains an invalid perfect-combo flag")
    if replay_length < 0 or replay_length > len(content) - offset:
        raise ScoreImportError("The uploaded replay contains an invalid frame length")
    offset += replay_length

    online_score_id: int | None = None
    if header.client_version >= 20140721:
        raw_online_id, offset = _read_osr_number(content, offset, "<q", "online score ID")
        online_score_id = raw_online_id if raw_online_id > 0 else None
    elif header.client_version >= 20121008:
        raw_online_id, offset = _read_osr_number(content, offset, "<i", "online score ID")
        online_score_id = raw_online_id if raw_online_id > 0 else None

    try:
        ended_at = datetime(1, 1, 1, tzinfo=UTC) + timedelta(microseconds=ticks // 10)
    except (OverflowError, ValueError) as exc:
        raise ScoreImportError("The uploaded replay contains an invalid timestamp") from exc
    if ended_at.year < 2007 or ended_at > utcnow() + timedelta(days=1):
        raise ScoreImportError("The uploaded replay contains an implausible timestamp")

    return ParsedOsrScore(
        header=OsrReplayHeader(
            ruleset=header.ruleset,
            client_version=header.client_version,
            beatmap_checksum=header.beatmap_checksum,
            player_name=player_name,
        ),
        replay_hash=replay_hash,
        n300=counts[0],
        n100=counts[1],
        n50=counts[2],
        ngeki=counts[3],
        nkatu=counts[4],
        nmiss=counts[5],
        total_score=total_score,
        max_combo=max_combo,
        perfect_combo=bool(perfect_combo_raw),
        legacy_mods=legacy_mods,
        ended_at=ended_at,
        online_score_id=online_score_id,
    )


def rewrite_osr_player_name(content: bytes, player_name: str) -> bytes:
    """Rewrite the username field used by osu! when an exported replay is opened."""

    # Validate the complete score payload before changing any bytes.
    parse_uploaded_osr(content)
    _, player_field_start = _read_osr_string(content, 5)
    _, player_field_end = _read_osr_string(content, player_field_start)
    return content[:player_field_start] + _encode_osr_string(player_name) + content[player_field_end:]


def _osr_replay_data_end(content: bytes) -> int:
    """Locate the byte immediately after the compressed replay-frame payload."""

    _, offset = _read_osr_string(content, 5)  # beatmap checksum
    _, offset = _read_osr_string(content, offset)  # player name
    _, offset = _read_osr_string(content, offset)  # replay checksum
    fixed_score_fields_size = struct.calcsize("<6HiHBI")
    if offset > len(content) - fixed_score_fields_size:
        raise ScoreImportError("The uploaded replay is truncated before score metadata")
    offset += fixed_score_fields_size
    _, offset = _read_osr_string(content, offset)  # life-bar graph
    _, offset = _read_osr_number(content, offset, "<q", "timestamp")
    replay_length, offset = _read_osr_number(content, offset, "<i", "replay frame length")
    if replay_length < 0 or replay_length > len(content) - offset:
        raise ScoreImportError("The uploaded replay contains an invalid frame length")
    return offset + replay_length


def _embedded_osr_score_metadata(content: bytes) -> dict[str, Any]:
    """Read lazer's optional compressed score-info block without trusting its declared size."""

    version = int.from_bytes(content[1:5], byteorder="little", signed=False)
    if version < 30_000_001:
        return {}
    offset = _osr_replay_data_end(content)
    _, offset = _read_osr_number(content, offset, "<q", "legacy online score ID")
    metadata_length, offset = _read_osr_number(content, offset, "<i", "score metadata length")
    if metadata_length <= 13 or metadata_length > len(content) - offset or metadata_length > MAX_REPLAY_METADATA_BYTES:
        return {}
    compressed = content[offset : offset + metadata_length]
    declared_size = int.from_bytes(compressed[5:13], byteorder="little", signed=False)
    if declared_size > MAX_REPLAY_METADATA_BYTES:
        return {}
    try:
        try:
            decoder = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE)
            raw = decoder.decompress(compressed, max_length=MAX_REPLAY_METADATA_BYTES + 1)
        except lzma.LZMAError:
            # Some liblzma versions reject a known-size stream that also has
            # an end marker (as emitted by our encoder). Let the marker end
            # that stream, then still enforce its original declared size.
            decoder = lzma.LZMADecompressor(format=lzma.FORMAT_ALONE)
            raw = decoder.decompress(
                compressed[:5] + b"\xff" * 8 + compressed[13:],
                max_length=MAX_REPLAY_METADATA_BYTES + 1,
            )
        if len(raw) != declared_size or len(raw) > MAX_REPLAY_METADATA_BYTES or not decoder.eof:
            return {}
        metadata = json.loads(raw.decode("ascii"))
    except (UnicodeDecodeError, json.JSONDecodeError, lzma.LZMAError):
        return {}
    return metadata if isinstance(metadata, dict) else {}


def _normalise_hit_result_counts(values: Mapping[HitResult, int] | Mapping[str, int]) -> dict[str, int]:
    return {
        key.value if isinstance(key, HitResult) else str(key): int(value)
        for key, value in values.items()
        if int(value) != 0
    }


def _database_score_statistics(score: Score) -> dict[str, int]:
    values: dict[HitResult, int] = {
        HitResult.MISS: score.nmiss,
        HitResult.MEH: score.n50,
        HitResult.OK: score.n100,
        HitResult.GREAT: score.n300,
        HitResult.PERFECT: score.ngeki,
        HitResult.GOOD: score.nkatu,
    }
    optional_fields = {
        HitResult.LARGE_TICK_MISS: score.nlarge_tick_miss,
        HitResult.LARGE_TICK_HIT: score.nlarge_tick_hit,
        HitResult.SLIDER_TAIL_HIT: score.nslider_tail_hit,
        HitResult.SMALL_TICK_HIT: score.nsmall_tick_hit,
        HitResult.SMALL_TICK_MISS: score.nsmall_tick_miss,
    }
    values.update({result: value for result, value in optional_fields.items() if value is not None})
    return _normalise_hit_result_counts(values)


def rewrite_osr_server_score_metadata(
    content: bytes,
    score: ServerReplayScoreMetadata,
    player_name: str,
) -> bytes:
    """Attach local score/user IDs so a downloaded replay resolves as an online server score in lazer."""

    parse_uploaded_osr(content)
    existing = _embedded_osr_score_metadata(content)
    rewritten = rewrite_osr_player_name(content, player_name)
    replay_data_end = _osr_replay_data_end(rewritten)
    statistics = existing.get("statistics")
    maximum_statistics = existing.get("maximum_statistics")
    metadata = {
        "client_version": existing.get("client_version") or score.client_version,
        "rank": score.rank,
        "user_id": score.user_id,
        "online_id": score.online_score_id,
        "mods": score.mods,
        "statistics": statistics if isinstance(statistics, dict) else score.statistics,
        "maximum_statistics": (
            maximum_statistics if isinstance(maximum_statistics, dict) else score.maximum_statistics
        ),
        "total_score_without_mods": score.total_score_without_mods or None,
        "pauses": existing.get("pauses") if isinstance(existing.get("pauses"), list) else [],
    }
    encoded_metadata = json.dumps(metadata, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    compressed_metadata = lzma.compress(encoded_metadata, format=lzma.FORMAT_ALONE)
    # Python's FORMAT_ALONE encoder may use an unknown-size marker. osu!'s
    # decoder expects the standard 5-byte properties followed by an explicit
    # 8-byte uncompressed size, so write that size exactly as lazer does.
    compressed_metadata = (
        compressed_metadata[:5]
        + len(encoded_metadata).to_bytes(8, byteorder="little", signed=False)
        + compressed_metadata[13:]
    )

    result = bytearray(rewritten[:replay_data_end])
    current_version = int.from_bytes(result[1:5], byteorder="little", signed=False)
    result[1:5] = max(current_version, LAZER_REPLAY_METADATA_VERSION).to_bytes(4, byteorder="little", signed=False)
    result.extend(struct.pack("<q", -1))
    result.extend(struct.pack("<i", len(compressed_metadata)))
    result.extend(compressed_metadata)
    return bytes(result)


def _uploaded_statistics(parsed: ParsedOsrScore) -> tuple[dict[HitResult, int], dict[HitResult, int], float]:
    mode = parsed.header.ruleset
    if mode is GameMode.FRUITS:
        statistics = {
            HitResult.GREAT: parsed.n300,
            HitResult.LARGE_TICK_HIT: parsed.n100,
            HitResult.SMALL_TICK_HIT: parsed.n50,
            HitResult.MISS: parsed.nmiss,
            HitResult.SMALL_TICK_MISS: parsed.nkatu,
        }
        total = parsed.n300 + parsed.n100 + parsed.n50 + parsed.nmiss + parsed.nkatu
        earned = parsed.n300 + parsed.n100 + parsed.n50
        maximum_statistics = {HitResult.GREAT: total}
        accuracy = earned / total if total else 1.0
    elif mode is GameMode.MANIA:
        statistics = {
            HitResult.PERFECT: parsed.ngeki,
            HitResult.GREAT: parsed.n300,
            HitResult.GOOD: parsed.nkatu,
            HitResult.OK: parsed.n100,
            HitResult.MEH: parsed.n50,
            HitResult.MISS: parsed.nmiss,
        }
        total = sum(statistics.values())
        earned = 305 * parsed.ngeki + 300 * parsed.n300 + 200 * parsed.nkatu + 100 * parsed.n100 + 50 * parsed.n50
        maximum_statistics = {HitResult.PERFECT: total}
        accuracy = earned / (305 * total) if total else 1.0
    else:
        statistics = {
            HitResult.GREAT: parsed.n300,
            HitResult.OK: parsed.n100,
            HitResult.MEH: parsed.n50,
            HitResult.MISS: parsed.nmiss,
        }
        total = parsed.n300 + parsed.n100 + parsed.n50 + parsed.nmiss
        ok_weight = 150 if mode is GameMode.TAIKO else 100
        earned = 300 * parsed.n300 + ok_weight * parsed.n100 + 50 * parsed.n50
        maximum_statistics = {HitResult.GREAT: total}
        accuracy = earned / (300 * total) if total else 1.0
    return statistics, maximum_statistics, accuracy


def _uploaded_rank(parsed: ParsedOsrScore, accuracy: float, mods: list[APIMod]) -> Rank:
    hidden = any(mod["acronym"] in {"HD", "FL"} for mod in mods)
    if parsed.header.ruleset in {GameMode.OSU, GameMode.TAIKO}:
        if accuracy == 1:
            return Rank.XH if hidden else Rank.X
        if accuracy >= 0.95 and parsed.nmiss == 0:
            return Rank.SH if hidden else Rank.S
        thresholds = ((0.90, Rank.A), (0.80, Rank.B), (0.70, Rank.C))
    elif parsed.header.ruleset is GameMode.FRUITS:
        if accuracy == 1:
            return Rank.XH if hidden else Rank.X
        if accuracy >= 0.98:
            return Rank.SH if hidden else Rank.S
        thresholds = ((0.94, Rank.A), (0.90, Rank.B), (0.85, Rank.C))
    else:
        if not (parsed.nkatu or parsed.n100 or parsed.n50 or parsed.nmiss):
            return Rank.XH if hidden else Rank.X
        if accuracy >= 0.95:
            return Rank.SH if hidden else Rank.S
        thresholds = ((0.90, Rank.A), (0.80, Rank.B), (0.70, Rank.C))
    return next((rank for threshold, rank in thresholds if accuracy >= threshold), Rank.D)


def prepare_uploaded_osr_score(
    content: bytes,
    beatmap: Mapping[str, Any],
    *,
    filename: str | None = None,
) -> PreparedOfficialScore:
    """Convert a validated .osr and official beatmap lookup into the normal import pipeline."""

    parsed = parse_uploaded_osr(content)
    try:
        beatmap_id = int(beatmap["id"])
        beatmapset_id = int(beatmap["beatmapset_id"])
    except (KeyError, TypeError, ValueError) as exc:
        raise ScoreImportError("Official beatmap lookup returned incomplete metadata") from exc
    if beatmap_id <= 0 or beatmapset_id <= 0:
        raise ScoreImportError("Official beatmap lookup returned invalid metadata")

    digest = hashlib.sha256(content).hexdigest()
    numeric_source_id = parsed.online_score_id or (int(digest[:15], 16) + 1)
    source_score_id = str(parsed.online_score_id) if parsed.online_score_id is not None else digest[:16]
    statistics, maximum_statistics, accuracy = _uploaded_statistics(parsed)
    try:
        mods = int_to_mods(parsed.legacy_mods)
    except KeyError as exc:
        raise ScoreImportError("The uploaded replay contains unsupported legacy mods") from exc
    # The legacy bitmask cannot represent custom speeds, Difficulty Adjust or
    # their settings. Lazer exports carry the complete configuration here.
    embedded = _embedded_osr_score_metadata(content)
    if isinstance(embedded.get("mods"), list):
        mods = _normalise_mods(embedded["mods"])
    rank = _uploaded_rank(parsed, accuracy, mods)
    beatmapset_value = beatmap.get("beatmapset")
    beatmapset = beatmapset_value if isinstance(beatmapset_value, dict) else {}
    raw: dict[str, Any] = {
        "id": numeric_source_id,
        "beatmap_id": beatmap_id,
        "total_score": parsed.total_score,
        "total_score_without_mods": parsed.total_score,
        "classic_total_score": parsed.total_score,
        "accuracy": accuracy,
        "max_combo": parsed.max_combo,
        "ruleset_id": int(parsed.header.ruleset),
        "passed": True,
        "rank": rank.value,
        "mods": mods,
        "statistics": {key.value: value for key, value in statistics.items()},
        "maximum_statistics": {key.value: value for key, value in maximum_statistics.items()},
        "user": {"username": parsed.header.player_name},
        "ended_at": parsed.ended_at.isoformat(),
        "build_id": parsed.header.client_version or None,
        "beatmap": {
            "id": beatmap_id,
            "beatmapset_id": beatmapset_id,
            "checksum": parsed.header.beatmap_checksum,
            "version": beatmap.get("version"),
        },
        "beatmapset": {
            "id": beatmapset_id,
            "artist": beatmapset.get("artist"),
            "title": beatmapset.get("title"),
        },
        "has_replay": True,
        "uploaded_replay_sha256": digest,
        "uploaded_filename": filename,
    }
    prepared = prepare_official_score(OfficialScoreReference(numeric_source_id), raw)
    prepared.source = "uploaded_osr"
    prepared.source_score_id = source_score_id
    prepared.source_url = None
    prepared.source_fingerprint = f"uploaded_osr:sha256:{digest}"
    return prepared


async def _fetch_official_replay(
    fetcher: Fetcher,
    prepared: PreparedOfficialScore,
) -> tuple[DownloadedOfficialReplay | None, str]:
    if not prepared.raw.get("has_replay"):
        return None, "not_advertised"
    try:
        await fetcher.ensure_valid_access_token()
        async with (
            AsyncClient(timeout=45.0, follow_redirects=True) as client,
            client.stream(
                "GET",
                prepared.reference.replay_path,
                headers={
                    **fetcher.header,
                    "x-api-version": OFFICIAL_API_VERSION,
                    "Accept": "application/octet-stream",
                    "Accept-Encoding": "identity",
                },
            ) as response,
        ):
            if response.status_code in {403, 404}:
                return None, "unavailable"
            response.raise_for_status()
            raw_content_length = response.headers.get("content-length")
            try:
                content_length = int(raw_content_length) if raw_content_length is not None else None
            except ValueError:
                content_length = None
            if content_length is not None and content_length > MAX_REPLAY_BYTES:
                logger.warning(
                    "Official replay is too large for score {} (declared {} bytes)",
                    prepared.reference.score_id,
                    content_length,
                )
                return None, "too_large"

            content = bytearray()
            # Raw iteration avoids an attacker-controlled Content-Encoding
            # expanding one decoded chunk before our byte limit can run.
            async for chunk in response.aiter_raw():
                if len(content) + len(chunk) > MAX_REPLAY_BYTES:
                    logger.warning(
                        "Official replay exceeded the size limit for score {}",
                        prepared.reference.score_id,
                    )
                    return None, "too_large"
                content.extend(chunk)
    except (HTTPError, TokenAuthError):
        logger.warning("Official replay download failed for score {}", prepared.reference.score_id)
        return None, "unavailable"
    if not content:
        logger.warning("Official replay was empty for score {}", prepared.reference.score_id)
        return None, "empty"
    replay_content = bytes(content)
    try:
        header = parse_osr_header(replay_content)
    except ScoreImportError as exc:
        logger.warning("Official replay validation failed for score {}: {}", prepared.reference.score_id, exc)
        return None, "invalid"
    return DownloadedOfficialReplay(content=replay_content, header=header), "downloaded"


async def verify_official_score_revision(
    fetcher: Fetcher,
    prepared: PreparedOfficialScore,
    *,
    beatmapset_id: int,
    current_checksum: str,
) -> ScoreRevisionVerification:
    """Download and validate replay evidence against freshly refreshed metadata."""

    normalised_current_checksum = current_checksum.lower()
    if not MD5_PATTERN.fullmatch(normalised_current_checksum):
        raise ScoreImportError("The refreshed local beatmap has an invalid checksum")
    replay, replay_status = await _fetch_official_replay(fetcher, prepared)
    if replay is not None:
        if replay.header.ruleset != prepared.ruleset:
            replay_status = "ruleset_mismatch"
        elif replay.header.beatmap_checksum != normalised_current_checksum:
            replay_status = "checksum_mismatch"
        else:
            replay_status = "verified"
    return ScoreRevisionVerification(
        beatmap_id=prepared.beatmap_id,
        beatmapset_id=beatmapset_id,
        current_checksum=normalised_current_checksum,
        source_checksum=prepared.source_checksum,
        replay=replay,
        replay_status=replay_status,
    )


def verify_uploaded_score_revision(
    prepared: PreparedOfficialScore,
    replay_content: bytes,
    *,
    beatmapset_id: int,
    current_checksum: str,
) -> ScoreRevisionVerification:
    """Use the uploaded replay itself as revision evidence without another download."""

    header = parse_osr_header(replay_content)
    replay_status = "verified"
    if header.ruleset != prepared.ruleset:
        replay_status = "ruleset_mismatch"
    elif header.beatmap_checksum != current_checksum.lower():
        replay_status = "checksum_mismatch"
    return ScoreRevisionVerification(
        beatmap_id=prepared.beatmap_id,
        beatmapset_id=beatmapset_id,
        current_checksum=current_checksum.lower(),
        source_checksum=header.beatmap_checksum,
        replay=DownloadedOfficialReplay(content=replay_content, header=header),
        replay_status=replay_status,
    )


def _source_snapshot(
    prepared: PreparedOfficialScore,
    verification: ScoreRevisionVerification,
    *,
    allow_unverified_revision: bool,
) -> dict[str, Any]:
    keys = {
        "accuracy",
        "beatmap_id",
        "build_id",
        "classic_total_score",
        "created_at",
        "ended_at",
        "has_replay",
        "id",
        "legacy_score_id",
        "legacy_total_score",
        "max_combo",
        "maximum_statistics",
        "mods",
        "passed",
        "pp",
        "rank",
        "ruleset_id",
        "started_at",
        "statistics",
        "total_score",
        "total_score_without_mods",
        "type",
        "user_id",
    }
    snapshot = {key: prepared.raw[key] for key in keys if key in prepared.raw}
    snapshot["source_url"] = prepared.public_preview()["source_url"]
    if prepared.source == "uploaded_osr":
        snapshot["uploaded_replay"] = {
            "sha256": prepared.raw.get("uploaded_replay_sha256"),
            "filename": prepared.raw.get("uploaded_filename"),
            "original_username": prepared.source_username,
            "client_version": prepared.build_id,
        }
    snapshot["revision_verification"] = verification.snapshot(
        prepared,
        allow_unverified_revision=allow_unverified_revision,
    )
    return snapshot


async def import_official_score(
    session: AsyncSession,
    redis: Redis,
    fetcher: Fetcher,
    *,
    target_user: User,
    imported_by: User,
    prepared: PreparedOfficialScore,
    verification: ScoreRevisionVerification,
    reason: str,
    include_replay: bool,
    allow_unverified_revision: bool = False,
    commit: bool = True,
) -> ImportedOfficialScore:
    """Materialise an official score locally and run the normal PP/statistics pipeline.

    Set ``commit`` to false when a caller needs to add records, such as an
    administrative audit event, to the same database transaction. Replay
    bytes and a possible PP retry are returned for post-commit finalization;
    this function deliberately performs neither of those external writes.
    """

    target_user_id = target_user.id
    imported_by_user_id = imported_by.id

    if verification.beatmap_id != prepared.beatmap_id:
        raise ScoreImportError("The beatmap revision check does not belong to this score")

    duplicate = (
        await session.exec(
            select(ScoreImport).where(
                ScoreImport.source_fingerprint == prepared.source_fingerprint,
            )
        )
    ).first()
    if duplicate is not None:
        local_score = f" as local score {duplicate.score_id}" if duplicate.score_id is not None else ""
        label = "replay" if prepared.source == "uploaded_osr" else "official score"
        raise ScoreImportConflictError(f"This {label} was already imported{local_score}")

    locked_beatmapset = (
        await session.exec(
            select(Beatmapset)
            .where(Beatmapset.id == verification.beatmapset_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if locked_beatmapset is None:
        raise ScoreImportError(f"Beatmapset {verification.beatmapset_id} is unavailable")
    beatmap = (
        await session.exec(
            select(Beatmap)
            .where(Beatmap.id == prepared.beatmap_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if beatmap is None or beatmap.beatmapset_id != verification.beatmapset_id:
        raise ScoreImportError(f"Beatmap {prepared.beatmap_id} is unavailable after refresh")
    if beatmap.checksum.lower() != verification.current_checksum:
        raise ScoreImportConflictError("The beatmap revision changed during import; check the score again")

    score_mode = prepared.ruleset.to_special_mode(prepared.info.mods)
    statistics = (
        await session.exec(
            select(UserStatistics)
            .where(
                UserStatistics.user_id == target_user_id,
                UserStatistics.mode == score_mode,
            )
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if statistics is None:
        raise ScoreImportError(f"Target user has no statistics row for {score_mode.value}")

    start = prepared.started_at or prepared.ended_at - timedelta(seconds=max(1, beatmap.total_length))
    use_normal_ranking = verification.allows_normal_ranking(
        prepared,
        allow_unverified_revision=allow_unverified_revision,
    )
    played_checksum = verification.replay_checksum or prepared.source_checksum or verification.current_checksum
    token_checksum = verification.current_checksum if use_normal_ranking else played_checksum
    token = ScoreToken(
        user_id=target_user_id,
        beatmap_id=beatmap.id,
        beatmap_checksum=token_checksum,
        ruleset_id=prepared.ruleset,
        client_version=f"official-import:{prepared.build_id or 'unknown'}"[:50],
        created_at=start,
        updated_at=prepared.ended_at,
    )
    session.add(token)
    await session.flush()

    score = await process_score(target_user, beatmap, token, prepared.info, session)
    token.score_id = score.id
    score.started_at = start
    score.ended_at = prepared.ended_at
    score.build_id = prepared.build_id
    score.classic_total_score = prepared.classic_total_score
    score.total_score = prepared.source_total_score
    score.total_score_without_mods = prepared.source_total_score_without_mods
    score.client_version = token.client_version
    score.has_replay = False
    if not use_normal_ranking:
        score.pp = 0
        score.ranked = False
        score.leaderboard_eligible = False
        score.ranked_score_eligible = False
    provenance = ScoreImport(
        score_id=score.id,
        target_user_id=target_user_id,
        imported_by_user_id=imported_by_user_id,
        source=prepared.source,
        source_fingerprint=prepared.source_fingerprint,
        source_ruleset=prepared.ruleset.value,
        source_score_id=prepared.source_score_id or str(prepared.reference.score_id),
        source_user_id=prepared.source_user_id,
        source_username=prepared.source_username,
        reason=reason,
        replay_imported=False,
        source_snapshot=_source_snapshot(
            prepared,
            verification,
            allow_unverified_revision=allow_unverified_revision,
        ),
    )
    session.add(provenance)

    try:
        policy = await get_effective_beatmap_policy(session, beatmap, for_update=True)
        pp_ready = await process_user(
            session,
            redis,
            fetcher,
            target_user,
            score,
            token.id,
            beatmap.total_length,
            policy,
            queue_pp_failure=False,
            emit_playtime_event=False,
            create_events=False,
            commit=False,
        )
        provenance.source_snapshot = {
            **provenance.source_snapshot,
            "pp_pending": not pp_ready,
        }
        session.add(provenance)
        if commit:
            await session.commit()
        else:
            await session.flush()
    except Exception:
        await session.rollback()
        raise

    if commit:
        await session.refresh(score)
        await session.refresh(provenance)
    downloaded_replay = verification.replay
    replay_content = (
        downloaded_replay.content
        if include_replay and downloaded_replay is not None and downloaded_replay.header.ruleset == prepared.ruleset
        else None
    )
    if replay_content is not None:
        replay_content = rewrite_osr_player_name(replay_content, target_user.username)
    replay_unavailable = include_replay and bool(prepared.raw.get("has_replay")) and replay_content is None
    return ImportedOfficialScore(
        score=score,
        provenance=provenance,
        replay_content=replay_content,
        replay_unavailable=replay_unavailable,
        pp_pending=not pp_ready,
    )


__all__ = [
    "DownloadedOfficialReplay",
    "ImportedOfficialScore",
    "OfficialScoreReference",
    "OsrReplayHeader",
    "ParsedOsrScore",
    "PreparedOfficialScore",
    "ScoreImportConflictError",
    "ScoreImportError",
    "ScoreImportNotFoundError",
    "ScoreImportUpstreamError",
    "ScoreRevisionVerification",
    "ServerReplayScoreMetadata",
    "fetch_official_score",
    "import_official_score",
    "parse_official_score_reference",
    "parse_osr_header",
    "parse_uploaded_osr",
    "prepare_official_score",
    "prepare_uploaded_osr_score",
    "rewrite_osr_player_name",
    "rewrite_osr_server_score_metadata",
    "verify_official_score_revision",
    "verify_uploaded_score_revision",
]
