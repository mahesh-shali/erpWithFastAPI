#!/usr/bin/env bash
# LOCAL environment: the whole platform on this machine in Docker — Postgres, Redis, API, AI service,
# frontend, nginx — plus Prometheus, Grafana and k6 for load testing. No cloud services.
#
#   ./local.sh up                 build + start everything (migrations and demo data run automatically)
#   ./local.sh load [profile]     run k6: smoke (default) | load | stress | spike | soak | breakpoint
#                                 extra k6 env: VUS=300 DURATION=10m USERS=100 THINK=0.5 AI=1 ./local.sh load load
#   ./local.sh conns [total]      hold N concurrent keep-alive connections (default 100000) against nginx
#                                 SOAK_INTERVAL=30 SOAK_HOLD=5m SOAK_RAMP=1000 SOAK_PATH=/health/live ./local.sh conns 100000
#   ./local.sh scale <n>          run n API replicas (horizontal scaling test)
#   ./local.sh status             containers + health
#   ./local.sh logs [service]     follow logs
#   ./local.sh migrate            apply EF Core migrations (+ demo data) to the local database
#   ./local.sh psql               SQL shell on the local database
#   ./local.sh redis              redis-cli on the local Redis
#   ./local.sh down               stop (keeps data)
#   ./local.sh reset              stop and DELETE all local data (database, metrics, dashboards)
#
# PowerShell: bash ./local.sh up
#
#   App  http://localhost:8088  (admin@demo.com / Admin@123)    Grafana  http://localhost:3000
#   Postgres  localhost:5434                                     Prometheus  http://localhost:9090
set -euo pipefail
cd "$(dirname "$0")"

log() { printf '\033[1;36m[local]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[local]\033[0m %s\n' "$*" >&2; exit 1; }

ENV_FILE=.env.local
COMPOSE=(docker compose --env-file "$ENV_FILE" -f docker-compose.yml -f docker-compose.local.yml)

command -v docker >/dev/null || die "docker not found"
docker info >/dev/null 2>&1 || die "Docker is not running. Start Docker Desktop and try again."

if [ ! -f "$ENV_FILE" ]; then
  cp .env.local.example "$ENV_FILE"
  log "Created $ENV_FILE from .env.local.example"
  # Reuse your Groq key for /ai/chat if you have one in .env.development (stays on this machine).
  if [ -f .env.development ] && key=$(grep -E '^GROQ_API_KEY=' .env.development | head -1 | cut -d= -f2-) \
     && [ -n "$key" ] && [ "$key" != "gsk_..." ]; then
    sed -i "s|^GROQ_API_KEY=.*|GROQ_API_KEY=$key|" "$ENV_FILE"
    log "Copied GROQ_API_KEY from .env.development"
  fi
fi
# Shell variables override --env-file values in compose. If the terminal has another env loaded
# (e.g. VS Code injecting .env with APP_ENV=development), compose would target the wrong project and
# ports. Drop every variable this env file defines so .env.local always wins.
while IFS= read -r var; do unset "$var"; done < <(grep -oE '^[A-Za-z_][A-Za-z0-9_]*=' "$ENV_FILE" | tr -d '=')
envval() { { grep -E "^$1=" "$ENV_FILE" || true; } | tail -1 | cut -d= -f2- | sed -E 's/[[:space:]]+#.*$//; s/[[:space:]]+$//'; }
HTTP_PORT=$(envval HTTP_PORT); HTTP_PORT=${HTTP_PORT:-8088}
GRAFANA_PORT=$(envval GRAFANA_PORT); GRAFANA_PORT=${GRAFANA_PORT:-3000}
# `--profile x` on the command line replaces COMPOSE_PROFILES from the env file, so keep those (localdb) too.
WITH_K6=(--profile loadtest)
for p in $(envval COMPOSE_PROFILES | tr ',' ' '); do WITH_K6+=(--profile "$p"); done

wait_ready() {
  log "Waiting for the API to become ready (first build can take a few minutes)…"
  for _ in $(seq 1 120); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$HTTP_PORT/health/ready" || true)
    [ "$code" = 200 ] && { log "API ready"; return 0; }
    sleep 2
  done
  "${COMPOSE[@]}" ps
  die "API did not become ready. Check: ./local.sh logs api   (and: ./local.sh logs migrator)"
}

urls() {
  cat <<EOF

  App         http://localhost:$HTTP_PORT        login admin@demo.com / Admin@123
  Grafana     http://localhost:$GRAFANA_PORT        dashboard "ERP — Load test & capacity"
  Prometheus  http://localhost:$(envval PROMETHEUS_PORT)
  Postgres    localhost:$(envval LOCAL_PG_PORT)  db=$(envval POSTGRES_DB) user=$(envval POSTGRES_USER)

  Next: ./local.sh load smoke     then watch Grafana
EOF
}

cmd=${1:-help}; shift || true
case "$cmd" in
  up)
    log "Building and starting the local stack"
    "${COMPOSE[@]}" up -d --build --remove-orphans
    wait_ready
    urls ;;

  load)
    profile=${1:-smoke}
    case "$profile" in smoke|load|stress|spike|soak|breakpoint) ;; *) die "Unknown profile: $profile" ;; esac
    code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$HTTP_PORT/health/ready" || true)
    [ "$code" = 200 ] || die "Stack is not running (or not ready). Run: ./local.sh up"
    testid="$profile-$(date +%Y%m%d-%H%M%S)"
    log "k6 profile=$profile testid=$testid  →  Grafana: http://localhost:$GRAFANA_PORT/d/erp-load-test?var-testid=$testid&refresh=5s"
    k6env=()
    for v in VUS DURATION USERS THINK AI AI_VUS ADMIN_EMAIL ADMIN_PASSWORD; do
      [ -n "${!v:-}" ] && k6env+=(-e "$v=${!v}")
    done
    MSYS_NO_PATHCONV=1 "${COMPOSE[@]}" "${WITH_K6[@]}" run --rm --no-deps --quiet-pull "${k6env[@]}" -e PROFILE="$profile" -e RESULTS_DIR=/results k6 \
      run -o experimental-prometheus-rw --tag testid="$testid" erp-all-apis.js \
      && log "Done. JSON summary in load-tests/results/" \
      || { rc=$?; log "k6 exited with $rc (thresholds crossed or run aborted — see summary above)"; exit $rc; } ;;

  conns)
    total=${1:-100000}
    [[ "$total" =~ ^[0-9]+$ ]] && [ "$total" -gt 0 ] || die "usage: ./local.sh conns <total-connections>"
    code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$HTTP_PORT/health/ready" || true)
    [ "$code" = 200 ] || die "Stack is not running (or not ready). Run: ./local.sh up"
    # ~20k per generator: one IP has ~64k source ports; staying well below avoids TIME_WAIT exhaustion on redials.
    replicas=$(( (total + 19999) / 20000 )); per=$(( (total + replicas - 1) / replicas ))
    # Apply the soak-sized nginx limits (ulimits/sysctls need a recreate) and the new Prometheus scrape job.
    "${COMPOSE[@]}" up -d --no-deps nginx >/dev/null
    "${COMPOSE[@]}" exec -T nginx nginx -s reload >/dev/null 2>&1 || true
    "${COMPOSE[@]}" kill -s SIGHUP prometheus >/dev/null 2>&1 || true
    log "Holding $total connections: $replicas generators x $per  (ramp ${SOAK_RAMP:-1000}/s each, request every ~${SOAK_INTERVAL:-30}s, hold ${SOAK_HOLD:-5m})"
    log "Watch: http://localhost:$GRAFANA_PORT  → 'nginx connections / requests'   Ctrl+C stops the test"
    SOAK_CONNS_PER_REPLICA=$per "${COMPOSE[@]}" "${WITH_K6[@]}" up --build --no-deps --scale conn-soak="$replicas" conn-soak
    "${COMPOSE[@]}" "${WITH_K6[@]}" rm -fs conn-soak >/dev/null 2>&1 || true
    log "Done. Per-generator DONE lines above have totals and p50/p95/p99." ;;

  scale)
    n=${1:?usage: ./local.sh scale <api-replicas>}
    "${COMPOSE[@]}" up -d --no-deps --scale api="$n" api
    log "API replicas: $n (nginx and Prometheus pick up new replicas within ~10s)" ;;

  status)
    "${COMPOSE[@]}" ps
    printf '\nAPI ready: '; curl -s "http://localhost:$HTTP_PORT/health/ready" || echo "not reachable"; echo ;;

  logs)  "${COMPOSE[@]}" logs -f --tail=100 "$@" ;;
  migrate)
    "${COMPOSE[@]}" up -d postgres redis
    "${COMPOSE[@]}" run --rm migrator && log "Migrations applied." ;;
  redis) "${COMPOSE[@]}" exec redis redis-cli "$@" ;;
  psql)  "${COMPOSE[@]}" exec postgres psql -U "$(envval POSTGRES_USER)" -d "$(envval POSTGRES_DB)" ;;
  down)  "${COMPOSE[@]}" "${WITH_K6[@]}" down; log "Stopped (data kept). ./local.sh up to start again." ;;
  reset)
    read -r -p "Delete ALL local data (database, Prometheus, Grafana volumes)? [y/N] " ans
    [ "$ans" = y ] || [ "$ans" = Y ] || die "Cancelled"
    "${COMPOSE[@]}" "${WITH_K6[@]}" down -v; log "Local stack and volumes removed." ;;
  urls)  urls ;;
  *) sed -n '2,22p' "$0" ;;
esac
