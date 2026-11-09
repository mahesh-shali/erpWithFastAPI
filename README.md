# ERP Platform — ASP.NET Core + FastAPI + React + PostgreSQL + Groq LLM

A multi-tenant ERP with an AI chatbot that answers business questions in plain English
("What is my revenue by city?") by querying PostgreSQL through an LLM on Groq
(default model `openai/gpt-oss-120b`, OpenAI-compatible tool calling).

The database is **code-first**: every table, column, index, constraint, RLS policy and role is
defined in C# (EF Core entities + migrations). No hand-written schema SQL to run.

```
             ┌──────────── nginx / k8s Ingress (rate limit, gzip, LB) ────────────┐
 Browser ──▶ │  /           → React SPA (static, CDN-cacheable)                     │
             │  /api/*      → ASP.NET Core 8 Web API  (auth, CRUD, dashboard)      │
             │  /ai/*       → FastAPI AI service      (LLM NL→SQL agent)           │
             └─────────────────────────────────────────────────────────────────────┘
   ASP.NET Core ──▶ PostgreSQL (Aiven / local) ◀── FastAPI (read-only role + RLS)
        └──────────── Redis (cache, rate limits, answer cache) ───────────┘
                                                      FastAPI ──▶ Groq API  
```

| Folder | Tech | Responsibility |
|---|---|---|
| `backend/` | ASP.NET Core 8, EF Core 8, Npgsql, JWT, Redis | Auth (issues JWT), customers/products/orders, dashboard, **owns the DB schema (migrations)** |
| `ai-service/` | FastAPI, Groq SDK, asyncpg, sqlglot | LLM tool-calling agent: question → SQL → rows → answer |
| `frontend/` | React 18, Vite, TypeScript, React Router | Login, role-based routes, dashboard, chat UI |
| `infra/` | nginx, PgBouncer, Kubernetes | Edge routing, connection pooling, HPA autoscaling |
| `load-tests/` | k6 | Smoke / load / stress / spike profiles |

---

> **Deploying to the cloud for free?** See [DEPLOYMENT.md](DEPLOYMENT.md): Render (frontend, API and AI)
> + Aiven + Upstash + Groq, with GitHub Actions for migrations and deploys.

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| Docker + Docker Compose | Compose ≥ 2.20 | Running the full stack |
| .NET SDK | 8.0 | Backend, migrations |
| `dotnet-ef` | `dotnet tool install -g dotnet-ef` | Creating migrations |
| Python | 3.12+ | AI service dev/tests |
| Node.js | 22 | Frontend dev/tests |
| A Groq API key ([console.groq.com/keys](https://console.groq.com/keys)) | — | The chatbot |

`make` targets are shortcuts. On Windows without `make`, run the command shown next to each one.

---

## Environments

There are two environments. Each has its own env file (gitignored, holds secrets) and a committed template:

| | **Development / testing** | **Production** |
|---|---|---|
| Env file | `.env.development` | `.env.production` |
| Template | `.env.example` | `.env.production.example` |
| Compose files | `docker-compose.yml` | `docker-compose.yml` + `docker-compose.prod.yml` |
| Compose project | `erp-development` | `erp-production` |
| Database | Aiven `defaultdb` (or local Docker Postgres) | Aiven `erp_prod`, same server, separate database |
| AI DB login | `erp_ai_ro_dev` | `erp_ai_ro_prod` |
| `ASPNETCORE_ENVIRONMENT` / `APP_ENV` | `Development` / `development` | `Production` / `production` |
| Demo data (`admin@demo.com`) | seeded | **never** (the API refuses to start with it) |
| Swagger `/swagger`, `/ai/docs` | on | off |
| Logging | SQL commands logged | warnings + startup only |
| Login rate limit | 100/min | 10/min |
| Restart policy, log rotation, CPU/mem limits | – | yes (`docker-compose.prod.yml`) |
| Replicas / DB pool per pod | 1 api × 3, 1 ai × 2 | 2 api × 3, 2 ai × 2 |
| JWT secret, AI password | dev values | different values (dev tokens don't work in prod) |

**Production guard rails.** In production the API refuses to start if `JWT_SECRET` is the placeholder,
demo seeding is enabled, or `Database:MigrateOnStartup` is on. The AI service refuses to start if
`JWT_SECRET` is the placeholder, `GROQ_API_KEY` is empty, or it uses the dev AI password.

**Shared server, isolated databases.** Postgres roles are server-wide, so each environment gets its own
AI login role. The migrator also revokes `CONNECT` from `PUBLIC` on the environment's database, which
means `erp_ai_ro_dev` cannot even connect to `erp_prod`, and vice versa. Both environments share the
Aiven plan's 20-connection limit (3 reserved), which is why the pools are small. Give production its
own Aiven service when you outgrow this.

**Specific `appsettings` per environment.** `backend/src/Erp.Api/appsettings.Development.json` and
`appsettings.Production.json` layer on top of `appsettings.json`, selected by `ASPNETCORE_ENVIRONMENT`.

## Quick start

### 1. Configure

```bash
cp .env.example .env.development                    # development / testing
cp .env.production.example .env.production          # production (fill in EVERY value)
```

For development, pick a database:

- **Local Postgres in Docker**: keep the template defaults (`COMPOSE_PROFILES=localdb`, `AI_DB_USER=erp_ai_ro`).
- **Managed Postgres (Aiven)**: set `COMPOSE_PROFILES=` (empty), the `POSTGRES_*` values from the
  Aiven console, `POSTGRES_SSLMODE=require`, and a per-environment `AI_DB_USER` (e.g. `erp_ai_ro_dev`).

For a new production database on the same Aiven server, create it once:

```bash
psql "host=<host> port=<port> dbname=defaultdb user=avnadmin sslmode=require" -c "CREATE DATABASE erp_prod;"
```

Generate secrets with `python -c "import secrets;print(secrets.token_urlsafe(48))"`.

### 2. Run

```bash
make up                       # development  = docker compose --env-file .env.development up -d --build
make up ENV=production        # production   = docker compose --env-file .env.production \
                              #                  -f docker-compose.yml -f docker-compose.prod.yml up -d --build
make migrate [ENV=production] # apply migrations only
make logs    [ENV=production]
make down    [ENV=production] # stops containers, keeps volumes
```

On Windows without `make`, run the `docker compose ...` command shown in the comments.

Startup order: `migrator` applies EF Core migrations (and seeds demo data when `SEED_DEMO_DATA=true`),
then exits. After it finishes, `api` and `ai-service` start.

In development, open **http://localhost** and log in with `admin@demo.com` / `Admin@123`.
In production there are no users yet. Register the first organization through the app
(`POST /api/auth/register`); its first user becomes the organization's Admin.

Try: *"What is my revenue by city?"*, *"Top 5 products by quantity sold"*, *"Which products are low on stock?"*

- Swagger (development only): `/swagger` on the API · AI service docs: `/ai/docs`
- Health: `/health/live`, `/health/ready` (checks Postgres + Redis)

---

## Database: code-first with EF Core

### Where the schema lives

| What | File |
|---|---|
| Entities (tables) | [backend/src/Erp.Api/Domain/Entities.cs](backend/src/Erp.Api/Domain/Entities.cs) |
| Mapping: keys, indexes, FKs, check constraints, defaults | [backend/src/Erp.Api/Data/Configurations/EntityConfigurations.cs](backend/src/Erp.Api/Data/Configurations/EntityConfigurations.cs) |
| DbContext + multi-tenant query filter | [backend/src/Erp.Api/Data/ErpDbContext.cs](backend/src/Erp.Api/Data/ErpDbContext.cs) |
| Migrations (generated + reviewed, committed to git) | [backend/src/Erp.Api/Data/Migrations/](backend/src/Erp.Api/Data/Migrations/) |
| Migration runner + demo seed | [backend/src/Erp.Api/Data/DatabaseInitializer.cs](backend/src/Erp.Api/Data/DatabaseInitializer.cs) |

Names are mapped to `snake_case` automatically (`OrderItem.UnitPrice` → `order_items.unit_price`).

Current migrations:

1. `InitialCreate`: generated from the model (organizations, users, customers, products, orders,
   order_items, chat_audit_log, plus all indexes and constraints).
2. `AddRowLevelSecurityAndAiRole`: hand-written `migrationBuilder.Sql(...)` for things EF can't model:
   RLS policies, the read-only `erp_ai_ro` role, and the `log_chat()` SECURITY DEFINER function.
3. `AddRolesAndUserSessions`: the `roles` table (rows seeded from code with `HasData`), `users.role_id`
   (replacing the old `users.role` text column), `users.last_login_at`, the `user_sessions` table,
   and the `session_is_active()` function for the AI service. The generated migration would have
   dropped `users.role` before copying it, so it was edited by hand. It now copies each user's role
   into `role_id` first (`UPDATE users ... FROM roles`), and `Down()` restores the text column.
   This is the pattern to follow whenever a migration says *"may result in the loss of data"*.
4. `AddOrgUsersViewForAi`: the `org_users` view, so the chatbot can answer questions about login
   users ("how many users?", "who are the admins?"). It has no password hashes, is filtered to the current
   organization, and is a `security_barrier` view. The AI still has no access to the `users` table.
5. `AddMasterPagesAndRights`: the `pages` master, the SuperAdmin role with role `level`s,
   `role_page_permissions` (system defaults seeded from code, NULLs-not-distinct unique key),
   `user_page_permissions`, and the SQL functions `effective_page_permissions()` and `ai_user_access()`.
   A hand-written data step makes each existing organization's earliest Admin its SuperAdmin.
6. `AddThreeLevelMenu`: the `menu_modules` and `menu_groups` tables, plus `pages.group_id` and
   `pages.is_implemented` (replacing the old `pages.module` text column). It moves existing pages into
   the tree with their ids unchanged, so granted rights are kept, and adds 16 new pages with default rights.

### Changing the schema

1. **Edit the model.** Add or change an entity in `Domain/`, its mapping in `Data/Configurations/`,
   and a `DbSet<>` in `ErpDbContext` if it's a new table. If the table holds tenant data, implement
   `ITenantOwned`; the global query filter and `organization_id` stamping then apply automatically.
2. **Generate a migration.**
   ```bash
   make migration name=AddInvoices
   # = dotnet ef migrations add AddInvoices --project backend/src/Erp.Api --output-dir Data/Migrations
   ```
3. **Review it.** Read the generated `Up()`/`Down()` and preview the SQL:
   ```bash
   dotnet ef migrations script --project backend/src/Erp.Api --idempotent
   ```
   For a new tenant table the AI should query, add the RLS policy and `GRANT SELECT ... TO erp_ai_ro`
   with `migrationBuilder.Sql(...)`. Also describe the table in `ai-service/app/schema.py` and
   allow-list it in `ai-service/app/sql_guard.py`.
4. **Apply it.**
   ```bash
   make migrate                                  # development; add ENV=production for prod
   # or, without Docker:
   ASPNETCORE_ENVIRONMENT=Development    ConnectionStrings__Postgres="Host=...;Port=...;Database=...;Username=...;Password=...;SSL Mode=Require" \
   AI_DB_PASSWORD=... \
   dotnet run --project backend/src/Erp.Api -- --migrate --seed
   ```
5. Commit the entity changes **and** the migration files together.

Undo the last migration that has **not** been applied: `dotnet ef migrations remove --project backend/src/Erp.Api`.
Roll back an applied one: `dotnet ef database update <PreviousMigrationName> --project backend/src/Erp.Api`
(uses `ConnectionStrings__Postgres` from the environment).

### How migrations run

| Context | How |
|---|---|
| docker compose | `migrator` service (`dotnet Erp.Api.dll --migrate`, seeding per `SEED_DEMO_DATA`) runs before `api` / `ai-service` |
| Kubernetes / prod | Run the API image once as a Job with `--migrate` before rolling out pods. Don't migrate from every replica. |
| Local `dotnet run` / tests | Set `Database__MigrateOnStartup=true` (and `Database__SeedDemoData=true` for demo data) |

`--seed` is idempotent. It skips seeding if the demo org already exists. It creates two tenants:
*Demo Corp* (200 customers, 100 products, 1000 orders) and *Other Tenant Ltd* (1 customer, used by
tests to prove tenant isolation).

The migration creates `erp_ai_ro` as a `NOLOGIN` role that holds the AI's grants, so no password is
ever committed. The migrator then sets up `AI_DB_USER`:

- If it is `erp_ai_ro` (a server only this environment uses), it enables login on that role.
- Otherwise (e.g. `erp_ai_ro_dev`), it creates that login role as a member of `erp_ai_ro`, sets its
  password and limits, and restricts `CONNECT` on the database to that environment.

---

## Configuration reference

| Variable | Used by | Purpose |
|---|---|---|
| `APP_ENV` | compose, ai-service | `development` / `production`. Sets the compose project name and AI service behaviour. |
| `ASPNETCORE_ENVIRONMENT` | api, migrator | `Development` / `Production`. Selects `appsettings.<Env>.json` and the guard rails. |
| `SEED_DEMO_DATA` | migrator | `true` seeds demo tenants (dev only) |
| `CORS_ORIGINS` | api, ai-service | Comma-separated allowed browser origins |
| `HTTP_PORT` | nginx | Host port for the stack |
| `API_REPLICAS` / `AI_REPLICAS` | compose | Pod counts |
| `COMPOSE_PROFILES` | compose | `localdb` starts a local Postgres container; empty for managed Postgres |
| `POSTGRES_HOST` / `PORT` / `DB` / `USER` / `PASSWORD` | api, migrator, ai-service | Database server. The API user owns the schema. |
| `POSTGRES_SSLMODE` | api, migrator, ai-service | `require` for Aiven/cloud, `disable` for local |
| `API_DB_POOL_SIZE` | api | Npgsql max pool size per pod |
| `DB_POOL_MIN` / `DB_POOL_MAX` | ai-service | asyncpg pool size per pod |
| `AI_DB_USER` / `AI_DB_PASSWORD` | ai-service, migrator | This environment's read-only AI login. The migrator creates it and sets its password. |
| `JWT_SECRET` / `JWT_ISSUER` / `JWT_AUDIENCE` | api, ai-service | Shared HS256 JWT settings (secret ≥ 32 bytes) |
| `GROQ_API_KEY` / `GROQ_MODEL` | ai-service | Groq API key and model (default `openai/gpt-oss-120b`; must support tool calling) |
| `LLM_MAX_TOKENS` / `LLM_MAX_CONCURRENCY` | ai-service | Completion token cap incl. reasoning (default 4096); concurrent LLM calls per pod (default 64) |
| `REDIS_URL` | ai-service | Rate limits + answer cache |
| `Database__MigrateOnStartup` / `Database__SeedDemoData` | api | Migrate/seed when the API boots (dev/tests only) |
| `Jwt__AccessTokenMinutes` | api | Access-token lifetime (default 15) |
| `Auth__SessionDays` | api | Login session lifetime (default 14) |
| `Auth__ActiveCheckCacheSeconds` | api | How long a session-active check is cached (default 30) |
| `Auth__RetainEndedSessionsDays` | api | Keep ended sessions this long before cleanup (default 30) |
| `SESSION_CHECK_TTL_SECONDS` | ai-service | How long the AI caches a session-active check (default 30) |

---

## Authentication: login sessions and roles

### Roles (stored in the database)

| Table | Contents |
|---|---|
| `roles` | `id`, `name` (unique), `description`. Seeded by migration: `1 = Admin`, `2 = User`. |
| `users.role_id` | FK → `roles.id` (one role per user) |

- Seed rows are defined in code (`RoleConfiguration.HasData`). To add a role, add a row there (plus a
  constant in `Domain/Roles` if code checks it) and run `make migration name=AddXRole`.
- The role name goes into the JWT `role` claim and is enforced with `[Authorize(Roles = Roles.Admin)]`.
- It is re-read from the database at every login and every token refresh.

### Sessions

Every sign-in creates a row in **`user_sessions`** (one per device):

| Column | |
|---|---|
| `refresh_token_hash` | SHA-256 of the refresh token. The token itself is never stored. |
| `previous_refresh_token_hash` | The token before the last rotation, used to detect stolen-token reuse |
| `created_at`, `last_seen_at`, `expires_at` | Absolute lifetime `Auth:SessionDays` (default 14 days) |
| `revoked_at`, `revoked_reason` | `logout`, `logout_others`, `revoked_by_user`, `role_changed`, `refresh_token_reuse` |
| `ip_address`, `user_agent` | Shown in the "your devices" list |

```
login ──▶ user_sessions row ──▶ Set-Cookie: erp_refresh=<random>  (HttpOnly, SameSite=Strict, Path=/api/auth, Secure on HTTPS)
      └─▶ body: { accessToken (JWT, 15 min, contains "sid"), user, sessionId, sessionExpiresAt }

every API call:  JWT signature + expiry  AND  session "sid" still active (cached 30 s in Redis)
AI service:      same JWT check          AND  session_is_active(sid, user) (cached 30 s in Redis)
access token expires ──▶ frontend gets 401 ──▶ POST /api/auth/refresh (cookie) ──▶ new JWT + rotated cookie ──▶ retry
logout / revoke ──▶ revoked_at set ──▶ every token of that session is rejected immediately
```

Properties:

- **The refresh token can't be stolen by XSS.** It lives only in an HttpOnly cookie that is sent only
  to `/api/auth/*`. The access token in `localStorage` is short-lived and dies when its session is revoked.
- **Rotation with reuse detection.** Every refresh issues a new refresh token. If a rotated-out token is
  replayed more than 30 s later, it is treated as stolen and the whole session is revoked. Concurrent
  refreshes are race-safe: a conditional `UPDATE ... WHERE refresh_token_hash = @old` means only one wins.
- **Role changes take effect immediately.** Changing a user's role signs that user out of every session.
- **Same cost for unknown emails.** Login takes the same time whether or not the email exists, so it
  doesn't reveal which emails are registered.
- **Cleanup.** A background job deletes sessions that ended more than `Auth:RetainEndedSessionsDays` ago.
- **Database access.** The AI role can't read `user_sessions`. It can only call `session_is_active()`,
  which answers yes or no for one session id.

### Endpoints

| Method & path | Auth | Purpose |
|---|---|---|
| `POST /api/auth/register` | – | New organization + its first user (Admin); starts a session |
| `POST /api/auth/login` | – | Starts a session (rate limited per IP) |
| `POST /api/auth/refresh` | refresh cookie | New access token; rotates the cookie |
| `POST /api/auth/logout` | token or cookie | Revokes the current session, clears the cookie |
| `GET /api/auth/me` | token | Current user from the token |
| `GET /api/auth/sessions` | token | Your active sessions (`current: true` marks this one) |
| `DELETE /api/auth/sessions/{id}` | token | Sign out one of your devices |
| `POST /api/auth/logout-others` | token | Sign out everywhere except here |
| `GET /api/roles` | token | Roles from the database |
| `GET /api/users` | Admin | Users in your organization |
| `POST /api/users` | Admin | Add a user with a role |
| `PUT /api/users/{id}/role` | Admin | Change a user's role (signs them out everywhere; you can't demote yourself) |

Frontend (`frontend/src/api/client.ts`): on a 401 it calls `/api/auth/refresh` once, with one refresh
shared by concurrent requests, then retries. If the refresh fails, it returns to the login page.
"Sign out" calls `/api/auth/logout`.

> **Use HTTPS in production.** Browsers only send `Secure` cookies over HTTPS. The API sets `Secure`
> when the request arrives over HTTPS, as reported by `X-Forwarded-Proto` from nginx or your load balancer.

## Master module: roles, pages and rights

### Hierarchy (per organization)

| Role | Level | Can manage | Can assign roles |
|---|---|---|---|
| **SuperAdmin** | 100 | Admins and Users, role defaults of Admin and User | Admin, User |
| **Admin** | 50 | Users | User |
| **User** | 10 | nobody | – |

- The person who **registers an organization becomes its SuperAdmin**. The migration promoted each
  existing organization's earliest Admin.
- You can only create, edit, delete, or change the rights of users **below** your own level, never your own account.
- **No escalation:** an Admin can only grant rights they hold themselves. Example: an Admin without
  Role Rights access can't give it to a User.
- Changing someone's role signs them out everywhere. Rights changes apply immediately, with no re-login.

### Side menu: Module > Group > Page

The side menu has three levels, stored in `menu_modules` > `menu_groups` > `pages`. All three are
defined in code (`Data/Configurations/DefaultPermissions.cs`) and seeded by migration:

```
Dashboard, AI Assistant                        (top level)
Master          Users > Users, User Rights, Role Rights · Customers > Customers · Vendors > Vendors
                Products > Products · Settings > Pages
Inventory       Stock Position > Stock Position · Stock Addition > Stock Addition
Sales           Orders > Sales Orders · Invoices > Sales Invoices
Purchase        Orders > Purchase Orders · Bills > Purchase Bills
Production      Planning > Production Plan · Work Orders > Work Orders
Cash Flow       Receipts > Receipts · Payments > Payments
Inspection      Incoming > Incoming Inspection · Quality > Quality Checks
Human Resource  Employees > Employees · Attendance > Attendance
Reports         Sales > Sales Report · Inventory > Stock Report
```

- The menu is built per user from their rights. Pages they can't see are hidden, and so are groups and
  modules left with no pages.
- Pages with `is_implemented = false` show a **Coming soon** screen. They are already in the rights
  matrix, so access can be granted before the screen exists.
- **Responsive:** on screens 900px wide or less, the sidebar becomes a slide-in drawer behind the ☰
  (hamburger) button. It closes on navigation, on a backdrop tap, or with Escape. Touch targets are ≥ 44px.

**To add a page:**
1. Add a `PageKeys` constant.
2. Add a `P(...)` row with a **new id** (never reuse or renumber ids) in the right group, and add its
   default rights in `Matrix`. Add a `G(...)`/module row first if the group or module is new.
3. Run `make migration name=AddXyzPage` and apply it. The page appears in the menu (as Coming soon)
   and in the rights grids.
4. When the screen is built, set `implemented: true` and add its `<Route>` in `frontend/src/App.tsx`.
   Protect its API with `[RequirePage(PageKeys.Xyz, PageAction.…)]`.

### Pages master and the six rights

Every screen is a row in **`pages`** (see the menu above); rights are granted per page.

| Right | Meaning |
|---|---|
| `isVisible` | Shows in the menu |
| `canRead` | Open the page / `GET` its API |
| `canWrite` | Create (`POST`) |
| `canUpdate` | Edit (`PUT`/`PATCH`) |
| `canDelete` | Delete (`DELETE`) |
| `isRestricted` | **Hard block**: page hidden and every API call returns 403, whatever the other rights say. Turn it off to restore them. |

### Where rights come from (first match wins)

1. **User override** (`user_page_permissions`): set in Master > User Rights.
2. **Organization role default** (`role_page_permissions` with `organization_id`): set in Master > Role Rights.
3. **System role default** (`role_page_permissions` with `organization_id IS NULL`): seeded from code.
4. Otherwise no rights. **SuperAdmin always has every right.**

One SQL function, `effective_page_permissions(user)`, implements these rules. Both the API and the
AI service use it, so they can't disagree.

### Enforcement

- **API:** `[RequirePage(PageKeys.Products, PageAction.Write)]` on each endpoint (dynamic policy + handler).
  Rights are cached per user and invalidated for the whole organization on any change.
- **AI chatbot:** needs `canRead` on *AI Assistant*. It can only query tables for pages the user can read
  (`customers` → Customers, `orders`/`order_items` → Orders, `products` → Products, `org_users` → Users).
  The answer cache is keyed by those rights, so users with different rights never share an answer.
- **UI:** the menu, route guards and buttons follow `GET /api/me/permissions`. This is convenience only;
  the API is the real check.

### Endpoints

| Method & path | Page right needed | |
|---|---|---|
| `GET /api/me/permissions` | – | Your effective rights (menu) |
| `GET /api/roles`, `GET /api/roles/assignable` | – | All roles / roles you may assign |
| `GET /api/users` · `POST` · `PUT /{id}/role` · `DELETE /{id}` | master-users read · write · update · delete | Plus hierarchy rules |
| `GET /api/master/pages` | master-pages read | Pages master |
| `GET`/`PUT /api/master/users/{id}/rights` | master-user-rights read / update | Body: `{ items: [{ pageId, useDefault, rights }] }` |
| `GET`/`PUT /api/master/roles/{roleId}/rights` | master-role-rights read / update | Org defaults for a role |

Default matrix (system): **Admin** has full rights on the business pages, Users (all), User Rights
(read/update) and Pages (read), with no Role Rights. **User** has read on Dashboard, Products and
AI Assistant, read/write/update on Customers and Orders, and nothing in Master. Change it in
`DefaultPermissions.cs` plus a migration, or per organization in Master > Role Rights.

Demo logins (fresh database): `admin@demo.com` / `Admin@123` (SuperAdmin), `manager@demo.com` /
`Manager@123` (Admin), `staff@demo.com` / `Staff@1234` (User).

## How the AI chatbot works

1. React posts `{question, history}` to `/ai/chat` with the user's JWT.
2. FastAPI validates the JWT (same HS256 secret as ASP.NET Core) → `org_id`, `user_id`, `sid`, and
   checks the login session is still active (`session_is_active()`, cached in Redis).
3. Per-user rate limit (Redis) → answer cache lookup (Redis, keyed by org + normalised question).
4. **LLM agent loop** (`ai-service/app/agent.py`): the model (on Groq) gets the annotated schema and one tool,
   `run_sql`. It writes SQL, sees results or errors, can retry and fix, then writes a plain-English answer.
5. Answer + SQL + result table go back to the UI. The call is audited in `chat_audit_log`.

The static system prompt (with the schema) is always the first message, so Groq's automatic prompt
caching can reuse it on models that support it. If the model emits a malformed tool call (Groq error
`tool_use_failed`), the agent retries that step. A Groq 429 becomes HTTP 503 "AI is busy".

To switch models, set `GROQ_MODEL` to any Groq model with tool-calling support
(e.g. `llama-3.3-70b-versatile`) and restart the AI service.

### Security: why the AI can't damage or leak data

There are three independent layers, and any one of them stops a bad query:

1. **Database role `erp_ai_ro`**: `SELECT` on 4 business tables plus the `org_users` view only, read-only transactions by default,
   5 s `statement_timeout`, no access to `users`, password hashes or the audit log.
2. **Row-Level Security**: every query runs in a transaction with `app.org_id` set from the JWT.
   Postgres itself filters rows, so even `WHERE organization_id = '<other tenant>'` returns nothing.
3. **SQL guard** (`ai-service/app/sql_guard.py`, sqlglot AST): single `SELECT` only, allow-listed tables,
   blocked functions (`pg_sleep`, `pg_read_file`, `set_config`, …), comments stripped, hard `LIMIT`.

Layers 1 and 2 are created by the `AddRowLevelSecurityAndAiRole` migration. Only the first 50 rows
go to the LLM. The UI gets up to 500.

The API's own DB user owns the tables, so it bypasses RLS. It isolates tenants with EF Core global
query filters instead, and stamps `organization_id` on every insert in `SaveChangesAsync`.

---

## Scaling

The design is **stateless and horizontally scalable**:

- **Stateless services**: JWT auth with no server sessions, so any pod can serve any request. The k8s HPA
  scales the API from 4 to 200 pods and the AI service from 2 to 100 pods (`infra/k8s/platform.yaml`).
- **Connection pooling**: keep total pool size below Postgres `max_connections`. At scale, put a pooler
  in front: Aiven's built-in PgBouncer pools, or `infra/pgbouncer` when self-hosting. Npgsql and asyncpg
  are already configured for transaction pooling (no server-side prepared statements).
- **Caching**: Redis cache-aside for product lists (tenant-versioned keys), dashboard stats (30 s),
  and AI answers (120 s).
- **Efficient queries**: composite `(organization_id, …)` indexes, keyset pagination for orders,
  `AsNoTracking`, and atomic `UPDATE … WHERE stock >= qty` (no oversell under concurrency).
- **Back-pressure**: nginx per-IP limits, an ASP.NET Core token bucket per user, a Redis per-user AI
  limit, and a per-pod cap on concurrent LLM calls.

For production at that scale you would also want read replicas (point the AI service at one), Redis
Cluster, a CDN for the SPA, raised Groq rate limits (or a paid tier), and OpenTelemetry for observability.

---

## Testing

| Phase | What | Command |
|---|---|---|
| 1. Static | ruff, `tsc`, `dotnet build` | CI job `static` |
| 2. Unit | xUnit · pytest (mocked LLM) · Vitest | `make test` |
| 3. Integration | .NET + Testcontainers (real Postgres built **from the EF migrations**) · pytest vs real Postgres (RLS, read-only role, audit) | `make test-integration` |
| 4. E2E | Playwright against the docker-compose stack | `make e2e` |
| 5. Load | k6 smoke/load/stress/spike | `make load` |

Run the AI-service DB security tests against any migrated database (writes one row to `chat_audit_log`):

```bash
cd ai-service
TEST_AI_DATABASE_URL="postgresql://erp_ai_ro:<AI_DB_PASSWORD>@<host>:<port>/<db>?sslmode=require" pytest -m integration
```

CI pipeline: `.github/workflows/ci.yml` (each phase gates the next).

---

## Project layout

```
backend/
  src/Erp.Api/
    Domain/              entities (the code-first model)
    Data/
      Configurations/    EF Fluent API mapping (indexes, FKs, constraints)
      Migrations/        EF Core migrations (source of truth for the schema)
      DatabaseInitializer.cs   --migrate / --seed
    Controllers/ Services/ Infrastructure/ Contracts/
  tests/                 xUnit unit + Testcontainers integration tests
ai-service/              FastAPI + Groq LLM agent + pytest
docker-compose.yml       base stack (all environments)
docker-compose.prod.yml  production overrides (restart, log rotation, resource limits)
.env.example             development template  ->  .env.development   (gitignored)
.env.production.example  production template   ->  .env.production    (gitignored)
frontend/                React + Vite + Vitest + Playwright
infra/                   nginx, pgbouncer, k8s manifests
load-tests/k6/           api.js, chat.js
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| `remaining connection slots are reserved` / `too many clients` | Lower `API_DB_POOL_SIZE` / `DB_POOL_MAX`, or use an Aiven connection pool |
| AI service: `password authentication failed for user "erp_ai_ro"` | `AI_DB_PASSWORD` was not set when the migrator ran. Set it and rerun `make migrate`. |
| `api` never starts in compose | `make logs` / `docker compose --env-file .env.<env> logs migrator`. The migration failed (check DB host and SSL mode). |
| Signed out every ~15 minutes in production | The refresh cookie isn't sent: serve the site over HTTPS (the cookie is `Secure` on HTTPS) and keep frontend and API on the same origin |
| `401 Session is no longer active` | The session was logged out, revoked (role change, "sign out other devices") or expired. Sign in again. |
| API exits with `... not allowed in Production` | A production guard rail tripped. Fix `.env.production` as the message says. |
| `502 Bad Gateway` right after `docker compose up --build` | nginx still points at the old containers: `docker compose --env-file .env.development restart nginx` |
| A user gets 403 on a page they should see | Check Master > User Rights for that user: look at `Source`, and remember a `Restricted` box blocks everything |
| `permission denied for database` from the AI service | `AI_DB_USER` belongs to a different environment's database. Use that environment's AI user. |
| SSL errors against Aiven | `POSTGRES_SSLMODE=require` |
| `JWT secret must be at least 32 bytes` | Use a longer `JWT_SECRET` |
