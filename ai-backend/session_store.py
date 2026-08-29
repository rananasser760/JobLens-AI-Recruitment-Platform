# ══════════════════════════════════════════════════════════════════════════════
#  session_store.py
#  Shared in-memory state between the interview and integrity routers.
#  Both routers import from here — never from each other.
# ══════════════════════════════════════════════════════════════════════════════

from typing import Dict, Any, Optional
import collections


# ── Interview sessions ────────────────────────────────────────────────────────
# Key  : session_id  (str UUID, set by interview router at /interview/start)
# Value: dict with keys:
#   cv_text       str
#   history       list[{role, content}]
#   turn_count    int
#   max_questions int
#   criteria      str
#   summary       dict | None          ← filled when interview ends
#   integrity_id  int | None           ← FK to DBSession.id (set after integrity starts)
INTERVIEW_SESSIONS: Dict[str, Dict[str, Any]] = {}


# ── Integrity processors ──────────────────────────────────────────────────────
# Key  : db_session_id  (int, primary key from DBSession)
# Value: WebSessionProcessor instance
ACTIVE_PROCESSORS: Dict[int, Any] = {}


# ── Live log buffers (used by /ws/logs and /api/logs) ────────────────────────
# Key  : str(session_id)  or "global"
# Value: deque[dict]  — each dict: {ts, alert_type, details, session_id}
LOG_BUFFERS: Dict[str, collections.deque] = {}
LOG_SUBSCRIBERS: Dict[str, list] = {}   # list of asyncio.Queue


def push_log(alert_type: str, details: dict, session_id=None) -> None:
    """
    Append a log entry and fan it out to any live WebSocket subscribers.
    Called from both routers so it lives here.
    """
    from datetime import datetime
    entry = {
        "ts"         : datetime.now().strftime("%H:%M:%S"),
        "alert_type" : alert_type,
        "details"    : details,
        "session_id" : session_id,
    }
    sid = str(session_id) if session_id else "global"
    if sid not in LOG_BUFFERS:
        LOG_BUFFERS[sid] = collections.deque(maxlen=200)
    LOG_BUFFERS[sid].append(entry)

    for q in LOG_SUBSCRIBERS.get(sid, []):
        try:
            q.put_nowait(entry)
        except Exception:
            pass


# ── Helpers ───────────────────────────────────────────────────────────────────

def get_interview_session(session_id: str) -> Optional[Dict[str, Any]]:
    return INTERVIEW_SESSIONS.get(session_id)


def get_processor(db_session_id: int):
    return ACTIVE_PROCESSORS.get(db_session_id)


def link_integrity_to_interview(interview_sid: str, db_session_id: int) -> None:
    """
    Store the DB session id on the interview session so the unified report
    endpoint can pull integrity data for the same candidate.
    """
    sess = INTERVIEW_SESSIONS.get(interview_sid)
    if sess is not None:
        sess["integrity_id"] = db_session_id
