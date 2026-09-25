"""Local comments and voting for catalogue beatmap pages."""

from app.database import BeatmapComment, BeatmapCommentVote, User

from fastapi import HTTPException
from sqlmodel import col, func, select


def can_delete_comment(comment, viewer):
    return bool(viewer and (viewer.id == comment.user_id or viewer.is_owner or viewer.is_admin or viewer.is_gmt))


async def comment_page(session, beatmapset_id, viewer, page=1, sort="new"):
    votes = (
        select(BeatmapCommentVote.comment_id, func.count().label("votes"))
        .group_by(col(BeatmapCommentVote.comment_id))
        .subquery()
    )
    visible = [
        col(BeatmapComment.beatmapset_id) == beatmapset_id,
        col(User.is_active).is_(True),
        ~User.is_restricted_query(col(User.id)),
    ]
    total = int((await session.exec(select(func.count()).select_from(BeatmapComment).join(User).where(*visible))).one())
    count = func.coalesce(votes.c.votes, 0)
    order = {
        "new": (col(BeatmapComment.id).desc(),),
        "old": (col(BeatmapComment.id).asc(),),
        "top": (count.desc(), col(BeatmapComment.id).desc()),
    }[sort]
    rows = (
        await session.exec(
            select(BeatmapComment, User, count)
            .join(User)
            .outerjoin(votes, votes.c.comment_id == col(BeatmapComment.id))
            .where(*visible)
            .order_by(*order)
            .offset((page - 1) * 20)
            .limit(20)
        )
    ).all()
    liked = (
        set(
            (
                await session.exec(
                    select(BeatmapCommentVote.comment_id).where(
                        BeatmapCommentVote.user_id == viewer.id,
                        col(BeatmapCommentVote.comment_id).in_([row[0].id for row in rows]),
                    )
                )
            ).all()
        )
        if viewer and rows
        else set()
    )
    return rows, liked, total


async def add_comment(session, user, beatmapset_id, body):
    # Serialize account writes so concurrent posts cannot bypass the cooldown.
    await session.exec(select(User.id).where(User.id == user.id).with_for_update())
    previous = (
        await session.exec(
            select(BeatmapComment)
            .where(BeatmapComment.user_id == user.id)
            .order_by(col(BeatmapComment.id).desc())
            .limit(1)
            .with_for_update()
        )
    ).first()
    from app.helpers import utcnow

    now = utcnow()
    if previous and (now.replace(tzinfo=None) - previous.created_at.replace(tzinfo=None)).total_seconds() < 15:
        raise HTTPException(429, "Подождите 15 секунд перед следующим комментарием")
    comment = BeatmapComment(beatmapset_id=beatmapset_id, user_id=user.id, body=body, created_at=now)
    session.add(comment)
    await session.commit()


async def remove_comment(session, user, comment_id):
    comment = await session.get(BeatmapComment, comment_id)
    if comment is None:
        raise HTTPException(404, "Комментарий не найден")
    if not can_delete_comment(comment, user):
        raise HTTPException(403, "Вы можете удалять только свои комментарии")
    await session.delete(comment)
    await session.commit()


async def vote_comment(session, user, comment_id, active):
    comment = (
        await session.exec(select(BeatmapComment).where(BeatmapComment.id == comment_id).with_for_update())
    ).first()
    if comment is None:
        raise HTTPException(404, "Комментарий не найден")
    vote = (
        await session.exec(
            select(BeatmapCommentVote)
            .where(BeatmapCommentVote.comment_id == comment_id, BeatmapCommentVote.user_id == user.id)
            .with_for_update()
        )
    ).first()
    if active and vote is None:
        session.add(BeatmapCommentVote(comment_id=comment_id, user_id=user.id))
    elif not active and vote is not None:
        await session.delete(vote)
    await session.commit()
