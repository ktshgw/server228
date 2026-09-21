FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim AS builder
WORKDIR /app

RUN apt-get update \
 && apt-get install -y curl git gcc pkg-config default-libmysqlclient-dev \
 && rm -rf /var/lib/apt/lists/* \
 && curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y

ENV PATH="/root/.cargo/bin:${PATH}" \
    PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 UV_PROJECT_ENVIRONMENT=/app/.venv

COPY pyproject.toml uv.lock ./
COPY packages/ ./packages/

RUN uv sync --frozen --no-dev

COPY alembic.ini ./
COPY tools/ ./tools/
COPY migrations/ ./migrations/
COPY static/ ./static/
COPY app/ ./app/
COPY main.py ./
COPY install-all-deps.py ./
COPY scripts/ ./scripts/
COPY nginx/ ./nginx/
COPY LICENSE README.md README.zh-cn.md CONTRIBUTING.md ./
COPY .dockerignore .editorconfig .gitignore .pre-commit-config.yaml .env.example ./
COPY Dockerfile docker-entrypoint.sh ./
COPY docker-compose.yml docker-compose.easy.yml docker-compose-osurx.yml Caddyfile.easy ./

# Publish the exact corresponding source used for this image. Keep the input
# list explicit so local .env files, credentials, databases, and player data can
# never be included accidentally. The output is written outside /app first so
# the archive cannot contain itself when rebuilding an existing context.
RUN tar -czf /tmp/pulse-server-source.tar.gz \
      --exclude='static/source' \
      app migrations packages static tools scripts nginx \
      main.py install-all-deps.py alembic.ini pyproject.toml uv.lock \
      LICENSE README.md README.zh-cn.md CONTRIBUTING.md \
      .dockerignore .editorconfig .gitignore .pre-commit-config.yaml .env.example \
      Dockerfile docker-entrypoint.sh \
      docker-compose.yml docker-compose.easy.yml docker-compose-osurx.yml Caddyfile.easy \
 && mkdir -p /app/static/source /app/static/site/source \
 && mv /tmp/pulse-server-source.tar.gz /app/static/source/pulse-server-source.tar.gz \
 && cp /app/static/source/pulse-server-source.tar.gz /app/static/site/source/pulse-server-source.tar.gz

# ---

FROM ghcr.io/astral-sh/uv:python3.13-bookworm-slim
WORKDIR /app

RUN apt-get update \
 && apt-get install -y curl netcat-openbsd git \
 && rm -rf /var/lib/apt/lists/*

ENV PATH="/app/.venv/bin:${PATH}" \
    PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1

COPY --from=builder /app/.venv /app/.venv
COPY --from=builder /app /app

RUN mkdir -p /app/logs
VOLUME ["/app/logs"]

COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN sed -i 's/\r$//' /app/docker-entrypoint.sh \
 && chmod +x /app/docker-entrypoint.sh

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8000/health || exit 1

ENTRYPOINT ["/app/docker-entrypoint.sh"]
CMD ["uv", "run", "--no-sync", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000", "--proxy-headers", "--forwarded-allow-ips=*"]
