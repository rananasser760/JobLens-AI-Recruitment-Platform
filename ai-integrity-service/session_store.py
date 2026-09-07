from typing import Dict, Any, Optional
import collections
import threading

# Guard thread-safe access to global dictionaries
_PROCESSORS_LOCK = threading.Lock()
ACTIVE_PROCESSORS: Dict[Any, Any] = {}

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


def get_processor(db_session_id: Any) -> Optional[Any]:
    with _PROCESSORS_LOCK:
        proc = ACTIVE_PROCESSORS.get(db_session_id)
        if proc is None and str(db_session_id).isdigit():
            proc = ACTIVE_PROCESSORS.get(int(db_session_id))
        return proc


def set_processor(db_session_id: Any, processor: Any) -> None:
    with _PROCESSORS_LOCK:
        ACTIVE_PROCESSORS[db_session_id] = processor
        if str(db_session_id).isdigit():
            ACTIVE_PROCESSORS[int(db_session_id)] = processor


def pop_processor(db_session_id: Any) -> Optional[Any]:
    with _PROCESSORS_LOCK:
        proc = ACTIVE_PROCESSORS.pop(db_session_id, None)
        if str(db_session_id).isdigit():
            proc2 = ACTIVE_PROCESSORS.pop(int(db_session_id), None)
            proc = proc or proc2
        return proc