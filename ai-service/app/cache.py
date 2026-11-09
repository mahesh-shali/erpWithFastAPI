"""Redis-backed answer cache and distributed per-user rate limiter (shared by all pods)."""
import hashlib
import time
from dataclasses import dataclass
from uuid import UUID

import orjson
from redis.asyncio import Redis


def _norm(q: str) -> str:
    return " ".join(q.lower().split())


class AnswerCache:
    def __init__(self, redis: Redis, ttl: int):
        self._r, self._ttl = redis, ttl

    @staticmethod
    def key(org_id: UUID, question: str, scope: str = "") -> str:
        # scope = the user's allowed tables: users with different rights never share an answer
        h = hashlib.sha256(f"{org_id}:{scope}:{_norm(question)}".encode()).hexdigest()
        return f"ai:answer:{h}"

    async def get(self, org_id: UUID, question: str, scope: str = "") -> dict | None:
        raw = await self._r.get(self.key(org_id, question, scope))
        return orjson.loads(raw) if raw else None

    async def set(self, org_id: UUID, question: str, value: dict, scope: str = "") -> None:
        await self._r.set(self.key(org_id, question, scope), orjson.dumps(value), ex=self._ttl)


@dataclass(frozen=True)
class UserAccess:
    session_active: bool
    readable_pages: frozenset[str]


class AccessGuard:
    """Per request: is the login session still active, and which pages may the user read?

    One call to ai_user_access() (SECURITY DEFINER: the AI role can't read user_sessions or the
    rights tables), cached in Redis for `ttl` seconds. Logout or a rights change reaches the AI
    within `ttl` seconds without a DB round trip on every question.
    """

    def __init__(self, redis: Redis, executor, ttl: int = 30):
        self._r, self._ex, self._ttl = redis, executor, ttl

    async def get(self, session_id: UUID, user_id: UUID) -> UserAccess:
        key = f"ai:access:{session_id}"
        cached = await self._r.get(key)
        if cached is not None:
            d = orjson.loads(cached)
            return UserAccess(d["a"], frozenset(d["p"]))
        active, pages = await self._ex.user_access(session_id, user_id)
        await self._r.set(key, orjson.dumps({"a": active, "p": sorted(pages)}), ex=self._ttl)
        return UserAccess(active, frozenset(pages))


class RateLimiter:
    """Fixed-window counter: INCR + EXPIRE in one pipeline. O(1), cluster-safe."""

    def __init__(self, redis: Redis, limit_per_minute: int):
        self._r, self._limit = redis, limit_per_minute

    async def hit(self, user_id: UUID) -> tuple[bool, int]:
        window = int(time.time() // 60)
        key = f"ai:rl:{user_id}:{window}"
        async with self._r.pipeline(transaction=True) as p:
            p.incr(key)
            p.expire(key, 61)
            count, _ = await p.execute()
        return count <= self._limit, max(0, self._limit - count)
