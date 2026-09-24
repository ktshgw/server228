"""Import four small public tournament rounds. Dry run unless --apply is supplied.

Run from server: python -m scripts.seed_somsai_pools [--apply] [--activate]
Existing source+round entries are kept, including operator changes.
"""

import argparse
import asyncio
import json

from app.database import AdminAuditEvent, SomsaiPool
from app.dependencies.database import engine, with_db
from app.dependencies.fetcher import get_fetcher
from app.features.somsai.models.somsai_admin import SomsaiImportRequest, SomsaiPoolSpec
from app.features.somsai.services.somsai_collector_service import preview_collector
from app.features.somsai.services.somsai_pool_service import pool_payload, prepare_missing_metadata, preview_pool, save_pool

from sqlmodel import select

BASE_POOLS = (
    (1630, "round of 32"),  # Watermelon Cup: upstream base SR ~4.55.
    (1623, "Round of 32"),  # 4 digit world cup 2024: upstream base SR ~6.09.
    (2186, "Round 1"),  # LA SPAGHETTATA 2026: upstream base SR ~6.60.
    (998, "Round of 16"),  # Vietnam osu! Championship 2022: upstream base SR ~5.78.
)


async def seed(*, apply: bool, activate: bool) -> None:
    for source_id, round_name in BASE_POOLS:
        request = SomsaiImportRequest(url=f"https://osucollector.com/tournaments/{source_id}", round=round_name)
        imported = await preview_collector(request)
        if any(not slot["checksum"] for slot in imported["slots"]):
            raise ValueError(f"Source {source_id}/{round_name} contains maps without checksums")
        spec = SomsaiPoolSpec(name=imported["name"], slots=imported["slots"], active=activate)
        if not apply:
            print(
                json.dumps(
                    {
                        "source": request.url,
                        "round": round_name,
                        "slots": len(spec.slots),
                        "name": spec.name,
                        "warnings": imported["warnings"],
                        "dry_run": True,
                    }
                )
            )
            continue
        async with with_db() as session:
            existing = (
                await session.exec(
                    select(SomsaiPool).where(
                        SomsaiPool.source_kind == "collector_tournament",
                        SomsaiPool.source_id == source_id,
                        SomsaiPool.source_round == round_name,
                    )
                )
            ).first()
            if existing:
                print(json.dumps({"id": existing.id, "kept_existing": True, "name": existing.name}))
                continue
            metadata = await prepare_missing_metadata(session, spec, await get_fetcher())
            before, pool = await save_pool(session, spec, None, None, metadata, imported)
            result = await preview_pool(session, spec)
            after = pool_payload(pool)
            session.add(
                AdminAuditEvent(
                    actor_username="somsai-seed-cli",
                    action="somsai.pool.create",
                    target_type="somsai_pool",
                    target_id=str(pool.id),
                    reason="Initial public Collector tournament catalogue",
                    before=before,
                    after=after,
                )
            )
            await session.commit()
            print(
                json.dumps(
                    {
                        "id": after["id"],
                        "name": after["name"],
                        "active": after["active"],
                        "average_stars": after["average_stars"],
                        "errors": result["errors"],
                    }
                )
            )
    await engine.dispose()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="Persist new pools and canonical beatmap metadata")
    parser.add_argument("--activate", action="store_true", help="Activate only if every slot passes validation")
    args = parser.parse_args()
    asyncio.run(seed(apply=args.apply, activate=args.activate))
