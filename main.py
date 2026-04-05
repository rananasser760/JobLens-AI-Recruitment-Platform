# ══════════════════════════════════════════════════════════════════════════════
#  main.py  —  JobLens AI  unified entry point
#  Runs both the AI-interview service and the integrity-monitoring service
#  on a single FastAPI app / single port.
#
#  Start:
#      uvicorn main:app --host 0.0.0.0 --port 8000 --reload
# ══════════════════════════════════════════════════════════════════════════════

import asyncio
import json
import os

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles

from routers.integrity import router as integrity_router
from routers.interview  import router as interview_router
from models             import DBSession, DBSessionLocal
from session_store      import LOG_BUFFERS, LOG_SUBSCRIBERS

# ── App ───────────────────────────────────────────────────────────────────────
app = FastAPI(
    title="JobLens AI",
    description="Unified AI interview + integrity monitoring platform",
    version="2.0.0",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# ── Routers ───────────────────────────────────────────────────────────────────
app.include_router(integrity_router)   # /api/sessions/…  /api/ws/…  /api/dashboard/…
app.include_router(interview_router)   # /interview/start  /interview/ws/…

# ── Static files (templates served by the UI layer) ──────────────────────────
if os.path.isdir("static"):
    app.mount("/static", StaticFiles(directory="static"), name="static")


# ══════════════════════════════════════════════════════════════════════════════
#  Live log WebSocket  (shared between both services)
#  ws://host/ws/logs/{session_id}
# ══════════════════════════════════════════════════════════════════════════════

@app.websocket("/ws/logs/{session_id}")
async def ws_logs(websocket: WebSocket, session_id: int):
    await websocket.accept()
    sid = str(session_id)
    q: asyncio.Queue = asyncio.Queue()

    LOG_SUBSCRIBERS.setdefault(sid, []).append(q)
    try:
        for entry in list(LOG_BUFFERS.get(sid, [])):
            await websocket.send_json(entry)

        while True:
            try:
                entry = await asyncio.wait_for(q.get(), timeout=30.0)
                await websocket.send_json(entry)
            except asyncio.TimeoutError:
                try: await websocket.send_json({"ping": True})
                except: break
            except WebSocketDisconnect: break
            except Exception: break
    except WebSocketDisconnect: pass
    finally:
        try: LOG_SUBSCRIBERS[sid].remove(q)
        except (KeyError, ValueError): pass


@app.get("/api/logs/{session_id}")
def get_logs(session_id: int):
    return list(LOG_BUFFERS.get(str(session_id), []))


# ══════════════════════════════════════════════════════════════════════════════
#  Unified report  —  GET /api/report/{db_session_id}
#  Merges integrity data (DB) + interview data (in-memory) into one payload.
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/api/report/{session_id}")
def unified_report(session_id: int):
    """
    Single endpoint the HR dashboard calls to get everything about a session.

    Returns:
        candidate        — name / id
        integrity        — cheating score, alerts, timeline, keyframes
        interview        — AI-generated summary, score, conversation history
        recommendation   — combined verdict
    """
    from fastapi import HTTPException
    from session_store import INTERVIEW_SESSIONS

    db = DBSessionLocal()
    try:
        s = db.query(DBSession).filter(DBSession.id == session_id).first()
        if not s:
            raise HTTPException(status_code=404, detail="Session not found")

        # ── integrity side ────────────────────────────────────────────────
        integrity_bd: dict = {}
        for a in s.alerts:
            integrity_bd[a.alert_type] = integrity_bd.get(a.alert_type, 0) + 1

        yolo_bd: dict = {}
        for ya in s.yolo_alerts:
            yolo_bd[ya.alert_type] = yolo_bd.get(ya.alert_type, 0) + 1

        timeline = []
        seen = set()
        for a in sorted(s.alerts, key=lambda x: x.elapsed_secs):
            kf = next((k for k in s.keyframes
                       if abs(k.elapsed_secs - a.elapsed_secs) < 0.5
                       and k.alert_type == a.alert_type), None)
            key = (round(a.elapsed_secs, 1), a.alert_type)
            if key not in seen:
                seen.add(key)
                timeline.append({
                    "alert_type":   a.alert_type,
                    "elapsed_secs": a.elapsed_secs,
                    "timestamp":    a.timestamp.isoformat(),
                    "keyframe_id":  kf.id if kf else None,
                    "has_keyframe": kf is not None,
                    "source":       "mediapipe",
                })
        for ya in sorted(s.yolo_alerts, key=lambda x: x.elapsed_secs):
            timeline.append({
                "alert_type":   ya.alert_type,
                "elapsed_secs": ya.elapsed_secs,
                "timestamp":    ya.timestamp.isoformat(),
                "keyframe_id":  None,
                "has_keyframe": False,
                "source":       "yolo",
            })
        timeline.sort(key=lambda x: x["elapsed_secs"])

        score_history = [
            {"timestamp": sh.timestamp.isoformat(), "score": sh.score}
            for sh in s.score_history
        ]

        # ── interview side ────────────────────────────────────────────────
        interview_summary  = None
        interview_score    = s.interview_score
        interview_history  = []

        # Try in-memory first (session still live)
        if s.interview_session_id and s.interview_session_id in INTERVIEW_SESSIONS:
            iv = INTERVIEW_SESSIONS[s.interview_session_id]
            interview_summary = iv.get("summary")
            interview_history = iv.get("history", [])
        elif s.interview_summary_json:
            try:
                interview_summary = json.loads(s.interview_summary_json)
            except Exception:
                pass

        # ── combined recommendation ───────────────────────────────────────
        cheating_score  = s.final_score or 0.0
        int_rec         = s.recommendation or "PENDING"

        combined_rec = _combined_recommendation(
            cheating_score=cheating_score,
            cheating_rec=int_rec,
            interview_score=interview_score,
        )

        return {
            "session_id":       session_id,
            "started_at":       s.started_at.isoformat() if s.started_at else None,
            "ended_at":         s.ended_at.isoformat()   if s.ended_at   else None,
            "duration_seconds": s.duration_seconds,

            "candidate": {
                "name": s.candidate_name,
                "id":   s.candidate_id,
            },

            "integrity": {
                "cheating_score":     cheating_score,
                "recommendation":     int_rec,
                "total_alerts":       len(s.alerts),
                "alert_breakdown":    integrity_bd,
                "yolo_breakdown":     yolo_bd,
                "total_yolo_alerts":  len(s.yolo_alerts),
                "timeline":           timeline,
                "score_history":      score_history,
            },

            "interview": {
                "interview_session_id": s.interview_session_id,
                "score":                interview_score,
                "summary":              interview_summary,
                "history":              interview_history,
            },

            "combined_recommendation": combined_rec,
        }
    finally:
        db.close()


def _combined_recommendation(
    cheating_score: float,
    cheating_rec: str,
    interview_score,
) -> dict:
    """
    Simple rule-based combiner.
    Extend this with your own business logic as needed.
    """
    if cheating_rec == "ABANDONED":
        return {"verdict": "ABANDONED", "reason": "Candidate left the session"}

    if cheating_rec == "REJECT" or cheating_score >= 70:
        return {"verdict": "REJECT",
                "reason": f"High cheating score ({cheating_score:.0f}%)"}

    if interview_score is None:
        return {"verdict": cheating_rec,
                "reason": "Interview not completed yet"}

    if interview_score >= 70 and cheating_rec == "ACCEPT":
        return {"verdict": "ACCEPT",
                "reason": f"Strong interview ({interview_score:.0f}%) + clean session"}

    if interview_score < 40:
        return {"verdict": "REJECT",
                "reason": f"Weak interview performance ({interview_score:.0f}%)"}

    return {"verdict": "REVIEW",
            "reason": f"Interview {interview_score:.0f}% / Cheating {cheating_score:.0f}% — manual review"}


# ── Dev entry point ───────────────────────────────────────────────────────────
if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)