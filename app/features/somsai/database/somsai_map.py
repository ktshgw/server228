"""Reusable SOMSAI map warehouse entries."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import Column, DateTime, Index, UniqueConstraint
from sqlmodel import JSON, Field, SQLModel


class SomsaiMap(SQLModel, table=True):
    __tablename__: str = "somsai_maps"
    __table_args__ = (
        UniqueConstraint("slot", "beatmap_id", name="uq_somsai_map_slot_beatmap"),
        Index("ix_somsai_map_slot_ruleset", "slot", "ruleset_id", "variant_id"),
    )

    id: int | None = Field(default=None, primary_key=True)
    slot: str = Field(max_length=4, index=True)
    category: str = Field(max_length=2)
    beatmap_id: int = Field(index=True)
    beatmapset_id: int
    ruleset_id: int = 0
    variant_id: int = 0
    checksum: str | None = Field(default=None, max_length=32)
    artist: str = Field(default="", max_length=255)
    title: str = Field(default="", max_length=255)
    version: str = Field(default="", max_length=255)
    cover_url: str | None = Field(default=None, max_length=1000)
    mods: list[dict[str, Any]] = Field(default_factory=list, sa_column=Column(JSON, nullable=False))
    stats: dict[str, Any] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
    eligible_ranks: list[str] = Field(default_factory=list, sa_column=Column(JSON, nullable=False))
    eligibility_label: str = Field(default="", max_length=255)
    source_kind: str = Field(default="manual", max_length=32)
    source_url: str | None = Field(default=None, max_length=500)
    source_round: str | None = Field(default=None, max_length=160)
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    refreshed_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
