"""The chat use case shared by the REST endpoint (/ai/chat) and the internal gRPC service (grpc_server.py).

Both transports authenticate the caller first (auth.decode_token) and then call `answer()`, so the session,
page-right, rate-limit, cache and audit rules can't drift apart between them.
"""
import logging
from dataclasses import dataclass
from typing import Any

import groq

from .auth import Principal
from .sql_guard import tables_for_pages

log = logging.getLogger("ai-service")

AI_PAGE = "ai-chat"  # Pages master key of the AI Assistant


class ChatError(Exception):
    """A refusal with the HTTP status it maps to (gRPC maps the same status to a status code)."""

    def __init__(self, status_code: int, message: str):
        super().__init__(message)
        self.status_code = status_code
        self.message = message


@dataclass(frozen=True)
class ChatOutcome:
    payload: dict[str, Any]          # answer, sql, columns, rows, latency_ms
    cached: bool
    audit: tuple | None = None       # args for executor.audit(), to run after the reply is sent


async def answer(state: Any, user: Principal, question: str, history: list[dict]) -> ChatOutcome:
    """`state` = app.state (agent, cache, limiter, access, optional executor)."""
    access = await state.access.get(user.session_id, user.user_id)
    if not access.session_active:
        raise ChatError(401, "Session is no longer active")
    if AI_PAGE not in access.readable_pages:
        raise ChatError(403, "You don't have access to the AI assistant.")
    allowed_tables = tables_for_pages(access.readable_pages)
    scope = ",".join(sorted(allowed_tables))

    allowed, _remaining = await state.limiter.hit(user.user_id)
    if not allowed:
        raise ChatError(429, "Rate limit exceeded. Try again in a minute.")

    # Only stand-alone questions are cacheable (follow-ups depend on history)
    cacheable = not history
    if cacheable and (hit := await state.cache.get(user.org_id, question, scope)):
        return ChatOutcome(hit, cached=True)

    try:
        result = await state.agent.ask(question, user.org_id, history, allowed_tables)
    except groq.RateLimitError as e:
        raise ChatError(503, "AI is busy, please retry shortly.") from e
    except groq.APIError as e:
        log.exception("Groq API error")
        raise ChatError(502, "AI provider error.") from e

    payload = {"answer": result.answer, "sql": result.sql, "columns": result.columns,
               "rows": result.rows, "latency_ms": result.latency_ms}
    if cacheable:
        await state.cache.set(user.org_id, question, payload, scope)

    audit = None
    if getattr(state, "executor", None) is not None:
        audit = (user.org_id, user.user_id, question, "\n;\n".join(result.sql) or None,
                 len(result.rows), result.latency_ms)
    return ChatOutcome(payload, cached=False, audit=audit)
