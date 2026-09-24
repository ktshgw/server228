# AGENTS

## Setup & Dev Commands

```
uv sync
uv run uvicorn main:app --reload --host 0.0.0.0 --port 8000
uv run pyright                    # type check
uv run ruff check --fix .         # lint
uv run ruff format .              # formatter
pre-commit run --all-files        # runs both ruff-check & ruff-format
```

## Architecture

- **Entrypoint:** `main.py` — FastAPI app with `lifespan` (startup tasks, scheduler, fetchers, GeoIP, cache, IPC, email processor).
- **ORM models:** shared SQLModel tables live in `app/database/`; feature-owned tables may live below `app/features/<feature>/database/`. Import public tables from `app.database`.
- **Pydantic/schema models:** `app/models/` — non-DB data structures (enums, NamedTuples, request/response shapes, achievement definitions). NOT SQLModel tables.
- **Services:** shared business logic lives in `app/service/`; complete product areas live in `app/features/` with their models, services, routers and tasks grouped together. Keep route handlers thin.
- **Tasks:** shared scheduled jobs live in `app/tasks/`; feature tasks live below the feature and are registered from `app/tasks/__init__.py`.
- **Dependencies:** `app/dependencies/` — DI for DB (`Database`, `NoContextDB`), Redis (`Redis`), auth (`get_current_user`), rate limits, scheduler, etc.
- **Helpers:** `app/helpers/` — `bg_tasks`, time utils, string utils (`snake_to_camel`, `snake_to_pascal`), GeoIP, asset proxy.
- **Config:** `app/config.py` — `pydantic-settings` from `.env`. Cast via `settings = Settings()`.

## Router Rules

- **`app/router/v1/`**, **`app/router/v2/`**, **`app/router/notification/`** — must match official osu! API. No custom endpoints here.
- **`app/router/private/`** — custom/experimental endpoints go here.
- V1 API serializes all values as strings (`AllStrModel` in `app/router/v1/router.py`).
- Rate limiting is applied at the v1/v2 router level via `LIMITERS` from `app.dependencies.rate_limit`.

## Auth Patterns

```python
from typing import Annotated
from fastapi import Security
from app.dependencies.user import ClientUser, get_current_user

@router.get("/some-api")
async def _(current_user: Annotated[User, Security(get_current_user, scopes=["public"])]):
    ...

@router.get("/some-client-api")
async def _(current_user: ClientUser):
    ...
```

V1 endpoints use `v1_authorize` dependency (API key in query param `k`).

## Database

- MySQL via aiomysql (async engine), Redis via `redis.asyncio`.
- **Redis DBs:** DB 0 = cache, DB 1 = chat messages, DB 2 = binary (audio, replays).
- `Database` = `Annotated[AsyncSession, Depends(get_db), FastDepends(get_db)]` — scoped session (uses `ContextVar`).
- `NoContextDB` = always-fresh session (use when you must not share the request's session).
- `Redis` = `Annotated[redis.Redis, Depends(get_redis), FastDepends(get_redis)]` — DB 0.
- `app/database/__init__.py` calls `model_rebuild()` on all `*Model` and `*Resp` types at import time. Do not remove this; it resolves forward references.

## Migrations

Use the custom `soms-migrate` CLI (wraps Alembic with plugin support):

```
uv run soms-migrate revision --autogenerate -m "feat(db): ..."
uv run soms-migrate upgrade head
uv run soms-migrate upgrade-all          # server + all plugins (run from repo root)
```

After modifying any `app/database/` model, generate a migration and manually review the SQL & indexes.

## Logging

```python
from app.log import log, system_logger, service_logger, task_logger, fetcher_logger

log("ModuleName").info("...")               # general use (routers)
system_logger("Component").info("...")      # system/subsystem startup
service_logger("Name").info("...")          # service classes
task_logger("Name").info("...")             # task modules
fetcher_logger("Name").info("...")          # fetcher classes
```

## Error Handling

- Client errors: `HTTPException` or `RequestError` from `app.models.error` (structured JSON with `msg_key` and `details`).
- Server errors: structured logging via `app.log`.

## Background Tasks

- In API routes: FastAPI's `BackgroundTasks`.
- Elsewhere: `app.helpers.bg_tasks` (same API).

## Workspace & Conventions

- uv workspace: members are `packages/osupyparser` and `packages/soms-migrations`.
- Python >= 3.12. CI uses 3.13.
- Ruff line-length: 120.
- Commit style: Angular (`type(scope): subject`).
- All route handlers must be `async`.

## API Reference

- v1: https://github.com/ppy/osu-api/wiki
- v2 OpenAPI: https://osu.ppy.sh/docs/openapi.yaml

## Gotchas

- Focused server tests live in `tests/`; feature tests are grouped in subdirectories such as `tests/somsai/`. Generated client check output must not be committed.
- `/_lio` path must be blocked from public network access (spectator server).
- `app.database` import does a `model_rebuild()` loop — any new Model/Resp class must be added to `__all__` in `app/database/__init__.py`.
