"""Fun compilations and their isolated scores; never part of PP/MMR tables."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import Column, Index
from sqlmodel import JSON, Field, SQLModel


class Marathon(SQLModel, table=True):
    __tablename__: str = "soms_marathons"
    __table_args__ = (Index("ix_soms_marathons_mode_deleted_id", "ruleset_id", "deleted", "id"),)

    id: int | None = Field(default=None, primary_key=True)
    owner_id: int = Field(foreign_key="lazer_users.id", index=True)
    name: str = Field(max_length=100)
    ruleset_id: int
    compiler_version: int = Field(default=1)
    segments: list[dict[str, Any]] = Field(sa_column=Column(JSON, nullable=False))
    duration_ms: int
    deleted: bool = Field(default=False)
    created_at: datetime = Field(default_factory=utcnow)


class MarathonScore(SQLModel, table=True):
    __tablename__: str = "soms_marathon_scores"
    __table_args__ = (Index("ix_soms_marathon_scores_board", "marathon_id", "total_score", "id"),)

    id: int | None = Field(default=None, primary_key=True)
    marathon_id: int = Field(foreign_key="soms_marathons.id")
    user_id: int = Field(foreign_key="lazer_users.id")
    attempt_id: str = Field(max_length=36, unique=True)
    total_score: int
    accuracy: float
    max_combo: int
    mods: list[dict[str, Any]] = Field(sa_column=Column(JSON, nullable=False))
    created_at: datetime = Field(default_factory=utcnow)
