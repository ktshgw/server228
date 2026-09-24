"""Persistent tournament matches, independent ratings and shared queue parties."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import Column, DateTime, Index, UniqueConstraint
from sqlmodel import JSON, Field, SQLModel


class SomsaiLock(SQLModel, table=True):
    __tablename__: str = "somsai_lock"
    id: int = Field(default=1, primary_key=True)


class SomsaiNativeRoom(SQLModel, table=True):
    __tablename__: str = "somsai_native_rooms"
    user_id: int = Field(primary_key=True, foreign_key="lazer_users.id")
    room_id: int
    expires_at: datetime = Field(sa_column=Column(DateTime, nullable=False))


class SomsaiParty(SQLModel, table=True):
    __tablename__: str = "somsai_parties"
    id: int | None = Field(default=None, primary_key=True)
    captain_id: int = Field(foreign_key="lazer_users.id")
    members: list[int] = Field(sa_column=Column(JSON, nullable=False))
    active: bool = True
    revision: int = 1
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))


class SomsaiPartyInvite(SQLModel, table=True):
    __tablename__: str = "somsai_party_invites"
    id: int | None = Field(default=None, primary_key=True)
    party_id: int = Field(foreign_key="somsai_parties.id")
    inviter_id: int = Field(foreign_key="lazer_users.id")
    target_id: int = Field(foreign_key="lazer_users.id", index=True)
    status: str = Field(default="pending", max_length=16)
    expires_at: datetime = Field(sa_column=Column(DateTime, nullable=False))


class SomsaiActivity(SQLModel, table=True):
    __tablename__: str = "somsai_activity"
    user_id: int = Field(primary_key=True, foreign_key="lazer_users.id")
    party_id: int | None = Field(default=None, foreign_key="somsai_parties.id")
    reservation_id: str | None = Field(default=None, max_length=36, index=True)
    match_id: int | None = Field(default=None, index=True)


class SomsaiReservation(SQLModel, table=True):
    __tablename__: str = "somsai_reservations"
    id: str = Field(primary_key=True, max_length=36)
    captain_id: int = Field(foreign_key="lazer_users.id")
    party_id: int | None = Field(default=None)
    kind: str = Field(max_length=32)
    members: list[int] = Field(sa_column=Column(JSON, nullable=False))
    released: bool = False
    expires_at: datetime = Field(sa_column=Column(DateTime, nullable=False, index=True))


class SomsaiRating(SQLModel, table=True):
    __tablename__: str = "somsai_ratings"
    __table_args__ = (UniqueConstraint("user_id", "ruleset_id", "variant_id", "format", name="uq_somsai_rating"),)
    id: int | None = Field(default=None, primary_key=True)
    user_id: int = Field(foreign_key="lazer_users.id", index=True)
    ruleset_id: int = 0
    variant_id: int = 0
    format: str = Field(max_length=8)
    rating: float = 1500
    initial_rating: float = 1500
    initial_rank: int | None = None
    initial_population: int = 0
    wins: int = 0
    losses: int = 0
    draws: int = 0
    games: int = 0
    last_delta: float = 0
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    updated_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))


class SomsaiQueue(SQLModel, table=True):
    __tablename__: str = "somsai_queue"
    __table_args__ = (Index("ix_somsai_queue_format", "ruleset_id", "variant_id", "format"),)
    id: int | None = Field(default=None, primary_key=True)
    reservation_id: str = Field(max_length=36, unique=True)
    captain_id: int
    members: list[int] = Field(sa_column=Column(JSON, nullable=False))
    format: str = Field(max_length=8)
    ruleset_id: int = 0
    variant_id: int = 0
    rating: float
    joined_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    expires_at: datetime = Field(sa_column=Column(DateTime, nullable=False))


class SomsaiMatch(SQLModel, table=True):
    __tablename__: str = "somsai_matches"
    id: int | None = Field(default=None, primary_key=True)
    name: str = Field(max_length=100)
    format: str = Field(max_length=8)
    ruleset_id: int = 0
    variant_id: int = 0
    ranked: bool = True
    owner_id: int = Field(foreign_key="lazer_users.id")
    pool_id: int
    # Rooms are transient; match history and rating changes must survive cleanup.
    room_id: int | None = Field(default=None, index=True)
    password: str = Field(default="", max_length=64)
    stage: str = Field(default="waiting", max_length=16, index=True)
    revision: int = 1
    state: dict[str, Any] = Field(sa_column=Column(JSON, nullable=False))
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    updated_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    ended_at: datetime | None = Field(default=None, sa_column=Column(DateTime))
