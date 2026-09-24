from copy import deepcopy
from datetime import UTC, datetime
import struct
from types import SimpleNamespace
from typing import Any, Self, cast
import unittest
from unittest.mock import AsyncMock, patch

from app.models.mods import init_mods
from app.models.score import GameMode, HitResult, Rank
from app.service.score_import_service import (
    DownloadedOfficialReplay,
    OfficialScoreReference,
    OsrReplayHeader,
    ScoreImportError,
    ScoreRevisionVerification,
    ServerReplayScoreMetadata,
    _embedded_osr_score_metadata,
    parse_official_score_reference,
    parse_osr_header,
    parse_uploaded_osr,
    prepare_official_score,
    prepare_uploaded_osr_score,
    rewrite_osr_player_name,
    rewrite_osr_server_score_metadata,
    verify_official_score_revision,
    verify_uploaded_score_revision,
)


def assert_import_error(action) -> None:
    try:
        action()
    except ScoreImportError:
        return
    raise AssertionError("Expected ScoreImportError to be raised")


def modern_score() -> dict:
    return {
        "id": 987654321,
        "beatmap_id": 12345,
        "total_score": 765432,
        "total_score_without_mods": 700000,
        "classic_total_score": 654321,
        "accuracy": 0.9876,
        "max_combo": 420,
        "ruleset_id": 0,
        "passed": True,
        "rank": "S",
        "mods": [{"acronym": "HD"}],
        "statistics": {
            "great": 300,
            "ok": 5,
            "meh": 1,
            "miss": 0,
            "large_tick_hit": 100,
        },
        "maximum_statistics": {"great": 306, "large_tick_hit": 100},
        "user_id": 99,
        "user": {"id": 99, "username": "official-player"},
        "started_at": "2026-09-01T12:30:00+00:00",
        "ended_at": "2026-09-01T12:34:56+00:00",
        "build_id": 20260831,
        "beatmap": {"beatmapset_id": 54321, "checksum": "ABCDEF"},
        "beatmapset": {"id": 54321, "artist": "Artist", "title": "Title"},
        "has_replay": True,
        "pp": 321.45,
    }


def osr_header(mode: int, checksum: str, *, marker: int = 0x0B) -> bytes:
    encoded = checksum.encode()
    assert len(encoded) < 128
    return bytes([mode]) + (20260831).to_bytes(4, "little") + bytes([marker, len(encoded)]) + encoded


def osr_string(value: str) -> bytes:
    encoded = value.encode()
    assert len(encoded) < 128
    return b"\x0b" + bytes([len(encoded)]) + encoded if encoded else b"\x00"


def osr_file(*, mode: int = 0, checksum: str = "abcdef0123456789abcdef0123456789", username: str = "old-name") -> bytes:
    ended_at = datetime(2026, 8, 31, 12, 34, 56, tzinfo=UTC)
    epoch = datetime(1, 1, 1, tzinfo=UTC)
    elapsed = ended_at - epoch
    ticks = (elapsed.days * 86400 + elapsed.seconds) * 10_000_000 + elapsed.microseconds * 10
    return b"".join(
        (
            bytes([mode]),
            struct.pack("<i", 20260831),
            osr_string(checksum),
            osr_string(username),
            osr_string("replay-md5"),
            struct.pack("<6H", 300, 5, 1, 0, 0, 2),
            struct.pack("<iHBI", 765432, 420, 0, 1 << 3),
            osr_string(""),
            struct.pack("<q", ticks),
            struct.pack("<i", 4),
            b"lzma",
            struct.pack("<q", 987654321),
        )
    )


class OfficialScoreReferenceTests(unittest.TestCase):
    def test_accepts_numeric_id_and_normalises_catch_ruleset(self) -> None:
        reference = parse_official_score_reference(" 123456 ", "catch")

        assert reference == OfficialScoreReference(score_id=123456, ruleset="fruits")
        assert reference.api_path == "https://osu.ppy.sh/api/v2/scores/fruits/123456"
        assert reference.replay_path == "https://osu.ppy.sh/api/v2/scores/fruits/123456/download"

    def test_accepts_current_and_legacy_official_score_urls(self) -> None:
        current = parse_official_score_reference("https://osu.ppy.sh/scores/123456?foo=bar#details")
        legacy = parse_official_score_reference("osu.ppy.sh/scores/mania/654321")
        www = parse_official_score_reference("https://www.osu.ppy.sh/scores/taiko/42")

        assert current == OfficialScoreReference(score_id=123456)
        assert legacy == OfficialScoreReference(score_id=654321, ruleset="mania")
        assert www == OfficialScoreReference(score_id=42, ruleset="taiko")

    def test_rejects_non_official_or_malformed_references(self) -> None:
        invalid_values = [
            "",
            "0",
            "not-a-score",
            "http://osu.ppy.sh/scores/123",
            "https://evil.example/scores/123",
            "https://osu.ppy.sh/users/123",
            "https://osu.ppy.sh/scores/osu/not-a-number",
            "https://osu.ppy.sh/scores/osu/0",
            "https://osu.ppy.sh/scores/osu/123/extra",
        ]

        for value in invalid_values:
            with self.subTest(value=value):
                assert_import_error(lambda value=value: parse_official_score_reference(value))

    def test_rejects_unknown_or_conflicting_rulesets(self) -> None:
        assert_import_error(lambda: parse_official_score_reference("123", "relax"))
        assert_import_error(lambda: parse_official_score_reference("https://osu.ppy.sh/scores/123", "osu"))
        assert_import_error(lambda: parse_official_score_reference("https://osu.ppy.sh/scores/osu/123", "mania"))


class PrepareOfficialScoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        init_mods()

    def test_prepares_modern_score_payload(self) -> None:
        prepared = prepare_official_score(OfficialScoreReference(987654321), modern_score())

        assert prepared.reference == OfficialScoreReference(987654321)
        assert prepared.beatmap_id == 12345
        assert prepared.ruleset is GameMode.OSU
        assert prepared.source_user_id == 99
        assert prepared.source_username == "official-player"
        assert prepared.source_total_score == 765432
        assert prepared.source_total_score_without_mods == 700000
        assert prepared.classic_total_score == 654321
        assert prepared.source_checksum == "abcdef"
        assert prepared.source_fingerprint == "official_osu:lazer:987654321"
        assert prepared.build_id == 20260831
        assert prepared.started_at is not None
        assert prepared.ended_at.isoformat() == "2026-09-01T12:34:56+00:00"
        assert prepared.info.rank is Rank.S
        assert prepared.info.statistics[HitResult.GREAT] == 300
        assert prepared.info.statistics[HitResult.LARGE_TICK_HIT] == 100
        assert prepared.info.maximum_statistics[HitResult.GREAT] == 306
        assert prepared.info.mods == [{"acronym": "HD"}]

        preview = prepared.public_preview()
        assert preview["source"] == "official_osu"
        assert preview["source_url"] == "https://osu.ppy.sh/scores/987654321"
        assert preview["artist"] == "Artist"
        assert preview["title"] == "Title"
        assert preview["pp_official"] == 321.45
        assert preview["has_replay"] is True

    def test_prepares_legacy_score_payload(self) -> None:
        raw = {
            "id": 24680,
            "beatmap_id": 13579,
            "score": 1234567,
            "legacy_total_score": 1200000,
            "accuracy": 0.95,
            "max_combo": 250,
            "mode": "catch",
            "rank": "a",
            "mods": [],
            "statistics": {
                "count_300": "90",
                "count_100": 7,
                "count_50": 2,
                "count_miss": 1,
                "count_geki": 11,
                "count_katu": 5,
            },
            "user": {"id": 77, "username": "legacy-player"},
            "created_at": "2020-05-06T07:08:09+00:00",
        }

        prepared = prepare_official_score(OfficialScoreReference(24680, "fruits"), raw)

        assert prepared.ruleset is GameMode.FRUITS
        assert prepared.source_total_score == 1234567
        assert prepared.source_total_score_without_mods == 1234567
        assert prepared.classic_total_score == 1200000
        assert prepared.source_user_id == 77
        assert prepared.source_username == "legacy-player"
        assert prepared.source_fingerprint == "official_osu:legacy:fruits:24680"
        assert prepared.started_at is None
        assert prepared.ended_at.isoformat() == "2020-05-06T07:08:09+00:00"
        assert prepared.info.rank is Rank.A
        assert prepared.info.statistics == {
            HitResult.GREAT: 90,
            HitResult.OK: 7,
            HitResult.MEH: 2,
            HitResult.MISS: 1,
            HitResult.PERFECT: 11,
            HitResult.GOOD: 5,
        }

    def test_rejects_failed_play(self) -> None:
        raw = modern_score()
        raw["passed"] = False

        assert_import_error(lambda: prepare_official_score(OfficialScoreReference(987654321), raw))

    def test_rejects_invalid_or_inconsistent_payloads(self) -> None:
        cases = []

        incomplete = modern_score()
        del incomplete["beatmap_id"]
        cases.append(incomplete)

        wrong_id = modern_score()
        wrong_id["id"] = 1
        cases.append(wrong_id)

        invalid_accuracy = modern_score()
        invalid_accuracy["accuracy"] = 1.1
        cases.append(invalid_accuracy)

        invalid_ruleset = modern_score()
        invalid_ruleset["ruleset_id"] = 9
        cases.append(invalid_ruleset)

        invalid_rank = modern_score()
        invalid_rank["rank"] = "SSS"
        cases.append(invalid_rank)

        invalid_timestamp = modern_score()
        invalid_timestamp["ended_at"] = "not-a-date"
        cases.append(invalid_timestamp)

        negative_statistics = modern_score()
        negative_statistics["statistics"] = {"miss": -1}
        cases.append(negative_statistics)

        oversized_total = modern_score()
        oversized_total["total_score"] = 2**31
        cases.append(oversized_total)

        for raw in cases:
            with self.subTest(raw=raw):
                assert_import_error(
                    lambda raw=deepcopy(raw): prepare_official_score(OfficialScoreReference(987654321), raw)
                )

    def test_rejects_ruleset_mismatch_between_url_and_payload(self) -> None:
        assert_import_error(lambda: prepare_official_score(OfficialScoreReference(987654321, "mania"), modern_score()))

    def test_modern_and_legacy_urls_share_one_canonical_fingerprint(self) -> None:
        modern_raw = modern_score()
        modern_raw["legacy_score_id"] = 24680
        modern = prepare_official_score(OfficialScoreReference(987654321), modern_raw)

        legacy_raw = modern_score()
        legacy_raw["legacy_score_id"] = 24680
        legacy = prepare_official_score(OfficialScoreReference(24680, "osu"), legacy_raw)

        assert modern.source_fingerprint == "official_osu:legacy:osu:24680"
        assert legacy.source_fingerprint == modern.source_fingerprint

    def test_rejects_invalid_legacy_score_id(self) -> None:
        for legacy_score_id in (0, "not-an-id"):
            with self.subTest(legacy_score_id=legacy_score_id):
                raw = modern_score()
                raw["legacy_score_id"] = legacy_score_id
                assert_import_error(lambda: prepare_official_score(OfficialScoreReference(987654321), raw))


class ReplayRevisionVerificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        init_mods()

    def test_parses_bounded_osr_header_and_normalises_checksum(self) -> None:
        checksum = "ABCDEF0123456789ABCDEF0123456789"

        parsed = parse_osr_header(osr_header(3, checksum))

        assert parsed.ruleset is GameMode.MANIA
        assert parsed.client_version == 20260831
        assert parsed.beatmap_checksum == checksum.lower()

    def test_rejects_malformed_or_unsupported_osr_headers(self) -> None:
        checksum = "abcdef0123456789abcdef0123456789"
        invalid = [
            b"",
            osr_header(9, checksum),
            osr_header(0, checksum, marker=0x01),
            osr_header(0, "not-an-md5"),
            bytes([0]) + (1).to_bytes(4, "little") + bytes([0x0B, 65]) + (b"a" * 65),
            bytes([0]) + (1).to_bytes(4, "little") + b"\x0b\x80\x80\x80\x80\x80",
        ]

        for replay in invalid:
            with self.subTest(replay=replay):
                assert_import_error(lambda replay=replay: parse_osr_header(replay))

    def test_source_metadata_match_alone_does_not_verify_played_revision(self) -> None:
        prepared = prepare_official_score(OfficialScoreReference(987654321), modern_score())
        current_checksum = "abcdef0123456789abcdef0123456789"
        prepared.source_checksum = current_checksum
        old_checksum = "11111111111111111111111111111111"
        replay = DownloadedOfficialReplay(
            content=osr_header(0, old_checksum),
            header=OsrReplayHeader(
                ruleset=GameMode.OSU,
                client_version=20260831,
                beatmap_checksum=old_checksum,
            ),
        )
        verification = ScoreRevisionVerification(
            beatmap_id=prepared.beatmap_id,
            beatmapset_id=54321,
            current_checksum=current_checksum,
            source_checksum=current_checksum,
            replay=replay,
            replay_status="checksum_mismatch",
        )

        assert verification.current_metadata_matches is True
        assert verification.revision_verified_for(prepared) is False
        safe_snapshot = verification.snapshot(prepared, allow_unverified_revision=False)
        override_snapshot = verification.snapshot(prepared, allow_unverified_revision=True)
        assert safe_snapshot["ranking_outcome"] == "visible_unranked"
        assert safe_snapshot["allow_unverified_revision"] is False
        assert override_snapshot["ranking_outcome"] == "normal"
        assert override_snapshot["allow_unverified_revision"] is True

    def test_revision_requires_matching_replay_ruleset_and_current_checksum(self) -> None:
        prepared = prepare_official_score(OfficialScoreReference(987654321), modern_score())
        checksum = "abcdef0123456789abcdef0123456789"

        def verification(mode: GameMode) -> ScoreRevisionVerification:
            return ScoreRevisionVerification(
                beatmap_id=prepared.beatmap_id,
                beatmapset_id=54321,
                current_checksum=checksum,
                source_checksum=prepared.source_checksum,
                replay=DownloadedOfficialReplay(
                    content=osr_header(int(mode), checksum),
                    header=OsrReplayHeader(ruleset=mode, client_version=20260831, beatmap_checksum=checksum),
                ),
                replay_status="downloaded",
            )

        assert verification(GameMode.OSU).revision_verified_for(prepared) is True
        assert verification(GameMode.TAIKO).revision_verified_for(prepared) is False


class UploadedReplayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        init_mods()

    def test_parses_complete_replay_metadata(self) -> None:
        parsed = parse_uploaded_osr(osr_file())

        assert parsed.header.ruleset is GameMode.OSU
        assert parsed.header.player_name == "old-name"
        assert parsed.total_score == 765432
        assert parsed.max_combo == 420
        assert parsed.n300 == 300
        assert parsed.n100 == 5
        assert parsed.n50 == 1
        assert parsed.nmiss == 2
        assert parsed.online_score_id == 987654321
        assert parsed.ended_at == datetime(2026, 8, 31, 12, 34, 56, tzinfo=UTC)

    def test_rewrites_only_replay_player_identity(self) -> None:
        original = osr_file(username="official-player")
        rewritten = rewrite_osr_player_name(original, "mindblock")
        parsed = parse_uploaded_osr(rewritten)

        assert parsed.header.player_name == "mindblock"
        assert parsed.total_score == 765432
        assert parsed.online_score_id == 987654321
        assert b"official-player" not in rewritten

    def test_embeds_local_user_and_score_ids_into_legacy_official_replay(self) -> None:
        metadata = ServerReplayScoreMetadata(
            online_score_id=67,
            user_id=1_500_000_001,
            client_version="2026.821.0-tachyon-windows",
            rank="S",
            mods=[{"acronym": "HD"}, {"acronym": "CL"}],
            statistics={"great": 300, "ok": 5, "meh": 1, "miss": 2},
            maximum_statistics={"great": 308},
            total_score_without_mods=765432,
        )

        rewritten = rewrite_osr_server_score_metadata(
            osr_file(username="official-player"),
            metadata,
            "mindblock",
        )
        header = parse_osr_header(rewritten)
        embedded = _embedded_osr_score_metadata(rewritten)

        assert header.client_version == 30_000_019
        assert header.player_name == "mindblock"
        assert embedded["online_id"] == 67
        assert embedded["user_id"] == 1_500_000_001
        assert embedded["mods"] == [{"acronym": "HD"}, {"acronym": "CL"}]
        assert embedded["statistics"] == {"great": 300, "ok": 5, "meh": 1, "miss": 2}

    def test_replaces_foreign_ids_but_preserves_existing_lazer_statistics(self) -> None:
        official = ServerReplayScoreMetadata(
            online_score_id=7_346_217_987,
            user_id=7_562_902,
            client_version="2026.821.0-tachyon-windows",
            rank="S",
            mods=[],
            statistics={"great": 2838, "ok": 18, "ignore_hit": 626},
            maximum_statistics={"great": 2856, "ignore_hit": 626},
            total_score_without_mods=988924,
        )
        official_replay = rewrite_osr_server_score_metadata(osr_file(), official, "official-player")
        local = ServerReplayScoreMetadata(
            online_score_id=10,
            user_id=1_500_000_001,
            client_version="",
            rank="S",
            mods=[],
            statistics={"great": 1},
            maximum_statistics={"great": 1},
            total_score_without_mods=988924,
        )

        rewritten = rewrite_osr_server_score_metadata(official_replay, local, "mindblock")
        embedded = _embedded_osr_score_metadata(rewritten)

        assert embedded["online_id"] == 10
        assert embedded["user_id"] == 1_500_000_001
        assert embedded["client_version"] == "2026.821.0-tachyon-windows"
        assert embedded["statistics"] == {"great": 2838, "ok": 18, "ignore_hit": 626}

    def test_uploaded_lazer_replay_preserves_custom_mods_for_retention_and_pp(self) -> None:
        for mods in [
            [{"acronym": "DT", "settings": {"speed_change": 1.3}}],
            [{"acronym": "NC", "settings": {"speed_change": 1.7}}],
            [{"acronym": "DA", "settings": {"approach_rate": 9.5, "overall_difficulty": 8}}],
        ]:
            with self.subTest(mods=mods):
                metadata = ServerReplayScoreMetadata(
                    online_score_id=55,
                    user_id=7,
                    client_version="2026.804.2",
                    rank="A",
                    mods=cast(Any, mods),
                    statistics={},
                    maximum_statistics={},
                    total_score_without_mods=765432,
                )
                content = rewrite_osr_server_score_metadata(osr_file(), metadata, "player")
                prepared = prepare_uploaded_osr_score(content, {"id": 12345, "beatmapset_id": 54321})
                assert prepared.info.mods == mods

    def test_prepares_uploaded_replay_for_shared_import_pipeline(self) -> None:
        checksum = "abcdef0123456789abcdef0123456789"
        prepared = prepare_uploaded_osr_score(
            osr_file(checksum=checksum),
            {
                "id": 12345,
                "beatmapset_id": 54321,
                "checksum": checksum,
                "version": "Insane",
                "beatmapset": {"id": 54321, "artist": "Artist", "title": "Title"},
            },
            filename="score.osr",
        )

        assert prepared.source == "uploaded_osr"
        assert prepared.source_score_id == "987654321"
        assert prepared.source_username == "old-name"
        assert prepared.source_checksum == checksum
        assert prepared.source_fingerprint.startswith("uploaded_osr:sha256:")
        assert prepared.info.mods == [{"acronym": "HD"}]
        assert 0.97 < prepared.info.accuracy < 0.99
        preview = prepared.public_preview()
        assert preview["source"] == "uploaded_osr"
        assert preview["source_url"] is None
        assert preview["artist"] == "Artist"
        verification = verify_uploaded_score_revision(
            prepared,
            osr_file(checksum=checksum),
            beatmapset_id=54321,
            current_checksum=checksum,
        )
        assert verification.replay_status == "verified"
        assert verification.revision_verified_for(prepared) is True

    def test_rejects_truncated_and_implausible_replays(self) -> None:
        replay = osr_file()
        for invalid in (b"", replay[:-9], replay[:100]):
            with self.subTest(length=len(invalid)):
                assert_import_error(lambda invalid=invalid: parse_uploaded_osr(invalid))


class FakeReplayResponse:
    def __init__(self, chunks: list[bytes], *, content_length: int | None = None) -> None:
        self.status_code = 200
        self.headers = {} if content_length is None else {"content-length": str(content_length)}
        self.chunks = chunks
        self.iterated = False

    def raise_for_status(self) -> None:
        return None

    async def aiter_raw(self):
        self.iterated = True
        for chunk in self.chunks:
            yield chunk


class FakeStreamContext:
    def __init__(self, response: FakeReplayResponse) -> None:
        self.response = response

    async def __aenter__(self) -> FakeReplayResponse:
        return self.response

    async def __aexit__(self, *_args: object) -> None:
        return None


class FakeAsyncClient:
    def __init__(self, response: FakeReplayResponse) -> None:
        self.response = response

    async def __aenter__(self) -> Self:
        return self

    async def __aexit__(self, *_args: object) -> None:
        return None

    def stream(self, *_args: Any, **_kwargs: Any) -> FakeStreamContext:
        return FakeStreamContext(self.response)


class ReplayDownloadTests(unittest.IsolatedAsyncioTestCase):
    @classmethod
    def setUpClass(cls) -> None:
        init_mods()

    async def test_streamed_replay_verifies_current_revision(self) -> None:
        prepared = prepare_official_score(OfficialScoreReference(987654321), modern_score())
        checksum = "abcdef0123456789abcdef0123456789"
        response = FakeReplayResponse([osr_header(0, checksum)[:10], osr_header(0, checksum)[10:]])
        fetcher = SimpleNamespace(header={}, ensure_valid_access_token=AsyncMock())

        with patch("app.service.score_import_service.AsyncClient", return_value=FakeAsyncClient(response)):
            verification = await verify_official_score_revision(
                cast(Any, fetcher),
                prepared,
                beatmapset_id=54321,
                current_checksum=checksum,
            )

        assert verification.replay_status == "verified"
        assert verification.revision_verified_for(prepared) is True
        assert response.iterated is True

    async def test_declared_oversized_replay_is_rejected_before_streaming(self) -> None:
        prepared = prepare_official_score(OfficialScoreReference(987654321), modern_score())
        checksum = "abcdef0123456789abcdef0123456789"
        response = FakeReplayResponse([osr_header(0, checksum)], content_length=(32 * 1024 * 1024) + 1)
        fetcher = SimpleNamespace(header={}, ensure_valid_access_token=AsyncMock())

        with patch("app.service.score_import_service.AsyncClient", return_value=FakeAsyncClient(response)):
            verification = await verify_official_score_revision(
                cast(Any, fetcher),
                prepared,
                beatmapset_id=54321,
                current_checksum=checksum,
            )

        assert verification.replay is None
        assert verification.replay_status == "too_large"
        assert response.iterated is False


if __name__ == "__main__":
    unittest.main()
