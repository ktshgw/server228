"""Allocate and resolve short website-facing user identifiers."""

from contextlib import suppress

from app.database.user import User

from sqlalchemy import func, text
from sqlmodel import col, or_, select
from sqlmodel.ext.asyncio.session import AsyncSession

_SERVER_ID_LOCK = "private_osu_server_user_id"


async def assign_server_id(session: AsyncSession, user: User) -> int:
    """Assign the next short human-user ID while holding a MySQL named lock."""

    if user.server_id is not None:
        return user.server_id

    acquired = (await session.execute(text("SELECT GET_LOCK(:name, 10)"), {"name": _SERVER_ID_LOCK})).scalar_one()
    if acquired != 1:
        raise RuntimeError("Could not allocate a short server user ID")
    try:
        highest = (
            await session.exec(
                select(func.max(col(User.server_id))).where(
                    col(User.is_bot).is_(False),
                    col(User.server_id).is_not(None),
                )
            )
        ).one()
        user.server_id = int(highest or 0) + 1
        session.add(user)
        await session.flush()
        return user.server_id
    finally:
        # If a flush invalidated the transaction, MySQL releases the named
        # lock when that connection is returned/closed.
        if session.in_transaction():
            with suppress(Exception):
                await session.execute(text("SELECT RELEASE_LOCK(:name)"), {"name": _SERVER_ID_LOCK})


async def resolve_human_user(session: AsyncSession, identifier: int, *, for_update: bool = False) -> User | None:
    """Resolve a short server ID first, then a collision-safe internal ID."""

    statement = select(User).where(
        col(User.is_bot).is_(False),
        or_(col(User.server_id) == identifier, col(User.id) == identifier),
    )
    # A real user's short ID can theoretically equal BanchoBot's internal ID;
    # explicit ordering guarantees that server_id remains the public meaning.
    statement = statement.order_by((col(User.server_id) == identifier).desc(), col(User.id)).limit(1)
    if for_update:
        statement = statement.with_for_update().execution_options(populate_existing=True)
    return (await session.exec(statement)).first()
