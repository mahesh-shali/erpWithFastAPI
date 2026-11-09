"""
LLM tool-calling agent (Groq, OpenAI-compatible chat API): natural language -> SQL -> rows -> answer.

The model is given one tool, `run_sql`. It may call it several times (e.g. to fix a
column name after an error) up to `max_steps`. Every SQL string passes the
static guard and then runs inside a read-only, RLS-scoped transaction.
"""
import asyncio
import datetime as dt
import decimal
import json
import logging
import time
import uuid
from dataclasses import dataclass, field
from typing import Any

import groq

from .db import QueryError, QueryResult, SqlExecutor
from .schema import SCHEMA_DESCRIPTION
from .sql_guard import SqlRejected, validate_sql

log = logging.getLogger("ai-service.agent")

SYSTEM_PROMPT = f"""You are the analytics assistant inside an ERP system.
Answer the user's business questions by querying their PostgreSQL database with the `run_sql` tool.

Rules:
- Only write a single read-only SELECT (CTEs allowed). Never modify data.
- Do NOT filter by organization; tenant isolation is enforced by the database.
- Prefer aggregates over dumping raw rows. Always add ORDER BY for "top"/"most" questions.
- If a query errors, read the error, fix the SQL and retry.
- If the question is not about the data, answer briefly without calling the tool.
- Final answer: concise, plain language, include the key numbers. Amounts are in INR (₹).
  Do not include the SQL in the final answer; the UI shows it separately.

Database schema:
{SCHEMA_DESCRIPTION}
"""

# OpenAI-style function tool (Groq chat completions format)
RUN_SQL_TOOL = {
    "type": "function",
    "function": {
        "name": "run_sql",
        "description": (
            "Execute one read-only PostgreSQL SELECT against the ERP database and return "
            "columns and rows as JSON. Results are capped; use aggregates for large data."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "sql": {"type": "string", "description": "A single PostgreSQL SELECT statement."},
            },
            "required": ["sql"],
        },
    },
}


@dataclass
class ChatResult:
    answer: str
    sql: list[str] = field(default_factory=list)
    columns: list[str] = field(default_factory=list)
    rows: list[list[Any]] = field(default_factory=list)
    steps: int = 0
    latency_ms: int = 0


def _json_default(o: Any) -> Any:
    if isinstance(o, decimal.Decimal):
        return float(o)
    if isinstance(o, (dt.datetime, dt.date)):
        return o.isoformat()
    if isinstance(o, uuid.UUID):
        return str(o)
    return str(o)


def to_jsonable(rows: list[list[Any]]) -> list[list[Any]]:
    return json.loads(json.dumps(rows, default=_json_default))


def _is_malformed_tool_call(e: Exception) -> bool:
    """Groq returns 400 `tool_use_failed` when the model emits an unparsable tool call; worth a retry."""
    return isinstance(e, groq.BadRequestError) and "tool_use_failed" in str(e)


class NL2SQLAgent:
    def __init__(self, client: Any, executor: SqlExecutor, *, model: str, max_tokens: int = 4096,
                 max_steps: int = 5, max_rows: int = 500, rows_to_model: int = 50,
                 concurrency: int = 64):
        self._client = client
        self._exec = executor
        self._model = model
        self._max_tokens = max_tokens
        self._max_steps = max_steps
        self._max_rows = max_rows
        self._rows_to_model = rows_to_model
        self._sem = asyncio.Semaphore(concurrency)

    async def _call_llm(self, messages: list[dict]) -> Any:
        async with self._sem:
            # The static system prompt is always the first message, so Groq's automatic
            # prompt caching (on supported models) can reuse it across requests.
            return await self._client.chat.completions.create(
                model=self._model,
                max_completion_tokens=self._max_tokens,  # includes reasoning tokens on reasoning models
                messages=messages,
                tools=[RUN_SQL_TOOL],
                tool_choice="auto",
            )

    async def _run_tool(self, sql: str, org_id: uuid.UUID, allowed_tables: frozenset[str] | None = None,
                        ) -> tuple[str, bool, str | None, QueryResult | None]:
        """Returns (tool_result_text, is_error, safe_sql, result)."""
        try:
            safe = validate_sql(sql, self._max_rows, allowed_tables)
        except SqlRejected as e:
            return f"REJECTED: {e}", True, None, None
        try:
            result = await self._exec.run(safe, org_id)
        except QueryError as e:
            return f"DATABASE ERROR: {e}", True, safe, None

        preview = to_jsonable(result.rows[: self._rows_to_model])
        payload = {
            "columns": result.columns,
            "row_count": len(result.rows),
            "rows": preview,
            "truncated_for_model": len(result.rows) > self._rows_to_model,
        }
        return json.dumps(payload, default=_json_default), False, safe, result

    async def ask(self, question: str, org_id: uuid.UUID, history: list[dict] | None = None,
                  allowed_tables: frozenset[str] | None = None) -> ChatResult:
        """`allowed_tables`: tables this user's page rights allow (None = all AI tables)."""
        started = time.perf_counter()
        messages: list[dict] = [{"role": "system", "content": SYSTEM_PROMPT}]
        if allowed_tables is not None:
            # After the static prompt, so the cached prefix stays identical for everyone.
            listed = ", ".join(sorted(allowed_tables)) or "none"
            messages.append({"role": "system", "content":
                             f"This user's access rights allow querying only these tables: {listed}. "
                             "If the question needs other data, say they don't have access to it."})
        messages += [*(history or []), {"role": "user", "content": question}]
        out = ChatResult(answer="")
        last: QueryResult | None = None

        for step in range(1, self._max_steps + 1):
            out.steps = step
            try:
                resp = await self._call_llm(messages)
            except groq.BadRequestError as e:
                if _is_malformed_tool_call(e) and step < self._max_steps:
                    log.warning("Model produced a malformed tool call; retrying (step %d)", step)
                    continue
                raise
            msg = resp.choices[0].message
            tool_calls = list(msg.tool_calls or [])

            if not tool_calls:
                out.answer = (msg.content or "").strip()
                break

            # Echo the assistant turn (content + tool calls only; reasoning text is not sent back)
            messages.append({
                "role": "assistant",
                "content": msg.content or "",
                "tool_calls": [
                    {"id": tc.id, "type": "function",
                     "function": {"name": tc.function.name, "arguments": tc.function.arguments}}
                    for tc in tool_calls
                ],
            })
            for tc in tool_calls:
                text, result = await self._handle_tool_call(tc, org_id, out, allowed_tables)
                if result is not None:
                    last = result
                messages.append({"role": "tool", "tool_call_id": tc.id, "content": text})
        else:
            out.answer = "I couldn't complete that analysis within the allowed number of steps. Try rephrasing."

        if last is not None:
            out.columns = last.columns
            out.rows = to_jsonable(last.rows)
        out.latency_ms = int((time.perf_counter() - started) * 1000)
        return out

    async def _handle_tool_call(self, tc: Any, org_id: uuid.UUID, out: ChatResult,
                                allowed_tables: frozenset[str] | None = None) -> tuple[str, QueryResult | None]:
        """Runs one tool call. Returns (text for the model, result if the SQL ran)."""
        if tc.function.name != "run_sql":
            return "ERROR: Unknown tool.", None
        try:
            args = json.loads(tc.function.arguments or "{}")
        except json.JSONDecodeError:
            return 'ERROR: tool arguments must be JSON like {"sql": "SELECT ..."}.', None
        text, _is_err, safe_sql, result = await self._run_tool(str(args.get("sql", "")), org_id, allowed_tables)
        if safe_sql:
            out.sql.append(safe_sql)
        return text, result
