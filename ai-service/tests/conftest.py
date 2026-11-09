import copy
import json
import time
import uuid
from dataclasses import dataclass, field
from types import SimpleNamespace
from typing import Any

import jwt
import pytest

from app.config import get_settings
from app.db import QueryError, QueryResult

ORG_A = uuid.UUID("11111111-1111-1111-1111-111111111111")
ORG_B = uuid.UUID("22222222-2222-2222-2222-222222222222")


def make_token(org_id=ORG_A, user_id=None, exp_offset=3600, secret=None, session_id=None, **overrides) -> str:
    s = get_settings()
    claims = {
        "sub": str(user_id or uuid.uuid4()), "org_id": str(org_id), "role": "Admin",
        "sid": str(session_id or uuid.uuid4()),
        "iss": s.jwt_issuer, "aud": s.jwt_audience,
        "iat": int(time.time()), "exp": int(time.time()) + exp_offset, **overrides,
    }
    return jwt.encode(claims, secret or s.jwt_secret, algorithm="HS256")


# ---------- Fake LLM (Groq / OpenAI-compatible chat completions) ----------
def text_block(t: str):
    return ("text", t)


def tool_block(sql: str, id_: str | None = None, arguments: str | None = None):
    return SimpleNamespace(id=id_ or f"call_{uuid.uuid4().hex[:8]}", type="function",
                           function=SimpleNamespace(name="run_sql",
                                                    arguments=arguments or json.dumps({"sql": sql})))


def response(*blocks, stop=None):
    """One chat completion. `stop` is informational; tool calls are what drive the loop."""
    text = "".join(b[1] for b in blocks if isinstance(b, tuple)) or None
    calls = [b for b in blocks if not isinstance(b, tuple)] or None
    msg = SimpleNamespace(role="assistant", content=text, tool_calls=calls)
    return SimpleNamespace(choices=[SimpleNamespace(message=msg,
                                                    finish_reason=stop or ("tool_calls" if calls else "stop"))])


class FakeCompletions:
    def __init__(self, script: list):
        self.script = list(script)
        self.calls: list[dict] = []

    async def create(self, **kwargs):
        self.calls.append(copy.deepcopy(kwargs))  # snapshot; agent keeps mutating its list
        if not self.script:
            raise AssertionError("FakeLLM ran out of scripted responses")
        nxt = self.script.pop(0)
        if isinstance(nxt, Exception):
            raise nxt
        return nxt


class FakeLLM:
    def __init__(self, script: list):
        self.chat = SimpleNamespace(completions=FakeCompletions(script))

    @property
    def calls(self) -> list[dict]:
        return self.chat.completions.calls


# ---------- Fake DB ----------
@dataclass
class FakeExecutor:
    result: QueryResult = field(default_factory=lambda: QueryResult(["n"], [[42]]))
    fail_first: bool = False
    executed: list[tuple[str, uuid.UUID]] = field(default_factory=list)
    audits: list[tuple] = field(default_factory=list)
    revoked_sessions: set[uuid.UUID] = field(default_factory=set)
    session_checks: int = 0
    readable_pages: list[str] = field(default_factory=lambda: [
        "dashboard", "customers", "products", "orders", "ai-chat", "master-users"])

    async def run(self, sql: str, org_id: uuid.UUID) -> QueryResult:
        self.executed.append((sql, org_id))
        if self.fail_first and len(self.executed) == 1:
            raise QueryError('UndefinedColumnError: column "revenue" does not exist')
        return self.result

    async def audit(self, *args: Any) -> None:
        self.audits.append(args)

    async def session_active(self, session_id: uuid.UUID, user_id: uuid.UUID) -> bool:
        return session_id not in self.revoked_sessions

    async def user_access(self, session_id: uuid.UUID, user_id: uuid.UUID) -> tuple[bool, list[str]]:
        self.session_checks += 1
        return session_id not in self.revoked_sessions, list(self.readable_pages)


@pytest.fixture
def fake_executor():
    return FakeExecutor()
