"""Narrow score admission checks for server-managed SOMSAI rooms."""

from math import isclose
from typing import Any

from app.database import Playlist
from app.features.somsai.database.somsai import SomsaiMatch
from app.models.mods import API_MODS, APIMod

from fastapi import HTTPException
from sqlmodel import select
from sqlmodel.ext.asyncio.session import AsyncSession


async def _snapshot(session: AsyncSession, room_id: int) -> tuple[str, dict[str, Any]] | None:
    # Submissions already hold Room FOR UPDATE. Never acquire the SOMSAI
    # coordinator or a match lock here: the match pump locks coordinator first.
    # Select columns to avoid stale ORM identities and eager relationship loads.
    row = (
        await session.exec(select(SomsaiMatch.stage, SomsaiMatch.state).where(SomsaiMatch.room_id == room_id))
    ).first()
    return (row[0], dict(row[1])) if row is not None else None


def _check_member(state: dict[str, Any], user_id: int) -> None:
    if not any(user_id in team for team in state.get("teams", [])):
        raise HTTPException(403, "Вы не участвуете в этом матче SOMSAI.")


async def validate_somsai_score_token(session: AsyncSession, room_id: int, playlist_id: int, user_id: int) -> None:
    snapshot = await _snapshot(session, room_id)
    if snapshot is None:
        return
    stage, state = snapshot
    _check_member(state, user_id)
    if stage != "playing" or state.get("playlist_item_id") != playlist_id or not state.get("current_slot"):
        raise HTTPException(409, "Сейчас нельзя начинать игру на этой карте SOMSAI.")


def _same_setting(left: object, right: object) -> bool:
    if isinstance(left, bool) or isinstance(right, bool):
        return type(left) is bool and type(right) is bool and left == right
    if isinstance(left, (int, float)) and isinstance(right, (int, float)):
        return isclose(left, right, rel_tol=1e-7, abs_tol=1e-7)
    return type(left) is type(right) and left == right


def _validate_settings(actual: APIMod, expected: APIMod, ruleset_id: int) -> None:
    acronym = actual["acronym"]
    configured = expected.get("settings", {})
    submitted = actual.get("settings", {})
    defaults = {
        setting["Name"]: setting["DefaultValue"]
        for setting in API_MODS.get(ruleset_id, {}).get(acronym, {}).get("Settings", [])
    }
    for key in submitted.keys() | configured.keys():
        # Pitch is cosmetic and has changed defaults across native client builds.
        if acronym == "DT" and key == "adjust_pitch" and key not in configured and isinstance(submitted[key], bool):
            continue
        if key not in configured and key not in defaults:
            raise HTTPException(422, f"Неизвестная настройка мода {acronym} в матче SOMSAI.")
        expected_value = configured.get(key, defaults.get(key))
        submitted_value = submitted.get(key, defaults.get(key))
        if not _same_setting(submitted_value, expected_value):
            raise HTTPException(422, f"Настройки мода {acronym} не соответствуют карте SOMSAI.")


async def validate_somsai_score_submission(
    session: AsyncSession, room_id: int, item: Playlist, user_id: int, mods: list[APIMod], *, ruleset_id: int
) -> None:
    snapshot = await _snapshot(session, room_id)
    if snapshot is None:
        return
    _, state = snapshot
    _check_member(state, user_id)
    if ruleset_id != item.ruleset_id:
        raise HTTPException(422, "Режим скора не соответствует карте SOMSAI.")
    # An issued token may arrive after a round or the match ends. Validate its
    # immutable pool chart, not the current slot/stage. Settled results stay final.
    slot = next((slot for slot in state.get("slots", []) if slot.get("beatmap_id") == item.beatmap_id), None)
    if slot is None:
        raise HTTPException(422, "Карта не принадлежит пулу этого матча SOMSAI.")
    required: dict[str, APIMod] = {mod["acronym"]: mod for mod in slot.get("mods", [])}
    # Tokens already issued before the update retain their playlist's NF requirement.
    # New playlist items contain only the pool slot's mods.
    if any(mod["acronym"] == "NF" for mod in item.required_mods):
        required["NF"] = {"acronym": "NF"}
    approved = dict(required)
    if slot.get("category") in {"FM", "TB"}:
        approved.update({acronym: {"acronym": acronym} for acronym in ("HD", "HR")})
    actual = {mod["acronym"]: mod for mod in mods}
    if len(actual) != len(mods) or not required.keys() <= actual.keys() or not actual.keys() <= approved.keys():
        raise HTTPException(422, "Моды скора не соответствуют выбранному слоту SOMSAI.")
    for acronym, mod in actual.items():
        _validate_settings(mod, approved[acronym], item.ruleset_id)
