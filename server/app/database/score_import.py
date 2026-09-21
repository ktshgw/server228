"""Provenance records for scores imported by a server owner."""

from datetime import datetime
from typing import Any

from app.helpers import utcnow

from sqlalchemy import Boolean, Column, DateTime, ForeignKey, Index, Integer, String, Text, UniqueConstraint
from sqlmodel import JSON, BigInteger, Field, SQLModel


class ScoreImport(SQLModel, table=True):
    """Append-only provenance attached to one locally materialised score."""

    __tablename__: str = "score_imports"
    __table_args__ = (
        UniqueConstraint("source_fingerprint", name="uq_score_import_source_fingerprint"),
        Index("ix_score_imports_source_score", "source", "source_ruleset", "source_score_id"),
    )

    id: int | None = Field(default=None, sa_column=Column(Integer, primary_key=True, autoincrement=True))
    score_id: int | None = Field(
        sa_column=Column(
            BigInteger,
            ForeignKey("scores.id", ondelete="SET NULL"),
            nullable=True,
            unique=True,
            index=True,
        )
    )
    target_user_id: int = Field(
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="RESTRICT"), nullable=False, index=True)
    )
    imported_by_user_id: int | None = Field(
        default=None,
        sa_column=Column(Integer, ForeignKey("lazer_users.id", ondelete="SET NULL"), nullable=True, index=True),
    )
    source: str = Field(default="official_osu", sa_column=Column(String(32), nullable=False))
    source_fingerprint: str = Field(sa_column=Column(String(96), nullable=False))
    source_ruleset: str = Field(default="", sa_column=Column(String(16), nullable=False))
    source_score_id: str = Field(sa_column=Column(String(64), nullable=False))
    source_user_id: int | None = Field(default=None, sa_column=Column(BigInteger, nullable=True, index=True))
    source_username: str | None = Field(default=None, sa_column=Column(String(32), nullable=True))
    reason: str = Field(sa_column=Column(Text, nullable=False))
    replay_imported: bool = Field(default=False, sa_column=Column(Boolean, nullable=False))
    source_snapshot: dict[str, Any] = Field(default_factory=dict, sa_column=Column(JSON, nullable=False))
    imported_at: datetime = Field(
        default_factory=utcnow,
        sa_column=Column(DateTime(timezone=True), nullable=False, index=True),
    )
