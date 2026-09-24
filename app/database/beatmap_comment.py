"""Local website discussions, independent of imported catalogue rows."""

from datetime import datetime

from app.helpers import utcnow

from sqlalchemy import Column, DateTime, Index, Text
from sqlmodel import Field, SQLModel


class BeatmapComment(SQLModel, table=True):
    __tablename__ = "web_beatmap_comments"
    __table_args__ = (Index("ix_web_beatmap_comments_set_created", "beatmapset_id", "created_at", "id"),)

    id: int | None = Field(default=None, primary_key=True)
    # A catalogue page does not need to materialise the set in the game database.
    beatmapset_id: int
    user_id: int = Field(foreign_key="lazer_users.id", index=True)
    body: str = Field(sa_column=Column(Text, nullable=False))
    created_at: datetime = Field(default_factory=utcnow, sa_column=Column(DateTime, nullable=False))


class BeatmapCommentVote(SQLModel, table=True):
    __tablename__ = "web_beatmap_comment_votes"

    comment_id: int = Field(primary_key=True, foreign_key="web_beatmap_comments.id", ondelete="CASCADE")
    user_id: int = Field(primary_key=True, foreign_key="lazer_users.id")
