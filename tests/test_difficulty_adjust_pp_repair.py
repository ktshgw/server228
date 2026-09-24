from types import SimpleNamespace
from typing import cast
import unittest
from unittest.mock import patch

from app.database import Score
from app.models.mods import RANKED_MODS
from app.models.mods.performance import DEFAULT_RANKED_MODS
from app.models.score import GameMode
from app.service.difficulty_adjust_pp_service import replacement_is_eligible


class DifficultyAdjustRepairTests(unittest.TestCase):
    def test_replacement_requires_current_revision_and_score_eligibility(self):
        baseline = {
            "processed": True,
            "passed": True,
            "ranked": True,
            "pp": 200,
            "map_md5": "current",
            "gamemode": GameMode.OSU,
            "mods": [{"acronym": "DT", "settings": {"speed_change": 1.3}}],
        }
        with patch.dict(RANKED_MODS, DEFAULT_RANKED_MODS, clear=True):

            def eligible(**changes):
                score = cast(Score, SimpleNamespace(**(baseline | changes)))
                return replacement_is_eligible(score, map_pp_enabled=True, checksum="current")

            assert eligible()
            for changes in (
                {"processed": False},
                {"passed": False},
                {"ranked": False},
                {"pp": 0},
                {"map_md5": "old-revision"},
                {"mods": [{"acronym": "DA"}]},
                {"mods": [*baseline["mods"], {"acronym": "DA"}]},
                {"mods": [{"acronym": "AT"}]},
            ):
                with self.subTest(changes=changes):
                    assert not eligible(**changes)
            assert not replacement_is_eligible(
                cast(Score, SimpleNamespace(**baseline)), map_pp_enabled=False, checksum="current"
            )
