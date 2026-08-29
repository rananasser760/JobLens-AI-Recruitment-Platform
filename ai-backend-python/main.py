# ══════════════════════════════════════════════════════════════════════════════
#  main.py  —  JobLens AI  unified entry point
#  Runs both the AI-interview service and the integrity-monitoring service
#  on a single FastAPI app / single port.
#
#  Start:
#      python main.py
# ══════════════════════════════════════════════════════════════════════════════

import asyncio
import json
import os
import sys
import subprocess
import atexit
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse, JSONResponse
from dotenv import load_dotenv

load_dotenv()

RUNTIME_ENV = os.getenv("JOBLENS_ENV", "development").strip().lower()
INTERNAL_API_KEY = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()

_public_routes_flag = os.getenv("JOBLENS_ENABLE_PUBLIC_ROUTES")
if _public_routes_flag is None:
    ALLOW_PUBLIC_ROUTES = RUNTIME_ENV in {"development", "dev", "local", "test"}
else:
    ALLOW_PUBLIC_ROUTES = _public_routes_flag.strip().lower() == "true"

from routers.integrity import router as integrity_router
from routers.interview  import router as interview_router
from routers.internal_api import router as internal_router
from routers.recruitment import router as recruitment_router
from models             import DBSession, DBSessionLocal
from session_store      import LOG_BUFFERS, LOG_SUBSCRIBERS
from recruitment.config import get_recruitment_settings
from recruitment.scheduler import scheduler_status, start_scheduler, stop_scheduler
from recruitment.vector_store import store
from request_context import set_request_id

# ── App ───────────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    _validate_internal_key_configuration()

    try:
        app.state.recruitment_scheduler = start_scheduler()
    except Exception as exc:
        app.state.recruitment_scheduler = {
            "enabled": False,
            "running": False,
            "error": str(exc),
        }

    yield

    try:
        stop_scheduler()
    except Exception:
        pass


def _docs_enabled() -> bool:
    return os.getenv("JOBLENS_ENABLE_DOCS", "true").strip().lower() == "true"


def _validate_internal_key_configuration() -> None:
    is_non_production = RUNTIME_ENV in {"development", "dev", "local", "test"}
    if not is_non_production and not INTERNAL_API_KEY:
        raise RuntimeError("JOBLENS_INTERNAL_API_KEY must be configured outside development environments")


def _is_websocket_authorized(websocket: WebSocket) -> bool:
    if not INTERNAL_API_KEY:
        return True

    provided = (websocket.headers.get("x-api-key") or websocket.query_params.get("api_key") or "").strip()
    return provided == INTERNAL_API_KEY


app = FastAPI(
    title="JobLens AI",
    description="Unified interview, integrity, CV parsing, scraping, and matching platform",
    version="2.0.0",
    docs_url="/docs" if _docs_enabled() else None,
    redoc_url="/redoc" if _docs_enabled() else None,
    openapi_url="/openapi.json" if _docs_enabled() else None,
    lifespan=lifespan,
)

cors_origins = [
    origin.strip()
    for origin in os.getenv("JOBLENS_CORS_ORIGINS", "http://localhost:4200,http://localhost:5245").split(",")
    if origin.strip()
] or ["http://localhost:4200", "http://localhost:5245"]

app.add_middleware(
    CORSMiddleware,
    allow_origins=cors_origins,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    correlation_id = request.headers.get("X-Correlation-Id", "").strip() or uuid.uuid4().hex
    request.state.correlation_id = correlation_id
    set_request_id(correlation_id)

    response = await call_next(request)
    response.headers["X-Correlation-Id"] = correlation_id
    return response


@app.middleware("http")
async def internal_api_key_guard(request: Request, call_next):
    if INTERNAL_API_KEY:
        path = request.url.path
        protected = path.startswith("/internal/v1/")
        if ALLOW_PUBLIC_ROUTES:
            protected = protected or path.startswith("/api/") or path.startswith("/interview/")

        if protected:
            provided_key = request.headers.get("X-API-Key", "")
            if provided_key != INTERNAL_API_KEY:
                return JSONResponse(
                    status_code=401,
                    content={"detail": "Invalid or missing X-API-Key"},
                )

    return await call_next(request)

# ── Routers ───────────────────────────────────────────────────────────────────
if ALLOW_PUBLIC_ROUTES:
    app.include_router(integrity_router)   # /api/sessions/…  /api/ws/…  /api/dashboard/…
    app.include_router(interview_router)   # /interview/start  /interview/ws/…
    app.include_router(recruitment_router) # /api/cv/… /api/scraping/… /api/recommendations/…

app.include_router(internal_router)        # /internal/v1/*


def _ensure_public_routes_enabled() -> None:
    if not ALLOW_PUBLIC_ROUTES:
        raise HTTPException(status_code=404, detail="Not found")

# ── Static files (templates served by the UI layer) ──────────────────────────
if os.path.isdir("static"):
    app.mount("/static", StaticFiles(directory="static"), name="static")


# ══════════════════════════════════════════════════════════════════════════════
#  Live log WebSocket  (shared between both services)
#  ws://host/ws/logs/{session_id}
# ══════════════════════════════════════════════════════════════════════════════

@app.websocket("/ws/logs/{session_id}")
async def ws_logs(websocket: WebSocket, session_id: int):
    if not ALLOW_PUBLIC_ROUTES:
        await websocket.close(code=1008)
        return

    if not _is_websocket_authorized(websocket):
        await websocket.accept()
        await websocket.close(code=1008)
        return

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
    _ensure_public_routes_enabled()
    return list(LOG_BUFFERS.get(str(session_id), []))


# ══════════════════════════════════════════════════════════════════════════════
#  Unified report  —  GET /api/report/{db_session_id}
#  Merges integrity data (DB) + interview data (in-memory) into one payload.
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/api/report/{session_id}")
def unified_report(session_id: int):
    from session_store import INTERVIEW_SESSIONS

    _ensure_public_routes_enabled()

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

            "final_score":       cheating_score,
            "recommendation":    int_rec,
            "alert_breakdown":   integrity_bd,
            "yolo_alert_breakdown": yolo_bd,
            "total_alerts":      len(s.alerts),
            "total_yolo_alerts": len(s.yolo_alerts),
            "score_history":     score_history,
            "timeline_events":   timeline,
            "interview_summary": interview_summary,
            "interview_score":   interview_score,

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


@app.get("/health")
def health():
    settings = get_recruitment_settings()
    provider = settings.provider.lower().strip()

    if provider == "groq":
        llm_ok = bool(settings.groq_api_key)
    else:
        llm_ok = bool(settings.openrouter_api_key)

    services = {
        "llm": "configured" if llm_ok else "missing_api_key",
        "scheduler": scheduler_status(),
    }

    status = "healthy"
    try:
        vector_stats = store.stats()
        services["chromadb"] = "connected"
        services["vector_store"] = vector_stats
    except Exception as exc:
        services["chromadb"] = "error"
        services["chromadb_error"] = str(exc)
        status = "degraded"

    return {
        "status": status,
        "version": app.version,
        "environment": RUNTIME_ENV,
        "publicRoutesEnabled": ALLOW_PUBLIC_ROUTES,
        "services": services,
    }


@app.get("/")
def serve_home():
    """Serves the main frontend UI."""
    return FileResponse("index.html")

# ── Dev entry point & Subprocess Launcher ─────────────────────────────────────

def launch_mcq_server():
    """Spawns the MCQ server as a background subprocess before starting uvicorn."""
    mcq_dir = os.path.join(os.getcwd(), "Pre-Interview MCQ Assessment")
    if not os.path.exists(mcq_dir):
        print("[System] Warning: 'Pre-Interview MCQ Assesment' directory not found. MCQ server skipped.")
        return

    print("[System] Starting Pre-Interview MCQ server on port 8001...")
    
    # We use sys.executable to guarantee the MCQ server runs using your active virtual environment
    mcq_process = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001"],
        cwd=mcq_dir
    )
    
    # Cleanly terminate the subprocess when you press Ctrl+C to stop main.py
    def cleanup():
        print("\n[System] Shutting down MCQ server...")
        mcq_process.terminate()
        mcq_process.wait()
        
    atexit.register(cleanup)


if __name__ == "__main__":
    import uvicorn
    
    # Launch the background microservice
    launch_mcq_server()
    
    # Start the primary JobLens server
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)