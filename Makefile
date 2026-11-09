.PHONY: up down config logs migrate migration test test-dotnet test-python test-frontend test-integration e2e load

# ---- Environments: make <target> ENV=development (default) | ENV=production ----
ENV ?= development
ENV_FILE = .env.$(ENV)
ifeq ($(ENV),production)
COMPOSE = docker compose --env-file $(ENV_FILE) -f docker-compose.yml -f docker-compose.prod.yml
else
COMPOSE = docker compose --env-file $(ENV_FILE)
endif

$(ENV_FILE):
	@echo "Missing $(ENV_FILE). Create it from .env.example (dev) or .env.production.example (prod)." && exit 1

up: $(ENV_FILE)    ## Build and start the stack:  make up  |  make up ENV=production
	$(COMPOSE) up -d --build

down: $(ENV_FILE)  ## Stop the stack (keeps data volumes)
	$(COMPOSE) down

config: $(ENV_FILE) ## Print the resolved compose config for ENV (contains secrets!)
	$(COMPOSE) config

logs: $(ENV_FILE)
	$(COMPOSE) logs -f --tail=100

# ---- Code-first database (EF Core). Needs: dotnet tool install -g dotnet-ef ----
EF = dotnet ef --project backend/src/Erp.Api

migration:     ## Create a migration from model changes: make migration name=AddInvoices
	$(EF) migrations add $(name) --output-dir Data/Migrations

migrate: $(ENV_FILE) ## Apply migrations to ENV's DB (+ demo seed in development)
	$(COMPOSE) run --rm migrator

test: test-dotnet test-python test-frontend   ## Phase 2: all unit tests

test-dotnet:
	dotnet test backend/tests/Erp.Api.UnitTests

test-python:
	cd ai-service && pytest -m "not integration" --cov=app

test-frontend:
	cd frontend && npm test

test-integration:  ## Phase 3: needs Docker (Testcontainers) and the stack's local Postgres (localdb profile)
	dotnet test backend/tests/Erp.Api.IntegrationTests
	cd ai-service && TEST_AI_DATABASE_URL=postgresql://erp_ai_ro:ai_readonly_dev@localhost:5432/erp pytest -m integration

e2e:               ## Phase 4: needs `make up`
	cd frontend && npx playwright test

load:              ## Phase 5
	k6 run -e BASE_URL=http://localhost -e PROFILE=smoke load-tests/k6/api.js
