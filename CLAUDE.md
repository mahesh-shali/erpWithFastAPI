# CLAUDE.md

Guidance for Claude Code when working in this repository. [README.md](README.md) is the full reference
(environments, auth, rights, AI security, scaling, troubleshooting); [DEPLOYMENT.md](DEPLOYMENT.md) covers
Render + Aiven + Upstash. This file is the short version plus the rules that are easy to break.

## What this is

Multi-tenant ERP with an AI chatbot that answers business questions by generating SQL.

| Folder | Stack | Role |
|---|---|---|
| `backend/` | ASP.NET Core 8, EF Core 8 (Npgsql, snake_case), JWT, Redis | Auth/sessions, CRUD, dashboard, page rights. **Owns the DB schema** via EF migrations. |
| `ai-service/` | FastAPI, Groq (OpenAI-compatible tool calling), asyncpg, sqlglot | `/ai/chat`: question → `run_sql` tool loop → answer. Read-only DB role + RLS. |
| `frontend/` | React 18, Vite, TypeScript, React Router 6 (no UI lib) | SPA; Vite dev server proxies `/api` → :8080 and `/ai` → :8000 |
| `desktop/` | Electron 33, same React renderer (HashRouter), esbuild, electron-builder | Desktop client for the same API; own version/release cycle. See [desktop/README.md](desktop/README.md) |
| `infra/` | nginx, PgBouncer, k8s, Prometheus/Grafana | Edge routing (`/` SPA, `/api/*`, `/ai/*`), observability for local load tests |
| `load-tests/` | k6, Go (`conn-soak`) | Load profiles; results in `load-tests/results/` |

`db/init/*.sql` is **legacy and unused** (nothing references it; it still has the old `users.role` text column).
The schema source of truth is `backend/src/Erp.Api/Data/Migrations/`.

## Commands

Shell is Windows; the `.sh` scripts need `bash ./script.sh` from PowerShell. `make` may not be installed —
the Makefile shows the raw commands.

```bash
# Run
./dev.sh                         # Postgres (compose, profile localdb) + Redis on :6380 in Docker; API :8080, AI :8000, Vite :5173 natively
./run-local.sh [production]      # API + AI natively using .env.<mode>; migrates first (--skip-migrate)
make up [ENV=production]         # full docker-compose stack via .env.development / .env.production
./local.sh up | load <profile> | scale <n> | psql | down   # all-local stack + Prometheus/Grafana/k6 (app on :8088)

# Tests
dotnet test backend/tests/Erp.Api.UnitTests
dotnet test backend/tests/Erp.Api.UnitTests --filter "FullyQualifiedName~TokenServiceTests"   # single class
dotnet test backend/tests/Erp.Api.IntegrationTests        # needs Docker (Testcontainers Postgres/Redis, schema built from migrations)
cd ai-service && pytest -m "not integration"               # unit; CI requires --cov=app --cov-fail-under=85
cd ai-service && pytest tests/test_sql_guard.py -k name    # single test
cd ai-service && TEST_AI_DATABASE_URL=postgresql://erp_ai_ro:ai_readonly_dev@localhost:5432/erp pytest -m integration
cd frontend && npm test                                    # vitest; `npx vitest run src/components/ChatPanel.test.tsx` for one file
cd frontend && npx playwright test                         # e2e, needs the stack running
cd desktop && npm test                                     # vitest: renderer screens + electron/main
cd desktop && npm run test:e2e                             # builds, then Playwright drives real Electron vs e2e/mock-server.ts
cd desktop && npm run dev | dev:prod                       # Vite + Electron against :8088 (local.sh) or the Render prod URL

# All test cases in one HTML report (results + per-test re-run commands + manual checklist); see TESTING.md
node scripts/test-report.mjs --open            # --e2e with the app running, --only frontend,ai for a subset

# Lint / typecheck / build (what CI "static" runs)
ruff check ai-service            # line-length 110, py312
cd frontend && npm run typecheck
cd desktop && npm run typecheck  # renderer + electron tsconfigs
dotnet build backend/ErpPlatform.sln

# Migrations (needs: dotnet tool install -g dotnet-ef)
dotnet ef migrations add <Name> --project backend/src/Erp.Api --output-dir Data/Migrations
dotnet ef migrations script --project backend/src/Erp.Api --idempotent    # review SQL
dotnet run --project backend/src/Erp.Api -- --migrate [--seed]             # apply (+ demo data), then exit
```

CI (`.github/workflows/ci.yml`) gates phases: static → unit → integration → Playwright e2e → k6 smoke (main only),
plus a separate `desktop` job (typecheck, vitest, build, Electron e2e).
`deploy.yml` runs migrations against prod, then triggers Render deploy hooks. `desktop-release.yml` publishes the
Windows installer to GitHub Releases on a `v<version>` tag that must equal `desktop/package.json` `version`
(installed apps auto-update from the repo's *latest* release, so nothing else should publish a "latest" release).

## Architecture essentials

**Tenancy (two different mechanisms).**
- API: the app DB user owns the tables and **bypasses RLS**. Isolation is the EF global query filter on every
  `ITenantOwned` entity (`Data/ErpDbContext.cs`) plus `organization_id` stamping in `SaveChangesAsync`.
  Tenant comes from the JWT `org_id` claim (`Infrastructure/TenantContext.cs`). Never set `OrganizationId`
  from request input; use `IgnoreQueryFilters()` only deliberately.
- AI service: connects as a per-environment login that is a member of the `NOLOGIN` role `erp_ai_ro`
  (SELECT on a few tables + `org_users` view only), runs each query in a read-only transaction with
  `app.org_id` set, and Postgres RLS filters rows.

**Auth.** HS256 JWT shared by API and AI service (`JWT_SECRET` ≥ 32 bytes). Access token 15 min with a `sid`
claim; every request also checks the `user_sessions` row is active (Redis-cached ~30 s) — API via
`JwtBearerEvents.OnTokenValidated` in `Program.cs`, AI via the SQL function `session_is_active()`. Refresh token
is an HttpOnly cookie (`erp_refresh`, path `/api/auth`) with rotation + reuse detection (`Services/SessionService.cs`).
Optional TOTP two-factor (`Services/TwoFactor.cs`: setup → enable with first code → recovery codes; a code is
accepted once via `TwoFactorLastStep`) with a separate "remember this device" cookie (trusted devices).
`/api/auth/sessions` and `/api/auth/logout-others` back the per-device sign-out screens.

**Messages.** End-to-end encrypted chat between org members (`ChatController`, `Services/ChatService.cs`).
Encryption is client-side only (`src/chat/crypto.ts`, identical in frontend and desktop); the server stores
ciphertext and public keys. Live updates are SSE on `GET /api/chat/stream`, fanned out across API replicas through
the Redis channel `erp:chat:events` (`Services/ChatEventBus.cs`, in-process without Redis). Events are content-free
notices; clients re-fetch through normal authorized endpoints.

**Rights.** Three-level menu `menu_modules > menu_groups > pages`, all defined in code in
`Data/Configurations/DefaultPermissions.cs` and seeded with `HasData`. Six rights per page
(`isVisible, canRead, canWrite, canUpdate, canDelete, isRestricted`). Resolution order: user override →
org role default → system role default; SuperAdmin has everything; `isRestricted` hard-blocks.
The single source of truth is the SQL function `effective_page_permissions(user)`, used by both API and AI.
API endpoints are protected with `[RequirePage(PageKeys.X, PageAction.Y)]` (`Services/PermissionService.cs`).
Role hierarchy by `level` (SuperAdmin 100 > Admin 50 > Manager 30 > User 10): users only manage roles strictly below them
and cannot grant rights they lack.

**Internal gRPC.** Clients call `POST /api/ai/chat` on the API, which checks the `ai-chat` right and forwards to
the AI service over gRPC (`backend/src/Erp.Api/Protos/ai_assistant.proto`, `Services/AiAssistantGateway.cs` →
`ai-service/app/grpc_server.py`, port `GRPC_PORT` 50051) with the caller's own JWT as metadata, so the AI service
re-checks everything. The REST `/ai/chat` and gRPC share `chat_flow.answer()`. After editing the .proto run
`cd ai-service && python scripts/gen_proto.py` (committed stubs in `app/grpc_gen/`; a test fails when stale).

**AI chat flow** (`ai-service/app/`): `auth.py` (JWT + session) → Redis rate limit and answer cache
(`cache.py`, keyed by org + question + user's readable pages) → `agent.py` tool loop with schema prompt from
`schema.py` → every SQL passes `sql_guard.py` (sqlglot AST: single SELECT, allow-listed tables, blocked
functions, forced LIMIT) → `db.py` executes under RLS → audited via `log_chat()`. Tables the AI may touch
for a user depend on page rights (`PAGE_TABLES` in `sql_guard.py`).

**Frontend.** `src/api/client.ts` handles 401 → single shared `/api/auth/refresh` → retry. `PermissionsContext`
loads `GET /api/me/permissions` to build the menu and guard routes (UI-only; the API is the real check).
Routes in `src/App.tsx` mirror each page's `route`; unbuilt pages fall through to `ComingSoonRoute`.

**Desktop.** The renderer is sandboxed with CSP `connect-src 'none'` and never sees a token: it calls
`window.erp.api.request(...)` (preload contextBridge, channel types in `electron/shared/contract.ts`), and the main
process (`electron/main/api-client.ts`) owns the session — access token in memory, refresh token in OS-encrypted
`safeStorage`, sent as a `Cookie` header; only relative `/api/…` and `/ai/…` paths are allowed. The SSE chat stream
also runs in main (`chat-stream.ts`). Modes `ERP_ENV=local|production` (URLs in `electron/main/environment.ts`) use
separate profiles. Don't relax the hardening in `security.ts`/`ipc.ts` or the Electron fuses in `package.json`.

## Rules when changing things

- **Schema changes are code-first only.** Edit `Domain/Entities.cs` + `Data/Configurations/EntityConfigurations.cs`
  (+ `DbSet` in `ErpDbContext`), generate a migration, **read the generated Up/Down**, commit model and migration
  together. If EF warns "may result in the loss of data", hand-edit to copy data first (see `AddRolesAndUserSessions`).
  RLS policies, grants, roles, views and SQL functions go in `migrationBuilder.Sql(...)`.
- **New tenant table the AI should query** needs all of: `ITenantOwned`, RLS policy + `GRANT SELECT ... TO erp_ai_ro`
  in the migration, a description in `ai-service/app/schema.py`, and an entry in `ALLOWED_TABLES`/`PAGE_TABLES`
  in `sql_guard.py`. Never grant the AI role access to `users`, `user_sessions` or `chat_audit_log`.
- **Adding a page:** `PageKeys` constant → `P(...)` row with a **new, never-reused id** and default rights in
  `Matrix` (`DefaultPermissions.cs`) → migration. When the screen exists set `implemented: true`, add the
  `<Route>` in `App.tsx`, and protect the API with `[RequirePage]`.
- **Production guard rails** in `Program.cs` and `ai-service/app/config.py` refuse to start with placeholder
  secrets, demo seed, or `Database:MigrateOnStartup`. Don't weaken them; migrations run as a one-off job (`--migrate`).
- Connection pools are deliberately small (shared Aiven plan, ~17 usable connections) and configured for
  transaction pooling (`Max Auto Prepare=0`, no server-side prepared statements). Keep it that way.
- Real `.env*` files hold secrets and are gitignored; only `*.example` templates are committed. Update the
  templates (and README's configuration table) when adding a variable. Never read secrets into commits or output.
- New screen or endpoint: add a screen test (use `renderScreen`/`mockApi`/`perm` from `frontend/src/test/utils.tsx`),
  API integration tests, a route case in `App.test.tsx`, an e2e journey in `e2e/screens.spec.ts`, and a manual
  check in `scripts/manual-checks.json` (see TESTING.md).
- **Keep `desktop/src` in sync with `frontend/src`.** The desktop renderer is a copy of the web app; when a screen
  changes on the web, copy it to desktop. Only `api/client.ts`, `auth/AuthContext.tsx`, `auth/PrivateRoute.tsx`,
  `main.tsx`, `pages/LoginPage.tsx` and `chat/events.ts` differ on purpose (plus desktop-only `pages/account/DevicesPage.tsx`).
  Desktop uses HashRouter.
- Running `dotnet test` rebuilds `Erp.Api` and stops a `dotnet watch` started by `dev.sh`; restart the API afterwards.
- **Stock changes only through `IStockPosting.PostAsync`** (inside a transaction, after `EnsureDefaultStoreAsync`). Never
  update `products.stock_quantity` or `stock_balances` directly: the invariant is stock_quantity = sum of balances, and
  every movement needs a ledger row. New stock documents (GRN, delivery challan…) follow the Stock Entry pattern:
  Draft → Submitted (post) → Cancelled (reverse the voucher's ledger rows), numbers from `NextNumberAsync(prefix)`.
- Vite's watcher can miss files written by scripts on Windows; if the dev app shows old code, `touch` the files.
- `ai-service/.env` is used only by `dev.sh`; `run-local.sh` sets `AI_SERVICE_ENV_FILE=none` so the root env file wins.

## Demo data

`--seed` (dev only) creates *Demo Corp* and *Other Tenant Ltd* (used by tests to prove isolation).
Logins: `admin@demo.com` / `Admin@123` (SuperAdmin), `manager@demo.com` / `Manager@123` (Admin),
`lead@demo.com` / `Lead@12345` (Manager), `staff@demo.com` / `Staff@1234` (User).
