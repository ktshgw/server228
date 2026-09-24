"""Website and administrator views of the actual SOMSAI ratings."""

import hashlib
import json

from app.database import User
from app.features.somsai.database.somsai import SomsaiActivity, SomsaiMatch, SomsaiRating
from app.helpers import utcnow
from app.models.score import GameMode
from app.features.somsai.services.somsai_party_service import lock_somsai, reject
from app.features.somsai.services.somsai_rating_service import ensure_rating

from sqlmodel import col, func, select
from sqlmodel.ext.asyncio.session import AsyncSession

MODES = ((0, 0), (1, 0), (2, 0), (3, 4), (3, 7))
MAX_ADMIN_MMR = 5000


def validate_mode(ruleset_id: int, variant_id: int, format: str) -> None:
    if (ruleset_id, variant_id) not in MODES or format not in ("1v1", "2v2"):
        reject("Неизвестный режим SOMSAI", 422)


def mmr_version(row: SomsaiRating | None) -> str:
    snapshot = row.model_dump(mode="json") if row else None
    return hashlib.sha256(json.dumps(snapshot, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def admin_payload(row: SomsaiRating | None, ruleset_id: int, variant_id: int, format: str) -> dict:
    return {
        "key": f"{ruleset_id}:{variant_id}:{format}",
        "ruleset_id": ruleset_id,
        "variant_id": variant_id,
        "format": format,
        "mmr": row.rating if row else None,
        "games": row.games if row else 0,
        "version": mmr_version(row),
    }


async def list_somsai_mmr(session: AsyncSession, user_id: int) -> list[dict]:
    rows = (await session.exec(select(SomsaiRating).where(SomsaiRating.user_id == user_id))).all()
    lookup = {(row.ruleset_id, row.variant_id, row.format): row for row in rows}
    return [
        admin_payload(lookup.get((mode, variant, format)), mode, variant, format)
        for mode, variant in MODES
        for format in ("1v1", "2v2")
    ]


async def change_somsai_mmr(
    session: AsyncSession, user_id: int, ruleset_id: int, variant_id: int, format: str, mmr: int, expected_version: str
) -> tuple[dict, dict]:
    validate_mode(ruleset_id, variant_id, format)
    if type(mmr) is not int or not 0 <= mmr <= MAX_ADMIN_MMR:
        reject("MMR SOMSAI должен быть целым числом от 0 до 5000", 422)
    await lock_somsai(session)
    state = (
        await session.exec(
            select(SomsaiActivity)
            .where(SomsaiActivity.user_id == user_id)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if state is not None:
        match = await session.get(SomsaiMatch, state.match_id) if state.match_id else None
        if state.reservation_id or (match and match.stage not in ("ended", "cancelled")):
            reject("Игрок должен выйти из поиска или матча перед изменением MMR SOMSAI")
    row = (
        await session.exec(
            select(SomsaiRating)
            .where(
                SomsaiRating.user_id == user_id,
                SomsaiRating.ruleset_id == ruleset_id,
                SomsaiRating.variant_id == variant_id,
                SomsaiRating.format == format,
            )
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    ).first()
    if mmr_version(row) != expected_version:
        reject("MMR SOMSAI изменился. Обновите данные игрока")
    before = admin_payload(row, ruleset_id, variant_id, format)
    row = row or await ensure_rating(session, user_id, ruleset_id, variant_id, format)
    row.rating = mmr
    row.last_delta = 0
    row.updated_at = utcnow()
    session.add(row)
    await session.flush()
    # Match MySQL DATETIME precision in the optimistic concurrency token.
    await session.refresh(row)
    return before, admin_payload(row, ruleset_id, variant_id, format)


def ranking_query(ruleset_id: int, variant_id: int, format: str):
    return (
        select(SomsaiRating)
        .join(User, col(User.id) == col(SomsaiRating.user_id))
        .where(
            SomsaiRating.ruleset_id == ruleset_id,
            SomsaiRating.variant_id == variant_id,
            SomsaiRating.format == format,
            col(User.is_active).is_(True),
            col(User.is_bot).is_(False),
            ~User.is_restricted_query(col(User.id)),
        )
    )


async def somsai_profile_payload(
    session: AsyncSession, user_id: int, mode: GameMode, mania_variant: int = 4, format: str = "1v1"
) -> dict:
    variant = mania_variant if mode == GameMode.MANIA else 0
    ruleset_id = int(mode)
    validate_mode(ruleset_id, variant, format)
    ranked = (
        ranking_query(ruleset_id, variant, format)
        .add_columns(
            func.row_number()
            .over(order_by=(col(SomsaiRating.rating).desc(), col(SomsaiRating.user_id)))
            .label("position")
        )
        .subquery()
    )
    row = (await session.exec(select(ranked.c.rating, ranked.c.position).where(ranked.c.user_id == user_id))).first()
    return {
        "variant_id": variant,
        "format": format,
        "mmr": row[0] if row else None,
        "global_rank": int(row[1]) if row else None,
    }
