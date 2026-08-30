from typing import Dict, Any

# ── Interview sessions ────────────────────────────────────────────────────────
# Key  : session_id  (str UUID, set by interview router at /interview/start)
INTERVIEW_SESSIONS: Dict[str, Dict[str, Any]] = {}

def get_interview_session(session_id: str) -> Dict[str, Any] | None:
    return INTERVIEW_SESSIONS.get(session_id)