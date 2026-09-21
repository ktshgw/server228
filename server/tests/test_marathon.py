# ruff: noqa: PT027 -- unittest runner and its async assertions are used in this suite.
import time
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from app.database import User
from app.database.marathon import Marathon, MarathonScore
from app.models.marathon import CreateMarathon, StartMarathon, SubmitMarathonScore
from app.models.mods import init_mods
from app.router.private.marathon import delete_marathon
from app.service.marathon_service import create_marathon, start_marathon, submit_marathon_score

from fastapi import HTTPException
from pydantic import ValidationError
from sqlmodel import Session, create_engine, select


class AsyncSessionAdapter:
    """Use a real temporary SQL database without a second async database driver."""

    def __init__(self, session):
        self.session = session

    async def exec(self, statement):
        return self.session.exec(statement)

    async def get(self, model, key):
        return self.session.get(model, key)

    def add(self, item):
        self.session.add(item)

    async def flush(self):
        self.session.flush()

    async def commit(self):
        self.session.commit()

    async def rollback(self):
        self.session.rollback()


class MemoryRedis:
    def __init__(self):
        self.items = {}

    async def set(self, key, value, **_):
        self.items[key] = value

    async def get(self, key):
        return self.items.get(key)

    async def delete(self, key):
        self.items.pop(key, None)


class MarathonTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        init_mods()
        self.engine = create_engine("sqlite://")
        for model in (User, Marathon, MarathonScore):
            model.__table__.create(self.engine)
        self.sql = Session(self.engine, expire_on_commit=False)
        self.session = AsyncSessionAdapter(self.sql)
        self.redis = MemoryRedis()
        self.user = SimpleNamespace(id=1, username="Player", is_restricted=AsyncMock(return_value=False))
        self.other = SimpleNamespace(id=2, username="Other", is_restricted=AsyncMock(return_value=False))
        self.payload = CreateMarathon(
            name="Compilation",
            ruleset_id=0,
            segments=[
                {
                    "beatmap_id": 1,
                    "beatmapset_id": 1,
                    "checksum": "a" * 32,
                    "title": "A",
                    "start_ms": 10000,
                    "end_ms": 30000,
                },
                {
                    "beatmap_id": 2,
                    "beatmapset_id": 2,
                    "checksum": "b" * 32,
                    "title": "B",
                    "start_ms": 20000,
                    "end_ms": 50000,
                },
            ],
        )

    def tearDown(self):
        self.sql.close()
        self.engine.dispose()

    def test_new_fragments_have_90_second_limit(self):
        data = self.payload.model_dump()
        data["segments"][0]["end_ms"] = data["segments"][0]["start_ms"] + 90_000
        CreateMarathon.model_validate(data)
        data["segments"][0]["end_ms"] += 1
        with self.assertRaises(ValidationError):
            CreateMarathon.model_validate(data)

    async def test_existing_long_fragments_still_start(self):
        saved = await create_marathon(self.session, self.user, self.payload)
        marathon = self.sql.get(Marathon, saved["id"])
        marathon.segments = [{**segment, "end_ms": segment["start_ms"] + 180_000} for segment in marathon.segments]
        self.sql.add(marathon)
        self.sql.commit()
        attempt = await start_marathon(self.session, self.redis, self.user, saved["id"], StartMarathon())
        assert attempt["attempt_id"]

    async def test_save_start_complete_retry_stays_in_own_table(self):
        playlist = await create_marathon(self.session, self.user, self.payload)
        assert playlist["duration_ms"] == 55000
        assert playlist["segments"][1]["start_ms"] == 20000
        with patch("app.service.marathon_service.time.time", return_value=time.time() - 100):
            attempt = await start_marathon(self.session, self.redis, self.other, playlist["id"], StartMarathon())
        result = SubmitMarathonScore(
            attempt_id=attempt["attempt_id"], total_score=789123, accuracy=0.987, max_combo=444
        )
        first = await submit_marathon_score(self.session, self.redis, self.other, playlist["id"], result)
        retry = await submit_marathon_score(self.session, self.redis, self.other, playlist["id"], result)
        assert first == retry == {"saved": True, "id": 1}
        assert len(self.sql.exec(select(MarathonScore)).all()) == 1
        assert self.sql.exec(select(MarathonScore)).one().user_id == 2
        assert not self.redis.items
        # The full flow succeeds with no normal scores, PP, statistics or MMR tables present.

    async def test_attempt_cannot_be_used_by_another_user_or_playlist(self):
        first = await create_marathon(self.session, self.user, self.payload)
        second = await create_marathon(self.session, self.user, self.payload)
        attempt = await start_marathon(self.session, self.redis, self.user, first["id"], StartMarathon())
        result = SubmitMarathonScore(attempt_id=attempt["attempt_id"], total_score=1, accuracy=1, max_combo=1)
        for user, playlist_id in ((self.other, first["id"]), (self.user, second["id"])):
            with self.assertRaises(HTTPException) as error:
                await submit_marathon_score(self.session, self.redis, user, playlist_id, result)
            assert error.exception.status_code == 404
        assert not self.sql.exec(select(MarathonScore)).all()

    async def test_too_early_and_autoplay_runs_do_not_enter_board(self):
        playlist = await create_marathon(self.session, self.user, self.payload)
        attempt = await start_marathon(self.session, self.redis, self.user, playlist["id"], StartMarathon())
        result = SubmitMarathonScore(attempt_id=attempt["attempt_id"], total_score=1, accuracy=1, max_combo=1)
        with self.assertRaises(HTTPException) as error:
            await submit_marathon_score(self.session, self.redis, self.user, playlist["id"], result)
        assert error.exception.status_code == 422
        attempt = await start_marathon(
            self.session, self.redis, self.user, playlist["id"], StartMarathon(mods=[{"acronym": "AT"}])
        )
        result.attempt_id = attempt["attempt_id"]
        assert (await submit_marathon_score(self.session, self.redis, self.user, playlist["id"], result))[
            "saved"
        ] is False
        assert not self.sql.exec(select(MarathonScore)).all()

    async def test_deletion_requires_owner_and_hides_playlist(self):
        playlist = await create_marathon(self.session, self.user, self.payload)
        with self.assertRaises(HTTPException):
            await delete_marathon(playlist["id"], self.session, self.other)
        assert await delete_marathon(playlist["id"], self.session, self.user) == {"deleted": True}
        with self.assertRaises(HTTPException) as error:
            await start_marathon(self.session, self.redis, self.user, playlist["id"], StartMarathon())
        assert error.exception.status_code == 404

    def test_invalid_ranges_and_score_values_are_rejected(self):
        for start, end in ((-1, 10000), (10000, 5000), (0, 4000), (0, 181000)):
            body = self.payload.model_dump()
            body["segments"][0].update(start_ms=start, end_ms=end)
            with self.assertRaises(ValidationError):
                CreateMarathon.model_validate(body)
        for accuracy in (float("nan"), float("inf"), -1, 1.01):
            with self.assertRaises(ValidationError):
                SubmitMarathonScore(
                    attempt_id="00000000-0000-0000-0000-000000000000", total_score=0, accuracy=accuracy, max_combo=0
                )


if __name__ == "__main__":
    unittest.main()
