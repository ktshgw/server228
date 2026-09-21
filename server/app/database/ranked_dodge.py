"""Persistent, idempotent penalties for abandoning an unconfirmed Ranked hand."""

from datetime import datetime

from sqlalchemy import Column, DateTime, Index
from sqlmodel import Field, SQLModel


class RankedDodgePenalty(SQLModel, table=True):
    __tablename__: str = "ranked_dodge_penalties"
    __table_args__ = (Index("ix_ranked_dodge_user_created", "user_id", "created_at"),)

    # One cancellation and one penalised player per room, even after a retry.
    # Retain progression even if completed rooms are later removed.
    room_id: int = Field(primary_key=True)
    user_id: int = Field(foreign_key="lazer_users.id")
    level: int
    created_at: datetime = Field(sa_column=Column(DateTime, nullable=False))
    expires_at: datetime | None = Field(default=None, sa_column=Column(DateTime, nullable=True))
    account_banned: bool = False
