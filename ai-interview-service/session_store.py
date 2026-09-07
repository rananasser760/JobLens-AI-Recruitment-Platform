from __future__ import annotations

import threading
import time
from collections import OrderedDict
from typing import Any, Dict, Optional

# Max number of interview sessions retained concurrently in memory
MAX_SESSIONS: int = 2000
# Retention duration (in seconds): 3 hours
SESSION_TTL_SECONDS: float = 3.0 * 3600.0

_store_lock = threading.Lock()
_raw_sessions: OrderedDict[str, Dict[str, Any]] = OrderedDict()


class SessionStore(dict):
    """
    Dictionary proxy for backwards-compatible access to INTERVIEW_SESSIONS
    with thread safety, TTL checks, and capacity caps.
    """

    def __getitem__(self, key: str) -> Dict[str, Any]:
        val = get_interview_session(key)
        if val is None:
            raise KeyError(key)
        return val

    def __setitem__(self, key: str, value: Dict[str, Any]) -> None:
        save_interview_session(key, value)

    def __contains__(self, key: object) -> bool:
        if not isinstance(key, str):
            return False
        return get_interview_session(key) is not None

    def get(self, key: str, default: Any = None) -> Any:
        val = get_interview_session(key)
        return val if val is not None else default

    def pop(self, key: str, default: Any = None) -> Any:
        return pop_interview_session(key, default)

    def __len__(self) -> int:
        with _store_lock:
            _purge_expired_locked()
            return len(_raw_sessions)


INTERVIEW_SESSIONS = SessionStore()


def _purge_expired_locked() -> None:
    now = time.time()
    expired_keys = [
        sid
        for sid, data in _raw_sessions.items()
        if now - data.get("_created_at", now) > SESSION_TTL_SECONDS
    ]
    for sid in expired_keys:
        _raw_sessions.pop(sid, None)


def save_interview_session(session_id: str, data: Dict[str, Any]) -> None:
    """Stores or updates an interview session with timestamp tracking and capacity eviction."""
    with _store_lock:
        _purge_expired_locked()
        if "_created_at" not in data:
            data["_created_at"] = time.time()
        data["_updated_at"] = time.time()

        if session_id in _raw_sessions:
            _raw_sessions.move_to_end(session_id)
        _raw_sessions[session_id] = data

        while len(_raw_sessions) > MAX_SESSIONS:
            _raw_sessions.popitem(last=False)


def get_interview_session(session_id: str) -> Optional[Dict[str, Any]]:
    """Retrieves an interview session if present and not expired."""
    with _store_lock:
        data = _raw_sessions.get(session_id)
        if data is None:
            return None

        if time.time() - data.get("_created_at", 0) > SESSION_TTL_SECONDS:
            _raw_sessions.pop(session_id, None)
            return None

        data["_updated_at"] = time.time()
        _raw_sessions.move_to_end(session_id)
        return data


def pop_interview_session(session_id: str, default: Any = None) -> Any:
    """Pops and deletes a completed session from memory."""
    with _store_lock:
        return _raw_sessions.pop(session_id, default)


def cleanup_expired_sessions() -> int:
    """Purges expired sessions and returns count of removed entries."""
    with _store_lock:
        before = len(_raw_sessions)
        _purge_expired_locked()
        return before - len(_raw_sessions)