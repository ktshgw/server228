"""Authorization helpers for private server staff actions.

The project already stores the common osu!/bancho privilege bitfield in
``User.priv`` and exposes the staff flags used by lazer profiles.  Keeping the
mapping here gives custom APIs and BanchoBot commands one authoritative place
to make access-control decisions.
"""

from collections.abc import Awaitable, Callable
from enum import StrEnum
from typing import Annotated

from app.database import User
from app.dependencies.database import Database
from app.models.error import ErrorType, RequestError

from .user import ClientUser

from fastapi import Depends


class StaffRole(StrEnum):
    """Roles understood by private administrative APIs."""

    MODERATOR = "moderator"
    RANKER = "ranker"
    BEATMAP_MODERATOR = "beatmap_moderator"
    ADMINISTRATOR = "administrator"
    OWNER = "owner"


def has_staff_role(user: User, role: StaffRole) -> bool:
    """Return whether ``user`` may act in ``role``.

    The server's explicit profile flags are authoritative. Owner and
    administrator are hierarchical; moderator and ranker stay separate to
    preserve least privilege.
    """

    if not user.is_active:
        return False

    is_owner = bool(user.is_owner)
    is_administrator = is_owner or bool(user.is_admin)

    if role is StaffRole.OWNER:
        return is_owner
    if role is StaffRole.ADMINISTRATOR:
        return is_administrator
    if role is StaffRole.RANKER:
        return is_administrator or bool(user.is_bng) or bool(user.is_qat)
    if role is StaffRole.BEATMAP_MODERATOR:
        # In-client and website ranking controls intentionally exclude QAT.
        # Only BNG members and administrators may change a beatmap's status.
        return is_administrator or bool(user.is_bng)
    if role is StaffRole.MODERATOR:
        return is_administrator or bool(user.is_gmt)
    return False


def require_staff_role(*allowed_roles: StaffRole) -> Callable[..., Awaitable[User]]:
    """Build a FastAPI dependency requiring at least one staff role."""

    if not allowed_roles:
        raise ValueError("At least one staff role is required")

    async def dependency(current_user: ClientUser, session: Database) -> User:
        if not current_user.is_active or await current_user.is_restricted(session):
            raise RequestError(ErrorType.ACCOUNT_RESTRICTED)
        if not any(has_staff_role(current_user, role) for role in allowed_roles):
            raise RequestError(
                ErrorType.FORBIDDEN,
                {"required_roles": [role.value for role in allowed_roles]},
            )
        return current_user

    return dependency


require_owner = require_staff_role(StaffRole.OWNER)
require_administrator = require_staff_role(StaffRole.ADMINISTRATOR)
require_ranker = require_staff_role(StaffRole.RANKER)
require_beatmap_moderator = require_staff_role(StaffRole.BEATMAP_MODERATOR)
require_moderator = require_staff_role(StaffRole.MODERATOR)

OwnerUser = Annotated[User, Depends(require_owner)]
AdministratorUser = Annotated[User, Depends(require_administrator)]
RankerUser = Annotated[User, Depends(require_ranker)]
BeatmapModeratorUser = Annotated[User, Depends(require_beatmap_moderator)]
ModeratorUser = Annotated[User, Depends(require_moderator)]


__all__ = [
    "AdministratorUser",
    "BeatmapModeratorUser",
    "ModeratorUser",
    "OwnerUser",
    "RankerUser",
    "StaffRole",
    "has_staff_role",
    "require_administrator",
    "require_beatmap_moderator",
    "require_moderator",
    "require_owner",
    "require_ranker",
    "require_staff_role",
]
