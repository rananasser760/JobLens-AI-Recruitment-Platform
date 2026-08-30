from typing import Dict, Any
import collections

ACTIVE_PROCESSORS: Dict[int, Any] = {}

LOG_BUFFERS: Dict[str, collections.deque] = {}
LOG_SUBSCRIBERS: Dict[str, list] = {}

def push_log(alert_type: str, details: dict, session_id=None) -> None:
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

def get_processor(db_session_id: int):
    return ACTIVE_PROCESSORS.get(db_session_id)