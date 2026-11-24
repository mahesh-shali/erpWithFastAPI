from contextlib import asynccontextmanager

import groq
from fastapi import BackgroundTasks, Depends, FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from redis.asyncio import Redis

from .agent import NL2SQLAgent
from .auth import Principal, current_principal
from .cache import AccessGuard, AnswerCache, RateLimiter
from .chat_flow import ChatError, answer
from .config import get_settings
from .db import PostgresExecutor
from .grpc_server import start_grpc_server
from .models import ChatRequest, ChatResponse

# ---------- Lifespan: one pool / client per worker ----------
@asynccontextmanager
async def lifespan(app: FastAPI):
    if getattr(app.state, "agent", None) is not None:  # injected by tests
        yield
        return
    s = get_settings()
    s.check_production()
    executor = await PostgresExecutor.create(s.database_url, s.db_pool_min, s.db_pool_max)
    redis = Redis.from_url(s.effective_redis_url, decode_responses=False, max_connections=200)
    client = groq.AsyncGroq(api_key=s.groq_api_key, max_retries=3, timeout=60)
    app.state.executor = executor
    app.state.agent = NL2SQLAgent(
        client, executor, model=s.groq_model, max_tokens=s.llm_max_tokens,
        max_steps=s.agent_max_steps, max_rows=s.max_result_rows,
        rows_to_model=s.rows_sent_to_model, concurrency=s.llm_max_concurrency)
    app.state.cache = AnswerCache(redis, s.answer_cache_ttl_seconds)
    app.state.limiter = RateLimiter(redis, s.rate_limit_per_minute)
    app.state.access = AccessGuard(redis, executor, s.session_check_ttl_seconds)
    # Internal gRPC endpoint for the API (same process and state as REST; GRPC_PORT=0 turns it off)
    grpc_server = await start_grpc_server(app.state, s.grpc_port) if s.grpc_port else None
    try:
        yield
    finally:
        if grpc_server is not None:
            await grpc_server.stop(grace=5)
        await executor.close()
        await redis.aclose()
        await client.close()


_settings = get_settings()
_docs = not _settings.is_production  # no public API docs in production
app = FastAPI(title="ERP AI Service", version="1.0.0", lifespan=lifespan,
              docs_url="/ai/docs" if _docs else None,
              openapi_url="/ai/openapi.json" if _docs else None)
app.add_middleware(CORSMiddleware, allow_origins=_settings.cors_origin_list,
                   allow_methods=["*"], allow_headers=["*"])


@app.get("/ai/health")
async def health() -> dict:
    return {"status": "ok"}


@app.post("/ai/chat", response_model=ChatResponse)
async def chat(body: ChatRequest, request: Request, background: BackgroundTasks,
               user: Principal = Depends(current_principal)) -> ChatResponse:
    try:
        outcome = await answer(request.app.state, user, body.question, [t.model_dump() for t in body.history])
    except ChatError as e:
        raise HTTPException(e.status_code, e.message) from e
    if outcome.audit is not None:
        background.add_task(request.app.state.executor.audit, *outcome.audit)
    return ChatResponse(**outcome.payload, cached=outcome.cached)
