"""Request/response models shared by the REST and gRPC transports."""
from typing import Literal

from pydantic import BaseModel, Field

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
