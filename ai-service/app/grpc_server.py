"""Internal gRPC endpoint (erp.ai.v1.AiAssistant) that the ASP.NET Core API calls for POST /api/ai/chat.

Same rules as REST /ai/chat: the API forwards the user's access token as `authorization` metadata, we
validate it with auth.decode_token, then run the shared chat_flow.answer(). Contract: backend/src/Erp.Api/
Protos/ai_assistant.proto (stubs in app/grpc_gen, regenerate with scripts/gen_proto.py).
"""
import asyncio
import logging
from typing import Any

import grpc
from fastapi import HTTPException
from pydantic import TypeAdapter, ValidationError

from .auth import Principal, decode_token
from .chat_flow import ChatError, answer
from .config import get_settings
from .grpc_gen import ai_assistant_pb2 as pb
from .grpc_gen import ai_assistant_pb2_grpc as pb_grpc
from .models import ChatRequest

log = logging.getLogger("ai-service.grpc")

# HTTP status from chat_flow -> gRPC status (the API maps them back to the same HTTP status)
_STATUS = {
    400: grpc.StatusCode.INVALID_ARGUMENT,
    401: grpc.StatusCode.UNAUTHENTICATED,
    403: grpc.StatusCode.PERMISSION_DENIED,
    429: grpc.StatusCode.RESOURCE_EXHAUSTED,
    502: grpc.StatusCode.INTERNAL,
    503: grpc.StatusCode.UNAVAILABLE,
}
# Same serializer FastAPI uses for the REST response, so rows_json matches the REST "rows" byte for byte.
_rows_json = TypeAdapter(list[list])


def _bearer(context: grpc.aio.ServicerContext) -> str | None:
    for key, value in context.invocation_metadata() or ():
        if key == "authorization" and isinstance(value, str):
            scheme, _, token = value.partition(" ")
            return token.strip() if scheme.lower() == "bearer" and token.strip() else None
    return None


class AiAssistantService(pb_grpc.AiAssistantServicer):
    def __init__(self, state: Any):
        self._state = state  # app.state: agent, cache, limiter, access, optional executor
        self._audits: set[asyncio.Task] = set()

    async def _principal(self, context: grpc.aio.ServicerContext) -> Principal:
        token = _bearer(context)
        if token is None:
            await context.abort(grpc.StatusCode.UNAUTHENTICATED, "Missing bearer token")
        try:
            return decode_token(token, get_settings())
        except HTTPException as e:
            await context.abort(grpc.StatusCode.UNAUTHENTICATED, str(e.detail))

    async def Chat(self, request: pb.ChatRequest, context: grpc.aio.ServicerContext) -> pb.ChatReply:  # noqa: N802
        user = await self._principal(context)
        try:
            body = ChatRequest(question=request.question,
                               history=[{"role": t.role, "content": t.content} for t in request.history])
        except ValidationError as e:
            first = e.errors()[0]
            where = ".".join(str(p) for p in first.get("loc", ()))
            await context.abort(grpc.StatusCode.INVALID_ARGUMENT, f"{where}: {first.get('msg', 'invalid')}")

        try:
            outcome = await answer(self._state, user, body.question, [t.model_dump() for t in body.history])
        except ChatError as e:
            await context.abort(_STATUS.get(e.status_code, grpc.StatusCode.INTERNAL), e.message)

        if outcome.audit is not None:  # after the reply, like REST's BackgroundTasks
            task = asyncio.create_task(self._audit(outcome.audit))
            self._audits.add(task)
            task.add_done_callback(self._audits.discard)

        p = outcome.payload
        return pb.ChatReply(answer=p["answer"], sql=p["sql"], columns=p["columns"],
                            rows_json=_rows_json.dump_json(p["rows"]).decode(),
                            cached=outcome.cached, latency_ms=int(p.get("latency_ms", 0)))

    async def _audit(self, args: tuple) -> None:
        try:
            await self._state.executor.audit(*args)
        except Exception:
            log.exception("Chat audit failed")


async def start_grpc_server(state: Any, port: int, host: str = "[::]") -> grpc.aio.Server:
    """Starts the server; returns it so the caller can stop() it. port=0 picks a free port (tests)."""
    server = grpc.aio.server(options=[
        ("grpc.so_reuseport", 1),                        # one port shared by all gunicorn workers
        ("grpc.max_receive_message_length", 1 << 20),    # requests are small (question + 10 turns)
        ("grpc.max_send_message_length", 8 << 20),       # up to max_result_rows rows
    ])
    pb_grpc.add_AiAssistantServicer_to_server(AiAssistantService(state), server)
    bound = server.add_insecure_port(f"{host}:{port}")
    await server.start()
    server.bound_port = bound  # type: ignore[attr-defined]
    log.info("gRPC AiAssistant listening on %s:%s", host, bound)
    return server
