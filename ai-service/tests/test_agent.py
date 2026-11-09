import decimal
import uuid

import groq
import httpx
import pytest

from app.agent import SYSTEM_PROMPT, NL2SQLAgent
from app.db import QueryResult
from tests.conftest import ORG_A, FakeExecutor, FakeLLM, response, text_block, tool_block


def make_agent(script, executor, **kw):
    return NL2SQLAgent(FakeLLM(script), executor, model="llm-test", **kw)


def tool_msgs(call: dict) -> list[dict]:
    """The `role: tool` results the agent sent in a request."""
    return [m for m in call["messages"] if m["role"] == "tool"]


async def test_tool_loop_runs_sql_and_returns_answer(fake_executor):
    agent = make_agent([
        response(tool_block("SELECT count(*) AS n FROM customers")),
        response(text_block("You have 42 customers.")),
    ], fake_executor)

    res = await agent.ask("How many customers do I have?", ORG_A)

    assert res.answer == "You have 42 customers."
    assert res.columns == ["n"] and res.rows == [[42]]
    assert len(res.sql) == 1 and "LIMIT 500" in res.sql[0]
    assert fake_executor.executed[0][1] == ORG_A  # tenant passed through
    assert res.steps == 2


async def test_request_has_system_prompt_first_and_run_sql_tool(fake_executor):
    llm = FakeLLM([response(text_block("hi"))])
    await NL2SQLAgent(llm, fake_executor, model="llm-test", max_tokens=777).ask("hello", ORG_A)
    call = llm.calls[0]
    assert call["model"] == "llm-test"
    assert call["max_completion_tokens"] == 777
    assert call["messages"][0] == {"role": "system", "content": SYSTEM_PROMPT}
    assert call["tools"][0]["type"] == "function"
    assert call["tools"][0]["function"]["name"] == "run_sql"


async def test_tool_call_and_result_are_echoed_in_openai_format(fake_executor):
    llm = FakeLLM([response(tool_block("SELECT count(*) AS n FROM orders", "call_1")),
                   response(text_block("42"))])
    await NL2SQLAgent(llm, fake_executor, model="m").ask("count orders", ORG_A)
    msgs = llm.calls[1]["messages"]
    assistant = msgs[-2]
    assert assistant["role"] == "assistant"
    assert assistant["tool_calls"][0]["id"] == "call_1"
    assert assistant["tool_calls"][0]["function"]["name"] == "run_sql"
    assert msgs[-1]["role"] == "tool" and msgs[-1]["tool_call_id"] == "call_1"
    assert '"columns": ["n"]' in msgs[-1]["content"]


async def test_rejected_sql_is_fed_back_and_never_executed(fake_executor):
    llm = FakeLLM([
        response(tool_block("DELETE FROM orders", "t1")),
        response(tool_block("SELECT count(*) AS n FROM orders", "t2")),
        response(text_block("There are 42 orders.")),
    ])
    res = await NL2SQLAgent(llm, fake_executor, model="m").ask("delete everything then count", ORG_A)

    assert len(fake_executor.executed) == 1
    assert "DELETE" not in fake_executor.executed[0][0]
    rejected = tool_msgs(llm.calls[1])[-1]
    assert rejected["tool_call_id"] == "t1" and rejected["content"].startswith("REJECTED")
    assert res.answer == "There are 42 orders."


async def test_db_error_lets_model_self_correct():
    ex = FakeExecutor(fail_first=True)
    llm = FakeLLM([
        response(tool_block("SELECT revenue FROM orders")),
        response(tool_block("SELECT sum(total_amount) AS n FROM orders")),
        response(text_block("Revenue is ₹42.")),
    ])
    res = await NL2SQLAgent(llm, ex, model="m").ask("revenue?", ORG_A)
    assert "DATABASE ERROR" in tool_msgs(llm.calls[1])[-1]["content"]
    assert res.answer == "Revenue is ₹42." and len(ex.executed) == 2


async def test_malformed_tool_arguments_are_reported_back(fake_executor):
    llm = FakeLLM([
        response(tool_block("", arguments="{not json")),
        response(text_block("sorry")),
    ])
    res = await NL2SQLAgent(llm, fake_executor, model="m").ask("q", ORG_A)
    assert tool_msgs(llm.calls[1])[-1]["content"].startswith("ERROR: tool arguments")
    assert fake_executor.executed == [] and res.answer == "sorry"


def _groq_400(code: str) -> groq.BadRequestError:
    req = httpx.Request("POST", "https://api.groq.com/openai/v1/chat/completions")
    body = {"error": {"message": "Failed to call a function", "code": code}}
    return groq.BadRequestError(f"Error code: 400 - {body}", response=httpx.Response(400, request=req), body=body)


async def test_groq_tool_use_failed_is_retried(fake_executor):
    llm = FakeLLM([_groq_400("tool_use_failed"), response(text_block("recovered"))])
    res = await NL2SQLAgent(llm, fake_executor, model="m").ask("q", ORG_A)
    assert res.answer == "recovered" and len(llm.calls) == 2


async def test_other_bad_requests_propagate(fake_executor):
    llm = FakeLLM([_groq_400("invalid_request_error")])
    with pytest.raises(groq.BadRequestError):
        await NL2SQLAgent(llm, fake_executor, model="m").ask("q", ORG_A)


async def test_non_data_question_skips_tool(fake_executor):
    res = await make_agent([response(text_block("I'm your ERP assistant."))], fake_executor).ask("who are you?", ORG_A)
    assert res.sql == [] and res.rows == [] and fake_executor.executed == []


async def test_stops_after_max_steps(fake_executor):
    script = [response(tool_block("SELECT 1 AS n FROM orders")) for _ in range(3)]
    res = await make_agent(script, fake_executor, max_steps=3).ask("loop", ORG_A)
    assert "couldn't complete" in res.answer and res.steps == 3


async def test_rows_sent_to_model_are_truncated_but_ui_gets_all():
    ex = FakeExecutor(result=QueryResult(["i", "amt", "id"],
                                         [[i, decimal.Decimal("1.50"), uuid.uuid4()] for i in range(120)]))
    llm = FakeLLM([response(tool_block("SELECT * FROM orders")), response(text_block("done"))])
    res = await NL2SQLAgent(llm, ex, model="m", rows_to_model=10).ask("all orders", ORG_A)
    sent = tool_msgs(llm.calls[1])[-1]["content"]
    assert '"row_count": 120' in sent and '"truncated_for_model": true' in sent
    assert len(res.rows) == 120 and res.rows[0][1] == 1.5 and isinstance(res.rows[0][2], str)


async def test_history_is_prepended_after_system_prompt(fake_executor):
    llm = FakeLLM([response(text_block("ok"))])
    hist = [{"role": "user", "content": "top product?"}, {"role": "assistant", "content": "Product 7"}]
    await NL2SQLAgent(llm, fake_executor, model="m").ask("and its price?", ORG_A, hist)
    msgs = llm.calls[0]["messages"]
    assert msgs[0]["role"] == "system" and msgs[1:3] == hist and msgs[-1]["content"] == "and its price?"
