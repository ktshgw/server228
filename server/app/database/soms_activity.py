"""Durable SOMS announcements and supporter inbox; written with the source transaction."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import JSON, Column, DateTime, Index, String
from sqlmodel import Field, SQLModel


class SomsActivity(SQLModel, table=True):
    __tablename__ = "soms_activity"
    __table_args__ = (
        Index("ix_soms_activity_recipient_read_id", "recipient_id", "is_read", "id"),
        Index("ix_soms_activity_delivered_id", "delivered", "id"),
    )

    id: int | None = Field(default=None, primary_key=True)
    event_key: str = Field(sa_column=Column(String(160), nullable=False, unique=True))
    kind: str = Field(max_length=32)
    actor_id: int | None = Field(default=None)
    recipient_id: int | None = Field(default=None)
    payload: dict[str, Any] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
    announcement: str | None = Field(default=None, max_length=1000)
    chat_message_id: int | None = Field(default=None)
    delivered: bool = False
    is_read: bool = False
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
