"""Transactional helpers shared by website and lazer score-pin endpoints."""

from dataclasses import dataclass

from app.database import Score, User

from sqlmodel import col, select
from sqlmodel.ext.asyncio.session import AsyncSession


@dataclass(slots=True)
class LockedScorePinState:
    score: Score | None
    pinned_scores: list[Score]
    order_normalised: bool


def reordered_score_pin_ids(
    ordered_ids: list[int],
    score_id: int,
    *,
    before_score_id: int | None,
    after_score_id: int | None,
) -> list[int]:
    """Return a compact, stable pin order after one relative move."""

    if score_id not in ordered_ids:
        raise ValueError("Score is not pinned")
    reference_id = before_score_id if before_score_id is not None else after_score_id
    if reference_id is None or reference_id not in ordered_ids:
        raise ValueError("Reference score is not pinned")
    if reference_id == score_id:
        raise ValueError("A score cannot be reordered relative to itself")
    reordered = [item for item in ordered_ids if item != score_id]
    reference_index = reordered.index(reference_id)
    destination = reference_index if before_score_id is not None else reference_index + 1
    reordered.insert(destination, score_id)
    return reordered


async def lock_score_pin_state(session: AsyncSession, user_id: int, score_id: int) -> LockedScorePinState:
    """Lock one user's pin namespace and compact its selected ruleset order.

    The user row is the stable lock sentinel, including when the ruleset has no
    pins yet. Every score-pin writer must enter through this helper so concurrent
    website and lazer requests use the same lock order.
    """

    owner_id = (
        await session.exec(select(User.id).where(User.id == user_id).with_for_update())
    ).first()
    if owner_id is None:
        return LockedScorePinState(score=None, pinned_scores=[], order_normalised=False)

    score = (
        await session.exec(
            select(Score).where(Score.id == score_id, Score.user_id == user_id).with_for_update()
        )
    ).first()
    if score is None:
        return LockedScorePinState(score=None, pinned_scores=[], order_normalised=False)

    pinned_scores = list(
        (
            await session.exec(
                select(Score)
                .where(
                    Score.user_id == user_id,
                    Score.gamemode == score.gamemode,
                    Score.pinned_order > 0,
                )
                .order_by(col(Score.pinned_order), col(Score.id))
                .with_for_update()
            )
        ).all()
    )
    order_normalised = False
    for expected_order, pinned_score in enumerate(pinned_scores, start=1):
        if pinned_score.pinned_order == expected_order:
            continue
        pinned_score.pinned_order = expected_order
        session.add(pinned_score)
        order_normalised = True

    return LockedScorePinState(
        score=score,
        pinned_scores=pinned_scores,
        order_normalised=order_normalised,
    )


__all__ = ["LockedScorePinState", "lock_score_pin_state", "reordered_score_pin_ids"]
