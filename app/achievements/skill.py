from functools import partial
from typing import Literal, cast

from app.database.beatmap import calculate_beatmap_attributes
from app.database.score import Beatmap, Score
from app.dependencies.database import get_redis
from app.dependencies.fetcher import get_fetcher
from app.models.achievement import Achievement, Medals
from app.models.mods import API_MODS, mods_can_get_pp_vanilla
from app.models.score import GameMode

from sqlmodel.ext.asyncio.session import AsyncSession


async def process_skill(
    target_gamemode: GameMode,
    star: int,
    type: Literal["pass", "fc"],
    session: AsyncSession,
    score: Score,
    beatmap: Beatmap,
) -> bool:
    from app.service.beatmap_ranking_service import get_effective_beatmap_policy

    if target_gamemode != score.gamemode:
        return False
    ruleset_id = int(score.gamemode)
    if not score.passed or not score.ranked:
        return False
    policy = await get_effective_beatmap_policy(session, beatmap)
    if not policy.pp_enabled:
        return False
    if not mods_can_get_pp_vanilla(ruleset_id, score.mods):
        return False
    difficulty_reduction_mods = [
        mod["Acronym"]
        for mod in API_MODS[cast(Literal[0, 1, 2, 3], ruleset_id)].values()
        if mod["Type"] == "DifficultyReduction"
    ]
    for mod in score.mods:
        if mod["acronym"] in difficulty_reduction_mods:
            return False

    fetcher = await get_fetcher()
    redis = get_redis()
    mods_ = score.mods.copy()
    mods_.sort(key=lambda x: x["acronym"])
    attribute = await calculate_beatmap_attributes(
        beatmap.id,
        score.gamemode,
        mods_,
        redis,
        fetcher,
        score.map_md5,
    )
    if attribute.star_rating < star or attribute.star_rating >= star + 1:
        return False
    return not (type == "fc" and not score.is_perfect_combo)


MEDALS: Medals = {
    Achievement(
        id=55,
        name="Rising Star",
        desc="Can't go forward without the first steps.",
        assets_id="osu-skill-pass-1",
    ): partial(process_skill, GameMode.OSU, 1, "pass"),
    Achievement(
        id=56,
        name="Constellation Prize",
        desc="Definitely not a consolation prize. Now things start getting hard!",
        assets_id="osu-skill-pass-2",
    ): partial(process_skill, GameMode.OSU, 2, "pass"),
    Achievement(
        id=57,
        name="Building Confidence",
        desc="Oh, you've SO got this.",
        assets_id="osu-skill-pass-3",
    ): partial(process_skill, GameMode.OSU, 3, "pass"),
    Achievement(
        id=58,
        name="Insanity Approaches",
        desc="You're not twitching, you're just ready.",
        assets_id="osu-skill-pass-4",
    ): partial(process_skill, GameMode.OSU, 4, "pass"),
    Achievement(
        id=59,
        name="These Clarion Skies",
        desc="Everything seems so clear now.",
        assets_id="osu-skill-pass-5",
    ): partial(process_skill, GameMode.OSU, 5, "pass"),
    Achievement(
        id=60,
        name="Above and Beyond",
        desc="A cut above the rest.",
        assets_id="osu-skill-pass-6",
    ): partial(process_skill, GameMode.OSU, 6, "pass"),
    Achievement(
        id=61,
        name="Supremacy",
        desc="All marvel before your prowess.",
        assets_id="osu-skill-pass-7",
    ): partial(process_skill, GameMode.OSU, 7, "pass"),
    Achievement(
        id=62,
        name="Absolution",
        desc="My god, you're full of stars!",
        assets_id="osu-skill-pass-8",
    ): partial(process_skill, GameMode.OSU, 8, "pass"),
    Achievement(
        id=242,
        name="Event Horizon",
        desc="No force dares to pull you under.",
        assets_id="osu-skill-pass-9",
    ): partial(process_skill, GameMode.OSU, 9, "pass"),
    Achievement(
        id=244,
        name="Phantasm",
        desc="Fevered is your passion, extraordinary is your skill.",
        assets_id="osu-skill-pass-10",
    ): partial(process_skill, GameMode.OSU, 10, "pass"),
    Achievement(
        id=63,
        name="Totality",
        desc="All the notes. Every single one.",
        assets_id="osu-skill-fc-1",
    ): partial(process_skill, GameMode.OSU, 1, "fc"),
    Achievement(
        id=64,
        name="Business As Usual",
        desc="Two to go, please.",
        assets_id="osu-skill-fc-2",
    ): partial(process_skill, GameMode.OSU, 2, "fc"),
    Achievement(
        id=65,
        name="Building Steam",
        desc="Hey, this isn't so bad.",
        assets_id="osu-skill-fc-3",
    ): partial(process_skill, GameMode.OSU, 3, "fc"),
    Achievement(
        id=66,
        name="Moving Forward",
        desc="Bet you feel good about that.",
        assets_id="osu-skill-fc-4",
    ): partial(process_skill, GameMode.OSU, 4, "fc"),
    Achievement(
        id=67,
        name="Paradigm Shift",
        desc="Surprisingly difficult.",
        assets_id="osu-skill-fc-5",
    ): partial(process_skill, GameMode.OSU, 5, "fc"),
    Achievement(
        id=68,
        name="Anguish Quelled",
        desc="Don't choke.",
        assets_id="osu-skill-fc-6",
    ): partial(process_skill, GameMode.OSU, 6, "fc"),
    Achievement(
        id=69,
        name="Never Give Up",
        desc="Excellence is its own reward.",
        assets_id="osu-skill-fc-7",
    ): partial(process_skill, GameMode.OSU, 7, "fc"),
    Achievement(
        id=70,
        name="Aberration",
        desc="They said it couldn't be done. They were wrong.",
        assets_id="osu-skill-fc-8",
    ): partial(process_skill, GameMode.OSU, 8, "fc"),
    Achievement(
        id=243,
        name="Chosen",
        desc="Reign among the Prometheans, where you belong.",
        assets_id="osu-skill-fc-9",
    ): partial(process_skill, GameMode.OSU, 9, "fc"),
    Achievement(
        id=245,
        name="Unfathomable",
        desc="You have no equal.",
        assets_id="osu-skill-fc-10",
    ): partial(process_skill, GameMode.OSU, 10, "fc"),
    Achievement(
        id=71,
        name="My First Don",
        desc="Marching to the beat of your own drum. Literally.",
        assets_id="taiko-skill-pass-1",
    ): partial(process_skill, GameMode.TAIKO, 1, "pass"),
    Achievement(
        id=72,
        name="Katsu Katsu Katsu",
        desc="Hora! Izuko!",
        assets_id="taiko-skill-pass-2",
    ): partial(process_skill, GameMode.TAIKO, 2, "pass"),
    Achievement(
        id=73,
        name="Not Even Trying",
        desc="Muzukashii? Not even.",
        assets_id="taiko-skill-pass-3",
    ): partial(process_skill, GameMode.TAIKO, 3, "pass"),
    Achievement(
        id=74,
        name="Face Your Demons",
        desc="The first trials are now behind you, but are you a match for the Oni?",
        assets_id="taiko-skill-pass-4",
    ): partial(process_skill, GameMode.TAIKO, 4, "pass"),
    Achievement(
        id=75,
        name="The Demon Within",
        desc="No rest for the wicked.",
        assets_id="taiko-skill-pass-5",
    ): partial(process_skill, GameMode.TAIKO, 5, "pass"),
    Achievement(
        id=76,
        name="Drumbreaker",
        desc="Too strong.",
        assets_id="taiko-skill-pass-6",
    ): partial(process_skill, GameMode.TAIKO, 6, "pass"),
    Achievement(
        id=77,
        name="The Godfather",
        desc="You are the Don of Dons.",
        assets_id="taiko-skill-pass-7",
    ): partial(process_skill, GameMode.TAIKO, 7, "pass"),
    Achievement(
        id=78,
        name="Rhythm Incarnate",
        desc="Feel the beat. Become the beat.",
        assets_id="taiko-skill-pass-8",
    ): partial(process_skill, GameMode.TAIKO, 8, "pass"),
    Achievement(
        id=95,
        name="Keeping Time",
        desc="Don, then katsu. Don, then katsu..",
        assets_id="taiko-skill-fc-1",
    ): partial(process_skill, GameMode.TAIKO, 1, "fc"),
    Achievement(
        id=96,
        name="To Your Own Beat",
        desc="Straight and steady.",
        assets_id="taiko-skill-fc-2",
    ): partial(process_skill, GameMode.TAIKO, 2, "fc"),
    Achievement(
        id=97,
        name="Big Drums",
        desc="Bigger scores to match.",
        assets_id="taiko-skill-fc-3",
    ): partial(process_skill, GameMode.TAIKO, 3, "fc"),
    Achievement(
        id=98,
        name="Adversity Overcome",
        desc="Difficult? Not for you.",
        assets_id="taiko-skill-fc-4",
    ): partial(process_skill, GameMode.TAIKO, 4, "fc"),
    Achievement(
        id=99,
        name="Demonslayer",
        desc="An Oni felled forevermore.",
        assets_id="taiko-skill-fc-5",
    ): partial(process_skill, GameMode.TAIKO, 5, "fc"),
    Achievement(
        id=100,
        name="Rhythm's Call",
        desc="Heralding true skill.",
        assets_id="taiko-skill-fc-6",
    ): partial(process_skill, GameMode.TAIKO, 6, "fc"),
    Achievement(
        id=101,
        name="Time Everlasting",
        desc="Not a single beat escapes you.",
        assets_id="taiko-skill-fc-7",
    ): partial(process_skill, GameMode.TAIKO, 7, "fc"),
    Achievement(
        id=102,
        name="The Drummer's Throne",
        desc="Percussive brilliance befitting royalty alone.",
        assets_id="taiko-skill-fc-8",
    ): partial(process_skill, GameMode.TAIKO, 8, "fc"),
    Achievement(
        id=79,
        name="A Slice Of Life",
        desc="Hey, this fruit catching business isn't bad.",
        assets_id="fruits-skill-pass-1",
    ): partial(process_skill, GameMode.FRUITS, 1, "pass"),
    Achievement(
        id=80,
        name="Dashing Ever Forward",
        desc="Fast is how you do it.",
        assets_id="fruits-skill-pass-2",
    ): partial(process_skill, GameMode.FRUITS, 2, "pass"),
    Achievement(
        id=81,
        name="Zesty Disposition",
        desc="No scurvy for you, not with that much fruit.",
        assets_id="fruits-skill-pass-3",
    ): partial(process_skill, GameMode.FRUITS, 3, "pass"),
    Achievement(
        id=82,
        name="Hyperdash ON!",
        desc="Time and distance is no obstacle to you.",
        assets_id="fruits-skill-pass-4",
    ): partial(process_skill, GameMode.FRUITS, 4, "pass"),
    Achievement(
        id=83,
        name="It's Raining Fruit",
        desc="And you can catch them all.",
        assets_id="fruits-skill-pass-5",
    ): partial(process_skill, GameMode.FRUITS, 5, "pass"),
    Achievement(
        id=84,
        name="Fruit Ninja",
        desc="Legendary techniques.",
        assets_id="fruits-skill-pass-6",
    ): partial(process_skill, GameMode.FRUITS, 6, "pass"),
    Achievement(
        id=85,
        name="Dreamcatcher",
        desc="No fruit, only dreams now.",
        assets_id="fruits-skill-pass-7",
    ): partial(process_skill, GameMode.FRUITS, 7, "pass"),
    Achievement(
        id=86,
        name="Lord of the Catch",
        desc="Your kingdom kneels before you.",
        assets_id="fruits-skill-pass-8",
    ): partial(process_skill, GameMode.FRUITS, 8, "pass"),
    Achievement(
        id=103,
        name="Sweet And Sour",
        desc="Apples and oranges, literally.",
        assets_id="fruits-skill-fc-1",
    ): partial(process_skill, GameMode.FRUITS, 1, "fc"),
    Achievement(
        id=104,
        name="Reaching The Core",
        desc="The seeds of future success.",
        assets_id="fruits-skill-fc-2",
    ): partial(process_skill, GameMode.FRUITS, 2, "fc"),
    Achievement(
        id=105,
        name="Clean Platter",
        desc="Clean only of failure. It is completely full, otherwise.",
        assets_id="fruits-skill-fc-3",
    ): partial(process_skill, GameMode.FRUITS, 3, "fc"),
    Achievement(
        id=106,
        name="Between The Rain",
        desc="No umbrella needed.",
        assets_id="fruits-skill-fc-4",
    ): partial(process_skill, GameMode.FRUITS, 4, "fc"),
    Achievement(
        id=107,
        name="Addicted",
        desc="That was an overdose?",
        assets_id="fruits-skill-fc-5",
    ): partial(process_skill, GameMode.FRUITS, 5, "fc"),
    Achievement(
        id=108,
        name="Quickening",
        desc="A dash above normal limits.",
        assets_id="fruits-skill-fc-6",
    ): partial(process_skill, GameMode.FRUITS, 6, "fc"),
    Achievement(
        id=109,
        name="Supersonic",
        desc="Faster than is reasonably necessary.",
        assets_id="fruits-skill-fc-7",
    ): partial(process_skill, GameMode.FRUITS, 7, "fc"),
    Achievement(
        id=110,
        name="Dashing Scarlet",
        desc="Speed beyond mortal reckoning.",
        assets_id="fruits-skill-fc-8",
    ): partial(process_skill, GameMode.FRUITS, 8, "fc"),
    Achievement(
        id=87,
        name="First Steps",
        desc="It isn't 9-to-5, but 1-to-9. Keys, that is.",
        assets_id="mania-skill-pass-1",
    ): partial(process_skill, GameMode.MANIA, 1, "pass"),
    Achievement(
        id=88,
        name="No Normal Player",
        desc="Not anymore, at least.",
        assets_id="mania-skill-pass-2",
    ): partial(process_skill, GameMode.MANIA, 2, "pass"),
    Achievement(
        id=89,
        name="Impulse Drive",
        desc="Not quite hyperspeed, but getting close.",
        assets_id="mania-skill-pass-3",
    ): partial(process_skill, GameMode.MANIA, 3, "pass"),
    Achievement(
        id=90,
        name="Hyperspeed",
        desc="Woah.",
        assets_id="mania-skill-pass-4",
    ): partial(process_skill, GameMode.MANIA, 4, "pass"),
    Achievement(
        id=91,
        name="Ever Onwards",
        desc="Another challenge is just around the corner.",
        assets_id="mania-skill-pass-5",
    ): partial(process_skill, GameMode.MANIA, 5, "pass"),
    Achievement(
        id=92,
        name="Another Surpassed",
        desc="Is there no limit to your skills?",
        assets_id="mania-skill-pass-6",
    ): partial(process_skill, GameMode.MANIA, 6, "pass"),
    Achievement(
        id=93,
        name="Extra Credit",
        desc="See me after class.",
        assets_id="mania-skill-pass-7",
    ): partial(process_skill, GameMode.MANIA, 7, "pass"),
    Achievement(
        id=94,
        name="Maniac",
        desc="There's just no stopping you.",
        assets_id="mania-skill-pass-8",
    ): partial(process_skill, GameMode.MANIA, 8, "pass"),
    Achievement(
        id=111,
        name="Keystruck",
        desc="The beginning of a new story",
        assets_id="mania-skill-fc-1",
    ): partial(process_skill, GameMode.MANIA, 1, "fc"),
    Achievement(
        id=112,
        name="Keying In",
        desc="Finding your groove.",
        assets_id="mania-skill-fc-2",
    ): partial(process_skill, GameMode.MANIA, 2, "fc"),
    Achievement(
        id=113,
        name="Hyperflow",
        desc="You can *feel* the rhythm.",
        assets_id="mania-skill-fc-3",
    ): partial(process_skill, GameMode.MANIA, 3, "fc"),
    Achievement(
        id=114,
        name="Breakthrough",
        desc="Many skills mastered, rolled into one.",
        assets_id="mania-skill-fc-4",
    ): partial(process_skill, GameMode.MANIA, 4, "fc"),
    Achievement(
        id=115,
        name="Everything Extra",
        desc="Giving your all is giving everything you have.",
        assets_id="mania-skill-fc-5",
    ): partial(process_skill, GameMode.MANIA, 5, "fc"),
    Achievement(
        id=116,
        name="Level Breaker",
        desc="Finesse beyond reason",
        assets_id="mania-skill-fc-6",
    ): partial(process_skill, GameMode.MANIA, 6, "fc"),
    Achievement(
        id=117,
        name="Step Up",
        desc="A precipice rarely seen.",
        assets_id="mania-skill-fc-7",
    ): partial(process_skill, GameMode.MANIA, 7, "fc"),
    Achievement(
        id=118,
        name="Behind The Veil",
        desc="Supernatural!",
        assets_id="mania-skill-fc-8",
    ): partial(process_skill, GameMode.MANIA, 8, "fc"),
}
