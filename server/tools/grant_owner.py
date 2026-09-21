"""Explicitly grant the private-server owner privilege to one user.

Run from the repository root, for example::

    python tools/grant_owner.py --username alice --confirm alice

The confirmation value intentionally has to match the selected database
username.  There is no automatic "first user is owner" behaviour.
"""

from __future__ import annotations

import argparse
import asyncio
import os
import sys

sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from app.database import User
from app.dependencies.database import engine, with_db

from sqlmodel import select


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Grant the explicit owner privilege to an existing user")
    selector = parser.add_mutually_exclusive_group(required=True)
    selector.add_argument("--user-id", type=int, help="Exact local user ID")
    selector.add_argument("--username", help="Exact local username (case-sensitive)")
    parser.add_argument(
        "--confirm",
        required=True,
        metavar="USERNAME",
        help="Safety check: repeat the exact username stored in the database",
    )
    return parser.parse_args()


async def grant_owner(args: argparse.Namespace) -> int:
    async with with_db() as session:
        query = select(User)
        if args.user_id is not None:
            query = query.where(User.id == args.user_id)
        else:
            query = query.where(User.username == args.username)

        user = (await session.exec(query)).one_or_none()
        if user is None:
            print("No user matched the supplied selector.", file=sys.stderr)
            return 1
        if args.confirm != user.username:
            print(
                f"Confirmation mismatch: selected user is {user.username!r}. No changes were made.",
                file=sys.stderr,
            )
            return 2
        if user.is_bot:
            print("Refusing to grant owner privilege to a bot account.", file=sys.stderr)
            return 3
        if not user.is_active or await user.is_restricted(session):
            print("Refusing to grant owner privilege to a restricted or inactive account.", file=sys.stderr)
            return 4

        # AsyncSession expires ORM attributes after commit. Capture the values
        # used for console output first; accessing them afterwards would try to
        # perform implicit async I/O and raise MissingGreenlet.
        username = user.username
        user_id = user.id

        if user.is_owner:
            if not user.is_admin:
                user.is_admin = True
                session.add(user)
                await session.commit()
                print(f"Repaired administrator flag for owner {username!r} (ID {user_id}).")
                return 0
            print(f"User {username!r} (ID {user_id}) is already an owner.")
            return 0

        user.is_owner = True
        user.is_admin = True
        session.add(user)
        await session.commit()
        print(f"Granted owner privilege to {username!r} (ID {user_id}).")
        return 0


async def main() -> int:
    try:
        return await grant_owner(parse_args())
    finally:
        await engine.dispose()


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
