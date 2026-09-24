"""Persistent audit trail for privileged web-panel actions."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import Column, DateTime, ForeignKey, Integer, String, Text
from sqlmodel import JSON, Field, SQLModel


class AdminAuditEvent(SQLModel, table=True):
    """One append-only administrative action."""

    __tablename__: str = "admin_audit_events"

    id: int | None = Field(default=None, sa_column=Column(Integer, primary_key=True, autoincrement=True))
    actor_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    actor_username: str = Field(sa_column=Column(String(32), nullable=False, index=True))
    action: str = Field(sa_column=Column(String(64), nullable=False, index=True))
    target_type: str = Field(sa_column=Column(String(32), nullable=False, index=True))
    target_id: str = Field(sa_column=Column(String(64), nullable=False, index=True))
    reason: str = Field(sa_column=Column(Text, nullable=False))
    before: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON, nullable=True))
    after: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON, nullable=True))
    ip_address: str | None = Field(default=None, sa_column=Column(String(45), nullable=True))
    created_at: datetime = Field(
        default_factory=utcnow,
        sa_column=Column(DateTime(timezone=True), nullable=False, index=True),
    )
