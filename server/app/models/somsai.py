"""Versioned commands used by the native SOMSAI screen and spectator."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, Field, StrictInt, field_validator


class SomsaiAction(BaseModel):
    action: Literal[
        "queue_join",
        "queue_leave",
        "party_create",
        "party_invite",
        "party_accept",
        "party_decline",
        "party_leave",
        "custom_create",
        "custom_join",
        "custom_start",
        "pool_vote",
        "ban",
        "pick",
        "ready",
        "unready",
        "leave_match",
    ]
    format: Literal["1v1", "2v2", "3v3", "4v4"] = "1v1"
    ruleset_id: int = Field(default=0, ge=0, le=3)
    variant_id: int = Field(default=0, ge=0, le=7)
    target_user_id: StrictInt | None = Field(default=None, gt=0)
    target_username: str | None = Field(default=None, min_length=1, max_length=32)
    invitation_id: StrictInt | None = Field(default=None, gt=0)
    match_id: StrictInt | None = Field(default=None, gt=0)
    slot_id: str | None = Field(default=None, max_length=12)
    expected_revision: StrictInt | None = Field(default=None, ge=1)
    team: int = Field(default=0, ge=0, le=1)
    pool_id: StrictInt | None = Field(default=None, gt=0)
    target_mmr: StrictInt | None = Field(default=None, ge=0, le=5000)
    target_rank: str | None = Field(
        default=None,
        pattern=r"^(?:ARCHSOM|(?:BRONZE|SILVER|GOLD|PLATINUM|DIAMOND) (?:I|II|III|IV|V))$",
    )
    name: str = Field(default="", max_length=100)
    with_bots: bool = False
    bot_level: Literal["easy", "medium", "hard", "top1000", "mrekk"] = "medium"

    @field_validator("bot_level", mode="before")
    @classmethod
    def legacy_bot_level(cls, value):
        # Older clients and saved rooms remain compatible with the merged tier.
        return "top1000" if value in ("expert", "impossible") else value


class SomsaiBotScore(BaseModel):
    user_id: StrictInt = Field(gt=0)
    score: StrictInt = Field(ge=0, le=10_000_000)
    accuracy: float = Field(ge=0, le=1, allow_inf_nan=False)
    max_combo: StrictInt = Field(ge=0, le=1_000_000)
    statistics: dict[str, int] = Field(default_factory=dict, max_length=32)
    maximum_statistics: dict[str, int] = Field(default_factory=dict, max_length=32)
    rank: str = Field(default="A", max_length=3)
    passed: bool = True


class SomsaiBotResults(BaseModel):
    playlist_item_id: StrictInt = Field(gt=0)
    scores: list[SomsaiBotScore] = Field(min_length=1, max_length=4)


class PartyReserveRequest(BaseModel):
    pool_id: StrictInt = Field(gt=0)
    request_id: UUID


class SomsaiRoomEvent(BaseModel):
    event: Literal["pulse", "started", "completed", "aborted"] = "pulse"
    playlist_item_id: int = Field(default=0, ge=0)
    connected: list[StrictInt] = Field(default_factory=list, max_length=8)
    ready: list[StrictInt] = Field(default_factory=list, max_length=8)
    available: list[StrictInt] | None = Field(default=None, max_length=8)
