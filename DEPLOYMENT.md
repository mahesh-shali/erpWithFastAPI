# Deploying to production on Render's free tier

```
Browser ── https://erpwithfastapi.onrender.com ── Render static site (React, free)
                     ├─ /api/*  ──proxy──▶  erpwithfastapi-api  (ASP.NET Core, Docker, free, Singapore)
                     └─ /ai/*   ──proxy──▶  erpwithfastapi-ai   (FastAPI, Docker, free, Singapore)
erpwithfastapi-api ──▶ Aiven Postgres `erp_prod` (Bengaluru)
erpwithfastapi-ai  ──▶ Aiven Postgres (read-only role) · Upstash Redis · Groq LLM
GitHub Actions: CI → database migrations → deploy API + AI · keep-alive pings in working hours
```

Why it's built this way:

- **The static site proxies `/api` and `/ai`.** The browser only ever sees one site, so the HttpOnly
  login cookie (`SameSite=Strict`) works and no CORS setup is needed.
- **Migrations run in GitHub Actions before each deploy.** Render's free plan has no pre-deploy step,
  and the schema must be updated before new code starts.

You need accounts on **GitHub and Render**, plus the Aiven, Upstash and Groq accounts you already
have. Secret values go into the Render dashboard and GitHub secrets, **never into the repository**.
They are collected in your local `.env.deploy-values`, which is gitignored.

---

## 1. Before you start

- [ ] **Rotate any credentials that were shared earlier:** reset the Aiven `avnadmin` password
  (Aiven console → service → *Users*) and create a separate **production** Groq key. Update
  `.env.deploy-values` with the new values.
- [ ] **Upstash:** console → your Redis database → *Details*. Copy **Endpoint**, **Port** and
  **Password** (the Redis password, *not* the REST token) into `REDIS_URL` in `.env.deploy-values`:
  `rediss://default:<password>@<endpoint>:<port>`. Note **rediss**, with TLS.
- [ ] Fill in the four `BOOTSTRAP_*` values in the same file. The password needs at least 10 characters.

## 2. Add the GitHub secrets

Repository → *Settings → Secrets and variables → Actions → New repository secret*. Add these from
the first section of `.env.deploy-values`:

| Secret | Value |
|---|---|
| `PROD_PG_CONNECTION` | `Host=<aiven-host>;Port=<port>;Database=erp_prod;Username=avnadmin;Password=<aiven-password>;SSL Mode=Require` |
| `PROD_JWT_SECRET` | your production JWT secret |
| `PROD_AI_DB_PASSWORD` | your production AI DB password |
| `BOOTSTRAP_ORG_NAME` | your company name |
| `BOOTSTRAP_EMAIL` | your login email (becomes the **SuperAdmin**) |
| `BOOTSTRAP_PASSWORD` | a strong password |
| `BOOTSTRAP_FULL_NAME` | your name |

## 3. Prepare the production database

Push to `main`, or use *Actions → Deploy (production) → Run workflow*. The **migrate** job:

- applies all migrations to `erp_prod`;
- sets up the AI login role `erp_ai_ro_prod`;
- creates your organization and SuperAdmin.

The **deploy** job warns that the Render hooks aren't set yet. That's expected.

Public sign-up is **disabled in production**. After this you add users from *Master → Users*.
**Delete the four `BOOTSTRAP_*` secrets** once the run is green.

## 4. Create everything on Render

1. Go to render.com → *New → Blueprint* → connect GitHub → choose this repository. Render reads
   [`render.yaml`](render.yaml), which defines the static site and both backends.
2. Paste the values it asks for, from `.env.deploy-values`:
   - **erpwithfastapi-api:** `ConnectionStrings__Postgres`, `JWT_SECRET`
   - **erpwithfastapi-ai:** `DATABASE_URL`, `REDIS_URL`, `GROQ_API_KEY`, `JWT_SECRET` (the same value)
3. Wait for the first deploys, then check that the URLs Render shows match the rewrite destinations
   in `render.yaml`. If Render added a suffix to a name, update `render.yaml`, and set GitHub
   variables `API_ORIGIN` and `AI_ORIGIN` to the real URLs.
4. Open `https://erpwithfastapi-api.onrender.com/health/live` (should show `Healthy`) and
   `https://erpwithfastapi-ai.onrender.com/ai/health` (should show `{"status":"ok"}`).
5. On each backend, go to *Settings → Deploy Hook*, copy the URL, and add it as a GitHub secret:
   - erpwithfastapi-api → **`RENDER_DEPLOY_HOOK`**
   - erpwithfastapi-ai → **`RENDER_DEPLOY_HOOK_AI`**

From now on, a push to `main` runs CI, then migrations, then both backend deploys. Render rebuilds
the frontend once CI passes.

## 5. Check it works

- [ ] `https://erpwithfastapi.onrender.com` shows the login page.
- [ ] You can log in with the bootstrap email and password. The header shows **SuperAdmin**.
- [ ] Refreshing the page keeps you logged in, which proves the cookie works through the proxy.
- [ ] *Master → Users* lets you create an Admin, log in as them, and confirm they can only create Users.
- [ ] The AI Assistant answers "How many customers do we have?" (0 at first, since production has no demo data).
- [ ] *Sign out* works, and refreshing the page afterwards returns you to the login screen.

---

## Free-tier limits

| Service | Free allowance | What it means here |
|---|---|---|
| Render web services | 750 instance-hours per workspace per month, shared. Sleeps after 15 min idle, wakes in about a minute. | Keep-alive holds both backends awake about 11 hours a day (about 690 hours in a 31-day month). Using all the hours suspends every free service until next month, so don't add other free services or widen the window past 12 hours. |
| Render bandwidth and builds | Limits per workspace; see *Billing* in the dashboard | Without a payment method, going over bandwidth suspends free services for the month, and going over build minutes stops new builds. |
| Aiven Postgres | 20 connections (3 reserved) | Prod uses ≤ 5 (API 3 + AI 2) and dev uses ≤ 5. Leave headroom for migrations. |
| Upstash Redis | 500k commands/month, 256 MB | Only the AI service uses it: a few commands per question. |
| Groq | Daily request and token limits for `gpt-oss-120b` (see console.groq.com) | Use a smaller model or a paid tier if you reach them. |

Outside the keep-alive window, the first request wakes the services and can take about a minute.
The window is 08:30–19:30 IST. To change it, edit the cron line in
[`keepalive.yml`](.github/workflows/keepalive.yml).

When you outgrow this, the cheapest upgrade is a paid Render instance for the API, which doesn't
sleep or use free hours, and a paid Aiven plan for more connections.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `502` or a long wait on the first request | A service was asleep. Wait a minute and retry. In working hours, check the *Keep-alive* workflow runs (GitHub pauses scheduled workflows after 60 days without commits). |
| Every free service suspended | The 750 hours or the bandwidth ran out. Check *Billing*, and narrow the keep-alive window. |
| Logged out every ~15 minutes | The refresh cookie isn't sent. Open the site via `https://` and use the static site's URL, not the backend's onrender.com URL. |
| API calls return the login page's HTML | The `/api` rewrite is missing or below `/*`. Compare *Redirects/Rewrites* on the static site with `render.yaml`. |
| API won't start: "placeholder JWT secret" / "Demo seed data…" | A production guard rail tripped. Fix the environment variable it names. |
| AI service: "Unsafe production config" | `JWT_SECRET` is a placeholder, `GROQ_API_KEY` is empty, or `DATABASE_URL` uses the dev password. |
| AI service: `permission denied for database` / password failed | `DATABASE_URL` must use `erp_ai_ro_prod` and `PROD_AI_DB_PASSWORD`, and the migrate job must have run since that password was set. |
| `too many clients` / `remaining connection slots are reserved` | Aiven's 20-connection limit: lower pool sizes, stop the local dev stack, or upgrade Aiven. |
