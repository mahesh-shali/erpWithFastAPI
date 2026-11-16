# Testing

Every screen and every piece of business logic has automated tests, and every screen has a manual
checklist. You can see all of it in one page:

```bash
node scripts/test-report.mjs --open          # or: make test-report
```

This runs the suites and writes `test-reports/index.html` (gitignored). The page has three tabs:

- **Automated tests:** every test case grouped by screen or feature, with pass/fail, duration, the
  failure message, and a **Copy** button with the command that re-runs only that test.
- **Manual checks:** step-by-step checks for each screen (login to use, URL, steps, expected
  result, and which automated test covers it). Tick them off as you go; ticks stay in your browser.
  The checks live in [scripts/manual-checks.json](scripts/manual-checks.json).
- **How to run:** the commands below.

Options: `--e2e` also runs the browser tests (start the app first), `--only frontend,ai` runs a subset,
and `--no-run` rebuilds the page from the last results.

## What is tested where

| Layer | Suite | Tests | Covers |
|---|---|---|---|
| Frontend | `frontend/src/**/*.test.tsx` (Vitest + Testing Library) | 89 | Every screen: Login, Dashboard, Analytics, AI Assistant, Customers, Products, Sales Orders, Users, User Rights, Role Rights, Pages, Coming soon. Routing, legacy URLs, menu, page guards, token refresh. |
| Backend | `backend/tests/Erp.Api.UnitTests` (xUnit) | 29 | Tokens, password hashing, refresh tokens, login lockout, order rules, analytics buckets |
| Backend | `backend/tests/Erp.Api.IntegrationTests` (xUnit + Testcontainers) | 46 | Every API endpoint against real Postgres built from the migrations: auth and sessions, customers, products, orders, dashboard, analytics, users, roles, rights, tenant isolation |
| AI service | `ai-service/tests` (pytest, LLM mocked) | 80 | Agent loop, SQL guard, chat API (auth, cache, rate limit, rights), production config checks |
| AI service | `ai-service/tests/test_db_integration.py` | 11 | Row-level security and the read-only AI role on a real database |
| End-to-end | `frontend/e2e/*.spec.ts` (Playwright) | 18 | One journey per screen in a real browser, for SuperAdmin and for the User role |

## Commands

```bash
# Everything except browser tests
make test                 # frontend + backend unit + AI unit
make test-integration     # backend API + AI database tests (Docker needed)

# One suite
cd frontend && npm test
dotnet test backend/tests/Erp.Api.UnitTests
dotnet test backend/tests/Erp.Api.IntegrationTests
cd ai-service && python -m pytest -m "not integration"

# One test (the report gives you the exact line)
cd frontend && npx vitest run src/pages/DataPages.test.tsx -t "Customers screen"
dotnet test backend/tests/Erp.Api.IntegrationTests --filter "FullyQualifiedName~BusinessApiTests.Order_lifecycle"
cd ai-service && python -m pytest "tests/test_sql_guard.py::test_allowed_tables_narrow_per_user"

# End-to-end, against a running app
./dev.sh                                                  # in another terminal
cd frontend && E2E_BASE_URL=http://localhost:5173 npx playwright test
```

## Visual runners

| Command | What you get |
|---|---|
| `cd frontend && npm run test:ui` (or `make test-ui`) | Vitest UI in the browser: test tree, re-run on save, coverage per file |
| `cd frontend && E2E_BASE_URL=http://localhost:5173 npm run test:e2e:ui` | Playwright UI: run each browser test step by step and inspect the page at every step |
| `cd frontend && npm run test:e2e:report` | HTML report of the last browser run, with traces for failures |
| `cd frontend && npm run test:coverage` | Frontend coverage in the terminal |
| `cd ai-service && python -m pytest -m "not integration" --cov=app --cov-report=html` | AI service coverage in `ai-service/htmlcov/index.html` |

On Windows, use the AI service's virtualenv Python (`ai-service/.venv/Scripts/python`) and run `.sh`
scripts with `bash ./script.sh`.

## Writing a test for a new screen

Screen tests fake the API with `renderScreen` and `mockApi` from
[frontend/src/test/utils.tsx](frontend/src/test/utils.tsx). Give the user's page rights as a
`"VRWUD"` string (visible, read, write, update, delete):

```tsx
const { fetch } = renderScreen(<CustomersPage />, [perm('customers', 'VRWUD')], [
  ['GET', '/api/customers*', { items: [...], page: 1, pageSize: 25, total: 1 }],
  ['DELETE', '/api/customers/c1', undefined],                      // undefined = 204
]);
expect(calls(fetch)).toContain('DELETE /api/customers/c1');
```

For each new screen, add:

1. A screen test next to the page: list, empty/error state, and what each right shows or hides.
2. API tests in `backend/tests/Erp.Api.IntegrationTests` for each endpoint, including 400, 403, 404,
   and another organization's data.
3. A route case in [frontend/src/App.test.tsx](frontend/src/App.test.tsx) and a journey in
   [frontend/e2e/screens.spec.ts](frontend/e2e/screens.spec.ts).
4. A manual check in [scripts/manual-checks.json](scripts/manual-checks.json).
