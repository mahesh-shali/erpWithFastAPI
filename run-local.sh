#!/usr/bin/env bash
# Run the backend API and the AI service natively (no app containers), in development or production mode.
#
#   ./run-local.sh                         development: settings from .env.development, hot reload
#   ./run-local.sh production              production:  settings from .env.production, Release build, no reload
#   ./run-local.sh <mode> --skip-migrate   don't apply EF Core migrations first
#
# From PowerShell:  bash ./run-local.sh production
#
# API on http://localhost:8080, AI service on http://localhost:8000. Ctrl+C stops both.
# Migrations run first (like the compose `migrator` job). If the env file points at the compose
# hostnames `postgres` / `redis`, local Docker containers are started and used instead.
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(pwd)

log() { printf '\033[1;36m[run-local]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[run-local]\033[0m %s\n' "$*" >&2; exit 1; }

MODE=development
MIGRATE=1
for arg in "$@"; do
  case "$arg" in
    dev|development) MODE=development ;;
    prod|production) MODE=production ;;
    --skip-migrate)  MIGRATE=0 ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) die "Unknown argument: $arg (expected development | production | --skip-migrate)" ;;
  esac
done
ENV_FILE=".env.$MODE"
[ -f "$ENV_FILE" ] || die "$ENV_FILE is missing. Create it from .env.example (dev) or .env.production.example (prod)."

# ---------- load the env file (KEY=VALUE, '# comments', optional quotes) into shell variables ----------
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  [[ "$line" =~ ^[[:space:]]*([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]] || continue
  key="${BASH_REMATCH[1]}"; val="${BASH_REMATCH[2]}"
  if [[ "$val" =~ ^\"(.*)\"[[:space:]]*(#.*)?$ ]] || [[ "$val" =~ ^\'(.*)\'[[:space:]]*(#.*)?$ ]]; then
    val="${BASH_REMATCH[1]}"
  else
    val=$(printf '%s' "$val" | sed -E 's/[[:space:]]+#.*$//; s/[[:space:]]+$//')
  fi
  printf -v "$key" '%s' "$val"
done < "$ENV_FILE"

ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-$([ "$MODE" = production ] && echo Production || echo Development)}
APP_ENV=${APP_ENV:-$MODE}
SEED_DEMO_DATA=${SEED_DEMO_DATA:-false}
[ "$MODE" = production ] && [ "$SEED_DEMO_DATA" = true ] && die "SEED_DEMO_DATA=true is not allowed in production."
for v in POSTGRES_HOST POSTGRES_PORT POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD AI_DB_USER AI_DB_PASSWORD JWT_SECRET; do
  [ -n "${!v:-}" ] || die "$v is not set in $ENV_FILE"
done

# ---------- prerequisites ----------
command -v dotnet >/dev/null || die "dotnet SDK not found"
port_busy() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }
for p in 8080:API 8000:"AI service"; do
  port_busy "${p%%:*}" && die "Port ${p%%:*} (${p#*:}) is already in use. Stop the other copy first."
done

if [ -x ai-service/.venv/Scripts/python ]; then PY="$ROOT/ai-service/.venv/Scripts/python"; else PY="$ROOT/ai-service/.venv/bin/python"; fi
if [ ! -x "$PY" ]; then
  log "Creating ai-service virtualenv"
  (cd ai-service && python -m venv .venv)
  if [ -x ai-service/.venv/Scripts/python ]; then PY="$ROOT/ai-service/.venv/Scripts/python"; else PY="$ROOT/ai-service/.venv/bin/python"; fi
  "$PY" -m pip install -q -r ai-service/requirements.txt -r ai-service/requirements-dev.txt
fi

# ---------- compose hostnames -> local Docker containers ----------
if [ "$POSTGRES_HOST" = postgres ]; then
  docker info >/dev/null 2>&1 || die "Docker is not running (needed for the local Postgres container)."
  log "Starting local Postgres container"
  docker compose --env-file "$ENV_FILE" --profile localdb up -d postgres >/dev/null
  for _ in $(seq 1 30); do
    docker compose --env-file "$ENV_FILE" exec -T postgres pg_isready -q >/dev/null 2>&1 && break; sleep 1
  done
  POSTGRES_HOST=localhost; POSTGRES_PORT=5432
fi
if [[ "${REDIS_URL:-}" == *"://redis:"* || "${API_REDIS_CONNECTION:-}" == redis:* ]]; then
  docker info >/dev/null 2>&1 || die "Docker is not running (needed for the local Redis container)."
  # Port 6379 is taken on this machine, so the local Redis listens on 6380 (same container as dev.sh).
  docker start erp-local-redis >/dev/null 2>&1 \
    || docker run -d --name erp-local-redis --restart unless-stopped -p 127.0.0.1:6380:6379 redis:7-alpine >/dev/null
  [[ "${REDIS_URL:-}" == *"://redis:"* ]] && REDIS_URL="redis://localhost:6380/0"
  [[ "${API_REDIS_CONNECTION:-}" == redis:* ]] && API_REDIS_CONNECTION="localhost:6380"
fi
# The API falls back to the compose Redis when API_REDIS_CONNECTION is unset; locally that is our container.
if [ -z "${API_REDIS_CONNECTION+x}" ]; then
  docker start erp-local-redis >/dev/null 2>&1 \
    || docker run -d --name erp-local-redis --restart unless-stopped -p 127.0.0.1:6380:6379 redis:7-alpine >/dev/null
  API_REDIS_CONNECTION="localhost:6380"
fi

# ---------- per-service settings (identical to docker-compose.yml, with localhost URLs) ----------
SSLMODE=${POSTGRES_SSLMODE:-disable}
npg_quote() { printf "'%s'" "${1//\'/\'\'}"; }
PG_CONN="Host=$POSTGRES_HOST;Port=$POSTGRES_PORT;Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$(npg_quote "$POSTGRES_PASSWORD");SSL Mode=$SSLMODE;Pooling=true;Maximum Pool Size=${API_DB_POOL_SIZE:-100};No Reset On Close=true;Max Auto Prepare=0"
urlenc() { "$PY" -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1],safe=""))' "$1"; }
AI_DB_URL="postgresql://$(urlenc "$AI_DB_USER"):$(urlenc "$AI_DB_PASSWORD")@$POSTGRES_HOST:$POSTGRES_PORT/$POSTGRES_DB?sslmode=$SSLMODE"
# Browsers on this machine reach the services from localhost; keep the env file's public origins too.
CORS=$(printf '%s\n' http://localhost:5173 http://localhost ${CORS_ORIGINS//,/ } | awk 'NF && !seen[$0]++' | paste -sd, -)

API_ENV=(
  ASPNETCORE_ENVIRONMENT="$ASPNETCORE_ENVIRONMENT"
  ASPNETCORE_URLS="http://localhost:8080"
  ConnectionStrings__Postgres="$PG_CONN"
  ConnectionStrings__Redis="$API_REDIS_CONNECTION"
  JWT_SECRET="$JWT_SECRET" JWT_ISSUER="${JWT_ISSUER:-erp-api}" JWT_AUDIENCE="${JWT_AUDIENCE:-erp-clients}"
  AiService__BaseUrl="http://localhost:8000"
  Cors__Origins__0="$CORS"
  Database__MigrateOnStartup=false
  Database__SeedDemoData="$SEED_DEMO_DATA"
  AI_DB_USER="$AI_DB_USER" AI_DB_PASSWORD="$AI_DB_PASSWORD"
)
AI_ENV=(
  AI_SERVICE_ENV_FILE=none   # ignore ai-service/.env; use only the values below
  APP_ENV="$APP_ENV"
  CORS_ORIGINS="$CORS"
  DATABASE_URL="$AI_DB_URL"
  DB_POOL_MIN="${DB_POOL_MIN:-2}" DB_POOL_MAX="${DB_POOL_MAX:-20}"
  REDIS_URL="${REDIS_URL:-redis://localhost:6380/0}"
  UPSTASH_REDIS_REST_URL="${UPSTASH_REDIS_REST_URL:-}" UPSTASH_REDIS_REST_TOKEN="${UPSTASH_REDIS_REST_TOKEN:-}"
  JWT_SECRET="$JWT_SECRET" JWT_ISSUER="${JWT_ISSUER:-erp-api}" JWT_AUDIENCE="${JWT_AUDIENCE:-erp-clients}"
  GROQ_API_KEY="${GROQ_API_KEY:-}" GROQ_MODEL="${GROQ_MODEL:-openai/gpt-oss-120b}"
  PYTHONUNBUFFERED=1
)

API_DIR="$ROOT/backend/src/Erp.Api"
PUBLISH_DIR="$API_DIR/bin/publish"   # gitignored (**/bin/)

# ---------- build + migrate ----------
if [ "$MODE" = production ]; then
  log "Publishing API (Release)"
  dotnet publish "$API_DIR/Erp.Api.csproj" -c Release -o "$PUBLISH_DIR" --nologo -v quiet >/dev/null \
    || die "dotnet publish failed (re-run: dotnet publish backend/src/Erp.Api -c Release)"
  API_CMD=(dotnet Erp.Api.dll); API_CWD="$PUBLISH_DIR"
  AI_CMD=("$PY" -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --workers "${AI_WORKERS:-1}" --timeout-keep-alive 30 --no-access-log)
else
  log "Building API (Debug)"
  dotnet build "$API_DIR/Erp.Api.csproj" --nologo -v quiet >/dev/null \
    || die "dotnet build failed (re-run: dotnet build backend/src/Erp.Api)"
  API_CMD=(dotnet watch run --no-launch-profile --non-interactive); API_CWD="$API_DIR"
  AI_CMD=("$PY" -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload)
fi

if [ "$MIGRATE" = 1 ]; then
  log "Applying migrations to $POSTGRES_HOST/$POSTGRES_DB (seed demo data: $SEED_DEMO_DATA)"
  if [ "$MODE" = production ]; then
    (export "${API_ENV[@]}"; cd "$PUBLISH_DIR" && dotnet Erp.Api.dll --migrate) || die "Migrations failed"
  else
    (export "${API_ENV[@]}"; cd "$API_DIR" && dotnet run --no-build --no-launch-profile -- --migrate) || die "Migrations failed"
  fi
fi

# ---------- run both services ----------
PID_DIR=$(mktemp -d)
run() { # run <name> <color> <dir> <env-array-name> <command...>: output prefixed with [name]
  local name=$1 color=$2 dir=$3 envref="$4[@]"; shift 4
  # export (not `env K=V cmd`) so secrets don't show up in process command lines
  ( echo "$BASHPID" > "$PID_DIR/$name"; export "${!envref}"; cd "$dir" && exec "$@" ) 2>&1 \
    | sed -u "s/^/$(printf '\033[%sm[%s]\033[0m ' "$color" "$name")/" &
}
cleanup() {
  trap - INT TERM EXIT
  log "Stopping…"
  for f in "$PID_DIR"/*; do
    [ -f "$f" ] || continue
    pid=$(cat "$f")
    if [ -r "/proc/$pid/winpid" ] && command -v taskkill >/dev/null 2>&1; then
      taskkill //F //T //PID "$(cat "/proc/$pid/winpid")" >/dev/null 2>&1 || true   # whole Windows process tree
    else
      kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  rm -rf "$PID_DIR"
  kill 0 2>/dev/null || true
}
trap cleanup INT TERM EXIT

log "Mode: $MODE  |  API http://localhost:8080  |  AI http://localhost:8000  (Ctrl+C to stop)"
run api "1;34" "$API_CWD"         API_ENV "${API_CMD[@]}"
run ai  "1;35" "$ROOT/ai-service" AI_ENV  "${AI_CMD[@]}"
[ "$MODE" = development ] && log "Swagger: http://localhost:8080/swagger   AI docs: http://localhost:8000/ai/docs"
log "Health:  http://localhost:8080/health/ready   http://localhost:8000/ai/health"
wait
