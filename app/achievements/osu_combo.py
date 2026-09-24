from functools import partial

from app.database.score import Beatmap, Score
from app.models.achievement import Achievement, Medals
from app.models.score import GameMode

from sqlmodel.ext.asyncio.session import AsyncSession


async def process_combo(
    combo: int,
    next_combo: int,
    session: AsyncSession,
    score: Score,
    beatmap: Beatmap,
) -> bool:
    from app.service.beatmap_ranking_service import get_effective_beatmap_policy

    policy = await get_effective_beatmap_policy(session, beatmap)
    if not score.passed or not score.ranked or not policy.pp_enabled or score.gamemode != GameMode.OSU:
        return False
    if combo < 1:
        return False
    if next_combo != 0 and combo >= next_combo:
        return False
    return bool(combo <= score.max_combo < next_combo or (next_combo == 0 and score.max_combo >= combo))


MEDALS: Medals = {
    Achievement(
        id=1,
        name="500 Combo",
        desc="500 big ones! You''re moving up in the world!",
        assets_id="osu-combo-500",
    ): partial(process_combo, 500, 750),
    Achievement(
        id=3,
        name="750 Combo",
        desc="750 notes back to back? Woah.",
        assets_id="osu-combo-750",
    ): partial(process_combo, 750, 1000),
    Achievement(
        id=4,
        name="1000 Combo",
        desc="A thousand reasons why you rock at this game.",
        assets_id="osu-combo-1000",
    ): partial(process_combo, 1000, 2000),
    Achievement(
        id=5,
        name="2000 Combo",
        desc="Nothing can stop you now.",
        assets_id="osu-combo-2000",
    ): partial(process_combo, 2000, 0),
}
