"""Rebase stored public URLs after changing SERVER_URL.

Run from the server directory with python -m tools.migrate_public_domain
--from-url https://old.example. The default is read-only; --apply requires
--backup pointing to a new private JSON file outside the public static tree.
"""

import argparse
import asyncio
from collections import Counter
import json
import os
from pathlib import Path
from urllib.parse import unquote, urlsplit, urlunsplit

URL_FIELDS = {
    "lazer_users": {"avatar_url": False, "cover": True, "badges": True},
    "user_events": {"event_payload": True},
    "teams": {"flag_url": False, "cover_url": False, "website": False},
}


def origin(value: str) -> str:
    parsed = urlsplit(value)
    if (
        parsed.scheme not in {"http", "https"}
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.path not in {"", "/"}
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("Expected an HTTP(S) origin without a path or credentials")
    return urlunsplit((parsed.scheme, parsed.netloc.lower(), "", "", ""))


def rebase_urls(value, old: str, new: str):
    """Change complete URLs recursively, preserving paths, queries and other data."""
    if isinstance(value, dict):
        return {key: rebase_urls(child, old, new) for key, child in value.items()}
    if isinstance(value, list):
        return [rebase_urls(child, old, new) for child in value]
    if not isinstance(value, str):
        return value
    try:
        parsed = urlsplit(value)
    except ValueError:
        return value
    if f"{parsed.scheme}://{parsed.netloc.lower()}" != old:
        return value
    target = urlsplit(new)
    return urlunsplit((target.scheme, target.netloc, parsed.path, parsed.query, parsed.fragment))


def check_local_files(value, new: str, storage_root: Path) -> None:
    if isinstance(value, dict):
        for child in value.values():
            check_local_files(child, new, storage_root)
    elif isinstance(value, list):
        for child in value:
            check_local_files(child, new, storage_root)
    elif isinstance(value, str) and value.startswith(new + "/file/"):
        relative = unquote(urlsplit(value).path.removeprefix("/file/"))
        file = (storage_root / relative).resolve()
        file.relative_to(storage_root)
        if not file.is_file():
            raise FileNotFoundError(f"Stored asset is missing: {relative}")


def write_backup(path: Path, payload: dict) -> None:
    # A backup can contain private profile data; never publish it as an asset.
    if "static" in path.resolve().parts:
        raise ValueError("The backup must not be written inside the public static tree")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as output:
        os.chmod(path, 0o600)
        json.dump(payload, output, ensure_ascii=False, indent=2)
        output.flush()
        os.fsync(output.fileno())


async def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--from-url", required=True)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--backup", type=Path)
    args = parser.parse_args()
    if args.apply and args.backup is None:
        parser.error("--apply requires --backup")

    from app.config import StorageServiceType, settings
    from app.dependencies.database import engine, redis_client

    from sqlalchemy import (
        String,
        cast,
        column,
        func,
        select,
        table as sql_table,
    )

    old = origin(args.from_url)
    new = origin(str(settings.server_url))
    if old == new:
        parser.error("Old and current origins must differ")
    changes = []
    try:
        storage_root = None
        if settings.storage_service == StorageServiceType.LOCAL:
            storage_root = await asyncio.to_thread(Path(settings.storage_settings.local_storage_path).resolve)
        async with engine.begin() as connection:
            for table, fields in URL_FIELDS.items():
                for field, is_json in fields.items():
                    relation = sql_table(table, column("id"), column(field))
                    statement = select(relation.c.id, relation.c[field].label("value")).where(
                        func.instr(cast(relation.c[field], String), old) > 0
                    )
                    if args.apply:
                        statement = statement.with_for_update()
                    rows = (await connection.execute(statement)).mappings().all()
                    for row in rows:
                        before = json.loads(row["value"]) if is_json else row["value"]
                        after = rebase_urls(before, old, new)
                        if after == before:
                            continue
                        if storage_root is not None:
                            await asyncio.to_thread(check_local_files, after, new, storage_root)
                        changes.append(
                            {"table": table, "field": field, "id": row["id"], "before": before, "after": after}
                        )

            if args.apply and changes:
                await asyncio.to_thread(write_backup, args.backup, {"from": old, "to": new, "changes": changes})
                for change in changes:
                    table, field = change["table"], change["field"]
                    after = (
                        json.dumps(change["after"], ensure_ascii=False) if URL_FIELDS[table][field] else change["after"]
                    )
                    relation = sql_table(table, column("id"), column(field))
                    await connection.execute(
                        relation.update().where(relation.c.id == change["id"]).values({field: after})
                    )

        invalidated = 0
        if args.apply:
            # Derived profiles and rankings embed URLs; sessions and queues stay intact.
            for pattern in ("user:*", "v1_user:*", "ranking:*"):
                async for key in redis_client.scan_iter(match=pattern, count=100):
                    if await redis_client.type(key) != "string":
                        continue
                    value = await redis_client.get(key)
                    if value and old in value:
                        invalidated += await redis_client.unlink(key)
        print(
            json.dumps(
                {
                    "applied": args.apply,
                    "from": old,
                    "to": new,
                    "changes": dict(Counter(f"{row['table']}.{row['field']}" for row in changes)),
                    "invalidated_cache_entries": invalidated,
                }
            ),
            flush=True,
        )
    finally:
        await engine.dispose()
        await redis_client.aclose()


if __name__ == "__main__":
    asyncio.run(main())
