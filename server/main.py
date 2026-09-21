from contextlib import asynccontextmanager
import hashlib
import json
from pathlib import Path
import re
import time

from app.calculating import init_calculator
from app.config import settings
from app.database import Screenshot, User
from app.dependencies.database import (
    Database,
    engine,
    redis_client,
    redis_clients,
)
from app.dependencies.fetcher import get_fetcher
from app.dependencies.scheduler import start_scheduler, stop_scheduler
from app.helpers import bg_tasks, utcnow
from app.log import add_file_logger, system_logger
from app.middleware.verify_session import VerifySessionMiddleware
from app.models.error import RequestError
from app.models.events.http import RequestHandledEvent, RequestReceivedEvent
from app.models.mods import init_mods, init_ranked_mods
from app.models.score import init_ruleset_version_hash
from app.path import STATIC_DIR
from app.plugins import hub, manager, plugin_router
from app.router import (
    api_v1_router,
    api_v2_router,
    auth_router,
    chat_router,
    file_router,
    lio_router,
    private_router,
    redirect_api_router,
)
from app.router.redirect import redirect_router
from app.router.somsai_lio import router as somsai_lio_router
from app.service.beatmap_download_service import download_service
from app.service.beatmapset_update_service import init_beatmapset_update_service
from app.service.client_verification_service import init_client_verification_service
from app.service.email_service import start_email_processor, stop_email_processor
from app.service.redis_message_system import redis_message_system
from app.service.subscribers.user_cache import user_online_subscriber
from app.service.user_identity_service import resolve_human_user
from app.tasks import (
    calculate_user_rank,
    create_banchobot,
    create_custom_ruleset_statistics,
    create_rx_statistics,
    daily_challenge_job,
    init_geoip,
    load_achievements,
    process_daily_challenge_top,
    start_cache_tasks,
    stop_cache_tasks,
)
from app.v2_ipc import init_ipc

from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
import sentry_sdk
from sqlmodel import select

add_file_logger()


@asynccontextmanager
async def lifespan(app: FastAPI):
    startup_logger = system_logger("Startup")
    shutdown_logger = system_logger("Shutdown")

    # === on startup ===
    startup_logger.info("Starting g0v0-server")

    # init mods, achievements and performance calculator
    startup_logger.info("Loading plugins")
    manager.load_all_plugins()
    app.include_router(plugin_router)

    startup_logger.info("Initializing mods, achievements and calculator")
    init_mods()
    init_ranked_mods()
    init_ruleset_version_hash()
    load_achievements()
    await init_calculator()

    if settings.check_client_version:
        startup_logger.info("Initializing client version verification service")
        await init_client_verification_service()

    # init fetcher
    startup_logger.info("Initializing osu! API fetcher")
    fetcher = await get_fetcher()
    # init GeoIP
    startup_logger.info("Initializing GeoIP databases")
    await init_geoip()
    # init IPC
    if settings.enable_v2_ipc:
        startup_logger.info("Initializing v2 IPC channel")
        startup_logger.warning(
            "v2 realtime server is enabled. It is under development and should not be used in production."
        )
        await init_ipc(redis_client)

    # init game server
    startup_logger.info("Preparing game server data")
    await create_rx_statistics()
    await create_custom_ruleset_statistics()
    await calculate_user_rank(True)
    await daily_challenge_job()
    await process_daily_challenge_top()
    await create_banchobot()

    # services
    startup_logger.info("Starting background services")
    await start_email_processor()
    await download_service.start_health_check()
    await start_cache_tasks()
    init_beatmapset_update_service(fetcher)  # 初始化谱面集更新服务
    redis_message_system.start()
    start_scheduler()

    if not settings.enable_v2_ipc:
        startup_logger.info("Starting user online subscriber")
        await user_online_subscriber.start_subscribe()

    # show the status of AssetProxy
    if settings.enable_asset_proxy:
        system_logger("AssetProxy").info(f"Asset Proxy enabled - Domain: {settings.custom_asset_domain}")
    else:
        system_logger("AssetProxy").info("Asset Proxy disabled")

    startup_logger.info("g0v0-server startup completed")

    yield

    # === on shutdown ===
    shutdown_logger.info("Stopping g0v0-server")

    # stop services
    shutdown_logger.info("Stopping background services")
    bg_tasks.stop()
    await stop_cache_tasks()
    stop_scheduler()
    await download_service.stop_health_check()
    await stop_email_processor()

    # close database & redis
    shutdown_logger.info("Closing database and Redis connections")
    await engine.dispose()
    for client in redis_clients.values():
        await client.close()
    shutdown_logger.info("g0v0-server shutdown completed")


desc = f"""g0v0-server is an osu!(lazer) server written in Python, supporting the latest osu!(lazer) client and providing additional features (e.g., Relax/Autopilot Mod statistics, custom ruleset support).

g0v0-server is implemented based on osu! API v2, achieving compatibility with the vast majority of osu! API v1 and v2. This means you can easily integrate existing osu! applications into g0v0-server.

Meanwhile, g0v0-server also provides a series of g0v0! APIs to implement operations for other functionalities outside of the osu! API.

g0v0-server is not just a score server. It implements most of the osu! website features (e.g., chat, user settings, etc.).

If you want to develop this or to run another instance, please check our [documentation](https://docs.g0v0.top/). If you are confused about this project, welcome to our [Discord server](https://discord.gg/AhzJXXWYfF) to seek answers.

g0v0-server is developed by [GooGuTeam](https://github.com/GooGuTeam) and licensed under **GNU Affero General Public License v3.0 (AGPL-3.0-only)**. Any derivative work, modification, or deployment **MUST** clearly and prominently attribute the original authors:
> GooGuTeam - https://github.com/GooGuTeam/g0v0-server

## Endpoint Specifications

All v2 APIs begin with `/api/v2/`, while all v1 APIs start with `/api/v1/` (direct access to `/api` for v1 APIs will redirect).
All additional APIs provided by g0v0-server (g0v0-api) begin with `/api/private/`. All additional APIs provided by plugins begin with `/api/plugins/<plugin-id>/`

## Authentication

v2 APIs use OAuth 2.0 authentication and support the following methods:
- `password`: Password authentication, applicable only to services like the osu!lazer client and frontend. Requires providing the user's username and password for login.
- `authorization_code`: Authorization code authentication, suitable for third-party applications. Requires providing the user's authorization code for login.
- `client_credentials`: Client credentials authentication for server-side applications, requiring the client ID and client secret for login.
`password` authentication grants full permissions. `authorization_code` grants permissions for specified scopes. `client_credentials` grants only `public` permissions. Refer to each Endpoint's Authorization section for specific permission requirements.

v1 API uses API Key authentication. Place the API Key in the Query `k` field.

{
    '''
## Rate Limiting

All API requests are subject to rate limiting. Specific restrictions are as follows:

- Maximum of 1200 requests per minute
- Burst requests capped at 200 requests per second

Additionally, the download replay API (`/api/v1/get_replay`, `/api/v2/scores/{score_id}/download`) is rate-limited to 10 requests per minute.
'''
    if settings.enable_rate_limit
    else ""
}

## References
- v2 API Documentation: [osu-web Documentation](https://osu.ppy.sh/docs/index.html)
- v1 API Documentation: [osu-api](https://github.com/ppy/osu-api/wiki)
"""  # noqa: E501

# 检查 New Relic 配置文件是否存在，如果存在则初始化 New Relic
newrelic_config_path = Path("newrelic.ini")
if newrelic_config_path.is_file():
    try:
        import newrelic.agent

        environment = settings.new_relic_environment or ("production" if not settings.debug else "development")

        newrelic.agent.initialize(newrelic_config_path, environment)
        system_logger("NewRelic").info(f"Enabled, environment: {environment}")
    except Exception as e:
        system_logger("NewRelic").error(f"Initialization failed: {e}")

if settings.sentry_dsn is not None:
    sentry_sdk.init(
        dsn=str(settings.sentry_dsn),
        send_default_pii=False,
        environment="production" if not settings.debug else "development",
    )

app = FastAPI(
    title="g0v0-server",
    version="0.1.0",
    lifespan=lifespan,
    description=desc,
)


app.include_router(api_v2_router)
app.include_router(api_v1_router)
app.include_router(chat_router)
app.include_router(redirect_api_router)
# app.include_router(fetcher_router)
app.include_router(file_router)
app.include_router(auth_router)
app.include_router(private_router)
app.include_router(lio_router)
app.include_router(somsai_lio_router)
app.mount("/admin", StaticFiles(directory=STATIC_DIR / "admin", html=True), name="admin-panel")
app.mount("/site", StaticFiles(directory=STATIC_DIR / "site", html=True), name="community-site")
app.mount(
    "/client/modules",
    StaticFiles(directory=STATIC_DIR / "client", check_dir=False),
    name="private-client-modules",
)

# 会话验证中间件
if settings.enable_session_verification:
    app.add_middleware(VerifySessionMiddleware)

# CORS 配置
origins = []
for url in [*settings.cors_urls, settings.server_url]:
    origins.append(str(url))
    origins.append(str(url).removesuffix("/"))
if settings.frontend_url:
    origins.append(str(settings.frontend_url))
    origins.append(str(settings.frontend_url).removesuffix("/"))
app.add_middleware(
    CORSMiddleware,
    allow_origins=origins,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

if settings.frontend_url is not None:
    app.include_router(redirect_router)


@app.get("/users/{user_id}/avatar", include_in_schema=False)
async def get_user_avatar_root(
    user_id: int,
    session: Database,
):
    """用户头像重定向端点 (根路径)"""
    user = await session.get(User, user_id)
    if user is None or not user.is_active or await user.is_restricted(session):
        raise HTTPException(status_code=404, detail="User not found")

    avatar_url = user.avatar_url
    if not avatar_url or avatar_url in {
        "https://lazer.g0v0.top/default.jpg",
        "https://lazer-data.g0v0.top/default.jpg",
    }:
        return FileResponse(
            STATIC_DIR / "site" / "soms-default-avatar.png",
            media_type="image/png",
            headers={"Cache-Control": "no-store"},
        )

    return RedirectResponse(
        url=avatar_url,
        status_code=302,
        headers={"Cache-Control": "no-store"},
    )


@app.get("/users/{user_id}", include_in_schema=False)
async def open_user_profile(user_id: int, session: Database):
    """Open game-generated profile links in the bundled community site."""

    if user_id <= 0:
        raise HTTPException(status_code=404, detail="User not found")
    user = await resolve_human_user(session, user_id)
    if user is None:
        bot = await session.get(User, user_id)
        if (
            bot is not None
            and bot.is_bot
            and bot.is_active
            and re.fullmatch(r"https://osu\.ppy\.sh/users/[1-9][0-9]*", bot.website or "")
        ):
            return RedirectResponse(url=bot.website, status_code=302)
    if user is None or not user.is_active or await user.is_restricted(session):
        raise HTTPException(status_code=404, detail="User not found")
    return RedirectResponse(url=f"/site/#profile/{user.server_id or user.id}", status_code=302)


@app.get("/beatmapsets/{beatmapset_id}", include_in_schema=False)
async def open_beatmapset(beatmapset_id: int):
    """Bridge lazer's official-style beatmap URL, including its client-only difficulty fragment."""

    if beatmapset_id <= 0:
        raise HTTPException(status_code=404, detail="Beatmapset not found")
    fallback_url = f"/site/#beatmap/{beatmapset_id}"
    return HTMLResponse(
        content=(
            '<!doctype html><html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width,initial-scale=1">'
            f'<meta name="soms-beatmapset-id" content="{beatmapset_id}">'
            f'<meta http-equiv="refresh" content="1;url={fallback_url}">'
            "<title>Opening beatmap…</title>"
            '<script src="/site/legacy-beatmap-bridge.js" defer></script></head>'
            f'<body><noscript><a href="{fallback_url}">Open beatmap</a></noscript></body></html>'
        ),
        headers={"Cache-Control": "no-store"},
    )


@app.get("/ss/{sha256_hash}", include_in_schema=False)
async def get_screenshot(
    sha256_hash: str,
    session: Database,
):
    """用户提交的截图访问端点"""
    screenshot = (await session.exec(select(Screenshot).where(Screenshot.sha256_hash == sha256_hash))).first()
    if not screenshot:
        raise HTTPException(status_code=404, detail="Screenshot not found")

    url = screenshot.url
    screenshot.hits += 1
    screenshot.last_access = utcnow()
    session.add(screenshot)
    await session.commit()
    return RedirectResponse(
        url=url,
        status_code=302,
        headers={
            "Content-Type": "image/jpeg",
            "Cache-Control": "max-age=31536000, public",
        },
    )


@app.get("/", include_in_schema=False)
async def root():
    if settings.frontend_url:
        return RedirectResponse(url=str(settings.frontend_url), status_code=302)
    return RedirectResponse(url="/site/", status_code=302)


@app.get("/health", include_in_schema=False)
async def health_check():
    return {"status": "ok", "timestamp": utcnow().isoformat()}


_CLIENT_VERSION_PATTERN = re.compile(r"^\d{4}\.\d+\.\d+$")
_CLIENT_MODULES = {
    "enhanced_auth": "osu.Game.Rulesets.EnhancedAuth.dll",
    "startup_hook": "PrivateOsu.StartupHook.dll",
}


def _client_module_payload(version: str, filename: str) -> dict[str, str | int]:
    path = STATIC_DIR / "client" / version / filename
    content = path.read_bytes()
    return {
        "url": f"/client/modules/{version}/{filename}",
        "sha256": hashlib.sha256(content).hexdigest(),
        "size": len(content),
        "filename": filename,
    }


def _switcher_compatibility_payload() -> dict[str, dict[str, dict[str, str | int]]]:
    root = STATIC_DIR / "client"
    if not root.is_dir():
        return {}

    result: dict[str, dict[str, dict[str, str | int]]] = {}

    for directory in sorted(root.iterdir(), reverse=True):
        if not directory.is_dir() or not _CLIENT_VERSION_PATTERN.fullmatch(directory.name):
            continue

        if not all(
            (directory / filename).is_file()
            for filename in _CLIENT_MODULES.values()
        ):
            continue

        version_payload = {
            key: _client_module_payload(directory.name, filename)
            for key, filename in _CLIENT_MODULES.items()
        }

        harmony = directory / "0Harmony.dll"

        if harmony.is_file():
            version_payload["harmony"] = _client_module_payload(
                directory.name,
                "0Harmony.dll",
            )

        result[directory.name] = version_payload

    return result


@app.get("/client/manifest.json", include_in_schema=False)
async def private_client_manifest():
    """Return the public desktop-client configuration and compatible modules.

    The game OAuth credential is necessarily public: every desktop game client
    must send it to the server. It must never be reused as an administrator,
    database, fetcher, or official osu! account credential.
    """

    base_url = str(settings.server_url).rstrip("/")
    compatibility = _switcher_compatibility_payload()
    if not compatibility:
        raise HTTPException(status_code=503, detail="No compatible osu!lazer client modules are published")

    content = {
        "schema_version": 1,
        "product_name": "SOMS!",
        "server_url": base_url,
        "website_url": f"{base_url}/site/",
        "health_url": f"{base_url}/health",
        "credential_target": f"PulsePrivateOsu/{settings.server_url.host}",
        "client": {
            "id": str(settings.osu_client_id),
            "secret": settings.osu_client_secret,
        },
        "endpoints": {
            "api": base_url,
            "website": base_url,
            "spectator": f"{base_url}/signalr/spectator",
            "multiplayer": f"{base_url}/signalr/multiplayer",
            "metadata": f"{base_url}/signalr/metadata",
            "beatmap_submission": f"{base_url}/beatmap-submission",
        },
        "compatibility": compatibility,
    }
    switcher_path = STATIC_DIR / "client" / "SOMS-switcher.exe"
    if switcher_path.is_file():
        switcher_content = switcher_path.read_bytes()
        content["launcher"] = {
            "version": "1.1.4",
            "url": "/client/SOMS-switcher.exe",
            "sha256": hashlib.sha256(switcher_content).hexdigest(),
            "size": len(switcher_content),
        }

    return JSONResponse(
        content=content,
        headers={
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
        },
    )


@app.get("/client/SOMS-switcher.exe", include_in_schema=False)
@app.get("/client/pulse-switcher.exe", include_in_schema=False)
async def download_private_client_switcher():
    path = STATIC_DIR / "client" / "SOMS-switcher.exe"
    if not path.is_file():
        raise HTTPException(status_code=404, detail="SOMS! switcher is not published")
    return FileResponse(
        path,
        media_type="application/vnd.microsoft.portable-executable",
        filename="SOMS-switcher.exe",
        headers={
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
        },
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):  # noqa: ARG001
    return JSONResponse(
        status_code=422,
        content={
            "error": json.dumps(exc.errors()),
        },
    )


@app.exception_handler(RequestError)
async def request_error_handler(request: Request, exc: RequestError):  # noqa: ARG001
    content = {
        "error": exc.formatted_message,
        "msg_key": exc.msg_key,
    }

    content.update(exc.details)
    return JSONResponse(status_code=exc.status_code, content=content)


@app.exception_handler(exc_class_or_status_code=HTTPException)
async def http_exception_handler(request: Request, exc: HTTPException):  # noqa: ARG001
    return JSONResponse(status_code=exc.status_code, content={"error": exc.detail}, headers=exc.headers)


@app.middleware("http")
async def http_event_emitter(request: Request, call_next):
    hub.emit(RequestReceivedEvent(time=time.time(), request=request))
    response = await call_next(request)
    hub.emit(RequestHandledEvent(time=time.time(), request=request, response=response))
    return response


@app.middleware("http")
async def browser_ui_security_headers(request: Request, call_next):
    response = await call_next(request)
    is_admin = request.url.path == "/admin" or request.url.path.startswith(("/admin/", "/api/private/admin-panel/"))
    is_site = request.url.path in {"/", "/site"} or request.url.path.startswith(
        ("/site/", "/beatmapsets/", "/api/private/web-site/")
    )
    if is_admin or is_site:
        response.headers["Cache-Control"] = "no-store"
        image_sources = "'self' data: blob: https:" if is_site else "'self' data:"
        media_sources = "'self' https:" if is_site else "'self'"
        style_sources = "'self' https://fonts.googleapis.com" if is_site else "'self'"
        font_sources = "'self' https://fonts.gstatic.com" if is_site else "'self'"
        response.headers["Content-Security-Policy"] = (
            "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; "
            f"object-src 'none'; img-src {image_sources}; media-src {media_sources}; "
            f"style-src {style_sources}; font-src {font_sources}; "
            "script-src 'self'; connect-src 'self'; worker-src 'none'"
        )
        response.headers["Cross-Origin-Opener-Policy"] = "same-origin"
        response.headers["Referrer-Policy"] = "no-referrer"
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()"
        if request.url.scheme == "https":
            response.headers["Strict-Transport-Security"] = "max-age=31536000"
    return response


unsafe_jwt_secrets = {
    "your_jwt_secret_here",
    "replace_with_64_random_hex_characters",
}
if settings.secret_key in unsafe_jwt_secrets or len(settings.secret_key) < 32:
    raise RuntimeError(
        "jwt_secret_key is unset or too short. Your server is unsafe. "
        "Use this command to generate it: openssl rand -hex 32"
    )
unsafe_web_client_secrets = {
    "your_osu_web_client_secret_here",
    "replace_with_a_different_random_secret",
}
if settings.osu_web_client_secret in unsafe_web_client_secrets:
    system_logger("Security").opt(colors=True).warning(
        "<y>osu_web_client_secret</y> is unset. Your server is unsafe. "
        "Use this command to generate: <blue>openssl rand -hex 40</blue>."
    )

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "main:app",
        host=settings.host,
        port=settings.port,
        reload=settings.debug,
        log_config=None,
        access_log=True,
    )
