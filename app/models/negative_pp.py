from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class NegativePPTarget(BaseModel):
    model_config = ConfigDict(str_strip_whitespace=True, extra="forbid")
    kind: Literal["beatmap", "beatmapset", "mapper"]
    reference: str = Field(min_length=1, max_length=300)


class NegativePPWrite(NegativePPTarget):
    reason: str = Field(default="no reason", max_length=500)


class NegativePPDelete(BaseModel):
    model_config = ConfigDict(str_strip_whitespace=True, extra="forbid")
    reason: str = Field(default="no reason", max_length=500)
