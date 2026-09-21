"""Website leaderboards with supporter filters applied before choosing each best."""

import json

from app.database import Beatmap, Relationship, RelationshipType, Score, TotalScoreBestScore, User
from app.models.mods import API_MODS
from app.models.score import GameMode

from fastapi import HTTPException
from sqlmodel import col, func, select


def parse_mod_filter(value: str | None, mode: GameMode) -> list[str] | None:
    if value is None:
        return None
    mods = sorted({part.strip().upper() for part in value.split(",") if part.strip()})
    if mods == ["NM"]:
        return []
    allowed = API_MODS.get(int(mode), {})
    if not mods or len(mods) > 12 or any(mod not in allowed for mod in mods):
        raise HTTPException(422, "Некорректный набор модов")
    return mods


def visible_score_conditions():
    return [
        col(Score.passed).is_(True),
        col(Score.processed).is_(True),
        col(Score.leaderboard_eligible).is_(True),
        col(User.is_active).is_(True),
        col(User.is_bot).is_(False),
        ~User.is_restricted_query(col(User.id)),
    ]


def first_place_score_ids(user_id: int, mode: GameMode):
    """One current global winner per difficulty, with the board's tie-breaker."""
    ranked = (
        select(
            col(TotalScoreBestScore.score_id).label("score_id"),
            col(TotalScoreBestScore.user_id).label("user_id"),
            func.row_number()
            .over(
                partition_by=TotalScoreBestScore.beatmap_id,
                order_by=(col(TotalScoreBestScore.total_score).desc(), col(TotalScoreBestScore.score_id).desc()),
            )
            .label("position"),
        )
        .join(Score, col(Score.id) == col(TotalScoreBestScore.score_id))
        .join(User, col(User.id) == col(TotalScoreBestScore.user_id))
        .where(TotalScoreBestScore.gamemode == mode, *visible_score_conditions())
        .subquery()
    )
    return select(ranked.c.score_id).where(ranked.c.position == 1, ranked.c.user_id == user_id)


async def leaderboard_page(session, beatmap_id, mode, page, page_size, viewer, scope="global", mods=None):
    if scope != "global" or mods is not None:
        if viewer is None:
            raise HTTPException(401, "Войдите с supporter, чтобы открыть дополнительные топы")
        if not viewer.is_supporter:
            raise HTTPException(403, "Топ страны, друзей и фильтр по модам доступны с supporter")
    beatmap = await session.get(Beatmap, beatmap_id)
    game_mode = GameMode(mode) if mode is not None else beatmap.mode if beatmap else GameMode.OSU
    selected_mods = parse_mod_filter(mods, game_mode)
    if selected_mods is not None and game_mode in {GameMode.OSURX, GameMode.OSUAP}:
        selected_mods = sorted(set(selected_mods) | {"RX" if game_mode == GameMode.OSURX else "AP"})
    conditions = [
        TotalScoreBestScore.beatmap_id == beatmap_id,
        TotalScoreBestScore.gamemode == game_mode,
        *visible_score_conditions(),
    ]
    if scope == "country":
        conditions.append(User.country_code == viewer.country_code)
    elif scope == "friends":
        followed = select(Relationship.target_id).where(
            Relationship.user_id == viewer.id, Relationship.type == RelationshipType.FOLLOW
        )
        conditions.append((User.id == viewer.id) | col(User.id).in_(followed))
    if selected_mods is not None:
        # Compare both directions, including [] for NM. Settings such as DT rate
        # stay on the score; the filter selects the exact acronym combination.
        encoded = json.dumps(selected_mods)
        conditions.extend(
            [
                func.json_contains(TotalScoreBestScore.mods, encoded) == 1,
                func.json_contains(encoded, TotalScoreBestScore.mods) == 1,
            ]
        )
    per_user = (
        select(
            col(TotalScoreBestScore.score_id).label("score_id"),
            col(TotalScoreBestScore.user_id).label("user_id"),
            col(TotalScoreBestScore.total_score).label("total_score"),
            func.row_number()
            .over(
                partition_by=TotalScoreBestScore.user_id,
                order_by=(col(TotalScoreBestScore.total_score).desc(), col(TotalScoreBestScore.score_id).desc()),
            )
            .label("user_row"),
        )
        .join(Score, col(Score.id) == col(TotalScoreBestScore.score_id))
        .join(User, col(User.id) == col(TotalScoreBestScore.user_id))
        .where(*conditions)
        .subquery()
    )
    ranked = (
        select(
            per_user.c.score_id,
            per_user.c.user_id,
            func.row_number()
            .over(order_by=(per_user.c.total_score.desc(), per_user.c.score_id.desc()))
            .label("position"),
        )
        .where(per_user.c.user_row == 1)
        .subquery()
    )
    total = int((await session.exec(select(func.count()).select_from(ranked))).one())
    query = (
        select(Score, User, ranked.c.position)
        .join(ranked, ranked.c.score_id == col(Score.id))
        .join(User, col(User.id) == col(Score.user_id))
        .order_by(ranked.c.position)
    )
    rows = list((await session.exec(query.offset((page - 1) * page_size).limit(page_size))).all())
    top = rows[0] if page == 1 and rows else (await session.exec(query.limit(1))).first() if total else None
    personal = (await session.exec(query.where(ranked.c.user_id == viewer.id).limit(1))).first() if viewer else None
    return {"rows": rows, "top": top, "personal": personal, "total": total, "mode": game_mode.value}
