"""Local PP penalties and authoritative per-difficulty mapper attribution."""

from datetime import datetime

from app.helpers import utcnow

from sqlalchemy import Column, DateTime, ForeignKey, Integer, UniqueConstraint
from sqlmodel import Field, SQLModel


class NegativePPRule(SQLModel, table=True):
    __tablename__: str = "negative_pp_rules"
    __table_args__ = (UniqueConstraint("kind", "target_id", name="uq_negative_pp_target"),)

    id: int | None = Field(default=None, primary_key=True)
    kind: str = Field(max_length=16)
    target_id: int = Field(index=True)
    label: str = Field(max_length=300)
    reason: str = Field(max_length=500)
    created_by: int = Field(foreign_key="lazer_users.id")
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))


class BeatmapMapperCredit(SQLModel, table=True):
    __tablename__: str = "beatmap_mapper_credits"

    beatmap_id: int = Field(sa_column=Column(Integer, ForeignKey("beatmaps.id", ondelete="CASCADE"), primary_key=True))
    mapper_id: int = Field(primary_key=True, index=True)
    username: str = Field(max_length=255)
