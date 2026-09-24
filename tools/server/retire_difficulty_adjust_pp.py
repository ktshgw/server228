"""Run with python -m tools.retire_difficulty_adjust_pp [--apply].

The default executes the complete repair in a rolled-back transaction. JSON
output records before/after values; --apply commits and refreshes caches.
"""

import argparse
import asyncio
import json

from app.log import logger

logger.remove()

from app.dependencies.database import engine, with_db
from app.models.score import GameMode
from app.service.beatmap_ranking_reconciliation_service import (
    ScoreReconciliationResult,
    invalidate_score_reconciliation_caches,
)
from app.service.difficulty_adjust_pp_service import retire_difficulty_adjust_pp


async def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    try:
        async with with_db() as session:
            report = await retire_difficulty_adjust_pp(session)
            print(json.dumps({"applied": args.apply, **report}, ensure_ascii=False), flush=True)
            if args.apply:
                await session.commit()
            else:
                await session.rollback()
        if args.apply:
            await invalidate_score_reconciliation_caches(
                ScoreReconciliationResult(
                    affected_user_modes=frozenset((row["user_id"], GameMode(row["mode"])) for row in report["profiles"])
                )
            )
            print("Committed and caches invalidated.", flush=True)
    finally:
        await engine.dispose()


if __name__ == "__main__":
    asyncio.run(main())
