# Deploying to production on free cloud tiers

```
Browser ── https://<your-site>.netlify.app ── Netlify (React app, free)
                     ├─ /api/*  ──proxy──▶  Render  · erp-api  (ASP.NET Core, Docker, free, Singapore)
                     └─ /ai/*   ──proxy──▶  Koyeb   · erp-ai   (FastAPI, Docker, free, Frankfurt)
erp-api ──▶ Aiven Postgres `erp_prod` (Bengaluru)
erp-ai  ──▶ Aiven Postgres (read-only role) · Upstash Redis · Groq LLM
GitHub Actions: CI → database migrations → deploy API + AI · keep-alive ping every 10 min
```

Why it's built this way:

- **Netlify proxies `/api` and `/ai`.** The browser only ever sees one site, so the HttpOnly login
  cookie (`SameSite=Strict`) works and no CORS setup is needed.
- **The API and AI service run on two different providers.** Each provider's free tier allows one
  always-on service.
- **Migrations run in GitHub Actions before each deploy.** Render's free plan has no pre-deploy step,
  and the schema must be updated before new code starts.

Everything below is free. You need accounts on **GitHub, Netlify, Render and Koyeb**. You already
have Aiven, Upstash and Groq. Keep all secret values in a password manager. They go into the
providers' dashboards and GitHub secrets, **never into the repository**. Your local
`.env.production` holds most of them, and it is gitignored.

---

## 0. Before you start

- [ ] **Rotate the credentials that were shared or committed earlier:**
  - Aiven `avnadmin` password: Aiven console → service → *Users* → reset.
  - Revoke the old Anthropic key, which is no longer used.
  - Create a separate **production** Groq key.
- [ ] **Generate fresh production secrets.** Run this once per secret:
  ```bash
  python -c "import secrets;print(secrets.token_urlsafe(48))"   # JWT_SECRET (≥ 32 bytes)
  python -c "import secrets;print(secrets.token_urlsafe(24))"   # AI DB password
  ```
- [ ] **Choose your Netlify site name now, e.g. `acme-erp`.** It becomes `https://acme-erp.netlify.app`
  and is needed in a few settings below.
- [ ] **Upstash:** console → your Redis database → *Details*. Copy **Endpoint**, **Port** and
  **Password**. This is the Redis password, *not* the REST token. A database in an EU region is best
  (the AI runs in Frankfurt), but any region works.

## 1. Push the code to a private GitHub repository

Create an **empty private** repository on github.com (no README or .gitignore), then:

```bash
git remote add origin https://github.com/<you>/erp-platform.git
git push -u origin main
```

Then check on GitHub that **no `.env.development` or `.env.production`** file is in the repository.
Only the `*.example` templates should be there.

## 2. Prepare the production database (GitHub Actions)

Go to repository → *Settings → Secrets and variables → Actions → New repository secret*:

| Secret | Value |
|---|---|
| `PROD_PG_CONNECTION` | `Host=<aiven-host>;Port=<port>;Database=erp_prod;Username=avnadmin;Password=<aiven-password>;SSL Mode=Require` |
| `PROD_JWT_SECRET` | your production JWT secret |
| `PROD_AI_DB_PASSWORD` | your production AI DB password |
| `BOOTSTRAP_ORG_NAME` | your company name, e.g. `Acme Pvt Ltd` |
| `BOOTSTRAP_EMAIL` | your login email (becomes the **SuperAdmin**) |
| `BOOTSTRAP_PASSWORD` | a strong password (≥ 10 characters) |
| `BOOTSTRAP_FULL_NAME` | your name |

Then go to *Actions → Deploy (production) → Run workflow*. The **migrate** job:

- applies all migrations to `erp_prod`;
- sets up the AI login role `erp_ai_ro_prod`;
- creates your organization and SuperAdmin.

The **deploy** job will warn that the Render and Koyeb secrets aren't set yet. That's expected.

Public sign-up is **disabled in production** (`Auth:AllowRegistration=false`). After this you add
users from *Master → Users*. **Delete the four `BOOTSTRAP_*` secrets** once the run is green.

## 3. API on Render

1. Go to render.com → *New → Blueprint* → connect GitHub → choose the repository. Render reads
   [`render.yaml`](render.yaml), which defines a free Docker web service `erp-api` in **Singapore**
   with health check `/health/live`.
2. When asked for the `sync: false` values:
   - `ConnectionStrings__Postgres`: same as `PROD_PG_CONNECTION`, **plus** `;Maximum Pool Size=3`
   - `JWT_SECRET`: your production JWT secret
   - `Cors__Origins__0`: `https://<your-site>.netlify.app`
3. Wait for the first deploy, then open `https://erp-api.onrender.com/health/live`. It should show
   `Healthy`. Your URL may differ; it's shown at the top of the service page.
4. Go to *Settings → Deploy Hook*, copy the URL, and add it as GitHub secret **`RENDER_DEPLOY_HOOK`**.

## 4. AI service on Koyeb

On koyeb.com, go to *Create service → GitHub* and select the repository, with branch `main`.

| Setting | Value |
|---|---|
| Builder | **Dockerfile** |
| Work directory | `ai-service` |
| Dockerfile location | `Dockerfile` |
| Instance | **Free** · Region **Frankfurt** |
| Port | `8000`, HTTP, public path `/` |
| Health check | HTTP, path `/ai/health` |
| Autodeploy | **off** (GitHub Actions redeploys it after migrations) |
| Service name | `erp-ai` |

Environment variables (use Koyeb *secrets* for the sensitive ones):

| Variable | Value |
|---|---|
| `APP_ENV` | `production` |
| `DATABASE_URL` | `postgresql://erp_ai_ro_prod:<ai-db-password>@<aiven-host>:<port>/erp_prod?sslmode=require` |
| `DB_POOL_MIN` / `DB_POOL_MAX` | `1` / `2` |
| `WEB_CONCURRENCY` | `1` |
| `REDIS_URL` | `rediss://default:<upstash-password>@<upstash-endpoint>:<port>` (note **rediss**, with TLS) |
| `GROQ_API_KEY` | your production Groq key |
| `GROQ_MODEL` | `openai/gpt-oss-120b` |
| `JWT_SECRET` | **same** value as on Render |
| `JWT_ISSUER` / `JWT_AUDIENCE` | `erp-api` / `erp-clients` |
| `CORS_ORIGINS` | `https://<your-site>.netlify.app` |

After deploying, open `https://<koyeb-url>/ai/health`. It should show `{"status":"ok"}`.
To redeploy it from GitHub Actions, add these GitHub secrets:

- **`KOYEB_TOKEN`**: Koyeb → *Settings → API* → create token.
- **`KOYEB_SERVICE_ID`**: the service id from its URL in the Koyeb console.

The AI service refuses to start in production if `JWT_SECRET` is a placeholder, `GROQ_API_KEY` is
empty, or the database URL uses the dev password. The logs say which one.

## 5. Frontend on Netlify

1. Go to app.netlify.com → *Add new site → Import from Git* → choose the repository. Netlify reads
   [`netlify.toml`](netlify.toml): base `frontend`, publish `dist`, Node 22.
2. Before the first build, set these under *Site configuration → Environment variables*:
   - `API_ORIGIN` = your Render URL, e.g. `https://erp-api.onrender.com` (no trailing slash)
   - `AI_ORIGIN` = your Koyeb URL, e.g. `https://erp-ai-<org>.koyeb.app`

   The build fails with a clear message if either is missing.
3. Go to *Site configuration → Site details → Change site name* and set it to the name you chose
   in step 0.
4. Deploy. Netlify builds again automatically on every push to `main`.

## 6. Keep-alive and the deploy pipeline

Under GitHub → *Settings → Secrets and variables → Actions → **Variables***, add:

- `API_ORIGIN` = Render URL
- `AI_ORIGIN` = Koyeb URL

The *Keep-alive* workflow then pings both every 10 minutes. Without it, the first request after
idling takes 30–60 s to wake the service, which is longer than Netlify's 26 s proxy limit, so it
would fail.

From now on, a push to `main` runs:

1. **CI**: tests.
2. **Deploy (production)**: migrations, then the Render API deploy, then the Koyeb AI redeploy.
3. **Netlify**: rebuilds the frontend.

## 7. Check it works

- [ ] `https://<your-site>.netlify.app` shows the login page.
- [ ] You can log in with the bootstrap email and password. The header shows **SuperAdmin**.
- [ ] The side menu shows all modules. On a phone, the ☰ button opens it.
- [ ] *Master → Users* lets you create an Admin, log in as them, and confirm they can only create Users.
- [ ] The AI Assistant answers "How many customers do we have?" (0 at first, since production has no demo data).
- [ ] *Sign out* works, and refreshing the page afterwards returns you to the login screen.

---

## Free-tier limits

| Service | Free allowance | What it means here |
|---|---|---|
| Render | 512 MB, 0.1 CPU, 750 instance-hours/month, sleeps after 15 min idle | One always-on API fits (~730 h). Slow-ish CPU, fine for a small team. |
| Koyeb | 1 service, 512 MB, 0.1 vCPU, Frankfurt/Washington, sleeps after 1 h idle | The AI service. Keep-alive prevents sleep. |
| Netlify | 300 credits/month (builds, bandwidth, requests) | API calls go through the proxy and use credits. Watch *Usage* in Netlify. |
| Aiven Postgres | 20 connections (3 reserved) | Prod uses ≤ 5 (API 3 + AI 2) and dev uses ≤ 5. Leave headroom for migrations. |
| Upstash Redis | 500k commands/month, 256 MB | Only the AI service uses it (rate limit, answer cache, session check): a few commands per question. |
| Groq | about 1,000 requests and 200k tokens per day for `gpt-oss-120b` (check console.groq.com) | Roughly a few hundred AI questions a day. Use a smaller model or a paid tier for more. |

When you outgrow this, the cheapest upgrade is Render's paid instance ($7/month: no sleep, more CPU)
and a paid Aiven plan, which allows more connections.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `502` on login right after a quiet period | A service was asleep longer than 26 s. Check the *Keep-alive* workflow runs (GitHub pauses scheduled workflows after 60 days without commits). |
| Logged out every ~15 minutes | The refresh cookie isn't sent. Open the site via `https://` only, and make sure `/api` is proxied by Netlify (not called directly on onrender.com). |
| API won't start: "placeholder JWT secret" / "Demo seed data…" | A production guard rail tripped. Fix the Render environment variable it names. |
| AI service: `permission denied for database` / password failed | `DATABASE_URL` must use `erp_ai_ro_prod` and `PROD_AI_DB_PASSWORD`, and the migrate job must have run since that password was set. |
| `too many clients` / `remaining connection slots are reserved` | Aiven's 20-connection limit: lower pool sizes, stop the local dev stack, or upgrade Aiven. |
| Netlify build fails: `API_ORIGIN must be set` | Add `API_ORIGIN` and `AI_ORIGIN` site environment variables, then *Retry deploy*. |
