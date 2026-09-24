"""Explicit tournament settings; Collector uploader ranks are never pool ratings."""

from typing import Literal, Self

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

Category = Literal["NM", "HD", "HR", "DT", "FM", "TB"]
CATEGORY_MODS = {
    category: ([{"acronym": "HR" if category == "FM" else category}] if category in {"HD", "HR", "DT", "FM"} else [])
    for category in ("NM", "HD", "HR", "DT", "FM", "TB")
}


class SomsaiSlotSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    id: str = Field(pattern=r"^(?:(?:NM|HD|HR|DT|FM)[1-9][0-9]?|TB)$")
    beatmap_id: int = Field(gt=0, strict=True)
    checksum: str | None = Field(default=None, pattern=r"^[0-9a-f]{32}$")
    source_url: str | None = Field(default=None, max_length=500)

    @property
    def category(self) -> str:
        return self.id[:2]


class SomsaiPoolSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True, allow_inf_nan=False)
    name: str = Field(min_length=2, max_length=160)
    ruleset_id: int = Field(default=0, ge=0, le=3, strict=True)
    variant_id: int = Field(default=0, strict=True)
    active: bool = False
    rating_min: int = Field(default=0, ge=0, le=10000, strict=True)
    rating_max: int = Field(default=5000, ge=0, le=10000, strict=True)
    best_of: Literal[7, 9, 11] = 7
    bans_per_team: int = Field(default=1, ge=0, le=3, strict=True)
    slots: list[SomsaiSlotSpec] = Field(default_factory=list, max_length=64)
    source_rank_min: int | None = Field(default=None, ge=1, strict=True)
    source_rank_max: int | None = Field(default=None, ge=1, strict=True)

    @model_validator(mode="after")
    def validate_pool(self) -> Self:
        if self.variant_id not in ((4, 7) if self.ruleset_id == 3 else (0,)):
            raise ValueError("Укажите 4K/7K для mania; остальные режимы используют вариант 0")
        if self.rating_min > self.rating_max:
            raise ValueError("Минимальный MMR не может превышать максимальный")
        if self.source_rank_min and self.source_rank_max and self.source_rank_min > self.source_rank_max:
            raise ValueError("Начало диапазона исходных рангов больше конца")
        if len({slot.id for slot in self.slots}) != len(self.slots):
            raise ValueError("Названия слотов не должны повторяться")
        if len({slot.beatmap_id for slot in self.slots}) != len(self.slots):
            raise ValueError("Одна сложность не может занимать несколько слотов")
        return self


class SomsaiPoolWrite(SomsaiPoolSpec):
    expected_revision: int | None = Field(default=None, ge=1, strict=True)
    reason: str = Field(default="no reason", max_length=500)


class SomsaiDelete(BaseModel):
    expected_revision: int = Field(ge=1, strict=True)
    reason: str = Field(default="no reason", max_length=500)


class SomsaiMapWrite(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    slot: str = Field(pattern=r"^(?:NM[1-6]|HD[1-3]|HR[1-3]|DT[1-4]|FM[1-3]|TB)$")
    url: str = Field(min_length=1, max_length=500)
    ruleset_id: int = Field(default=0, ge=0, le=3)
    variant_id: int = Field(default=0, ge=0, le=7)
    reason: str = Field(default="no reason", max_length=500)


class SomsaiMapDelete(BaseModel):
    reason: str = Field(default="no reason", max_length=500)


class SomsaiWarehouseImport(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    url: str = Field(min_length=10, max_length=500)
    round: str = Field(default="__all__", min_length=1, max_length=160)
    reason: str = Field(default="no reason", max_length=500)

    @field_validator("round", mode="before")
    @classmethod
    def all_rounds(cls, value):
        return value or "__all__"


class SomsaiImportRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", str_strip_whitespace=True)
    url: str = Field(min_length=10, max_length=500)
    round: str | None = Field(default=None, max_length=160)
    category: Category = "NM"


class SomsaiImportWrite(SomsaiImportRequest):
    pool: SomsaiPoolWrite
    append: bool = False
