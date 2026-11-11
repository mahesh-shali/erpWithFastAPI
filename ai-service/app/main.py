import logging
from contextlib import asynccontextmanager
from typing import Literal

import groq
from fastapi import BackgroundTasks, Depends, FastAPI, HTTPException, Request, status
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
from redis.asyncio import Redis

from .agent import NL2SQLAgent
from .auth import Principal, current_principal
from .cache import AccessGuard, AnswerCache, RateLimiter
from .config import get_settings
from .db import PostgresExecutor
from .sql_guard import tables_for_pages

log = logging.getLogger("ai-service")

AI_PAGE = "ai-chat"  # Pages master key of the AI Assistant


# ---------- Schemas ----------
class HistoryTurn(BaseModel):
    role: Literal["user", "assistant"]
    content: str = Field(max_length=4000)


class ChatRequest(BaseModel):
    question: str = Field(min_length=1, max_length=1000)
    history: list[HistoryTurn] = Field(default_factory=list, max_length=10)


class ChatResponse(BaseModel):
    answer: str
    sql: list[str]
    columns: list[str]
    rows: list[list]
    cached: bool = False
    latency_ms: int = 0


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
    try:
        yield
    finally:
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
    st = request.app.state

    access = await st.access.get(user.session_id, user.user_id)
    if not access.session_active:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Session is no longer active")
    if AI_PAGE not in access.readable_pages:
        raise HTTPException(status.HTTP_403_FORBIDDEN, "You don't have access to the AI assistant.")
    allowed_tables = tables_for_pages(access.readable_pages)
    scope = ",".join(sorted(allowed_tables))

    allowed, _remaining = await st.limiter.hit(user.user_id)
    if not allowed:
        raise HTTPException(status.HTTP_429_TOO_MANY_REQUESTS, "Rate limit exceeded. Try again in a minute.")

    # Only stand-alone questions are cacheable (follow-ups depend on history)
    cacheable = not body.history
    if cacheable and (hit := await st.cache.get(user.org_id, body.question, scope)):
        return ChatResponse(**hit, cached=True)

    try:
        result = await st.agent.ask(body.question, user.org_id,
                                    [t.model_dump() for t in body.history], allowed_tables)
    except groq.RateLimitError as e:
        raise HTTPException(status.HTTP_503_SERVICE_UNAVAILABLE, "AI is busy, please retry shortly.") from e
    except groq.APIError as e:
        log.exception("Groq API error")
        raise HTTPException(status.HTTP_502_BAD_GATEWAY, "AI provider error.") from e

    payload = {"answer": result.answer, "sql": result.sql, "columns": result.columns,
               "rows": result.rows, "latency_ms": result.latency_ms}
    if cacheable:
        await st.cache.set(user.org_id, body.question, payload, scope)

    executor = getattr(st, "executor", None)
    if executor is not None:
        background.add_task(executor.audit, user.org_id, user.user_id, body.question,
                            "\n;\n".join(result.sql) or None, len(result.rows), result.latency_ms)
    return ChatResponse(**payload)
