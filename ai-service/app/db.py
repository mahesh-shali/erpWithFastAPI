"""Tenant-scoped, read-only query execution on a shared asyncpg pool."""
from dataclasses import dataclass, field
from typing import Any, Protocol
from uuid import UUID

import asyncpg


@dataclass
class QueryResult:
    columns: list[str]
    rows: list[list[Any]] = field(default_factory=list)


class QueryError(Exception):
    """DB-side error (bad column, timeout...). Message is fed back to the LLM to self-correct."""


class SqlExecutor(Protocol):
    async def run(self, sql: str, org_id: UUID) -> QueryResult: ...
    async def audit(self, org_id: UUID, user_id: UUID, question: str, sql: str | None,
                    row_count: int | None, latency_ms: int) -> None: ...
    async def session_active(self, session_id: UUID, user_id: UUID) -> bool: ...
    async def user_access(self, session_id: UUID, user_id: UUID) -> tuple[bool, list[str]]: ...


class PostgresExecutor:
    def __init__(self, pool: asyncpg.Pool):
        self._pool = pool

    @classmethod
    async def create(cls, dsn: str, min_size: int, max_size: int) -> "PostgresExecutor":
        pool = await asyncpg.create_pool(
            dsn, min_size=min_size, max_size=max_size,
            command_timeout=10, max_inactive_connection_lifetime=60,
            statement_cache_size=0,  # PgBouncer transaction-mode compatible
        )
        return cls(pool)

    async def close(self) -> None:
        await self._pool.close()

    async def run(self, sql: str, org_id: UUID) -> QueryResult:
        async with self._pool.acquire() as conn:
            async with conn.transaction(readonly=True):
                # Scope RLS to this tenant for this transaction only
                await conn.execute("SELECT set_config('app.org_id', $1, true)", str(org_id))
                await conn.execute("SET LOCAL statement_timeout = '5s'")
                try:
                    stmt = await conn.prepare(sql)
                    records = await stmt.fetch()
                except asyncpg.PostgresError as e:
                    raise QueryError(f"{type(e).__name__}: {e}") from e
                columns = [a.name for a in stmt.get_attributes()]
                return QueryResult(columns, [list(r.values()) for r in records])

    async def session_active(self, session_id: UUID, user_id: UUID) -> bool:
        async with self._pool.acquire() as conn:
            return bool(await conn.fetchval("SELECT session_is_active($1, $2)", session_id, user_id))

    async def user_access(self, session_id: UUID, user_id: UUID) -> tuple[bool, list[str]]:
        """(session still active?, page keys the user may read) via the SECURITY DEFINER ai_user_access()."""
        async with self._pool.acquire() as conn:
            row = await conn.fetchrow("SELECT session_active, readable_pages FROM ai_user_access($1, $2)",
                                      session_id, user_id)
            return (bool(row["session_active"]), list(row["readable_pages"] or [])) if row else (False, [])

    async def audit(self, org_id, user_id, question, sql, row_count, latency_ms) -> None:
        # The role defaults to read-only transactions and has no INSERT grants;
        # the only write it can perform is through the SECURITY DEFINER log_chat().
        async with self._pool.acquire() as conn:
            async with conn.transaction():
                await conn.execute("SET TRANSACTION READ WRITE")
                await conn.execute("SELECT log_chat($1,$2,$3,$4,$5,$6)",
                                   org_id, user_id, question[:2000], sql, row_count, latency_ms)
