#!/usr/bin/env bash
# Local development without building containers: one command, one terminal.
#   ./dev.sh        Postgres + Redis in Docker, then API (:8080), AI service (:8000), frontend (:5173)
# Ctrl+C stops the three app processes; the Docker containers keep running.
set -euo pipefail
cd "$(dirname "$0")"
ROOT=$(pwd)

# Older VS Code terminals may carry values injected from the root .env (Docker hostnames, placeholder
# keys). Environment variables beat .env files, so drop them: each service uses its own local settings
# (backend launchSettings.json, ai-service/.env).
unset REDIS_URL DATABASE_URL POSTGRES_HOST GROQ_API_KEY GROQ_MODEL JWT_SECRET APP_ENV CORS_ORIGINS \
      UPSTASH_REDIS_REST_URL UPSTASH_REDIS_REST_TOKEN AI_DB_USER AI_DB_PASSWORD

log() { printf '\033[1;36m[dev]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[dev]\033[0m %s\n' "$*" >&2; exit 1; }

# ---------- prerequisites ----------
command -v docker >/dev/null || die "docker not found"
docker info >/dev/null 2>&1 || die "Docker is not running. Start Docker Desktop and try again."
command -v dotnet >/dev/null || die "dotnet SDK not found"
command -v npm >/dev/null || die "npm not found"
[ -f .env ] || { cp .env.example .env; log "Created .env from .env.example"; }
port_busy() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null || (exec 3<>"/dev/tcp/localhost/$1") 2>/dev/null; }
for p in 8080:API 8000:"AI service" 5173:frontend; do
  port_busy "${p%%:*}" && die "Port ${p%%:*} (${p#*:}) is already in use. Stop the copy running in another terminal first."
done
[ -f ai-service/.env ] || die "ai-service/.env is missing (local AI settings: DATABASE_URL, REDIS_URL=redis://localhost:6380/0, GROQ_API_KEY)"

# ---------- infrastructure ----------
log "Starting Postgres (docker compose, profile localdb)"
docker compose --profile localdb up -d postgres >/dev/null

# Port 6379 is taken on this machine, so the local Redis listens on 6380.
if ! docker start erp-local-redis >/dev/null 2>&1; then
  log "Creating Redis container on localhost:6380"
  docker run -d --name erp-local-redis --restart unless-stopped -p 127.0.0.1:6380:6379 redis:7-alpine >/dev/null
fi

log "Waiting for Postgres"
for _ in $(seq 1 30); do
  docker compose exec -T postgres pg_isready -q >/dev/null 2>&1 && break
  sleep 1
done
docker compose exec -T postgres pg_isready -q >/dev/null 2>&1 || die "Postgres did not become ready"

# ---------- first-run setup ----------
if [ -x ai-service/.venv/Scripts/python ]; then PY=.venv/Scripts/python; else PY=.venv/bin/python; fi
if [ ! -x "ai-service/$PY" ]; then
  log "Creating ai-service virtualenv"
  (cd ai-service && python -m venv .venv)
  if [ -x ai-service/.venv/Scripts/python ]; then PY=.venv/Scripts/python; else PY=.venv/bin/python; fi
  (cd ai-service && "$PY" -m pip install -q -r requirements.txt -r requirements-dev.txt)
fi
[ -d frontend/node_modules ] || { log "Installing frontend packages"; (cd frontend && npm install); }

# ---------- app processes ----------
PID_DIR=$(mktemp -d)
run() { # run <name> <color> <dir> <command...>: output prefixed with [name]
  local name=$1 color=$2 dir=$3; shift 3
  ( echo "$BASHPID" > "$PID_DIR/$name"; cd "$ROOT/$dir" && exec "$@" ) 2>&1 \
    | sed -u "s/^/$(printf '\033[%sm[%s]\033[0m ' "$color" "$name")/" &
}

cleanup() {
  trap - INT TERM EXIT
  log "Stopping…"
  for f in "$PID_DIR"/*; do
    [ -f "$f" ] || continue
    pid=$(cat "$f")
    if [ -r "/proc/$pid/winpid" ] && command -v taskkill >/dev/null 2>&1; then
      # Git Bash: the app is a native Windows process; kill its whole tree (dotnet watch -> Erp.Api, etc.)
      taskkill //F //T //PID "$(cat "/proc/$pid/winpid")" >/dev/null 2>&1 || true
    else
      kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  rm -rf "$PID_DIR"
  kill 0 2>/dev/null || true
}
trap cleanup INT TERM EXIT

log "Starting API :8080, AI service :8000, frontend :5173  (Ctrl+C to stop)"
run api  "1;34" backend/src/Erp.Api dotnet watch run --non-interactive
run ai   "1;35" ai-service          "$PY" -m uvicorn app.main:app --port 8000 --reload
run web  "1;32" frontend            npm run dev

log "Open http://localhost:5173  (demo login: admin@demo.com / Admin@123)"
wait
