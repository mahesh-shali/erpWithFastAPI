# ERP Platform — Desktop (Electron)

A Windows / macOS / Linux desktop client for the ERP Platform. It has the same screens and rights model as
the web app in [`../frontend`](../frontend) and talks to the same API ([`../backend`](../backend) and
[`../ai-service`](../ai-service)), so there is **one backend for web and desktop**.

| | |
|---|---|
| Stack | Electron 33, React 18, TypeScript, Vite, React Router (HashRouter), esbuild, electron-builder |
| Server | Any ERP edge URL (nginx / k8s Ingress serving `/api/*` and `/ai/*`). Default `http://localhost:8088` (`./local.sh up`) |
| Tests | Vitest (renderer screens + main-process auth client) and Playwright driving the real Electron app |

## Features

Everything the web app has, driven by the user's page rights (`GET /api/me/permissions`):

- **Sign in / sign out**, silent token refresh, session restore after restarting the app
- **Reopens where you left off**: the last page (per user) and the window size, position and maximised state,
  also after an update restart
- **Dashboard** (key figures + sales analytics) and **Analytics** (30 / 90 days, 12 months; trend, statuses,
  top products, customers, cities, categories)
- **AI Assistant**: plain-English questions answered from your data (`/ai/chat`)
- **Messages**: end-to-end encrypted chat between organization members, and read-only announcements from the
  SuperAdmin. Same identity and history as the web app (unlock with the same passphrase). The live event stream
  runs in the main process (`electron/main/chat-stream.ts`); the renderer only receives validated notices
  (`window.erp.chat`), so it still has no network access and never sees a token. Encryption happens in the renderer
  (`src/chat/crypto.ts`, identical to the web app's).
- **Master**: Customers, Products, Users (create, change role, delete), User rights, Role rights, Pages
- **Sales**: Orders
- **3-level menu** (Module › Group › Page) for every module in the Pages master: Sales & Labour Work,
  Purchase & Subcontract, Planning, Production, HRMS, Accounts, Inspection (QC), Maintenance, Reports.
  Pages without a screen yet show "Coming soon", exactly as on the web.
- **Desktop only: Devices** (top bar). Lists every signed-in session of the user, with
  "Sign out" per device and "Sign out all other devices" (`/api/auth/sessions`, `/api/auth/logout-others`).
- **Mode badge** on the sign-in screen (Production / Development); the server URL is not shown

## How authentication works

```
 Renderer (React)              Preload                  Main process                      ERP API
 no Node, no network   ──▶  window.erp (5 calls) ──IPC──▶ ApiClient ─────────── HTTPS ──▶ /api/auth/login
 CSP connect-src 'none'                                  · access token: memory only        /api/auth/refresh
 never sees a token                                      · refresh token: OS-encrypted      /api/* , /ai/*
                                                           (DPAPI / Keychain / libsecret)
```

1. **Login** – the main process posts to `/api/auth/login`. The API returns a 15-minute JWT and sets the
   refresh token as an HttpOnly cookie. The main process keeps the JWT in memory and stores the refresh
   token with Electron `safeStorage` (`userData/session.bin`, file mode 600). The renderer gets only the user.
2. **Every call** – the renderer calls `window.erp.api.request(method, path, body)`. The main process only
   accepts relative `/api/…` and `/ai/…` paths and standard methods, adds `Authorization: Bearer …` and sends it.
3. **401** – one shared refresh (`/api/auth/refresh` with the stored cookie), then one retry. The API rotates
   the refresh token on every refresh and **revokes the session if an old token is replayed**.
4. **Restart** – the stored refresh token resumes the session (no password). If the OS has no real key store
   (e.g. Linux without a keyring), nothing is written to disk and the user signs in again each launch.
5. **Revocation** – a session signed out from another device, or expired, sends the app back to sign-in.
6. **Logout** – revokes the session on the server and deletes the local token.

The API needed **no changes** for the desktop app: it already reads the refresh token from a cookie, and the
desktop sends it as a `Cookie` header from the main process. The API records `ERP-Desktop/<version> (<os>)`
as the session's user agent, which the Devices screen shows.

### Electron hardening

- `contextIsolation`, `sandbox`, `nodeIntegration: false`, `webSecurity` on every window
- Strict CSP in production builds: `default-src 'none'; script-src 'self'; connect-src 'none'; …`
- IPC handlers reject calls from any frame that is not the app's own top-level page
- Navigation, redirects, popups and `<webview>` blocked; `https://` links open in the system browser
- All permission requests (camera, mic, notifications…) denied
- Single instance lock, so two processes never race to rotate the same refresh token
- Server URL must be `https://` (plain `http://` only for localhost, or with `ERP_ALLOW_INSECURE_HTTP=1`)
- DevTools only in development builds (disabled in the window itself, not just hidden from the menu)

### Protection against tampering and reverse engineering

The app holds **no secrets**: every permission, tenant and data check happens on the API, and the session
token is encrypted with the OS key store. Reading the app's code therefore gives nobody extra access. On top
of that the installed app is made hard to inspect or modify:

- Release bundles are minified and ship **no source maps** (`scripts/build-electron.mjs`, `"!**/*.map"`)
- Electron fuses (`build.electronFuses` in `package.json`): `ELECTRON_RUN_AS_NODE`, `NODE_OPTIONS` and
  `--inspect` are ignored; code loads **only** from `app.asar`, whose hash is embedded in the executable and
  checked at start, so an edited or repacked app refuses to run; the cookie store is encrypted
- The installed app exits if started with debugging or security-bypass switches such as
  `--remote-debugging-port`, `--ignore-certificate-errors` or `--no-sandbox` (`forbiddenSwitch` in `security.ts`)
- Not yet done: **code signing**. Without it someone can still patch the `.exe` itself; sign releases
  (`CSC_LINK`/`CSC_KEY_PASSWORD`, or Azure Trusted Signing) to close that and to avoid SmartScreen warnings

## Run it

Prerequisites: Node.js 22 and a running ERP server. `./local.sh up` from the repo root starts the whole stack
behind nginx on <http://localhost:8088>, the app's default. With `./dev.sh` the API is on :8080 and the AI
service on :8000 without nginx: set the server to `http://localhost:8080` (everything except the AI Assistant works).

```bash
cd desktop
npm install
npm run dev          # Vite (hot reload) + Electron, LOCAL mode (Docker stack on http://localhost:8088)
npm run dev:prod     # same, PRODUCTION mode (https://erpwithfastapi.onrender.com)
npm run start:prod   # run the built app (npm run build) in production mode
npm test             # Vitest: renderer screens + main process (116 tests)
npm run test:e2e     # builds, then drives the real Electron app with Playwright against e2e/mock-server.ts
                     # (Linux without a display: xvfb-run -a npx playwright test)
npm run dist         # installer for this OS in release/ (dist:win | dist:mac | dist:linux)
```

### Modes: local and production

| Mode | Server | Profile (settings + saved session) | Used by |
|---|---|---|---|
| `local` | `http://localhost:8088`: nginx of the Docker stack (`./local.sh up` in erpWithFastAPI) | `<userData> (Local)` | `npm run dev`, `npm start` |
| `production` | `https://erpwithfastapi.onrender.com`: Render static site that rewrites `/api/*` and `/ai/*` to the API and AI services (`render.yaml`) | `<userData>` | `npm run dev:prod`, `npm run start:prod`, installers |

Set the mode with `ERP_ENV=local|production`. Without it, development runs use `local` and installed builds use
`production`. Each mode has its own profile, so a session never leaks from one server to the other, and both modes
can be open at once. The sign-in screen shows a **Development** / **Production** badge (not the URL) and the window title ends in
"(Local)" in local mode. The server URLs are in `electron/main/environment.ts`.

### Updates

Windows builds are **Squirrel.Windows** installers. `ERP Platform Setup <version>.exe` installs per user (no admin
rights, no wizard) into `%LocalAppData%\erp-desktop\`:

```
Update.exe            Squirrel's updater (also runs the uninstall)
ERP Platform.exe      stub that always starts the newest app-<version>
app-1.4.0\            the app itself, one folder per installed version
packages\             RELEASES + downloaded .nupkg files
SquirrelSetup.log     install/update log
```

The app creates and removes its Start-menu/desktop shortcuts itself when Squirrel starts it with
`--squirrel-install`, `--squirrel-updated` or `--squirrel-uninstall` (`electron/main/squirrel.ts`). Uninstall from
Windows "Installed apps".

Installed apps update from **GitHub Releases** of `mahesh-shali/erpWithFastAPI`: Windows through Electron's built-in
Squirrel `autoUpdater` (feed `releases/latest/download`: `RELEASES` + `.nupkg`), macOS/Linux through electron-updater.
The download symbol in the top bar shows a green dot when a newer version is out; its panel checks, downloads and
offers **Restart now** (Squirrel reports no percentage, so Windows shows an indeterminate bar). The app checks 10 s
after start and every 6 hours; it only downloads when the user asks. Squirrel stages the new version in a new
`app-<version>` folder; **Restart now** (or simply the next start) runs it, reopening on the page the user was on.
Dev runs show "Updates are available in the installed app"; `release/win-unpacked` (not installed by Setup.exe)
shows "Updates are available in the app installed with Setup.exe". Logs: `<userData>/logs/updater.log` and
`%LocalAppData%\erp-desktop\SquirrelSetup.log`.

`build/icon.ico` (from `npm run icon` / `npm run icon:ico`) is the "Installed apps" icon; Squirrel downloads it from
`squirrelWindows.iconUrl` (the file on `main` on GitHub), so it must be committed and pushed.

To publish a version: bump `version` in `package.json`, commit, and push a matching tag
(`git tag v1.2.0 && git push origin v1.2.0`). `.github/workflows/desktop-release.yml` builds the Squirrel
installer and uploads `ERP Platform Setup <v>.exe`, `RELEASES` and `erp-desktop-<v>-full.nupkg` to a **draft**
release, checks all three are there, then publishes it as latest (so apps never see a half-uploaded release). Locally, `GH_TOKEN=<token> npm run release:win`
does the same. The updater reads the repo's **latest** release, so other projects in the repo should not publish
releases marked "latest". Release notes written on the GitHub release appear in the update panel (as plain text).

### Configuration

| Variable | Default | Purpose |
|---|---|---|
| `ERP_ENV` | `local` in dev, `production` when installed | Picks the mode (see above). |
| `ERP_SERVER_URL` | – | Overrides the mode's server URL (e.g. a staging server). For managed installs. |
| `ERP_ALLOW_INSECURE_HTTP` | – | `1` allows `http://` to a non-localhost server (trusted LAN test servers only). |
| `ERP_USER_DATA_DIR` | OS default | Separate profile (settings + session), e.g. for tests or a second environment. |

Without `ERP_SERVER_URL`, each mode uses its URL from `electron/main/environment.ts`.

## Folder structure

```
desktop/
├── electron/
│   ├── main/            # Electron main process
│   │   ├── index.ts         app lifecycle, window, menu
│   │   ├── api-client.ts    session owner: login, refresh rotation, 401 retry, path allow-list
│   │   ├── chat-stream.ts   Messages live events (SSE) -> validated IPC notices
│   │   ├── token-vault.ts   refresh token in OS-encrypted storage (safeStorage)
│   │   ├── settings.ts      server URL (validated, persisted)
│   │   ├── security.ts      navigation/popup/permission hardening
│   │   └── ipc.ts           renderer-facing handlers with sender checks
│   ├── preload/index.ts # contextBridge: window.erp
│   └── shared/contract.ts # IPC channel names and types shared by main, preload and renderer
├── src/                 # Renderer (React). Same screens as ../frontend/src; api/client.ts uses window.erp
│   └── pages/account/DevicesPage.tsx   desktop-only screen
├── e2e/                 # Playwright-on-Electron tests + mock API with the real API's auth semantics
├── scripts/             # build-electron.mjs (esbuild), dev.mjs
└── vite.config.mts       # renderer build, CSP injection, Vitest config
```

The renderer started as a copy of `frontend/src`. When a screen changes in the web app, copy it here;
only `api/client.ts`, `auth/AuthContext.tsx`, `auth/PrivateRoute.tsx`, `main.tsx`, `pages/LoginPage.tsx` and
`chat/events.ts` differ on purpose.

## Scaling to 1 million concurrent users

A desktop app doesn't serve anyone; each install is one client. Concurrency is a property of the **server**,
and this repo's server is built for horizontal scale:

| Layer | What makes it scale | Where |
|---|---|---|
| API | Stateless ASP.NET Core pods: JWT auth, no server session state in memory. HPA 4 → 200 pods at 60 % CPU | `backend/`, `infra/k8s/platform.yaml` |
| Sessions | Session-active check cached in Redis (~30 s), so a request doesn't hit Postgres to authenticate | `Program.cs` `OnTokenValidated` |
| Rate limits, login throttle, AI answer cache | Redis (use Redis Cluster in production) | `Services/LoginThrottle.cs`, `ai-service/app/cache.py` |
| Database | PgBouncer transaction pooling; writes on the primary, reads and the AI service on read replicas; keyset paging | `infra/pgbouncer/`, README "Scaling" |
| Edge | nginx / Ingress load balancing, gzip, rate limiting; CDN for the web SPA | `infra/nginx/` |
| AI | Separate service with its own HPA (2 → 100), read-only DB role + RLS, cached answers | `ai-service/` |

What the desktop client adds to the load, per connected user:

- **No idle cost.** No websocket or polling: an open but idle app sends nothing until the user acts.
- **One refresh per 15 minutes.** 1,000,000 users ÷ 900 s ≈ **1,100 refresh requests/s** cluster-wide, each a
  single indexed `user_sessions` lookup and update.
- **User actions** at the same rate as the web app. With 10 s think time: 1,000,000 ÷ 10 ≈ **100,000 req/s**.

Pods needed = required req/s ÷ (req/s one pod sustains at your p95 target × 0.6 headroom). Measure "req/s one
pod sustains" with the `breakpoint` profile (`./local.sh load breakpoint`), check it scales linearly with
`./local.sh scale 4`, then **prove the 1M figure with a distributed k6 run** (k6-operator or Grafana Cloud k6)
against a staging cluster built from `infra/k8s/platform.yaml`. Code alone cannot guarantee a concurrency
number; the load test on real infrastructure is what proves it.
