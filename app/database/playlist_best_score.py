"""Playlist best score database models.

This module tracks users' best scores on playlist items in multiplayer rooms.
"""

from collections.abc import Sequence
from typing import TYPE_CHECKING

from .user import User

from sqlalchemy.orm import Mapped
from sqlmodel import BigInteger, Column, Field, ForeignKey, Integer, Relationship, SQLModel, col, func, select
from sqlmodel.ext.asyncio.session import AsyncSession

if TYPE_CHECKING:
    from .score import Score


class PlaylistBestScore(SQLModel, table=True):
    """Tracks best scores for playlist items in multiplayer rooms."""

    __tablename__: str = "playlist_best_scores"

    user_id: int = Field(sa_column=Column(Integer, ForeignKey("lazer_users.id"), index=True))
    score_id: int = Field(sa_column=Column(BigInteger, ForeignKey("scores.id"), primary_key=True))
    room_id: int = Field(foreign_key="rooms.id", index=True)
    playlist_id: int = Field(index=True)
    total_score: int = Field(default=0, sa_column=Column(BigInteger))
    attempts: int = Field(default=0)  # playlist

    user: Mapped[User] = Relationship()
    score: Mapped["Score"] = Relationship(
        sa_relationship_kwargs={
            "foreign_keys": "[PlaylistBestScore.score_id]",
            "lazy": "joined",
        }
    )


def partition_scores_around(
    scores: Sequence[PlaylistBestScore],
    pivot_score_id: int,
    *,
    limit: int = 10,
) -> tuple[list[PlaylistBestScore], list[PlaylistBestScore], bool, bool]:
    """Split a leaderboard into the nearest scores above and below a pivot.

    osu-web orders multiplayer scores by ``total_score DESC, score_id ASC``.
    The ``higher`` page is returned nearest-first (ascending towards the top),
    while ``lower`` is nearest-first (descending away from the pivot).  This is
    the order expected by lazer's ``PlaylistItemResultsScreen``.
    """
    if limit < 1:
        raise ValueError("limit must be positive")

    pivot = next((score for score in scores if score.score_id == pivot_score_id), None)
    if pivot is None:
        return [], [], False, False

    # One best-score row per user is expected, but excluding the pivot user as
    # well as the exact score keeps a damaged/legacy table from duplicating the
    # local player in the surrounding results.
    ordered = sorted(
        (score for score in scores if score.user_id != pivot.user_id),
        key=lambda score: (-score.total_score, score.score_id),
    )
    pivot_key = (-pivot.total_score, pivot.score_id)
    higher_all = [score for score in ordered if (-score.total_score, score.score_id) < pivot_key]
    lower_all = [score for score in ordered if (-score.total_score, score.score_id) > pivot_key]

    higher = list(reversed(higher_all[-limit:]))
    lower = lower_all[:limit]
    return higher, lower, len(higher_all) > limit, len(lower_all) > limit


async def process_playlist_best_score(
    room_id: int,
    playlist_id: int,
    user_id: int,
    score_id: int,
    total_score: int,
    session: AsyncSession,
):
    """Process and update best score for a playlist item.

    Args:
        room_id: The multiplayer room ID.
        playlist_id: The playlist item ID.
        user_id: The user ID.
        score_id: The score ID.
        total_score: The total score achieved.
        session: Database session.
        The caller owns the transaction and must commit it together with the
        score-token link and the other playlist submission aggregates.
    """
    previous = (
        await session.exec(
            select(PlaylistBestScore).where(
                PlaylistBestScore.room_id == room_id,
                PlaylistBestScore.playlist_id == playlist_id,
                PlaylistBestScore.user_id == user_id,
            )
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if previous is None:
        previous = PlaylistBestScore(
            user_id=user_id,
            score_id=score_id,
            room_id=room_id,
            playlist_id=playlist_id,
            total_score=total_score,
        )
        session.add(previous)
    elif not previous.score.passed or previous.total_score < total_score:
        previous.score_id = score_id
        previous.total_score = total_score
    previous.attempts += 1
    await session.flush()


async def get_position(
    room_id: int,
    playlist_id: int,
    score_id: int,
    session: AsyncSession,
) -> int:
    """Get the leaderboard position of a score.

    Args:
        room_id: The multiplayer room ID.
        playlist_id: The playlist item ID.
        score_id: The score ID.
        session: Database session.

    Returns:
        The position (1-indexed) of the score on the leaderboard.
    """
    rownum = (
        func.row_number()
        .over(
            partition_by=(
                col(PlaylistBestScore.playlist_id),
                col(PlaylistBestScore.room_id),
            ),
            order_by=(
                col(PlaylistBestScore.total_score).desc(),
                col(PlaylistBestScore.score_id).asc(),
            ),
        )
        .label("row_number")
    )
    subq = (
        select(PlaylistBestScore, rownum)
        .where(
            PlaylistBestScore.playlist_id == playlist_id,
            PlaylistBestScore.room_id == room_id,
        )
        .subquery()
    )
    stmt = select(subq.c.row_number).where(subq.c.score_id == score_id)
    result = await session.exec(stmt)
    s = result.one_or_none()
    return s or 0
