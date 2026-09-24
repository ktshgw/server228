"""Same-origin local discussions and activity for the website beatmap page."""

from typing import Annotated, Literal

from app.database import Score
from app.dependencies.database import Database, Redis
from app.dependencies.fetcher import Fetcher
from app.models.score import GameMode
from app.service.web_beatmap_community_service import (
    add_comment,
    can_delete_comment,
    comment_page,
    remove_comment,
    vote_comment,
)

from .router import router
from .web_site import WEB_API_PREFIX, ModeName, WebSession, _as_utc, _optional_web_user, _require_csrf, _user_payload

from fastapi import HTTPException, Query, Request
from httpx import HTTPError, HTTPStatusError
from pydantic import BaseModel, ConfigDict, Field, field_validator
from sqlalchemy import case
from sqlmodel import col, func, select


class CommentBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    body: str = Field(min_length=1, max_length=2000)

    @field_validator("body")
    @classmethod
    def clean_body(cls, value):
        value = value.strip()
        if not value:
            raise ValueError("Напишите комментарий")
        return value


class CommentVoteBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    active: bool


async def require_writer(request, context, session):
    _require_csrf(request, context)
    if await context.user.is_restricted(session):
        raise HTTPException(403, "Ограниченный аккаунт не может участвовать в обсуждении")


@router.get(f"{WEB_API_PREFIX}/beatmapsets/{{beatmapset_id}}/comments", include_in_schema=False)
async def get_comments(
    beatmapset_id: int,
    request: Request,
    session: Database,
    redis: Redis,
    page: Annotated[int, Query(ge=1)] = 1,
    sort: Literal["new", "old", "top"] = "new",
):
    viewer = await _optional_web_user(request, session, redis)
    rows, liked, total = await comment_page(session, beatmapset_id, viewer, page, sort)
    return {
        "items": [
            {
                "id": comment.id,
                "body": comment.body,
                "created_at": _as_utc(comment.created_at),
                "user": await _user_payload(session, user),
                "votes": int(votes),
                "voted": comment.id in liked,
                "can_delete": can_delete_comment(comment, viewer),
            }
            for comment, user, votes in rows
        ],
        "page": page,
        "pages": max(1, (total + 19) // 20),
        "total": total,
    }


@router.post(f"{WEB_API_PREFIX}/beatmapsets/{{beatmapset_id}}/comments", include_in_schema=False)
async def post_comment(
    beatmapset_id: int,
    body: CommentBody,
    request: Request,
    context: WebSession,
    session: Database,
    fetcher: Fetcher,
):
    await require_writer(request, context, session)
    if beatmapset_id <= 0:
        raise HTTPException(422, "Некорректный ID карты")
    try:
        await fetcher.get_beatmapset(beatmapset_id)
    except HTTPStatusError as exc:
        raise HTTPException(404 if exc.response.status_code == 404 else 503, "Не удалось проверить карту") from exc
    except HTTPError as exc:
        raise HTTPException(503, "Каталог временно недоступен") from exc
    await add_comment(session, context.user, beatmapset_id, body.body)
    return {"ok": True}


@router.delete(f"{WEB_API_PREFIX}/beatmap-comments/{{comment_id}}", include_in_schema=False)
async def delete_comment(comment_id: int, request: Request, context: WebSession, session: Database):
    await require_writer(request, context, session)
    await remove_comment(session, context.user, comment_id)
    return {"ok": True}


@router.put(f"{WEB_API_PREFIX}/beatmap-comments/{{comment_id}}/vote", include_in_schema=False)
async def put_comment_vote(
    comment_id: int,
    body: CommentVoteBody,
    request: Request,
    context: WebSession,
    session: Database,
):
    await require_writer(request, context, session)
    await vote_comment(session, context.user, comment_id, body.active)
    return {"ok": True}


@router.get(f"{WEB_API_PREFIX}/beatmaps/{{beatmap_id}}/activity", include_in_schema=False)
async def beatmap_activity(beatmap_id: int, session: Database, mode: ModeName | None = None):
    conditions = [Score.beatmap_id == beatmap_id, col(Score.processed).is_(True)]
    if mode is not None:
        conditions.append(Score.gamemode == GameMode(mode))
    total, passed = (
        await session.exec(
            select(func.count(), func.sum(case((col(Score.passed).is_(True), 1), else_=0))).where(
                *conditions,
            )
        )
    ).one()
    return {"plays": int(total), "passes": int(passed or 0)}
