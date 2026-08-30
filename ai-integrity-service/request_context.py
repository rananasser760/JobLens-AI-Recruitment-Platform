from contextvars import ContextVar

_request_id_ctx: ContextVar[str] = ContextVar("request_id", default="")


def set_request_id(value: str) -> None:
    _request_id_ctx.set((value or "").strip())


def get_request_id() -> str:
    return (_request_id_ctx.get() or "").strip()
