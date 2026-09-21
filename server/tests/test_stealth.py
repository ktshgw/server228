# ruff: noqa: PT027
import json
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock

from app.service.stealth_service import candidates_and_rank, nearby_players

from fastapi import HTTPException


def row(user_id, pp, rank, country="FI", country_rank=None):
    return {
        "user": {"id": user_id, "username": f"Player{user_id}", "country_code": country},
        "pp": pp,
        "global_rank": rank,
        "country_rank": country_rank,
    }


class StealthTests(unittest.IsolatedAsyncioTestCase):
    def test_candidate_country_is_the_official_players_country(self):
        rows = [row(10, 3000, 100000, "FI", 501), row(11, 3001, 99900, "NZ", 129)]
        result = candidates_and_rank(rows, 3000)
        assert {(p["country_code"], p["country_rank"]) for p in result["candidates"]} == {("FI", 501), ("NZ", 129)}
        assert rows[0]["user"]["country_code"] == "FI"

    async def test_rank_refresh_uses_selected_country_and_page_positions(self):
        requests = []

        async def request(_url, *, params, **_kwargs):
            requests.append(params.copy())
            if "country" not in params:
                return {"total": 10000, "ranking": [row(9, 9000, 10000)]}
            assert params["country"] == "NZ"
            page = int(params["cursor[page]"])
            rows = [
                row(10 + i, 4000 - i * 10, 80000 + i * 100, "NZ") for i in range((page - 1) * 50, min(page * 50, 150))
            ]
            return {"total": 150, "ranking": rows}

        fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=request))
        redis = SimpleNamespace(get=AsyncMock(return_value=None), set=AsyncMock())
        result = await nearby_players(fetcher, redis, "osu", 3105, "nz")
        assert result["country_code"] == "NZ"
        assert result["country_rank"] == 91
        assert not result["country_rank_estimated"]
        assert {p.get("country") for p in requests if "country" in p} == {"NZ"}
        assert all(p["country_code"] == "NZ" and p["country_rank"] for p in result["candidates"])
        improved = await nearby_players(fetcher, redis, "osu", 3115, "NZ")
        assert improved["country_rank"] == 90
        equal = await nearby_players(fetcher, redis, "osu", 3100, "NZ")
        assert equal["country_rank"] == 91
        with self.assertRaises(HTTPException):
            await nearby_players(fetcher, redis, "osu", 3000, "NZ&country=DE")

    async def test_country_rank_at_page_boundaries_and_across_tied_pages(self):
        players = [row(10 + i, 6000 - i * 10, 80000 + i * 100, "NZ") for i in range(500)]

        async def request(_url, *, params, **_kwargs):
            if "country" not in params:
                return {"total": 10000, "ranking": [row(9, 9000, 10000)]}
            page = int(params["cursor[page]"])
            return {"total": len(players), "ranking": players[(page - 1) * 50 : page * 50]}

        fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=request))
        redis = SimpleNamespace(get=AsyncMock(return_value=None), set=AsyncMock())
        for pp, rank in ((6001, 1), (4005, 201), (4000, 201), (3995, 202), (1000, 501)):
            assert (await nearby_players(fetcher, redis, "osu", pp, "NZ"))["country_rank"] == rank
        for player in players[175:251]:
            player["pp"] = 4250
        assert (await nearby_players(fetcher, redis, "osu", 4250, "NZ"))["country_rank"] == 176

    def test_close_random_pool_and_interpolated_rank_below_global_cutoff(self):
        rows = [row(10, 2990, 110000), row(11, 3000, 100000), row(12, 3010, 90000), row(13, 15000, 100)]
        result = candidates_and_rank(rows, 3005)
        assert {p["id"] for p in result["candidates"]} == {10, 11, 12}
        assert 90000 < result["global_rank"] < 100000
        assert result["estimated"]
        assert rows[0]["global_rank"] == 110000  # Never mutate source stats.
        assert result["candidates"][0]["avatar_url"].startswith("https://a.ppy.sh/")

    def test_rank_improves_monotonically_and_same_pp_is_preferred(self):
        rows = [row(10, 3000.1, 10000), row(11, 3000.2, 9999), row(12, 3009, 9950)]
        assert len(candidates_and_rank(rows, 3000)["candidates"]) == 2
        ranks = [candidates_and_rank(rows, p)["global_rank"] for p in (3000, 3001, 3005, 3009)]
        assert ranks == sorted(ranks, reverse=True)
        assert candidates_and_rank(rows, 3005, exact=True)["global_rank"] == 9951

    def test_invalid_and_restricted_users_excluded(self):
        invalid = row(12, 3000, 200)
        invalid["user"]["is_deleted"] = True
        result = candidates_and_rank([invalid, row(1, 3000, 1), row(10, 3000, 20000)], 3000)
        assert [u["id"] for u in result["candidates"]] == [10]
        with self.assertRaises(HTTPException):
            candidates_and_rank([], 100)

    async def test_global_cap_falls_back_to_countries_and_reads_cache(self):
        requests = []
        cache = {}

        async def get(key):
            return cache.get(key)

        async def set_cache(key, value, **_kwargs):
            cache[key] = value

        async def request(url, *, params, **_kwargs):
            requests.append((url, params))
            if "country" not in params:
                assert params["cursor[page]"] == "200"
                return {"total": 10000, "ranking": [row(9, 9000, 10000)]}
            return {"total": 3, "ranking": [row(10, 3000, 100000), row(11, 2990, 101000), row(12, 2980, 102000)]}

        fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=request))
        redis = SimpleNamespace(get=AsyncMock(side_effect=get), set=AsyncMock(side_effect=set_cache))
        result = await nearby_players(fetcher, redis, "osu", 2990)
        assert result["global_rank"] == 101000
        assert len(requests) == 3
        assert all(int(params["cursor[page]"]) <= 200 for _, params in requests)
        assert all(json.loads(value)["ranking"] for value in cache.values())

    async def test_bounded_failures_and_invalid_input(self):
        fetcher = SimpleNamespace(request_api=AsyncMock(side_effect=TimeoutError("secret")))
        redis = SimpleNamespace(get=AsyncMock(return_value=None), set=AsyncMock())
        with self.assertRaises(HTTPException) as caught:
            await nearby_players(fetcher, redis, "osu", 3000)
        assert caught.exception.status_code == 503
        assert "secret" not in caught.exception.detail
        for mode, pp in (("invalid", 3000), ("osu", float("nan")), ("osu", -1)):
            with self.assertRaises(HTTPException) as caught:
                await nearby_players(fetcher, redis, mode, pp)
            assert caught.exception.status_code == 400
