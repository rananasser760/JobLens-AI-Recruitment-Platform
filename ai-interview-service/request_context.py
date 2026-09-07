from __future__ import annotations

from contextlib import contextmanager
from contextvars import ContextVar, Token
from typing import Iterator

_request_id_ctx: ContextVar[str] = ContextVar("request_id", default="")


def set_request_id(value: str) -> Token[str]:
    """Sets the request or correlation ID for the active context and returns a reset token."""
    return _request_id_ctx.set((value or "").strip())


def get_request_id() -> str:
    """Gets the request or correlation ID from the active context."""
    return (_request_id_ctx.get() or "").strip()


def reset_request_id(token: Token[str]) -> None:
    """Resets the context variable to its prior state using the provided token."""
    _request_id_ctx.reset(token)


@contextmanager
def request_context_scope(request_id: str) -> Iterator[str]:
    """Context manager ensuring temporary correlation ID assignment and guaranteed restoration."""
    token = set_request_id(request_id)
    try:
        yield get_request_id()
    finally:
        reset_request_id(token)