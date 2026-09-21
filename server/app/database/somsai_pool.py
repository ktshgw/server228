"""Versioned tournament catalogues; matches retain their own immutable snapshot."""

from typing import Any

from sqlalchemy import Column, Index
from sqlmodel import JSON, Field, SQLModel


class SomsaiPool(SQLModel, table=True):
    __tablename__: str = "somsai_pools"
    __table_args__ = (Index("ix_somsai_pool_mode_active", "ruleset_id", "variant_id", "active"),)

    id: int | None = Field(default=None, primary_key=True)
    name: str = Field(max_length=160)
    ruleset_id: int = Field(default=0)
    variant_id: int = Field(default=0)
    active: bool = Field(default=False)
    rating_min: int = Field(default=0)
    rating_max: int = Field(default=5000)
    best_of: int = Field(default=7)
    bans_per_team: int = Field(default=1)
    slots: list[dict[str, Any]] = Field(default_factory=list, sa_column=Column(JSON, nullable=False))
    revision: int = Field(default=1)
    source_kind: str = Field(default="manual", max_length=32)
    source_id: int | None = Field(default=None)
    source_round: str | None = Field(default=None, max_length=160)
    source_url: str | None = Field(default=None, max_length=500)
    source_rank_min: int | None = Field(default=None)
    source_rank_max: int | None = Field(default=None)
    source_metadata: dict[str, Any] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
