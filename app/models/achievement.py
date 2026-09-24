from collections.abc import Awaitable, Callable
from typing import TYPE_CHECKING, NamedTuple

from sqlmodel.ext.asyncio.session import AsyncSession

if TYPE_CHECKING:
    from app.database import Beatmap, Score


class Achievement(NamedTuple):
    id: int
    name: str
    desc: str
    assets_id: str
    medal_url: str | None = None
    medal_url2x: str | None = None
    clientside: bool = False

    @property
    def url(self) -> str:
        if self.medal_url:
            return self.medal_url
        return (
            f"/medals/{self.assets_id}.png"
            if self.assets_id.startswith("soms_")
            else f"https://assets.ppy.sh/medals/client/{self.assets_id}.png"
        )

    @property
    def url2x(self) -> str:
        if self.medal_url2x:
            return self.medal_url2x
        return (
            f"/medals/{self.assets_id}@2x.png"
            if self.assets_id.startswith("soms_")
            else f"https://assets.ppy.sh/medals/client/{self.assets_id}@2x.png"
        )


MedalProcessor = Callable[[AsyncSession, "Score", "Beatmap"], Awaitable[bool]]
Medals = dict[Achievement, MedalProcessor]
MEDALS: Medals = {}
CLIENTSIDE_MEDALS: dict[int, Achievement] = {}
