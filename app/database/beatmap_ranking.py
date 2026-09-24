"""Local beatmap ranking policies, audit history, and operator events.

The existing ``Beatmap.beatmap_status`` and ``Beatmap.checksum`` fields are
upstream osu! metadata.  Local ranking decisions deliberately live in the
tables below so an upstream refresh can never silently turn a server policy
into a different policy.
"""

from datetime import datetime
from enum import StrEnum
from typing import Any

from app.helpers import utcnow
from app.models.beatmap import BeatmapRankStatus

from sqlalchemy import Boolean, Column, DateTime, ForeignKey, Integer, Text
from sqlmodel import JSON, Field, SQLModel


class RankingPolicyScope(StrEnum):
    """The level at which a local ranking decision was made."""

    BEATMAPSET = "beatmapset"
    BEATMAP = "beatmap"


class RankingPolicyAction(StrEnum):
    """Audited mutations of local ranking state."""

    APPLY = "apply"
    CLEAR = "clear"
    INVALIDATE = "invalidate"
    RESOLVE_EVENT = "resolve_event"


class RankingEventType(StrEnum):
    """Events requiring attention from a server operator."""

    UPSTREAM_REVISION_CHANGED = "upstream_revision_changed"


class BeatmapsetRankingPolicy(SQLModel, table=True):
    """Default local policy for the difficulties in one beatmapset.

    ``revision_manifest`` pins every difficulty which was reviewed when this
    policy was applied.  New or subsequently edited difficulties do not inherit
    the policy until an operator ranks the set again.
    """

    __tablename__: str = "beatmapset_ranking_policies"

    beatmapset_id: int = Field(
        sa_column=Column(
            Integer,
            ForeignKey("beatmapsets.id", ondelete="CASCADE"),
            primary_key=True,
        )
    )
    status: BeatmapRankStatus = Field(index=True)
    leaderboard_enabled: bool = Field(sa_column=Column(Boolean, nullable=False))
    pp_enabled: bool = Field(sa_column=Column(Boolean, nullable=False))
    # A restrictive local override is safe across upstream revisions: unlike
    # a local rank, it can never grant leaderboard or PP to unreviewed data.
    force_unranked: bool = Field(default=False, sa_column=Column(Boolean, nullable=False))
    revision_manifest: dict[str, str] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
    is_active: bool = Field(default=True, sa_column=Column(Boolean, nullable=False, index=True))
    reason: str = Field(sa_column=Column(Text, nullable=False))
    created_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    updated_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))
    updated_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))


class BeatmapRankingPolicy(SQLModel, table=True):
    """A per-difficulty local policy overriding a beatmapset default.

    When ``blocks_set_policy`` is true, the difficulty deliberately follows its
    upstream status instead.  This makes it possible to unrank one difficulty
    from an otherwise locally-ranked set without deleting any policy history.
    """

    __tablename__: str = "beatmap_ranking_policies"

    beatmap_id: int = Field(
        sa_column=Column(
            Integer,
            ForeignKey("beatmaps.id", ondelete="CASCADE"),
            primary_key=True,
        )
    )
    beatmapset_id: int = Field(
        sa_column=Column(
            Integer,
            ForeignKey("beatmapsets.id", ondelete="CASCADE"),
            nullable=False,
            index=True,
        )
    )
    status: BeatmapRankStatus = Field(index=True)
    leaderboard_enabled: bool = Field(sa_column=Column(Boolean, nullable=False))
    pp_enabled: bool = Field(sa_column=Column(Boolean, nullable=False))
    force_unranked: bool = Field(default=False, sa_column=Column(Boolean, nullable=False))
    blocks_set_policy: bool = Field(default=False, sa_column=Column(Boolean, nullable=False, index=True))
    ranked_checksum: str = Field(max_length=32, index=True)
    is_active: bool = Field(default=True, sa_column=Column(Boolean, nullable=False, index=True))
    reason: str = Field(sa_column=Column(Text, nullable=False))
    invalidated_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), nullable=True, index=True),
    )
    invalidation_reason: str | None = Field(default=None, sa_column=Column(Text, nullable=True))
    created_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    updated_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))
    updated_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))


class BeatmapRankingAudit(SQLModel, table=True):
    """Append-only audit trail for local ranking decisions."""

    __tablename__: str = "beatmap_ranking_audits"

    id: int | None = Field(default=None, primary_key=True)
    action: RankingPolicyAction = Field(index=True)
    scope: RankingPolicyScope = Field(index=True)
    beatmapset_id: int = Field(index=True)
    beatmap_id: int | None = Field(default=None, index=True)
    actor_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    reason: str = Field(sa_column=Column(Text, nullable=False))
    before: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON, nullable=True))
    after: dict[str, Any] | None = Field(default=None, sa_column=Column(JSON, nullable=True))
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))


class BeatmapRankingEvent(SQLModel, table=True):
    """Persistent admin inbox entry created when a local rank needs review."""

    __tablename__: str = "beatmap_ranking_events"

    id: int | None = Field(default=None, primary_key=True)
    event_type: RankingEventType = Field(index=True)
    scope: RankingPolicyScope = Field(index=True)
    beatmapset_id: int = Field(index=True)
    beatmap_id: int = Field(index=True)
    old_checksum: str | None = Field(default=None, max_length=32)
    new_checksum: str | None = Field(default=None, max_length=32)
    old_last_updated: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), nullable=True),
    )
    new_last_updated: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), nullable=True),
    )
    reason: str = Field(sa_column=Column(Text, nullable=False))
    details: dict[str, Any] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime(timezone=True), nullable=False))
    resolved_at: datetime | None = Field(
        default=None,
        sa_column=Column(DateTime(timezone=True), nullable=True, index=True),
    )
    resolved_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
