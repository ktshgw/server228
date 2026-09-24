from typing import Annotated, Literal
from uuid import UUID

from app.models.mods import APIMod

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator


class MarathonSegment(BaseModel):
    model_config = ConfigDict(extra="forbid")
    beatmap_id: int = Field(ge=-1)
    beatmapset_id: int = Field(ge=-1)
    checksum: Annotated[str, StringConstraints(pattern=r"^[a-fA-F0-9]{32}$")]
    title: str = Field(min_length=1, max_length=200)
    start_ms: int = Field(ge=0, le=7_200_000)
    end_ms: int = Field(ge=1, le=7_200_000)

    @model_validator(mode="after")
    def valid_range(self):
        if not 5_000 <= self.end_ms - self.start_ms <= 180_000:
            raise ValueError("Each fragment must last between 5 and 180 seconds")
        self.checksum = self.checksum.lower()
        return self


class CreateMarathon(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: Annotated[str, StringConstraints(strip_whitespace=True, min_length=1, max_length=100)]
    ruleset_id: int = Field(ge=0, le=3)
    compiler_version: Literal[1] = 1
    segments: list[MarathonSegment] = Field(min_length=2, max_length=20)

    @model_validator(mode="after")
    def editor_fragment_limit(self):
        # Previously saved compilations remain playable; new excerpts use the editor's 90s cap.
        if any(segment.end_ms - segment.start_ms > 90_000 for segment in self.segments):
            raise ValueError("New marathon fragments must not exceed 90 seconds")
        return self


class StartMarathon(BaseModel):
    mods: list[APIMod] = Field(default_factory=list, max_length=20)


class SubmitMarathonScore(BaseModel):
    attempt_id: UUID
    total_score: int = Field(ge=0, le=10_000_000)
    accuracy: float = Field(ge=0, le=1, allow_inf_nan=False)
    max_combo: int = Field(ge=0, le=500_000)
