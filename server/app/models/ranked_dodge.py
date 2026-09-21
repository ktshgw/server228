"""Internal spectator contract for persistent Ranked penalties."""

from datetime import datetime

from pydantic import BaseModel, Field, StrictInt


class RankedDodgeRequest(BaseModel):
    room_id: StrictInt = Field(gt=0)
    user_id: StrictInt = Field(gt=0)
    # The authenticated spectator already checked the live room state. Its
    # deferred write may arrive after the empty room was cleaned up.
    allow_missing_room: bool = False


class RankedDodgeStatus(BaseModel):
    user_id: int
    level: int = 0
    expires_at: datetime | None = None
    account_banned: bool = False
