"""Contracts for the Ranked catalogue editor."""

from typing import Annotated, Self

from pydantic import BaseModel, ConfigDict, Field, StrictInt, StringConstraints, model_validator


class RankedPresetSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    name: Annotated[str, StringConstraints(strip_whitespace=True, min_length=2, max_length=80)]
    ruleset_id: Annotated[StrictInt, Field(ge=0, le=3)]
    variant_id: StrictInt = 0
    min_stars: Annotated[float, Field(ge=0, le=20)] = 0
    max_stars: Annotated[float, Field(ge=0, le=20)] = 15
    min_length: Annotated[StrictInt, Field(ge=0, le=1800)] = 60
    max_length: Annotated[StrictInt, Field(ge=1, le=1800)] = 240
    beatmap_ids: Annotated[list[Annotated[StrictInt, Field(gt=0)]], Field(min_length=1, max_length=1000)] | None = None

    @model_validator(mode="after")
    def validate_ranges(self) -> Self:
        if self.variant_id not in ((4, 7) if self.ruleset_id == 3 else (0,)):
            raise ValueError("Choose 4K or 7K for mania; other rulesets use variant 0")
        if self.min_stars > self.max_stars or self.min_length > self.max_length:
            raise ValueError("Minimum must not exceed maximum")
        if self.beatmap_ids is not None:
            self.beatmap_ids = list(dict.fromkeys(self.beatmap_ids))
        return self


class RankedPresetWrite(RankedPresetSpec):
    expected_revision: Annotated[StrictInt, Field(ge=1)] | None = None
    reason: Annotated[str, StringConstraints(strip_whitespace=True, max_length=500)] = "no reason"


class RankedPresetAssignment(BaseModel):
    model_config = ConfigDict(extra="forbid")

    preset_id: Annotated[StrictInt, Field(gt=0)] | None
    expected_preset_id: Annotated[StrictInt, Field(gt=0)] | None
    reason: Annotated[str, StringConstraints(strip_whitespace=True, max_length=500)] = "no reason"
