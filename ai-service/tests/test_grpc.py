"""Internal gRPC endpoint (AiAssistant.Chat): same rules as REST /ai/chat, over a real in-process server."""
import asyncio
import json
import tempfile
import uuid
from datetime import date
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace

import fakeredis.aioredis
import groq
import grpc
import httpx
import pytest

from app.agent import NL2SQLAgent
from app.cache import AccessGuard, AnswerCache, RateLimiter
from app.db import QueryResult
from app.grpc_gen import ai_assistant_pb2 as pb
from app.grpc_gen import ai_assistant_pb2_grpc as pb_grpc
from app.grpc_server import start_grpc_server
from tests.conftest import ORG_A, FakeExecutor, FakeLLM, make_token, response, text_block, tool_block


@pytest.fixture
async def grpc_factory():
    servers, channels = [], []

    async def build(script=None, rate_limit=20, executor=None):
        ex = executor or FakeExecutor()
        llm = FakeLLM(script or [
            response(tool_block("SELECT count(*) AS n FROM customers"), stop="tool_use"),
            response(text_block("You have 42 customers.")),
        ])
        redis = fakeredis.aioredis.FakeRedis()
        state = SimpleNamespace(agent=NL2SQLAgent(llm, ex, model="m"), executor=ex,
                                cache=AnswerCache(redis, 60), limiter=RateLimiter(redis, rate_limit),
                                access=AccessGuard(redis, ex, 30))
        server = await start_grpc_server(state, 0, host="127.0.0.1")
        channel = grpc.aio.insecure_channel(f"127.0.0.1:{server.bound_port}")
        servers.append(server)
        channels.append(channel)
        return pb_grpc.AiAssistantStub(channel), llm, ex

    yield build
    for c in channels:
        await c.close()
    for s in servers:
        await s.stop(None)


def meta(token=None):
    return (("authorization", f"Bearer {token or make_token()}"),)


async def call(stub, question="How many customers?", history=(), metadata=None):
    req = pb.ChatRequest(question=question, history=[pb.HistoryTurn(role=r, content=c) for r, c in history])
    return await stub.Chat(req, metadata=meta() if metadata is None else metadata, timeout=10)


async def code_of(coro) -> grpc.StatusCode:
    with pytest.raises(grpc.aio.AioRpcError) as e:
        await coro
    return e.value.code()


async def test_happy_path_returns_answer_sql_rows_and_audits(grpc_factory):
    stub, _llm, ex = await grpc_factory()
    reply = await call(stub)
    assert reply.answer == "You have 42 customers."
    assert len(reply.sql) == 1 and "customers" in reply.sql[0] and reply.sql[0].endswith("LIMIT 500")
    assert list(reply.columns) == ["n"]
    assert json.loads(reply.rows_json) == [[42]]
    assert reply.cached is False
    await asyncio.sleep(0.05)  # audit runs after the reply
    assert len(ex.audits) == 1 and ex.audits[0][0] == ORG_A


async def test_rows_json_keeps_cell_types_like_rest(grpc_factory):
    ex = FakeExecutor(result=QueryResult(["total", "day", "id", "none"],
                                         [[Decimal("12.50"), date(2026, 1, 2), uuid.UUID(int=1), None]]))
    stub, *_ = await grpc_factory(executor=ex)
    reply = await call(stub)
    assert json.loads(reply.rows_json) == [[12.5, "2026-01-02", "00000000-0000-0000-0000-000000000001", None]]


@pytest.mark.parametrize("metadata", [
    (),
    (("authorization", "Bearer garbage"),),
    (("authorization", "Basic abc"),),
    (("authorization", f"Bearer {make_token(exp_offset=-10)}"),),
    (("authorization", f"Bearer {make_token(secret='wrong-secret-wrong-secret-wrong-secret!!')}"),),
])
async def test_rejects_missing_or_invalid_tokens(grpc_factory, metadata):
    stub, llm, _ = await grpc_factory()
    assert await code_of(call(stub, metadata=metadata)) == grpc.StatusCode.UNAUTHENTICATED
    assert llm.calls == []


async def test_revoked_session_is_unauthenticated(grpc_factory):
    sid = uuid.uuid4()
    ex = FakeExecutor(revoked_sessions={sid})
    stub, *_ = await grpc_factory(executor=ex)
    code = await code_of(call(stub, metadata=meta(make_token(session_id=sid))))
    assert code == grpc.StatusCode.UNAUTHENTICATED


async def test_without_ai_page_right_is_permission_denied(grpc_factory):
    stub, llm, _ = await grpc_factory(executor=FakeExecutor(readable_pages=["dashboard"]))
    assert await code_of(call(stub)) == grpc.StatusCode.PERMISSION_DENIED
    assert llm.calls == []


@pytest.mark.parametrize("question,history", [
    ("", ()),
    ("x" * 1001, ()),
    ("ok", (("robot", "hi"),)),
    ("ok", (("user", "x" * 4001),)),
    ("ok", tuple(("user", "hi") for _ in range(11))),
])
async def test_input_validation(grpc_factory, question, history):
    stub, *_ = await grpc_factory()
    assert await code_of(call(stub, question, history)) == grpc.StatusCode.INVALID_ARGUMENT


async def test_rate_limit_is_resource_exhausted(grpc_factory):
    stub, *_ = await grpc_factory(rate_limit=1, script=[response(text_block("a")), response(text_block("b"))])
    token = make_token()
    await call(stub, "q1", metadata=meta(token))
    assert await code_of(call(stub, "q2", metadata=meta(token))) == grpc.StatusCode.RESOURCE_EXHAUSTED


async def test_identical_question_is_served_from_cache(grpc_factory):
    stub, llm, _ = await grpc_factory()
    first = await call(stub)
    second = await call(stub)
    assert (first.cached, second.cached) == (False, True)
    assert second.answer == first.answer and len(llm.calls) == 2  # one tool round trip, then no LLM


async def test_follow_ups_pass_history_and_skip_cache(grpc_factory):
    stub, llm, _ = await grpc_factory(script=[response(text_block("Still 42.")), response(text_block("42."))])
    hist = (("user", "How many customers?"), ("assistant", "42."))
    await call(stub, "And now?", hist)
    reply = await call(stub, "And now?", hist)
    assert reply.cached is False
    assert any(m.get("content") == "How many customers?" for m in llm.calls[0]["messages"])


@pytest.mark.parametrize("error,code", [
    (groq.RateLimitError("busy", response=httpx.Response(429, request=httpx.Request("POST", "http://x")),
                         body=None), grpc.StatusCode.UNAVAILABLE),
    (groq.APIError("boom", request=httpx.Request("POST", "http://x"), body=None), grpc.StatusCode.INTERNAL),
])
async def test_llm_errors_map_to_status_codes(grpc_factory, error, code):
    stub, *_ = await grpc_factory(script=[error])
    assert await code_of(call(stub)) == code


def test_stubs_match_proto():
    """The committed app/grpc_gen must be regenerated when the .proto changes (scripts/gen_proto.py)."""
    import importlib.util

    root = Path(__file__).resolve().parent.parent
    spec = importlib.util.spec_from_file_location("gen_proto", root / "scripts" / "gen_proto.py")
    gen = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gen)
    with tempfile.TemporaryDirectory() as tmp:
        fresh = gen.generate(Path(tmp))
    for name, text in fresh.items():
        committed = (root / "app" / "grpc_gen" / name).read_text(encoding="utf-8")
        assert committed == text, f"app/grpc_gen/{name} is stale: run python scripts/gen_proto.py"
