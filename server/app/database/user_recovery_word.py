"""Hashed recovery words configured from the website security page."""

from datetime import datetime

from app.helpers import utcnow

from sqlmodel import Column, DateTime, Field, ForeignKey, Integer, SQLModel


class UserRecoveryWord(SQLModel, table=True):
    """One non-reversible recovery-word hash per account."""

    __tablename__: str = "user_recovery_words"

    user_id: int = Field(
        sa_column=Column(
            Integer,
            ForeignKey("lazer_users.id", ondelete="CASCADE"),
            primary_key=True,
        )
    )
    word_hash: str = Field(max_length=60)
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
    updated_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))
