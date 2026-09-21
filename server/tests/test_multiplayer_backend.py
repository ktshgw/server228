import asyncio
from types import SimpleNamespace
from typing import Any, cast
import unittest
from unittest.mock import ANY, AsyncMock, patch

from app.database.playlist_best_score import partition_scores_around
from app.database.room import RoomModel
from app.database.score import ScoreModel
from app.models.room import RoomCategory
from app.router.v2.room import get_room_events
from app.router.v2.score import show_playlist_score
from app.router.v2.user import get_users, lookup_users


class Result:
    def __init__(self, values: list[Any]):
        self.values = values

    def all(self) -> list[Any]:
        return self.values

    def __iter__(self):
        return iter(self.values)

    def first(self) -> Any | None:
        return self.values[0] if self.values else None

    def one(self) -> Any:
        if len(self.values) != 1:
            raise AssertionError("expected exactly one value")
        return self.values[0]


def best(score_id: int, total_score: int, *, user_id: int | None = None) -> Any:
    return SimpleNamespace(
        score_id=score_id,
        total_score=total_score,
        user_id=score_id if user_id is None else user_id,
        score=SimpleNamespace(id=score_id),
    )


class PlaylistScoreOrderingTests(unittest.TestCase):
    def test_partition_matches_lazer_nearest_first_contract_and_paginates(self) -> None:
        records = [best(score_id, 20_000 - score_id) for score_id in range(1, 27)]

        higher, lower, more_higher, more_lower = partition_scores_around(
            cast(Any, records),
            15,
            limit=10,
        )

        assert [score.score_id for score in higher] == list(range(14, 4, -1))
        assert [score.score_id for score in lower] == list(range(16, 26))
        assert more_higher
        assert more_lower

    def test_partition_uses_score_id_as_equal_score_tie_breaker(self) -> None:
        records = [best(10, 1_000), best(11, 1_000), best(12, 1_000)]

        higher, lower, _, _ = partition_scores_around(cast(Any, records), 11)

        assert [score.score_id for score in higher] == [10]
        assert [score.score_id for score in lower] == [12]

    def test_partition_does_not_duplicate_pivot_user(self) -> None:
        records = [best(1, 1_100), best(2, 1_000, user_id=7), best(3, 900, user_id=7)]

        higher, lower, _, _ = partition_scores_around(cast(Any, records), 2)

        assert [score.score_id for score in higher] == [1]
        assert lower == []


class MultiplayerScoreResponseTests(unittest.TestCase):
    def test_multiplayer_position_uses_room_leaderboard(self) -> None:
        playlist_position = AsyncMock(return_value=4)
        solo_position = AsyncMock(return_value=99)
        score = SimpleNamespace(id=55)

        with (
            patch("app.database.score.get_playlist_score_position", playlist_position),
            patch("app.database.score.get_score_position_by_id", solo_position),
        ):
            async def call_position() -> int | None:
                return await ScoreModel.position(cast(Any, object()), cast(Any, score), playlist_id=3, room_id=8)

            result = asyncio.run(call_position())

        assert result == 4
        playlist_position.assert_awaited_once_with(8, 3, 55, ANY)
        solo_position.assert_not_awaited()

    def test_scores_around_contains_neighbours_and_cursor_contract(self) -> None:
        records = [best(score_id, 20_000 - score_id) for score_id in range(1, 27)]
        session = SimpleNamespace(exec=AsyncMock(return_value=Result(records)))

        async def transform(score: Any, **_kwargs: Any) -> dict[str, int]:
            return {"id": score.id}

        model_factory = lambda **kwargs: SimpleNamespace(**kwargs)  # noqa: E731
        with (
            patch.object(ScoreModel, "transform", new=AsyncMock(side_effect=transform)),
            patch("app.database.score.MultiplayerScores", side_effect=model_factory),
            patch("app.database.score.ScoreAround", side_effect=model_factory),
        ):
            async def call_scores_around() -> Any:
                return await ScoreModel.scores_around(
                    cast(Any, session),
                    cast(Any, records[14].score),
                    playlist_id=2,
                    room_id=9,
                    is_playlist=False,
                )

            result = asyncio.run(call_scores_around())

        assert result is not None
        assert result.higher is not None
        assert result.lower is not None
        assert [score["id"] for score in result.higher.scores] == list(range(14, 4, -1))
        assert result.higher.params == {"limit": 10, "sort": "score_asc"}
        assert result.higher.cursor == {"total_score": 19_995, "score_id": 5}
        assert [score["id"] for score in result.lower.scores] == list(range(16, 26))
        assert result.lower.params == {"limit": 10, "sort": "score_desc"}
        assert result.lower.cursor == {"total_score": 19_975, "score_id": 25}

    def test_show_score_does_not_wait_on_nonexistent_gameplay_counter(self) -> None:
        room = SimpleNamespace(category=RoomCategory.REALTIME)
        record = SimpleNamespace(score=SimpleNamespace(id=44))
        session = SimpleNamespace(
            get=AsyncMock(return_value=room),
            exec=AsyncMock(return_value=Result([record])),
        )
        redis = SimpleNamespace(get=AsyncMock(side_effect=AssertionError("must not poll Redis")))
        transformed = {"id": 44, "position": 1, "scores_around": {"higher": None, "lower": None}}

        with patch.object(ScoreModel, "transform", new=AsyncMock(return_value=transformed)) as transform:
            result = asyncio.run(
                show_playlist_score(
                    cast(Any, session),
                    room_id=7,
                    playlist_id=2,
                    score_id=44,
                    current_user=cast(Any, SimpleNamespace(id=5)),
                    redis=cast(Any, redis),
                )
            )

        assert result == transformed
        redis.get.assert_not_awaited()
        call = transform.await_args
        assert call is not None
        assert "position" in call.kwargs["includes"]
        assert "scores_around" in call.kwargs["includes"]
        assert call.kwargs["playlist_id"] == 2
        assert call.kwargs["room_id"] == 7


class MultiplayerUserRankTests(unittest.TestCase):
    def test_batch_users_include_ruleset_statistics_and_preserve_requested_order(self) -> None:
        users = [SimpleNamespace(id=3), SimpleNamespace(id=7)]
        session = SimpleNamespace(exec=AsyncMock(return_value=Result(users)))

        async def transform(user: Any, **kwargs: Any) -> dict[str, Any]:
            return {"id": user.id, "includes": kwargs["includes"]}

        endpoint = cast(Any, get_users).__wrapped__
        with patch("app.router.v2.user.UserModel.transform", new=AsyncMock(side_effect=transform)):
            result = asyncio.run(
                endpoint(
                    cast(Any, session),
                    cast(Any, SimpleNamespace()),
                    cast(Any, SimpleNamespace()),
                    [7, 3, 7],
                    False,
                )
            )

        assert [user["id"] for user in result["users"]] == [7, 3]
        assert all("statistics_rulesets" in user["includes"] for user in result["users"])

    def test_realtime_lookup_uses_compact_cards_and_requested_ruleset_rank(self) -> None:
        users = [SimpleNamespace(id=3), SimpleNamespace(id=7)]
        session = SimpleNamespace(exec=AsyncMock(return_value=Result(users)))

        async def transform(user: Any, **kwargs: Any) -> dict[str, Any]:
            return {"id": user.id, "includes": kwargs["includes"]}

        async def statistics(_session: Any, user: Any, **_kwargs: Any) -> dict[str, int]:
            return {"global_rank": user.id * 10}

        endpoint = cast(Any, lookup_users).__wrapped__
        with (
            patch("app.router.v2.user.UserModel.transform", new=AsyncMock(side_effect=transform)),
            patch("app.router.v2.user.UserModel.statistics", new=AsyncMock(side_effect=statistics)),
        ):
            result = asyncio.run(
                endpoint(
                    cast(Any, session),
                    cast(Any, SimpleNamespace()),
                    cast(Any, SimpleNamespace()),
                    [7, 3, 7],
                    0,
                )
            )

        assert [user["id"] for user in result["users"]] == [7, 3]
        assert all("statistics_rulesets" not in user["includes"] for user in result["users"])
        assert [user["global_rank"] for user in result["users"]] == [
            {"rank": 70, "ruleset_id": 0},
            {"rank": 30, "ruleset_id": 0},
        ]


class MultiplayerRoomHistoryTests(unittest.TestCase):
    def test_events_are_chronological_with_global_bounds_and_official_users_key(self) -> None:
        room = SimpleNamespace(id=5, category=RoomCategory.NORMAL)
        newest = SimpleNamespace(id=8, user_id=None, playlist_item_id=None)
        older = SimpleNamespace(id=7, user_id=None, playlist_item_id=None)
        results = iter(
            [
                Result([room]),
                Result([newest, older]),
                Result([(2, 11)]),
                Result([]),
                Result([]),
            ]
        )
        session = SimpleNamespace(exec=AsyncMock(side_effect=lambda _query: next(results)))

        with (
            patch("app.router.v2.room.MultiplayerEventResp.from_db", side_effect=lambda event: {"id": event.id}),
            patch.object(RoomModel, "transform", new=AsyncMock(return_value={"id": 5})),
        ):
            result = asyncio.run(
                get_room_events(
                    cast(Any, session),
                    room_id=5,
                    current_user=None,
                    limit=100,
                    after=None,
                    before=None,
                )
            )

        assert [event["id"] for event in result["events"]] == [7, 8]
        assert result["first_event_id"] == 2
        assert result["last_event_id"] == 11
        assert result["users"] == []
        assert "user" not in result
