"""Matchmaking system database models.

This module provides models for matchmaking pools, user stats,
and pool beatmap configurations.
"""

from datetime import datetime
from typing import TYPE_CHECKING, Any, Optional

from app.models.model import UTCBaseModel
from app.models.mods import APIMod

from sqlalchemy import (
    BigInteger,
    Column,
    Computed,
    DateTime,
    Float,
    ForeignKey,
    Index,
    Integer,
    SmallInteger,
    String,
    UniqueConstraint,
)
from sqlalchemy.orm import Mapped
from sqlmodel import JSON, Field, Relationship, SQLModel, func

if TYPE_CHECKING:
    from .beatmap import Beatmap
    from .user import User


class MatchmakingUserStatsBase(SQLModel, UTCBaseModel):
    """Base fields for matchmaking user statistics."""

    user_id: int = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id"), nullable=False),
    )
    pool_id: int | None = Field(
        default=None,
        sa_column=Column(ForeignKey("matchmaking_pools.id"), nullable=True),
    )
    first_placements: int = Field(default=0, ge=0)
    total_points: int = Field(default=0, ge=0)
    elo_data: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON))
    created_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), server_default=func.now()),
    )
    updated_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), server_default=func.now(), onupdate=func.now()),
    )


class MatchmakingUserStats(MatchmakingUserStatsBase, table=True):
    """Database table for matchmaking user statistics."""

    __tablename__: str = "matchmaking_user_stats"
    __table_args__ = (
        UniqueConstraint("user_id", "pool_id", name="uq_matchmaking_user_pool"),
        Index("ix_matchmaking_stats_user", "user_id"),
        Index("matchmaking_user_stats_pool_first_idx", "pool_id", "first_placements"),
        Index("matchmaking_user_stats_pool_points_idx", "pool_id", "total_points"),
    )

    id: int | None = Field(default=None, primary_key=True)

    user: Mapped["User"] = Relationship(back_populates="matchmaking_stats", sa_relationship_kwargs={"lazy": "joined"})
    pool: Mapped["MatchmakingPool"] = Relationship()


class MatchmakingPoolBase(SQLModel, UTCBaseModel):
    """Base fields for matchmaking pools."""

    id: int | None = Field(default=None, primary_key=True)
    ruleset_id: int = Field(
        default=0,
        sa_column=Column(SmallInteger, nullable=False),
    )
    name: str = Field(max_length=255)
    active: bool = Field(default=True)
    variant_id: int = Field(default=0, sa_column=Column(SmallInteger, nullable=False, server_default="0"))
    type: str = Field(default="quick_play", max_length=32)
    ranked: bool = Field(default=False)
    lobby_size: int = Field(default=8)
    rating_search_radius: int = Field(default=20)
    rating_search_radius_max: int = Field(default=9999)
    rating_search_radius_exp: int = Field(default=15)
    use_dmr: bool = Field(default=False)
    beatmap_preset_id: int | None = Field(default=None, foreign_key="matchmaking_map_presets.id")
    created_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), server_default=func.now()),
    )
    updated_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), server_default=func.now(), onupdate=func.now()),
    )


class MatchmakingMapPreset(SQLModel, table=True):
    """Operator-controlled catalogue filters, independent from player Elo."""

    __tablename__: str = "matchmaking_map_presets"

    id: int | None = Field(default=None, primary_key=True)
    name: str = Field(max_length=80)
    ruleset_id: int
    variant_id: int = Field(default=0)
    min_stars: float = Field(default=0)
    max_stars: float = Field(default=15)
    min_length: int = Field(default=60)
    max_length: int = Field(default=240)
    beatmap_ids: list[int] | None = Field(default=None, sa_column=Column(JSON))
    revision: int = Field(default=1)


class MatchmakingPool(MatchmakingPoolBase, table=True):
    """Database table for matchmaking pools."""

    __tablename__: str = "matchmaking_pools"
    __table_args__ = (
        Index("matchmaking_pools_ruleset_active_idx", "ruleset_id", "active"),
        UniqueConstraint("ruleset_id", "variant_id", "name", "type", name="uq_matchmaking_pool_identity"),
    )

    beatmaps: Mapped[list["MatchmakingPoolBeatmap"]] = Relationship(
        back_populates="pool",
        # sa_relationship_kwargs={
        #     "lazy": "selectin",
        # },
    )


class MatchmakingPoolBeatmapBase(SQLModel, UTCBaseModel):
    """Base fields for beatmaps in matchmaking pools."""

    id: int | None = Field(default=None, primary_key=True)
    pool_id: int = Field(
        default=None,
        sa_column=Column(ForeignKey("matchmaking_pools.id"), nullable=False, index=True),
    )
    beatmap_id: int = Field(
        default=None,
        sa_column=Column(ForeignKey("beatmaps.id"), nullable=False),
    )
    mods: list[APIMod] | None = Field(default=None, sa_column=Column(JSON))
    rating: float = Field(default=1500, sa_column=Column(Float, nullable=False))
    rating_sig: float = Field(default=150, sa_column=Column(Float, nullable=False, server_default="150"))
    selection_count: int = Field(default=0)


class MatchmakingPoolBeatmap(MatchmakingPoolBeatmapBase, table=True):
    __tablename__: str = "matchmaking_pool_beatmaps"
    __table_args__ = (UniqueConstraint("pool_id", "beatmap_id", "mods_key", name="uq_matchmaking_pool_beatmap_mods"),)

    mods_key: str | None = Field(
        default=None,
        sa_column=Column(String(64), Computed("sha2(cast(coalesce(mods, json_array()) as char), 256)", persisted=True)),
    )

    pool: Mapped[MatchmakingPool] = Relationship(back_populates="beatmaps")
    beatmap: Mapped[Optional["Beatmap"]] = Relationship(
        # sa_relationship_kwargs={"lazy": "joined"},
    )


class MatchmakingRoomEvent(SQLModel, table=True):
    __tablename__: str = "matchmaking_room_events"

    id: int | None = Field(default=None, sa_column=Column(BigInteger, primary_key=True, autoincrement=True))
    room_id: int = Field(foreign_key="rooms.id", index=True)
    event_type: str = Field(max_length=64)
    playlist_item_id: int | None = Field(default=None)
    user_id: int | None = Field(default=None, foreign_key="lazer_users.id")
    event_detail: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON))
    created_at: datetime = Field(sa_column=Column(DateTime, nullable=False, server_default=func.now()))
    updated_at: datetime = Field(sa_column=Column(DateTime, nullable=False, server_default=func.now()))


class MatchmakingUserEloHistory(SQLModel, table=True):
    __tablename__: str = "matchmaking_user_elo_history"

    id: int | None = Field(default=None, sa_column=Column(BigInteger, primary_key=True, autoincrement=True))
    room_id: int = Field(foreign_key="rooms.id", index=True)
    pool_id: int = Field(foreign_key="matchmaking_pools.id")
    user_id: int = Field(foreign_key="lazer_users.id", index=True)
    opponent_id: int = Field(foreign_key="lazer_users.id")
    result: str = Field(max_length=8)
    elo_before: int
    elo_after: int
    created_at: datetime = Field(sa_column=Column(DateTime, nullable=False, server_default=func.now()))
    updated_at: datetime = Field(sa_column=Column(DateTime, nullable=False, server_default=func.now()))
