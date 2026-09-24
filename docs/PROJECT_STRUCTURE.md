# Project structure

The repository root is the runnable Python server. `main.py`, `pyproject.toml`,
Docker Compose files, migrations and the `app` package therefore stay at the root.

- `app/` — server application code.
- `app/features/` — complete product features whose models, routes, services and tasks belong together.
- `assets/` — source assets that are not served directly by the website.
- `client/enhanced-auth/` — the EnhancedAuth osu!lazer module.
- `client/startup-hook/` — the private-server startup hook.
- `client/launcher/` — local and shared launchers.
- `client/switcher/` — client version switcher and its build scripts.
- `static/` — files served by the server, including published client releases.
- `tools/checks/` — isolated client/integration check projects. Their build output is disposable.
- `tools/server/` — server maintenance tools.
- `vendor/` — pinned upstream source trees used while adapting osu!, osu-framework and spectator.
- `scripts/server/`, `scripts/client/`, `scripts/site/` — operational scripts grouped by target.
- `docs/` — project notes and historical implementation records.

Caches, browser profiles, test client profiles, build output and downloaded tooling are
ignored. Test profiles use the operating system's temporary directory and are cleaned
when their client process exits. Runtime `storage/` and `logs/` remain local and are not
versioned.