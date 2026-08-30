# Repository Snapshot

This document contains the directory structure and file contents of the project. It is formatted specifically for AI context ingestion.

## Directory Structure
```text
📁 ai-integrity-service/
    📄 .env
    📄 .env.example
    📄 Dockerfile
    📄 main.py
    📄 models.py
    📄 request_context.py
    📄 requirements.txt
    📄 session_store.py
    📁 routers/
        📄 integrity.py
        📄 internal_api.py
        📄 recruitment.py
        📄 __init__.py
```

## File Contents

### `Dockerfile`
```text
FROM python:3.11-slim

WORKDIR /app

RUN apt-get update && apt-get install -y \
    build-essential \
    libgl1 \
    libglib2.0-0 \
    curl \
    gnupg \
    unixodbc-dev \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y msodbcsql17 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-build-isolation uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,target=/root/.cache/uv \
    uv pip install --system --no-build-isolation -r requirements.txt

RUN playwright install chromium --with-deps

COPY . .

EXPOSE 8000 8001

CMD ["python", "main.py"]
```

### `main.py`
```py
# ══════════════════════════════════════════════════════════════════════════════
#  main.py  —  JobLens AI Recruitment & Integrity Service
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
    title="JobLens AI - Recruitment Service",
    description="Integrity, CV parsing, scraping, and matching platform",
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
            protected = protected or path.startswith("/api/")

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
    app.include_router(recruitment_router) # /api/cv/… /api/scraping/… /api/recommendations/…

app.include_router(internal_router)        # /internal/v1/*


def _ensure_public_routes_enabled() -> None:
    if not ALLOW_PUBLIC_ROUTES:
        raise HTTPException(status_code=404, detail="Not found")

# ── Static files (templates served by the UI layer) ──────────────────────────
if os.path.isdir("static"):
    app.mount("/static", StaticFiles(directory="static"), name="static")


# ══════════════════════════════════════════════════════════════════════════════
#  Live log WebSocket 
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
# ══════════════════════════════════════════════════════════════════════════════

@app.get("/api/report/{session_id}")
def unified_report(session_id: int):
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

        if s.interview_summary_json:
            try:
                interview_summary = json.loads(s.interview_summary_json)
                if isinstance(interview_summary, dict) and "history" in interview_summary:
                    interview_history = interview_summary["history"]
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
    
    mcq_process = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001"],
        cwd=mcq_dir
    )
    
    def cleanup():
        print("\n[System] Shutting down MCQ server...")
        mcq_process.terminate()
        mcq_process.wait()
        
    atexit.register(cleanup)


if __name__ == "__main__":
    import uvicorn
    
    launch_mcq_server()
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)
```

### `models.py`
```py
# ══════════════════════════════════════════════════════════════════════════════
#  models.py
#  Single source of truth for every DB table.
#  Both routers import engine / DBSessionLocal / all model classes from here.
# ══════════════════════════════════════════════════════════════════════════════

import os
from datetime import datetime
from urllib.parse import quote_plus, unquote_plus

from sqlalchemy import (
    create_engine, Column, Integer, Float, String,
    DateTime, ForeignKey, Text, LargeBinary, inspect, text
)
from sqlalchemy.orm import declarative_base, sessionmaker, relationship

# ── Engine ────────────────────────────────────────────────────────────────────
DEFAULT_SQLSERVER_CONNECTION_STRING = (
    r"Server=(localdb)\MSSQLLocalDB;Database=GPAi;Integrated Security=True;TrustServerCertificate=True;"
)


def _build_database_url() -> str:
    configured_url = os.getenv("AI_DATABASE_URL")
    if configured_url:
        return configured_url

    sql_server_connection_string = os.getenv(
        "AI_SQLSERVER_CONNECTION_STRING",
        DEFAULT_SQLSERVER_CONNECTION_STRING,
    ).replace("\\\\", "\\")

    normalized_connection_string = sql_server_connection_string
    lowered = normalized_connection_string.lower()
    normalized_connection_string = normalized_connection_string.replace(
        "Integrated Security=True",
        "Trusted_Connection=yes",
    ).replace(
        "Integrated Security=true",
        "Trusted_Connection=yes",
    )

    if "trustservercertificate" not in lowered:
        normalized_connection_string = f"{normalized_connection_string.rstrip(';')};TrustServerCertificate=yes;"
    else:
        normalized_connection_string = normalized_connection_string.replace(
            "TrustServerCertificate=True",
            "TrustServerCertificate=yes",
        ).replace(
            "TrustServerCertificate=true",
            "TrustServerCertificate=yes",
        )

    if "encrypt" not in normalized_connection_string.lower():
        normalized_connection_string = f"{normalized_connection_string.rstrip(';')};Encrypt=no;"

    odbc_driver = os.getenv("AI_SQLSERVER_ODBC_DRIVER", "ODBC Driver 17 for SQL Server")
    odbc_connection = f"Driver={{{odbc_driver}}};{normalized_connection_string}"
    return f"mssql+pyodbc:///?odbc_connect={quote_plus(odbc_connection)}"


def _get_sql_server_database_name(database_url: str) -> str | None:
    marker = "odbc_connect="
    marker_index = database_url.find(marker)
    decoded_connection = database_url
    if marker_index != -1:
        encoded_connection = database_url[marker_index + len(marker):]
        decoded_connection = unquote_plus(encoded_connection)

    prefix = "database="
    for segment in decoded_connection.split(";"):
        if segment.strip().lower().startswith(prefix):
            return segment.split("=", 1)[1].strip()
    return None


def _build_master_database_url(database_url: str) -> str:
    decoded = database_url
    marker = "odbc_connect="
    marker_index = decoded.find(marker)
    if marker_index == -1:
        return database_url

    encoded_connection = decoded[marker_index + len(marker):]
    connection_string = unquote_plus(encoded_connection)
    parts = [part for part in connection_string.split(";") if part]
    rebuilt_parts: list[str] = []
    has_database = False
    for part in parts:
        if part.lower().startswith("database="):
            rebuilt_parts.append("Database=master")
            has_database = True
        else:
            rebuilt_parts.append(part)

    if not has_database:
        rebuilt_parts.append("Database=master")

    master_connection_string = ";".join(rebuilt_parts) + ";"
    return f"mssql+pyodbc:///?odbc_connect={quote_plus(master_connection_string)}"


def _ensure_sql_server_database_exists(database_url: str) -> None:
    database_name = _get_sql_server_database_name(database_url)
    if not database_name:
        return

    master_engine = create_engine(_build_master_database_url(database_url), pool_pre_ping=True)
    try:
        with master_engine.connect().execution_options(isolation_level="AUTOCOMMIT") as connection:
            exists_query = text("SELECT COUNT(1) FROM sys.databases WHERE name = :db_name")
            exists = connection.execute(exists_query, {"db_name": database_name}).scalar_one()
            if not exists:
                connection.execute(text(f"CREATE DATABASE [{database_name}]"))
    finally:
        master_engine.dispose()


DATABASE_URL = _build_database_url()
_ensure_sql_server_database_exists(DATABASE_URL)
engine = create_engine(DATABASE_URL, pool_pre_ping=True)
DBSessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)
Base = declarative_base()


# ══════════════════════════════════════════════════════════════════════════════
#  Tables
# ══════════════════════════════════════════════════════════════════════════════

class DBSession(Base):
    """One monitoring session per candidate sitting."""
    __tablename__ = "sessions"

    id                = Column(Integer, primary_key=True, index=True)
    started_at        = Column(DateTime, default=datetime.utcnow)
    ended_at          = Column(DateTime, nullable=True)
    final_score       = Column(Float,   default=0.0)
    recommendation    = Column(String(50), default="PENDING")
    duration_seconds  = Column(Float,   default=0.0)

    # ── Candidate profile (Feature 2) ─────────────────────────────────────
    candidate_name    = Column(String(200), nullable=True)
    candidate_id      = Column(String(100), nullable=True)

    # ── Interview link ─────────────────────────────────────────────────────
    # UUID from INTERVIEW_SESSIONS dict — nullable because integrity-only
    # sessions (no AI interview) are still valid.
    interview_session_id = Column(String(100), nullable=True, index=True)

    # ── Interview results (denormalised for fast report queries) ───────────
    interview_score       = Column(Float,   nullable=True)   # 0-100
    interview_summary_json = Column(Text,   nullable=True)   # JSON blob

    # ── Relationships ──────────────────────────────────────────────────────
    alerts        = relationship("DBAlert",        back_populates="session",
                                 cascade="all, delete-orphan")
    score_history = relationship("DBScoreHistory", back_populates="session",
                                 cascade="all, delete-orphan")
    yolo_alerts   = relationship("DBYoloAlert",    back_populates="session",
                                 cascade="all, delete-orphan")
    keyframes     = relationship("DBKeyframe",     back_populates="session",
                                 cascade="all, delete-orphan")


class DBAlert(Base):
    """MediaPipe-sourced alerts (gaze, head-pose, eye-movement, no-face …)."""
    __tablename__ = "alerts"

    id            = Column(Integer, primary_key=True, index=True)
    session_id    = Column(Integer, ForeignKey("sessions.id"))
    alert_type    = Column(String(100))
    timestamp     = Column(DateTime, default=datetime.utcnow)
    elapsed_secs  = Column(Float, default=0.0)
    metadata_json = Column(Text)
    session       = relationship("DBSession", back_populates="alerts")


class DBYoloAlert(Base):
    """YOLO-sourced alerts (multiple people, mobile phone …)."""
    __tablename__ = "yolo_alerts"

    id           = Column(Integer, primary_key=True, index=True)
    session_id   = Column(Integer, ForeignKey("sessions.id"))
    alert_type   = Column(String(100))
    timestamp    = Column(DateTime, default=datetime.utcnow)
    elapsed_secs = Column(Float, default=0.0)
    details_json = Column(Text)
    session      = relationship("DBSession", back_populates="yolo_alerts")


class DBScoreHistory(Base):
    """Time-series suspicion score snapshots (sampled every ~30 frames)."""
    __tablename__ = "score_history"

    id         = Column(Integer, primary_key=True, index=True)
    session_id = Column(Integer, ForeignKey("sessions.id"))
    timestamp  = Column(DateTime, default=datetime.utcnow)
    score      = Column(Float)
    session    = relationship("DBSession", back_populates="score_history")


class DBKeyframe(Base):
    """Blurred JPEG thumbnails captured at alert events (Feature 1)."""
    __tablename__ = "keyframes"

    id           = Column(Integer, primary_key=True, index=True)
    session_id   = Column(Integer, ForeignKey("sessions.id"))
    alert_type   = Column(String(100))
    timestamp    = Column(DateTime, default=datetime.utcnow)
    elapsed_secs = Column(Float, default=0.0)
    image_data   = Column(LargeBinary, nullable=True)
    session      = relationship("DBSession", back_populates="keyframes")


# ══════════════════════════════════════════════════════════════════════════════
#  Create all tables
# ══════════════════════════════════════════════════════════════════════════════

Base.metadata.create_all(bind=engine)


# ══════════════════════════════════════════════════════════════════════════════
#  Auto-migration  (adds columns that older DB files are missing)
# ══════════════════════════════════════════════════════════════════════════════

def run_migrations() -> None:
    # (table, column, sql_type)
    migrations = [
        ("sessions", "candidate_name", "NVARCHAR(200)"),
        ("sessions", "candidate_id", "NVARCHAR(100)"),
        ("sessions", "interview_session_id", "NVARCHAR(100)"),
        ("sessions", "interview_score", "FLOAT"),
        ("sessions", "interview_summary_json", "NVARCHAR(MAX)"),
        ("alerts", "elapsed_secs", "FLOAT"),
        ("yolo_alerts", "elapsed_secs", "FLOAT"),
    ]

    try:
        with engine.begin() as connection:
            inspector = inspect(connection)
            existing_tables = set(inspector.get_table_names())

            for table, column, definition in migrations:
                if table not in existing_tables:
                    continue

                existing_columns = {row["name"] for row in inspector.get_columns(table)}
                if column not in existing_columns:
                    connection.execute(text(f"ALTER TABLE {table} ADD {column} {definition}"))
                    print(f"[migration] added '{column}' to '{table}'")

            if "keyframes" not in existing_tables:
                DBKeyframe.__table__.create(bind=connection, checkfirst=True)
                print("[migration] ensured 'keyframes' table")

        print("[migration] complete")

    except Exception as exc:
        print(f"[migration] warning: {exc}")


run_migrations()
```

### `request_context.py`
```py
from contextvars import ContextVar

_request_id_ctx: ContextVar[str] = ContextVar("request_id", default="")


def set_request_id(value: str) -> None:
    _request_id_ctx.set((value or "").strip())


def get_request_id() -> str:
    return (_request_id_ctx.get() or "").strip()
```

### `requirements.txt`
```txt
fastapi
uvicorn
python-multipart
requests
websockets
python-dotenv
sqlalchemy
pyodbc
openai
opencv-python-headless
mediapipe==0.10.9
protobuf==3.20.3
ultralytics
numpy<2.0
chromadb
playwright
playwright-stealth
sentence-transformers>=2.7.0
scikit-learn
PyMuPDF
python-docx
pillow
docling
textstat
transformers
apscheduler
pyngrok
PyYAML>=6.0.1
```

### `session_store.py`
```py
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
```

### `routers/integrity.py`
```py
# ══════════════════════════════════════════════════════════════════════════════
#  routers/integrity.py
#  All camera / cheating-detection endpoints.
#  Ported from joblens_main.py — imports shared state from session_store.py
#  and DB models from models.py.
# ══════════════════════════════════════════════════════════════════════════════

import asyncio
import base64
import json
import math
import os
import sys
import threading
import time
import queue # Added for WebSocket frame routing
from datetime import datetime
from typing import Optional

# Dynamically add the root folder to the Python path
current_dir = os.path.dirname(os.path.abspath(__file__))
parent_dir = os.path.dirname(current_dir)
if parent_dir not in sys.path:
    sys.path.append(parent_dir)

import cv2
import mediapipe as mp
import numpy as np

# Pre-load MediaPipe solutions in the main thread
mp_face_mesh = mp.solutions.face_mesh
mp_drawing = mp.solutions.drawing_utils
mp_drawing_styles = mp.solutions.drawing_styles

from fastapi import APIRouter, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, PlainTextResponse, Response
from pydantic import BaseModel

from models import (
    DBSession, DBAlert, DBYoloAlert, DBScoreHistory, DBKeyframe,
    DBSessionLocal,
)
from session_store import ACTIVE_PROCESSORS, push_log

router = APIRouter(prefix="/api", tags=["integrity"])

# Shared state guards for concurrent API/thread access.
_ACTIVE_PROCESSORS_LOCK = threading.Lock()
_WS_CONNECTIONS_LOCK = threading.Lock()
_WS_CONNECTIONS: dict[int, int] = {}


def _active_get(session_id: int):
    with _ACTIVE_PROCESSORS_LOCK:
        return ACTIVE_PROCESSORS.get(session_id)


def _active_set(session_id: int, processor) -> None:
    with _ACTIVE_PROCESSORS_LOCK:
        ACTIVE_PROCESSORS[session_id] = processor


def _active_pop(session_id: int):
    with _ACTIVE_PROCESSORS_LOCK:
        return ACTIVE_PROCESSORS.pop(session_id, None)


def _ws_inc(session_id: int) -> None:
    with _WS_CONNECTIONS_LOCK:
        _WS_CONNECTIONS[session_id] = _WS_CONNECTIONS.get(session_id, 0) + 1


def _ws_dec(session_id: int) -> int:
    with _WS_CONNECTIONS_LOCK:
        remaining = max(0, _WS_CONNECTIONS.get(session_id, 0) - 1)
        if remaining == 0:
            _WS_CONNECTIONS.pop(session_id, None)
        else:
            _WS_CONNECTIONS[session_id] = remaining
        return remaining


def _ws_count(session_id: int) -> int:
    with _WS_CONNECTIONS_LOCK:
        return _WS_CONNECTIONS.get(session_id, 0)


def _is_ws_authorized(websocket: WebSocket) -> bool:
    expected_key = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()
    if not expected_key:
        return True

    provided_key = (websocket.headers.get("x-api-key") or websocket.query_params.get("api_key") or "").strip()
    return provided_key == expected_key


# ══════════════════════════════════════════════════════════════════════════════
#  CONFIG
# ══════════════════════════════════════════════════════════════════════════════

class Config:
    FRAME_WIDTH  = 640
    FRAME_HEIGHT = 480
    FPS          = 30

    MIN_DETECTION_CONFIDENCE = 0.5
    MIN_TRACKING_CONFIDENCE  = 0.5
    MAX_NUM_FACES    = 2
    REFINE_LANDMARKS = True

    GAZE_YAW_THRESHOLD       = 20
    GAZE_PITCH_UP_THRESHOLD  = 18
    GAZE_PITCH_DOWN_THRESHOLD= 20

    HEAD_YAW_THRESHOLD        = 35
    HEAD_PITCH_UP_THRESHOLD   = 18
    HEAD_PITCH_DOWN_THRESHOLD = 18
    HEAD_ROLL_THRESHOLD       = 22

    EYE_MOVEMENT_LEFT_THRESHOLD  = 0.22
    EYE_MOVEMENT_RIGHT_THRESHOLD = 0.22
    EYE_MOVEMENT_UP_THRESHOLD    = 0.20
    EYE_MOVEMENT_DOWN_THRESHOLD  = 0.20

    ALERT_COOLDOWN = 5

    SCORING_WINDOW_SECONDS = 180
    DECAY_HALF_LIFE        = 200.0
    SCORE_COOLDOWN_SECS    = 30
    SCORE_FLOORS = {80: 78, 60: 58, 40: 38}

    ALERT_WEIGHTS = {
        'NO_FACE': 6, 'MULTIPLE_FACES': 9,
        'LOOKING_LEFT': 2, 'LOOKING_RIGHT': 2,
        'LOOKING_UP': 3, 'LOOKING_DOWN': 2,
        'HEAD_TURNED_LEFT': 2, 'HEAD_TURNED_RIGHT': 2,
        'HEAD_TILTED_UP': 3, 'HEAD_TILTED_DOWN': 2, 'HEAD_TILTED_SIDE': 1,
        'EYE_LEFT': 3, 'EYE_RIGHT': 3, 'EYE_UP': 3, 'EYE_DOWN': 2,
        'MULTIPLE_PEOPLE': 20, 'CHEATING_ITEM_MOBILE': 25,
    }

    MAX_RAW_SCORE_FOR_NORMALIZATION = 100
    CALIBRATION_FRAMES = 70
    BLUR_FACE          = True
    BLUR_KERNEL_SIZE   = 51
    SAVE_ALERTS        = True
    ALERT_FRAMES_DIR   = "outputs/alert_frames"

    KEYFRAME_JPEG_QUALITY = 40
    KEYFRAME_MAX_WIDTH    = 320
    KEYFRAME_BLUR_KERNEL  = 35

    NO_FACE_AUTO_STOP_SECONDS = 30

    LEFT_EYE      = [33, 160, 158, 133, 153, 144]
    LEFT_EYE_IRIS = [468, 469, 470, 471, 472]
    RIGHT_EYE     = [362, 385, 387, 263, 373, 380]
    RIGHT_EYE_IRIS= [473, 474, 475, 476, 477]

    NOSE_TIP         = 1
    CHIN             = 152
    LEFT_EYE_CORNER  = 33
    RIGHT_EYE_CORNER = 263
    LEFT_MOUTH       = 61
    RIGHT_MOUTH      = 291


# ══════════════════════════════════════════════════════════════════════════════
#  DETECTION CLASSES  
# ══════════════════════════════════════════════════════════════════════════════

class FaceDetector:
    def __init__(self):
        self._mesh = mp_face_mesh.FaceMesh(
            max_num_faces=Config.MAX_NUM_FACES,
            refine_landmarks=Config.REFINE_LANDMARKS,
            min_detection_confidence=Config.MIN_DETECTION_CONFIDENCE,
            min_tracking_confidence=Config.MIN_TRACKING_CONFIDENCE,
        )
    def detect(self, frame):
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        rgb.flags.writeable = False
        return self._mesh.process(rgb)
    @staticmethod
    def face_count(results) -> int:
        return len(results.multi_face_landmarks) if results.multi_face_landmarks else 0
    def get_eye_data(self, lms, w, h):
        def _pts(idx):
            return np.array([(int(lms.landmark[i].x*w), int(lms.landmark[i].y*h)) for i in idx])
        return {'left_eye': _pts(Config.LEFT_EYE), 'right_eye': _pts(Config.RIGHT_EYE),
                'left_iris': _pts(Config.LEFT_EYE_IRIS), 'right_iris': _pts(Config.RIGHT_EYE_IRIS)}
    def get_face_bbox(self, lms, w, h):
        xs = [int(l.x*w) for l in lms.landmark]
        ys = [int(l.y*h) for l in lms.landmark]
        return max(0,min(xs)-20), max(0,min(ys)-20), min(w,max(xs)+20), min(h,max(ys)+20)
    def close(self): self._mesh.close()


class EyeMovementTracker:
    def __init__(self):
        self._bl, self._br = None, None
        self._sl, self._sr = [], []
        self._calibrated = False
        self._history = []

    def _iris_pos(self, eye, iris):
        ew = eye[:,0].max()-eye[:,0].min(); eh = eye[:,1].max()-eye[:,1].min()
        if ew==0 or eh==0: return 0.,0.
        cx,cy = np.median(iris[:,0]), np.median(iris[:,1])
        ecx = (eye[:,0].min()+eye[:,0].max())/2; ecy = (eye[:,1].min()+eye[:,1].max())/2
        return float(np.clip((cx-ecx)/(ew/2),-1,1)), float(np.clip((cy-ecy)/(eh/2),-1,1))

    def calibrate(self, le,li,re,ri):
        lh,lv=self._iris_pos(le,li); rh,rv=self._iris_pos(re,ri)
        self._sl.append((lh,lv)); self._sr.append((rh,rv))
        if len(self._sl)>=Config.CALIBRATION_FRAMES:
            self._bl=(float(np.median([s[0] for s in self._sl])),float(np.median([s[1] for s in self._sl])))
            self._br=(float(np.median([s[0] for s in self._sr])),float(np.median([s[1] for s in self._sr])))
            self._calibrated=True; return True
        return False

    @property
    def is_calibrated(self): return self._calibrated
    @property
    def calibration_progress(self): return min(len(self._sl)/Config.CALIBRATION_FRAMES,1.)

    def track(self, le,li,re,ri):
        if not self._calibrated: return 0.,0.,"CENTER",None
        lh,lv=self._iris_pos(le,li); rh,rv=self._iris_pos(re,ri)
        h=(lh-self._bl[0]+rh-self._br[0])/2; v=(lv-self._bl[1]+rv-self._br[1])/2
        self._history.append((h,v))
        if len(self._history)>3: self._history.pop(0)
        w=np.array([0.2,0.3,0.5])[:len(self._history)]; w/=w.sum()
        sh=float(np.average([p[0] for p in self._history],weights=w))
        sv=float(np.average([p[1] for p in self._history],weights=w))
        if abs(sh)>abs(sv):
            if sh>Config.EYE_MOVEMENT_RIGHT_THRESHOLD: return sh,sv,"RIGHT","EYE_RIGHT"
            if sh<-Config.EYE_MOVEMENT_LEFT_THRESHOLD: return sh,sv,"LEFT","EYE_LEFT"
        if sv<-Config.EYE_MOVEMENT_UP_THRESHOLD: return sh,sv,"UP","EYE_UP"
        if sv>Config.EYE_MOVEMENT_DOWN_THRESHOLD: return sh,sv,"DOWN","EYE_DOWN"
        return sh,sv,"CENTER",None


class GazeEstimator:
    def __init__(self): self._history=[]
    def _est(self, eye, iris):
        ew=eye[:,0].max()-eye[:,0].min(); eh=eye[:,1].max()-eye[:,1].min()
        if ew==0 or eh==0: return 0.,0.
        cx,cy=np.median(iris[:,0]),np.median(iris[:,1])
        return float(np.clip((cx-(eye[:,0].min()+eye[:,0].max())/2)/(ew/2),-1,1)), \
               float(np.clip((cy-(eye[:,1].min()+eye[:,1].max())/2)/(eh/2),-1,1))
    def combined_gaze(self,le,li,re,ri):
        lh,lv=self._est(le,li); rh,rv=self._est(re,ri)
        yaw=((lh+rh)/2)*35.; pitch=((lv+rv)/2)*30.
        self._history.append((yaw,pitch))
        if len(self._history)>5: self._history.pop(0)
        return float(np.mean([p[0] for p in self._history])), float(np.mean([p[1] for p in self._history]))
    @staticmethod
    def classify(yaw,pitch):
        if abs(yaw)>abs(pitch):
            if yaw>Config.GAZE_YAW_THRESHOLD: return "RIGHT","LOOKING_RIGHT"
            if yaw<-Config.GAZE_YAW_THRESHOLD: return "LEFT","LOOKING_LEFT"
        if pitch<-Config.GAZE_PITCH_UP_THRESHOLD: return "UP","LOOKING_UP"
        if pitch>Config.GAZE_PITCH_DOWN_THRESHOLD: return "DOWN","LOOKING_DOWN"
        return "CENTER",None


class HeadPoseEstimator:
    _M3D = np.array([(0,0,0),(0,-330,-65),(-225,170,-135),(225,170,-135),(-150,-150,-125),(150,-150,-125)],dtype=np.float64)
    _IDX = [Config.NOSE_TIP,Config.CHIN,Config.LEFT_EYE_CORNER,Config.RIGHT_EYE_CORNER,Config.LEFT_MOUTH,Config.RIGHT_MOUTH]
    def __init__(self): self._history=[]; self._baseline=None; self._cal=[]; self._calibrated=False
    @staticmethod
    def _norm(a):
        while a>180: a-=360
        while a<-180: a+=360
        return a
    @staticmethod
    def _angles(rvec):
        rm,_=cv2.Rodrigues(rvec); n=rm@np.array([0.,0.,-1.]); u=rm@np.array([0.,-1.,0.])
        return math.degrees(math.atan2(n[1],math.sqrt(n[0]**2+n[2]**2))), \
               math.degrees(math.atan2(-n[0],-n[2])), \
               math.degrees(math.atan2(-u[0],-u[1]))
    def _solve(self,lms,w,h):
        pts=np.array([[lms.landmark[i].x*w,lms.landmark[i].y*h] for i in self._IDX],dtype=np.float64)
        cam=np.array([[w,0,w/2],[0,w,h/2],[0,0,1]],dtype=np.float64)
        ok,rv,tv=cv2.solvePnP(self._M3D,pts,cam,np.zeros((4,1)),flags=cv2.SOLVEPNP_ITERATIVE)
        return (rv,tv) if ok else (None,None)
    def calibrate(self,lms,w,h):
        rv,_=self._solve(lms,w,h)
        if rv is None: return False
        p,y,r=self._angles(rv); self._cal.append((p,y,r))
        if len(self._cal)>=Config.CALIBRATION_FRAMES:
            self._baseline=(float(np.median([s[0] for s in self._cal])),
                            float(np.median([s[1] for s in self._cal])),
                            float(np.median([s[2] for s in self._cal])))
            self._calibrated=True; return True
        return False
    @property
    def is_calibrated(self): return self._calibrated
    @property
    def calibration_progress(self): return min(len(self._cal)/Config.CALIBRATION_FRAMES,1.)
    def estimate(self,lms,w,h):
        rv,tv=self._solve(lms,w,h)
        if rv is None: return None,None,None,None,None
        p,y,r=self._angles(rv)
        if self._calibrated and self._baseline:
            p=self._norm(p-self._baseline[0]); y=self._norm(y-self._baseline[1]); r=self._norm(r-self._baseline[2])
        self._history.append((p,y,r))
        if len(self._history)>4: self._history.pop(0)
        return float(np.mean([x[0] for x in self._history])), \
               float(np.mean([x[1] for x in self._history])), \
               float(np.mean([x[2] for x in self._history])), rv, tv
    @staticmethod
    def classify(p,y,r):
        if abs(y)>abs(p) and abs(y)>abs(r):
            if y>Config.HEAD_YAW_THRESHOLD: return "TURNED RIGHT","HEAD_TURNED_RIGHT"
            if y<-Config.HEAD_YAW_THRESHOLD: return "TURNED LEFT","HEAD_TURNED_LEFT"
        if abs(p)>abs(r):
            if p<-Config.HEAD_PITCH_UP_THRESHOLD: return "TILTED UP","HEAD_TILTED_UP"
            if p>Config.HEAD_PITCH_DOWN_THRESHOLD: return "TILTED DOWN","HEAD_TILTED_DOWN"
        if abs(r)>Config.HEAD_ROLL_THRESHOLD: return "TILTED SIDE","HEAD_TILTED_SIDE"
        return "FORWARD",None


class AlertManager:
    def __init__(self):
        self._alerts=[]; self._last_time={}; self._session_start=time.time()
        self._score_history=[]; self._keyframes={}
        if Config.SAVE_ALERTS: os.makedirs(Config.ALERT_FRAMES_DIR,exist_ok=True)

    def add(self, atype, frame=None, metadata=None):
        now=time.time()
        if atype in self._last_time and (now-self._last_time[atype])<Config.ALERT_COOLDOWN: return False
        self._last_time[atype]=now
        idx=len(self._alerts)
        rec={'type':atype,'time':now,'elapsed':now-self._session_start,'metadata':metadata or {},'keyframe_idx':idx}
        if frame is not None:
            try:
                kf=self._capture_keyframe(frame)
                if kf: self._keyframes[idx]=kf
            except: pass
            if Config.SAVE_ALERTS:
                path=os.path.join(Config.ALERT_FRAMES_DIR,f"{atype}_{int(now*1000)}.jpg")
                cv2.imwrite(path,frame); rec['frame_path']=path
        self._alerts.append(rec); return True

    def _capture_keyframe(self,frame):
        h,w=frame.shape[:2]; scale=Config.KEYFRAME_MAX_WIDTH/w
        small=cv2.resize(frame,(Config.KEYFRAME_MAX_WIDTH,int(h*scale)))
        k=Config.KEYFRAME_BLUR_KERNEL
        blurred=cv2.GaussianBlur(small,(k,k),0)
        _,buf=cv2.imencode('.jpg',blurred,[cv2.IMWRITE_JPEG_QUALITY,Config.KEYFRAME_JPEG_QUALITY])
        return buf.tobytes()

    def update_score_history(self,score):
        now=time.time()
        self._score_history.append((now,score))
        cutoff=now-Config.SCORING_WINDOW_SECONDS
        self._score_history=[(t,s) for t,s in self._score_history if t>=cutoff]

    def suspicion_score(self):
        now=time.time(); cutoff=now-Config.SCORING_WINDOW_SECONDS
        lam=math.log(2)/Config.DECAY_HALF_LIFE
        raw=0.; last_t=0.
        for a in self._alerts:
            if a['time']<cutoff: continue
            raw+=Config.ALERT_WEIGHTS.get(a['type'],3)*math.exp(-lam*(now-a['time']))
            if a['time']>last_t: last_t=a['time']
        if last_t>0 and (now-last_t)<Config.SCORE_COOLDOWN_SECS:
            raw*=1.+(1.-(now-last_t)/Config.SCORE_COOLDOWN_SECS)*0.4
        cur=min((raw/Config.MAX_RAW_SCORE_FOR_NORMALIZATION)*100.,100.)
        if not hasattr(self,'_peak'): self._peak=0.
        if cur>self._peak: self._peak=cur
        floor=0.
        for thr in sorted(Config.SCORE_FLOORS,reverse=True):
            if self._peak>=thr: floor=Config.SCORE_FLOORS[thr]; break
        return min(max(cur,floor),100.)

    def recommendation(self):
        s=self.suspicion_score()
        if s<30: return "ACCEPT","Low suspicion"
        if s<70: return "REVIEW","Medium suspicion — manual review recommended"
        return "REJECT","High suspicion — potential cheating detected"

    def summary(self):
        now=time.time(); cutoff=now-Config.SCORING_WINDOW_SECONDS
        bd={}
        for a in self._alerts:
            if a['time']>=cutoff: bd[a['type']]=bd.get(a['type'],0)+1
        return {'total_in_window':sum(bd.values()),'breakdown':bd,
                'score':self.suspicion_score(),'session_duration':now-self._session_start}

    def get_timeline_data(self):
        return [{'elapsed':a['elapsed'],'type':a['type'],'time':a['time'],
                 'keyframe_idx':a['keyframe_idx'],'has_keyframe':a['keyframe_idx'] in self._keyframes}
                for a in self._alerts]

    def export_report(self,filename="final_report.txt"):
        summ=self.summary(); rec,reason=self.recommendation()
        lines=["="*55,"  CHEATING DETECTION — SESSION REPORT","="*55,
               f"  Generated : {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}",
               f"  Duration  : {summ['session_duration']:.1f} s","",
               f"  Score     : {summ['score']:.1f} / 100",
               f"  Result    : {rec}","  Alerts:"]
        for t,c in summ['breakdown'].items(): lines.append(f"    {t:.<30} {c}")
        lines.append("="*55)
        os.makedirs("outputs",exist_ok=True)
        path=os.path.join("outputs",filename)
        with open(path,'w',encoding='utf-8') as f: f.write('\n'.join(lines))
        return path


# ── YOLO (optional) ───────────────────────────────────────────────────────────
try:
    from ultralytics import YOLO as _YOLO
    _YOLO_AVAILABLE = True
except ImportError:
    _YOLO_AVAILABLE = False


class YOLODetector:
    COOLDOWN=2; PERSON_CONF=0.70; PHONE_CONF=0.70; PHONE_MIN_AREA=0.002
    VOTE_WINDOW={'MULTIPLE_PEOPLE':5,'CHEATING_ITEM_MOBILE':6}
    VOTE_THRESH={'MULTIPLE_PEOPLE':3,'CHEATING_ITEM_MOBILE':2}

    def __init__(self, session_id=None):
        import collections
        self._ok=_YOLO_AVAILABLE; self._coco=None; self._custom=None
        if self._ok:
            try:
                coco_model_path = os.path.join(current_dir, "yolov8n.pt")
                phone_model_path = os.path.join(current_dir, "phones.pt")
                self._coco = _YOLO(coco_model_path)
                self._custom = _YOLO(phone_model_path)
            except Exception as e:
                print(f"YOLO load error: {e}"); self._ok=False
        self.session_id=session_id or datetime.now().strftime("%Y%m%d_%H%M%S")
        self._last_type=None; self._last_logged=0.; self._start=time.time()
        self._votes={k:collections.deque(maxlen=self.VOTE_WINDOW[k]) for k in self.VOTE_WINDOW}
        self.frame_counters={"total":0,"secure":0,"cheating":0,"MULTIPLE_PEOPLE":0,"CHEATING_ITEM_MOBILE":0}

    @property
    def available(self): return self._ok

    @staticmethod
    def _iou(a,b):
        ix1,iy1=max(a[0],b[0]),max(a[1],b[1]); ix2,iy2=min(a[2],b[2]),min(a[3],b[3])
        inter=max(0,ix2-ix1)*max(0,iy2-iy1)
        union=(a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-inter
        return inter/union if union>0 else 0.

    @classmethod
    def _dedup(cls,boxes,thr):
        kept=[]
        for c in sorted(boxes,key=lambda x:x[4],reverse=True):
            if not any(cls._iou(c[:4],k[:4])>=thr for k in kept): kept.append(c)
        return kept

    def detect(self, frame):
        if not self._ok: return None,"Secure",frame
        h,w=frame.shape[:2]; area=h*w
        pb=[]; phb=[]
        for r in self._coco.predict(frame,conf=self.PERSON_CONF,iou=0.45,verbose=False, device='cpu'):
            for b in r.boxes:
                if int(b.cls[0])!=0: continue
                x1,y1,x2,y2=map(int,b.xyxy[0]); pb.append((x1,y1,x2,y2,round(float(b.conf[0]),2)))
        pb=self._dedup(pb,0.45); pc=len(pb)
        for r in self._custom.predict(frame,conf=self.PHONE_CONF,iou=0.40,verbose=False, device='cpu'):
            for b in r.boxes:
                x1,y1,x2,y2=map(int,b.xyxy[0]); conf=round(float(b.conf[0]),2)
                if (x2-x1)*(y2-y1)<area*self.PHONE_MIN_AREA: continue
                phb.append((x1,y1,x2,y2,conf))
        phb=self._dedup(phb,0.40); phone=len(phb)>0
        raw="MULTIPLE_PEOPLE" if pc>1 else ("CHEATING_ITEM_MOBILE" if phone else None)
        for k,buf in self._votes.items(): buf.append(raw==k)
        confirmed=None
        for k in ("MULTIPLE_PEOPLE","CHEATING_ITEM_MOBILE"):
            if sum(self._votes[k])>=self.VOTE_THRESH[k]: confirmed=k; break
        self.frame_counters["total"]+=1
        if confirmed:
            self.frame_counters["cheating"]+=1; self.frame_counters[confirmed]+=1
            now=time.time()
            if confirmed!=self._last_type or now-self._last_logged>=self.COOLDOWN:
                push_log(f"[YOLO] {confirmed}",{},self.session_id)
                self._last_type=confirmed; self._last_logged=now
        else:
            self.frame_counters["secure"]+=1; self._last_type=None
        return confirmed, "ALERT" if confirmed else "Secure", frame


# ══════════════════════════════════════════════════════════════════════════════
#  WebSessionProcessor (Updated to receive frames via WebSocket queue)
# ══════════════════════════════════════════════════════════════════════════════

class WebSessionProcessor:
    def __init__(self, session_id: int):
        self.session_id      = session_id
        self.face_det        = FaceDetector()
        self.gaze_est        = GazeEstimator()
        self.head_est        = HeadPoseEstimator()
        self.eye_tracker     = EyeMovementTracker()
        self.alerts          = AlertManager()
        self._session_start  = time.time()
        self._frame_count    = 0
        self.calibrated      = False
        self._running        = False
        self._thread         = None
        self._lock           = threading.Lock()
        self._dropped_frames = 0
        self._no_face_since: Optional[float] = None
        self.abandoned       = False

        self._latest_state = {
            "face_count":0,"calibrated":False,"cal_progress":0.,
            "gaze_dir":"—","gaze_yaw":0.,"gaze_pitch":0.,
            "head_dir":"—","head_pitch":0.,"head_yaw":0.,"head_roll":0.,
            "eye_dir":"—","eye_h":0.,"eye_v":0.,
            "score":0.,"current_alert":None,"alert_breakdown":{},
            "frame_b64":None,"yolo_alert":None,"fps":0.,
            "abandoned":False,"no_face_countdown":None,
            "dropped_frames":0,
        }
        self._unpolled_alerts = set()

        # --- FIX: Removed cv2.VideoCapture() completely ---
        # Instead of pulling frames from hardware, we queue them from the browser
        self._frame_queue = queue.Queue(maxsize=5) 
        
        self._yolo           = YOLODetector(session_id=str(session_id))
        self._yolo_frame     = None
        self._yolo_lock      = threading.Lock()
        self._yolo_alert     = None
        self._yolo_thread    = None
        self._fps            = 0.
        self._fps_prev       = time.time()
        self._fps_cnt        = 0

    def push_frame(self, frame):
        """Called by the WebSocket endpoint when a new frame arrives from the browser."""
        if self._frame_queue.full():
            try:
                self._frame_queue.get_nowait()
                with self._lock:
                    self._dropped_frames += 1
                    dropped = self._dropped_frames
                if dropped % 50 == 0:
                    push_log("[FRAME_DROP] input queue overflow", {"dropped_frames": dropped}, self.session_id)
            except queue.Empty:
                pass

        try:
            self._frame_queue.put_nowait(frame)
        except queue.Full:
            # In rare races where queue fills again before put, skip this frame.
            with self._lock:
                self._dropped_frames += 1

    # ── helpers ───────────────────────────────────────────────────────────
    def _blur_face(self, frame, lms, w, h):
        x0,y0,x1,y1=self.face_det.get_face_bbox(lms,w,h)
        roi=frame[y0:y1,x0:x1]
        if roi.size>0:
            k=Config.BLUR_KERNEL_SIZE
            frame[y0:y1,x0:x1]=cv2.GaussianBlur(roi,(k,k),0)
        return frame

    def _tick_fps(self):
        self._fps_cnt+=1
        if self._fps_cnt>=15:
            now=time.time(); elapsed=now-self._fps_prev
            if elapsed>0: self._fps=self._fps_cnt/elapsed
            self._fps_prev=now; self._fps_cnt=0

    def _no_face_check(self, face_count) -> Optional[int]:
        now=time.time(); thr=Config.NO_FACE_AUTO_STOP_SECONDS
        if face_count>0: self._no_face_since=None; return None
        if self._no_face_since is None: self._no_face_since=now
        remaining=int(thr-(now-self._no_face_since))
        if remaining<=0:
            self.abandoned=True; self._running=False
            push_log("[AUTO-STOP] abandoned",{"duration":round(now-self._no_face_since,1)},self.session_id)
            return 0
        return max(remaining,1)

    # ── YOLO thread ───────────────────────────────────────────────────────
    def _yolo_loop(self, db_factory):
        cnt=0
        while self._running:
            with self._yolo_lock:
                frame=self._yolo_frame.copy() if self._yolo_frame is not None else None
            if frame is None: time.sleep(0.05); continue
            cnt+=1
            if cnt%4!=0: time.sleep(0.005); continue
            alert,_,_=self._yolo.detect(frame)
            self._yolo_alert=alert
            if alert:
                elapsed=time.time()-self._session_start
                self.alerts.add(alert,frame)
                with self._lock:
                    self._unpolled_alerts.add(alert)
                db=db_factory()
                try:
                    last=db.query(DBYoloAlert).filter(
                        DBYoloAlert.session_id==self.session_id,
                        DBYoloAlert.alert_type==alert
                    ).order_by(DBYoloAlert.timestamp.desc()).first()
                    if not last or (datetime.utcnow()-last.timestamp).total_seconds()>=YOLODetector.COOLDOWN:
                        db.add(DBYoloAlert(session_id=self.session_id,alert_type=alert,
                                           elapsed_secs=elapsed,details_json="{}"))
                        db.commit()
                finally: db.close()

    # ── camera loop ───────────────────────────────────────────────────────
    def _camera_loop(self, db_factory):
        pending=[]
        while self._running:
            # --- FIX: Pull from Queue instead of cv2.VideoCapture ---
            try:
                frame = self._frame_queue.get(timeout=0.1)
            except queue.Empty:
                continue

            h,w=frame.shape[:2]
            results=self.face_det.detect(frame)
            fc=self.face_det.face_count(results)

            gd,gy,gp="—",0.,0.; hd,hp,hw,hr="—",0.,0.,0.; ed,eh,ev="—",0.,0.; alert=None

            # calibration phase
            if not self.head_est.is_calibrated or not self.eye_tracker.is_calibrated:
                if results.multi_face_landmarks and fc==1:
                    lms=results.multi_face_landmarks[0]
                    if not self.head_est.is_calibrated: self.head_est.calibrate(lms,w,h)
                    if not self.eye_tracker.is_calibrated:
                        eyes=self.face_det.get_eye_data(lms,w,h)
                        self.eye_tracker.calibrate(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                    if self.head_est.is_calibrated and self.eye_tracker.is_calibrated:
                        self.calibrated=True
                prog=max(self.head_est.calibration_progress,self.eye_tracker.calibration_progress)
                _,jpg=cv2.imencode('.jpg',frame,[cv2.IMWRITE_JPEG_QUALITY,60])
                b64=base64.b64encode(jpg).decode()
                with self._lock:
                    self._latest_state.update({"calibrated":False,"cal_progress":prog,
                                               "face_count":fc,"frame_b64":b64,"fps":round(self._fps,1),
                                               "dropped_frames":self._dropped_frames})
                self._tick_fps(); continue

            # monitoring phase
            countdown=self._no_face_check(fc)
            if self.abandoned:
                threading.Thread(target=self._auto_abandon,args=(db_factory,),daemon=True).start()
                with self._lock:
                    self._latest_state["abandoned"]=True; self._latest_state["no_face_countdown"]=0
                    self._latest_state["dropped_frames"]=self._dropped_frames
                break

            if fc==0:
                if self.alerts.add("NO_FACE",frame): alert="NO_FACE"; push_log("[FACE] NO_FACE",{"count":0},self.session_id)
            elif fc>1:
                if self.alerts.add("MULTIPLE_FACES",frame): alert="MULTIPLE_FACES"; push_log("[FACE] MULTIPLE_FACES",{"count":fc},self.session_id)

            if results.multi_face_landmarks and fc>=1:
                lms=results.multi_face_landmarks[0]
                eyes=self.face_det.get_eye_data(lms,w,h)
                eh,ev,ed,e_alert=self.eye_tracker.track(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                gy,gp=self.gaze_est.combined_gaze(eyes['left_eye'],eyes['left_iris'],eyes['right_eye'],eyes['right_iris'])
                gd,g_alert=self.gaze_est.classify(gy,gp)
                hp2,hw2,hr2,rvec,tvec=self.head_est.estimate(lms,w,h)
                h_alert=None
                if hp2 is not None:
                    hp,hw,hr=hp2,hw2,hr2; hd,h_alert=self.head_est.classify(hp,hw,hr)
                af=frame.copy()
                if Config.BLUR_FACE: af=self._blur_face(af,lms,w,h)
                if alert is None:
                    for a_type,a_meta in [(e_alert,{'h':eh,'v':ev}),(g_alert,{'yaw':gy,'pitch':gp}),(h_alert,{'p':hp,'y':hw,'r':hr})]:
                        if a_type and self.alerts.add(a_type,af,a_meta):
                            alert=a_type; push_log(f"[DETECT] {a_type}",a_meta,self.session_id); break

            score=self.alerts.suspicion_score(); self.alerts.update_score_history(score)
            if alert: 
                pending.append((alert,time.time()-self._session_start))
                with self._lock:
                    self._unpolled_alerts.add(alert)

            self._frame_count+=1
            if self._frame_count%30==0:
                db=db_factory()
                try:
                    for at,el in pending:
                        db.add(DBAlert(session_id=self.session_id,alert_type=at,elapsed_secs=el,metadata_json="{}"))
                    for entry in self.alerts.get_timeline_data()[-(len(pending)+2):]:
                        if entry['has_keyframe']:
                            kd=self.alerts._keyframes.get(entry['keyframe_idx'])
                            if kd:
                                ex=db.query(DBKeyframe).filter(DBKeyframe.session_id==self.session_id,
                                    DBKeyframe.elapsed_secs==round(entry['elapsed'],1)).first()
                                if not ex:
                                    db.add(DBKeyframe(session_id=self.session_id,alert_type=entry['type'],
                                                      elapsed_secs=round(entry['elapsed'],1),image_data=kd))
                    pending.clear()
                    db.add(DBScoreHistory(session_id=self.session_id,score=score)); db.commit()
                except Exception as e: print(f"DB error: {e}"); db.rollback()
                finally: db.close()

            with self._yolo_lock: self._yolo_frame=frame.copy()
            _,jpg=cv2.imencode('.jpg',frame,[cv2.IMWRITE_JPEG_QUALITY,60])
            b64=base64.b64encode(jpg).decode()
            bd={}
            for a in self.alerts._alerts:
                if a['time']>=time.time()-Config.SCORING_WINDOW_SECONDS:
                    bd[a['type']]=bd.get(a['type'],0)+1
            with self._lock:
                self._latest_state={
                    "calibrated":True,"cal_progress":1.,"face_count":fc,
                    "gaze_dir":gd,"gaze_yaw":round(float(gy),1),"gaze_pitch":round(float(gp),1),
                    "head_dir":hd,"head_pitch":round(float(hp),1),"head_yaw":round(float(hw),1),"head_roll":round(float(hr),1),
                    "eye_dir":ed,"eye_h":round(float(eh),3),"eye_v":round(float(ev),3),
                    "score":round(score,1),"current_alert":alert,"alert_breakdown":bd,
                    "frame_b64":b64,"fps":round(self._fps,1),"yolo_alert":self._yolo_alert,
                    "abandoned":False,"no_face_countdown":countdown,
                    "dropped_frames":self._dropped_frames,
                }
            self._tick_fps()

    def _auto_abandon(self, db_factory):
        db=db_factory()
        try:
            score=self.alerts.suspicion_score(); duration=time.time()-self._session_start
            db.query(DBSession).filter(DBSession.id==self.session_id).update(
                {"ended_at":datetime.utcnow(),"final_score":score,"recommendation":"ABANDONED","duration_seconds":duration})
            db.commit()
        except Exception as e: print(f"abandon DB error: {e}"); db.rollback()
        finally: db.close()
        try:
            self.face_det.close()
            self.alerts.export_report(f"session_{self.session_id}_report.txt")
        except: pass
        _active_pop(self.session_id)

    def start(self):
        self._running=True
        self._thread=threading.Thread(target=self._camera_loop,args=(DBSessionLocal,),daemon=True)
        self._thread.start()
        if self._yolo.available:
            self._yolo_thread=threading.Thread(target=self._yolo_loop,args=(DBSessionLocal,),daemon=True)
            self._yolo_thread.start()

    def get_state(self) -> dict:
        with self._lock: return dict(self._latest_state)

    def consume_alerts(self) -> list:
        with self._lock:
            lst = list(self._unpolled_alerts)
            self._unpolled_alerts.clear()
            return lst

    def finalize(self, db):
        self._running=False
        for t in [self._thread,self._yolo_thread]:
            if t: t.join(timeout=3)
            
        score=self.alerts.suspicion_score()
        rec,_=self.alerts.recommendation()
        if self.abandoned: rec="ABANDONED"
        duration=time.time()-self._session_start
        for entry in self.alerts.get_timeline_data():
            if entry['has_keyframe']:
                kd=self.alerts._keyframes.get(entry['keyframe_idx'])
                if kd:
                    ex=db.query(DBKeyframe).filter(DBKeyframe.session_id==self.session_id,
                        DBKeyframe.elapsed_secs==round(entry['elapsed'],1)).first()
                    if not ex:
                        db.add(DBKeyframe(session_id=self.session_id,alert_type=entry['type'],
                                          elapsed_secs=round(entry['elapsed'],1),image_data=kd))
        for t,s in self.alerts._score_history:
            db.add(DBScoreHistory(session_id=self.session_id,score=s))
        db.query(DBSession).filter(DBSession.id==self.session_id).update(
            {"ended_at":datetime.utcnow(),"final_score":score,"recommendation":rec,"duration_seconds":duration})
        db.commit()
        self.alerts.export_report(f"session_{self.session_id}_report.txt")
        self.face_det.close()


# ══════════════════════════════════════════════════════════════════════════════
#  ENDPOINTS
# ══════════════════════════════════════════════════════════════════════════════

class StartSessionRequest(BaseModel):
    candidate_name: Optional[str] = None
    candidate_id:   Optional[str] = None
    interview_session_id: Optional[str] = None   # link to interview


@router.post("/sessions/start")
def start_session(req: StartSessionRequest = None):
    if req is None: req = StartSessionRequest()
    db = DBSessionLocal()
    try:
        session = DBSession(
            candidate_name=req.candidate_name,
            candidate_id=req.candidate_id,
            interview_session_id=req.interview_session_id,
        )
        db.add(session); db.commit(); db.refresh(session)
        try:
            proc = WebSessionProcessor(session.id)
        except RuntimeError as e:
            db.delete(session); db.commit()
            raise HTTPException(status_code=503, detail=str(e))
        proc.start()
        _active_set(session.id, proc)
        return {"session_id": session.id, "started_at": session.started_at.isoformat(),
                "candidate_name": session.candidate_name, "candidate_id": session.candidate_id}
    finally: db.close()


@router.post("/sessions/{session_id}/end")
def end_session(session_id: int):
    db = DBSessionLocal()
    try:
        proc = _active_pop(session_id)
        if not proc:
            s = db.query(DBSession).filter(DBSession.id==session_id).first()
            if s and s.recommendation=="ABANDONED":
                return {"session_id":session_id,"final_score":s.final_score,
                        "recommendation":"ABANDONED","duration_seconds":s.duration_seconds}
            raise HTTPException(status_code=404, detail="Session not found or already ended")
        proc.finalize(db)
        s = db.query(DBSession).filter(DBSession.id==session_id).first()
        rec,reason = proc.alerts.recommendation()
        if proc.abandoned: rec="ABANDONED"; reason="Auto-stopped — no face detected"
        return {"session_id":session_id,"final_score":s.final_score,"recommendation":rec,
                "reason":reason,"duration_seconds":s.duration_seconds}
    finally: db.close()


@router.get("/sessions")
def list_sessions():
    db = DBSessionLocal()
    try:
        rows = db.query(DBSession).order_by(DBSession.started_at.desc()).limit(50).all()
        return [{"id":s.id,"started_at":s.started_at.isoformat() if s.started_at else None,
                 "ended_at":s.ended_at.isoformat() if s.ended_at else None,
                 "final_score":s.final_score,"recommendation":s.recommendation,
                 "duration_seconds":s.duration_seconds,"alert_count":len(s.alerts),
                 "candidate_name":s.candidate_name,"candidate_id":s.candidate_id,
                 "interview_session_id":s.interview_session_id} for s in rows]
    finally: db.close()


@router.get("/sessions/{session_id}/report")
def get_report(session_id: int):
    db = DBSessionLocal()
    try:
        s = db.query(DBSession).filter(DBSession.id==session_id).first()
        if not s: raise HTTPException(status_code=404, detail="Session not found")
        bd={}
        for a in s.alerts: bd[a.alert_type]=bd.get(a.alert_type,0)+1
        yolo_bd={}
        for ya in s.yolo_alerts: yolo_bd[ya.alert_type]=yolo_bd.get(ya.alert_type,0)+1
        timeline=[]
        seen=set()
        for a in sorted(s.alerts,key=lambda x:x.elapsed_secs):
            kf=next((k for k in s.keyframes if abs(k.elapsed_secs-a.elapsed_secs)<0.5 and k.alert_type==a.alert_type),None)
            key=(round(a.elapsed_secs,1),a.alert_type)
            if key not in seen:
                seen.add(key)
                timeline.append({"alert_type":a.alert_type,"elapsed_secs":a.elapsed_secs,
                                  "timestamp":a.timestamp.isoformat(),"keyframe_id":kf.id if kf else None,
                                  "has_keyframe":kf is not None,"source":"mediapipe"})
        for ya in sorted(s.yolo_alerts,key=lambda x:x.elapsed_secs):
            timeline.append({"alert_type":ya.alert_type,"elapsed_secs":ya.elapsed_secs,
                              "timestamp":ya.timestamp.isoformat(),"keyframe_id":None,
                              "has_keyframe":False,"source":"yolo"})
        timeline.sort(key=lambda x:x['elapsed_secs'])

        # interview summary if linked
        interview_data = None
        if s.interview_summary_json:
            try: interview_data = json.loads(s.interview_summary_json)
            except: pass

        return {
            "session_id": session_id,
            "started_at": s.started_at.isoformat() if s.started_at else None,
            "ended_at":   s.ended_at.isoformat()   if s.ended_at   else None,
            "final_score": s.final_score,
            "recommendation": s.recommendation,
            "duration_seconds": s.duration_seconds,
            "candidate_name": s.candidate_name,
            "candidate_id":   s.candidate_id,
            "alert_breakdown": bd,
            "total_alerts": len(s.alerts),
            "yolo_alert_breakdown": yolo_bd,
            "total_yolo_alerts": len(s.yolo_alerts),
            "score_history": [{"timestamp":sh.timestamp.isoformat(),"score":sh.score} for sh in s.score_history],
            "timeline_events": timeline,
            "interview_session_id": s.interview_session_id,
            "interview_score": s.interview_score,
            "interview_summary": interview_data,
        }
    finally: db.close()


@router.get("/keyframes/{keyframe_id}")
def get_keyframe(keyframe_id: int):
    db = DBSessionLocal()
    try:
        kf = db.query(DBKeyframe).filter(DBKeyframe.id==keyframe_id).first()
        if not kf or not kf.image_data:
            raise HTTPException(status_code=404, detail="Keyframe not found")
        return Response(content=kf.image_data, media_type="image/jpeg",
                        headers={"Cache-Control":"max-age=3600"})
    finally: db.close()


@router.get("/sessions/{session_id}/download")
def download_report(session_id: int):
    path = os.path.join("outputs", f"session_{session_id}_report.txt")
    if os.path.exists(path):
        return FileResponse(path, media_type="text/plain",
                            filename=f"session_{session_id}_report.txt")
    raise HTTPException(status_code=404, detail="Report file not found")


@router.get("/dashboard/stats")
def dashboard_stats():
    db = DBSessionLocal()
    try:
        rows = db.query(DBSession).filter(DBSession.ended_at.isnot(None)).all()
        scores    = [s.final_score for s in rows if s.final_score is not None]
        durations = [s.duration_seconds for s in rows if s.duration_seconds]
        at={}
        for s in rows:
            for a in s.alerts: at[a.alert_type]=at.get(a.alert_type,0)+1
        return {
            "total":     len(rows),
            "accept":    sum(1 for s in rows if s.recommendation=="ACCEPT"),
            "review":    sum(1 for s in rows if s.recommendation=="REVIEW"),
            "reject":    sum(1 for s in rows if s.recommendation=="REJECT"),
            "abandoned": sum(1 for s in rows if s.recommendation=="ABANDONED"),
            "avg_score":    round(sum(scores)/len(scores),1) if scores else 0.,
            "avg_duration": round(sum(durations)/len(durations)) if durations else 0,
            "most_common_alert": max(at,key=at.get) if at else None,
            "alert_totals": at,
        }
    finally: db.close()


def _cleanup_orphan_processor(session_id: int, grace_seconds: float = 20.0):
    """Graceful cleanup when client disconnects and never calls /sessions/{id}/end."""
    time.sleep(grace_seconds)

    if _ws_count(session_id) > 0:
        return

    proc = _active_pop(session_id)
    if not proc:
        return

    db = DBSessionLocal()
    try:
        session = db.query(DBSession).filter(DBSession.id == session_id).first()
        if session and session.ended_at is None:
            proc.abandoned = True
            proc.finalize(db)
            push_log("[AUTO-CLEANUP] websocket disconnected", {"grace_seconds": grace_seconds}, session_id)
        else:
            proc._running = False
            for t in [proc._thread, proc._yolo_thread]:
                if t:
                    t.join(timeout=2)
            try:
                proc.face_det.close()
            except Exception:
                pass
    except Exception as e:
        print(f"orphan cleanup error: {e}")
        db.rollback()
    finally:
        db.close()


# ── WebSocket: live camera state (Updated for Ping-Pong Routing) ──────────────
@router.websocket("/ws/{session_id}")
async def ws_state(websocket: WebSocket, session_id: int):
    if not _is_ws_authorized(websocket):
        await websocket.accept()
        await websocket.close(code=1008)
        return

    await websocket.accept()
    _ws_inc(session_id)
    try:
        proc = _active_get(session_id)
        if not proc:
            await websocket.send_json({"error":"Session not found"})
            await websocket.close()
            return
            
        while True:
            # 1. Wait for the browser to send a frame
            data = await websocket.receive_json()
            if data.get("type") == "frame" and data.get("frame_b64"):
                try:
                    img_data = base64.b64decode(data["frame_b64"])
                    nparr = np.frombuffer(img_data, np.uint8)
                    frame = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
                    if frame is not None:
                        proc.push_frame(frame)
                except Exception as e:
                    print(f"Error decoding frame: {e}")

            # 2. Reply with the analyzed state and the processed face-mesh image
            state = proc.get_state()
            await websocket.send_json(state)
            
            if state.get("abandoned"):
                await asyncio.sleep(0.5)
                break
                
    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"WS error: {e}")
    finally:
        remaining = _ws_dec(session_id)
        if remaining == 0:
            threading.Thread(target=_cleanup_orphan_processor, args=(session_id,), daemon=True).start()
```

### `routers/internal_api.py`
```py
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import uuid
import tempfile
from datetime import datetime, timezone
from typing import Any

import cv2
import numpy as np
from fastapi import APIRouter
from pydantic import BaseModel, Field

from recruitment.ats_service import analyze_ats_with_llm
from recruitment.cv_service import parse_cv_with_llm
from recruitment.matcher_service import JobMatcher, recommend_candidates_for_job, recommend_jobs_for_candidate
from recruitment.scraper_service import run_scraper
from recruitment.vector_store import store
from request_context import get_request_id
from routers.integrity import _active_get

router = APIRouter(prefix="/internal/v1", tags=["internal"])

VIDEO_DEDUP_WINDOW_SECONDS = 1.0
VIDEO_MIN_INTERVAL_SECONDS = 0.45
_VIDEO_FRAME_STATE: dict[str, tuple[str, datetime, int]] = {}

_EXTERNAL_SCRAPED_SOURCE_TOKENS = {"wuzzuf", "linkedin", "scraped", "external"}
_EXTERNAL_SCRAPED_HOST_TOKENS = ("wuzzuf.net", "linkedin.com")

class ParseResumeTextRequest(BaseModel):
    resumeText: str

class ExtractResumeTextRequest(BaseModel):
    fileName: str
    contentType: str | None = None
    base64Content: str

class ScoreAtsRequest(BaseModel):
    resumeText: str
    jobDescription: str | None = None

class CandidateVectorSyncRequest(BaseModel):
    candidateId: int
    profileData: dict[str, Any] = Field(default_factory=dict)
    contentHash: str | None = None

class JobVectorSyncRequest(BaseModel):
    jobId: int
    jobData: dict[str, Any] = Field(default_factory=dict)
    contentHash: str | None = None

class DeleteCandidateVectorRequest(BaseModel):
    candidateId: int

class DeleteJobVectorRequest(BaseModel):
    jobId: int

class JobRecommendationRequest(BaseModel):
    candidateId: int
    resumeText: str
    limit: int = Field(default=10, ge=1, le=50)

class CandidateRecommendationRequest(BaseModel):
    jobId: int
    jobDescription: str
    limit: int = Field(default=50, ge=1, le=200)

class ScrapeJobsRequest(BaseModel):
    maxCategories: int | None = Field(default=None, ge=1)

class VideoAnalysisRequest(BaseModel):
    integritySessionId: int
    base64Frame: str
    sequence: int = Field(default=1, ge=1)

def _envelope_ok(data: Any) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": True, "data": data, "error": None}

def _envelope_error(code: str, message: str, details: str | None = None) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": False, "data": None, "error": {"code": code, "message": message, "details": details}}

def _safe_json(value: Any) -> str:
    try:
        return json.dumps(value, ensure_ascii=False)
    except Exception:
        return json.dumps(str(value), ensure_ascii=False)

def _to_float(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except Exception:
        return default

def _to_target_id(value: Any) -> int:
    if isinstance(value, int): return value if value > 0 else 0
    text = str(value or "").strip().lower()
    if text.startswith("job_"): return int(text[4:]) if text[4:].isdigit() else 0
    if text.startswith("candidate_"): return int(text[10:]) if text[10:].isdigit() else 0
    return int(text) if text.isdigit() else 0

def _flatten_skills(skills: Any) -> list[str]:
    items: list[str] = []
    if isinstance(skills, dict):
        for value in skills.values():
            if isinstance(value, list):
                items.extend(str(skill).strip() for skill in value if str(skill).strip())
    elif isinstance(skills, list):
        items.extend(str(skill).strip() for skill in skills if str(skill).strip())
    return list(dict.fromkeys(items))

def _normalize_iso_datetime(value: Any) -> str | None:
    if value is None: return None
    if isinstance(value, datetime):
        dt = value if value.tzinfo else value.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    text = str(value).strip()
    try:
        dt = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if dt.tzinfo is None: dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    except Exception:
        return None

def _decode_data_uri_base64(value: str) -> bytes:
    payload = value.split(",", 1)[1] if "," in value else value
    return base64.b64decode(payload)

def _decode_frame(base64_frame: str) -> np.ndarray | None:
    frame_array = np.frombuffer(_decode_data_uri_base64(base64_frame), dtype=np.uint8)
    return cv2.imdecode(frame_array, cv2.IMREAD_COLOR)

@router.post("/resumes/parse-text")
async def parse_resume_text(request: ParseResumeTextRequest):
    try:
        parsed = parse_cv_with_llm(request.resumeText)
        return _envelope_ok({
            "fullName": str(parsed.get("full_name", "")),
            "email": str(parsed.get("email", "")),
            "phone": str(parsed.get("phone", "")),
            "skills": _flatten_skills(parsed.get("skills")),
            "structuredJson": _safe_json(parsed),
        })
    except Exception as exc:
        return _envelope_error("ResumeParseFailed", "Could not parse resume text.", str(exc))

@router.post("/resumes/extract-text")
async def extract_resume_text(request: ExtractResumeTextRequest):
    try:
        from recruitment.cv_service import process_file
        ext = "." + request.fileName.rsplit(".", 1)[1].lower() if "." in (request.fileName or "") else ".bin"
        if not request.base64Content.strip(): return _envelope_ok({"text": ""})
        
        with tempfile.NamedTemporaryFile(delete=False, suffix=ext) as handle:
            handle.write(_decode_data_uri_base64(request.base64Content))
            temp_path = handle.name
            
        try:
            text, _ = process_file(temp_path)
        finally:
            if os.path.exists(temp_path): os.remove(temp_path)
            
        return _envelope_ok({"text": text or ""})
    except Exception as exc:
        return _envelope_error("ResumeTextExtractFailed", "Could not extract resume text.", str(exc))

@router.post("/resumes/score-ats")
async def score_ats(request: ScoreAtsRequest):
    try:
        parsed = parse_cv_with_llm(request.resumeText)
        ats_result = analyze_ats_with_llm(parsed, request.jobDescription or request.resumeText)
        
        suggestions = []
        for s in ats_result.get("improvement_suggestions", []) + ats_result.get("next_steps", []):
            text = str(s.get("suggestion", "") if isinstance(s, dict) else s).strip()
            if text: suggestions.append(text)
            
        missing_skills = [str(item).strip() for item in (ats_result.get("keywords_analysis", {}) or {}).get("missing_keywords", []) if str(item).strip()]
        
        return _envelope_ok({
            "score": round(_to_float(ats_result.get("overall_score")), 2),
            "summary": str(ats_result.get("summary_feedback", "")),
            "missingSkills": list(dict.fromkeys(missing_skills)),
            "suggestions": list(dict.fromkeys(suggestions)),
        })
    except Exception as exc:
        return _envelope_error("AtsScoreFailed", "Could not score resume text.", str(exc))

@router.post("/vectors/candidates/upsert")
async def upsert_candidate_vector(request: CandidateVectorSyncRequest):
    try:
        store.candidates_col.upsert(
            documents=[_safe_json(request.profileData)],
            metadatas=[{"candidate_id": request.candidateId, "content_hash": request.contentHash or ""}],
            ids=[str(request.candidateId)],
        )
        return _envelope_ok({"vectorId": str(request.candidateId), "collection": "candidates", "model": "chroma"})
    except Exception as exc:
        return _envelope_error("CandidateVectorUpsertFailed", "Could not upsert candidate vector.", str(exc))

@router.post("/vectors/jobs/upsert")
async def upsert_job_vector(request: JobVectorSyncRequest):
    try:
        normalized_id = f"job_{request.jobId}"
        store.internal_jobs_col.upsert(
            documents=[_safe_json(request.jobData)],
            metadatas=[{"job_id": request.jobId, "source": "Internal API", "title": request.jobData.get("title", ""), "company": request.jobData.get("company", ""), "location": request.jobData.get("location", ""), "json_detailed": _safe_json(request.jobData), "content_hash": request.contentHash or ""}],
            ids=[normalized_id],
        )
        return _envelope_ok({"vectorId": normalized_id, "collection": "job_listings_internal", "model": "chroma"})
    except Exception as exc:
        return _envelope_error("JobVectorUpsertFailed", "Could not upsert job vector.", str(exc))

@router.post("/vectors/candidates/delete")
async def delete_candidate_vector(request: DeleteCandidateVectorRequest):
    try:
        store.candidates_col.delete(ids=[str(request.candidateId)])
        return _envelope_ok(True)
    except Exception as exc:
        return _envelope_error("CandidateVectorDeleteFailed", "Could not delete candidate vector.", str(exc))

@router.post("/vectors/jobs/delete")
async def delete_job_vector(request: DeleteJobVectorRequest):
    try:
        store.internal_jobs_col.delete(ids=[f"job_{request.jobId}"])
        return _envelope_ok(True)
    except Exception as exc:
        return _envelope_error("JobVectorDeleteFailed", "Could not delete job vector.", str(exc))

@router.post("/recommendations/jobs")
async def recommend_jobs(request: JobRecommendationRequest):
    try:
        matches = recommend_jobs_for_candidate(request.candidateId, limit=request.limit) if request.candidateId > 0 else []
        if not matches:
            matches = JobMatcher().match_jobs_from_db(parse_cv_with_llm(request.resumeText), n_results=request.limit)
            
        result = []
        for match in matches:
            target_id = _to_target_id(match.get("db_id") or match.get("job_id") or match.get("external_job_id"))
            if target_id > 0:
                result.append({
                    "targetId": target_id,
                    "targetType": "Job",
                    "score": round(_to_float(match.get("match_score") or match.get("semantic_similarity")), 2),
                    "reason": str(match.get("recommendation") or match.get("match_level") or "Matched by AI."),
                    "previewJson": _safe_json(match),
                })
        return _envelope_ok(result)
    except Exception as exc:
        return _envelope_error("JobRecommendationFailed", "Could not generate job recommendations.", str(exc))

@router.post("/recommendations/candidates")
async def recommend_candidates(request: CandidateRecommendationRequest):
    try:
        result = []
        for match in recommend_candidates_for_job(str(request.jobId), limit=request.limit, min_score=0.0):
            score = _to_float(match.get("score"))
            result.append({
                "targetId": _to_target_id(match.get("candidate_id")),
                "targetType": "Candidate",
                "score": round(score * 100 if 0 <= score <= 1 else score, 2),
                "reason": "Recommended based on profile similarity.",
                "previewJson": _safe_json(match.get("candidate_preview", {})),
            })
        return _envelope_ok(result)
    except Exception as exc:
        return _envelope_error("CandidateRecommendationFailed", "Could not generate candidate recommendations.", str(exc))

@router.post("/scrape/jobs")
async def scrape_jobs(request: ScrapeJobsRequest):
    try:
        scrape_result = await run_scraper(max_categories=request.maxCategories)
        stored = store.scraped_jobs_col.get()
        ids, metadatas = stored.get("ids", []), stored.get("metadatas", [])
        
        jobs = []
        for i, meta in enumerate(metadatas):
            detail = json.loads(meta.get("json_detailed", "{}")) if meta.get("json_detailed") else {}
            src = str(meta.get("source", "") or detail.get("source", "")).strip()
            src_url = str(meta.get("job_page_link", "") or detail.get("job_page_link", "")).strip()
            red_url = str(meta.get("apply_link", "") or detail.get("apply_link", "") or src_url).strip()
            
            if src.lower() in _EXTERNAL_SCRAPED_SOURCE_TOKENS or any(t in f"{src_url} {red_url}".lower() for t in _EXTERNAL_SCRAPED_HOST_TOKENS):
                jobs.append({
                    "source": src,
                    "externalJobId": str(ids[i]) if i < len(ids) else uuid.uuid4().hex,
                    "sourceUrl": src_url,
                    "redirectUrl": red_url,
                    "title": str(meta.get("title", "")),
                    "company": str(meta.get("company", "")),
                    "location": str(detail.get("location", "") or meta.get("location", "")),
                    "city": str(detail.get("city", "") or meta.get("city", "")),
                    "country": str(detail.get("country", "") or meta.get("country", "")),
                    "description": str(detail.get("description", "") or meta.get("description_snippet", "")),
                    "requirements": str(detail.get("requirements", "") or meta.get("requirements_snippet", "")),
                    "responsibilities": str(detail.get("responsibilities", "") or meta.get("responsibilities_snippet", "")),
                    "employmentType": str(detail.get("employment_type", "") or meta.get("employment_type", "")),
                    "experienceLevel": str(detail.get("experience_level", "") or meta.get("experience_level", "")),
                    "enrichmentSource": str(meta.get("enrichment_source", "") or detail.get("_enrichment_source", "")),
                    "skills": [str(s).strip() for s in (detail.get("skills") if isinstance(detail.get("skills"), list) else []) if str(s).strip()],
                    "postedAtUtc": _normalize_iso_datetime(meta.get("posted_time")),
                    "metadata": detail,
                })

        return _envelope_ok({
            "processedCategories": int(scrape_result.get("processed_categories", 0)),
            "upsertedJobs": int(scrape_result.get("upserted_jobs", 0)),
            "totalJobs": int(scrape_result.get("total_jobs", len(jobs))),
            "jobs": jobs,
            "stats": scrape_result.get("stats"),
            "warning": scrape_result.get("warning", "").strip() if scrape_result.get("warning") else None
        })
    except Exception as exc:
        return _envelope_error("ScrapeJobsFailed", "Could not scrape jobs.", str(exc))

@router.post("/integrity/analyze-video")
async def analyze_video(request: VideoAnalysisRequest):
    try:
        now_utc = datetime.now(timezone.utc)
        frame_hash = hashlib.sha1(request.base64Frame.encode("utf-8")).hexdigest()
        state_key = str(request.integritySessionId)
        
        last_state = _VIDEO_FRAME_STATE.get(state_key)
        if last_state is not None:
            last_hash, last_at, last_sequence = last_state
            elapsed = (now_utc - last_at).total_seconds()
            if request.sequence <= last_sequence:
                return _envelope_ok({"events": [], "skipped": True, "reason": "sequence_not_advanced"})
            if frame_hash == last_hash and elapsed < VIDEO_DEDUP_WINDOW_SECONDS:
                return _envelope_ok({"events": [], "skipped": True, "reason": "duplicate_frame"})
            if elapsed < VIDEO_MIN_INTERVAL_SECONDS:
                return _envelope_ok({"events": [], "skipped": True, "reason": "throttled"})

        _VIDEO_FRAME_STATE[state_key] = (frame_hash, now_utc, request.sequence)

        try:
            proc = _active_get(request.integritySessionId)
            if not proc:
                return _envelope_ok({"events": [], "skipped": True, "reason": "processor_not_found"})

            frame = _decode_frame(request.base64Frame)
            if frame is not None:
                proc.push_frame(frame)
                
            state = proc.get_state()
            events = []
            alert_map = {
                "NO_FACE": ("No face detected in frame", "high"),
                "MULTIPLE_FACES": ("Multiple faces detected in frame (Mediapipe)", "high"),
                "LOOKING_LEFT": ("Looking far left", "low"),
                "LOOKING_RIGHT": ("Looking far right", "low"),
                "LOOKING_UP": ("Looking up", "medium"),
                "LOOKING_DOWN": ("Looking down", "medium"),
                "HEAD_TURNED_LEFT": ("Head turned left", "medium"),
                "HEAD_TURNED_RIGHT": ("Head turned right", "medium"),
                "HEAD_TILTED_UP": ("Head tilted up", "medium"),
                "HEAD_TILTED_DOWN": ("Head tilted down", "medium"),
                "HEAD_TILTED_SIDE": ("Head tilted sideways", "low"),
                "EYE_LEFT": ("Eyes looking left", "medium"),
                "EYE_RIGHT": ("Eyes looking right", "medium"),
                "EYE_UP": ("Eyes looking up", "medium"),
                "EYE_DOWN": ("Eyes looking down", "medium"),
                "MULTIPLE_PEOPLE": ("Multiple people detected in background (YOLO)", "high"),
                "CHEATING_ITEM_MOBILE": ("Mobile phone detected in frame (YOLO)", "high"),
            }
            
            consumed = proc.consume_alerts() if hasattr(proc, "consume_alerts") else []
            if not consumed:
                if state.get("current_alert"): consumed.append(state["current_alert"])
                if state.get("yolo_alert") and state["yolo_alert"] != state.get("current_alert"): consumed.append(state["yolo_alert"])
            
            for a in consumed:
                if a in alert_map:
                    events.append({
                        "eventType": a,
                        "severity": alert_map[a][1],
                        "source": "yolo" if "YOLO" in alert_map[a][0] else "vision",
                        "description": alert_map[a][0],
                        "mediaReference": None,
                    })

            return _envelope_ok({"events": events, "skipped": False, "reason": "calibrating" if not events and not state.get("calibrated") else None})

        except Exception as inner_exc:
            return _envelope_ok({"events": [], "skipped": True, "reason": f"processing_error: {str(inner_exc)}"})

    except Exception as exc:
        return _envelope_error("InterviewVideoAnalysisFailed", "Could not analyze video frame.", str(exc))
```

### `routers/recruitment.py`
```py
from __future__ import annotations

import asyncio
import json
import os
import tempfile
from typing import Optional

from fastapi import APIRouter, File, HTTPException, Query, UploadFile

from recruitment.ai_detector import get_ai_detector
from recruitment.ats_service import analyze_ats_with_llm, generate_improvements_with_llm
from recruitment.cv_service import parse_cv_with_docling_llm, parse_cv_with_llm, process_file
from recruitment.matcher_service import (
    JobMatcher,
    recommend_candidates_for_job,
    recommend_jobs_for_candidate,
)
from recruitment.scheduler import scheduler_status
from recruitment.schemas import (
    ATSRequest,
    CVFullAnalysisRequest,
    CVParseTextRequest,
    CandidateEmbeddingRequest,
    ImprovementRequest,
    JobEmbeddingRequest,
    ListResponse,
    StandardResponse,
)
from recruitment.scraper_service import get_scraper_embedding_model, run_scraper
from recruitment.vector_store import store

router = APIRouter(prefix="/api", tags=["recruitment"])

_scrape_task: Optional[asyncio.Task] = None
_scrape_lock = asyncio.Lock()


def _normalize_job_embedding_id(job_id: str) -> str:
    return job_id if str(job_id).startswith("job_") else f"job_{job_id}"


def _on_scrape_done(task: asyncio.Task) -> None:
    global _scrape_task
    try:
        task.result()
    except Exception as exc:
        print(f"[recruitment] scrape task failed: {exc}")
    finally:
        _scrape_task = None


@router.get("/recruitment/status")
def recruitment_status():
    stats = store.stats()
    stats["scheduler"] = scheduler_status()
    stats["scrape_running"] = bool(_scrape_task and not _scrape_task.done())
    return {"success": True, "data": stats}


@router.get("/scraping/jobs", summary="Get scraped jobs with optional keyword/location filter")
async def get_scraped_jobs(
    keyword: Optional[str] = Query(None),
    location: Optional[str] = Query(None),
    limit: int = 50,
):
    try:
        where = {"location": location} if location else None
        if keyword:
            query_vector = get_scraper_embedding_model().encode(keyword).tolist()
            result = store.scraped_jobs_col.query(query_embeddings=[query_vector], n_results=limit, where=where)
            ids = result["ids"][0]
            metadatas = result["metadatas"][0]
        else:
            result = store.scraped_jobs_col.get(limit=limit, where=where)
            ids = result.get("ids", [])
            metadatas = result.get("metadatas", [])

        data = []
        for index, metadata in enumerate(metadatas):
            detail = {}
            try:
                detail = json.loads(metadata.get("json_detailed", "{}"))
            except Exception:
                pass

            source_url = str(metadata.get("job_page_link", "") or detail.get("job_page_link", "") or "")
            apply_link = str(metadata.get("apply_link", "") or detail.get("apply_link", "") or source_url)
            location = str(detail.get("location", "") or metadata.get("location", "") or "")
            city = str(detail.get("city", "") or metadata.get("city", "") or "")
            country = str(detail.get("country", "") or metadata.get("country", "") or "")

            data.append(
                {
                    "db_id": ids[index],
                    "company": metadata.get("company"),
                    "title": metadata.get("title"),
                    "location": location,
                    "city": city,
                    "country": country,
                    "source": metadata.get("source"),
                    "job_page_link": source_url,
                    "apply_link": apply_link,
                    **detail,
                }
            )

        return {"success": True, "count": len(data), "data": data}
    except Exception as exc:
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@router.get("/scraping/status", summary="Check if a scrape task is currently running")
async def get_scraping_status():
    running = bool(_scrape_task and not _scrape_task.done())
    return {
        "success": True,
        "data": {
            "running": running,
            "scheduler": scheduler_status(),
        },
    }


@router.post("/scraping/trigger", status_code=202, summary="Manually trigger a background scrape")
async def trigger_scrape(
    max_categories: Optional[int] = Query(None, ge=1),
):
    global _scrape_task

    async with _scrape_lock:
        if _scrape_task and not _scrape_task.done():
            return {
                "success": True,
                "message": "Scraping is already running.",
            }

        _scrape_task = asyncio.create_task(run_scraper(max_categories=max_categories))
        _scrape_task.add_done_callback(_on_scrape_done)
        return {
            "success": True,
            "message": "Scraping job queued.",
        }


@router.post("/cv/parse", response_model=StandardResponse, summary="Upload CV file and get structured JSON")
async def parse_cv_file(file: UploadFile = File(...), mode: str = Query("llm")):
    temp_path = None
    try:
        extension = file.filename.split(".")[-1]
        with tempfile.NamedTemporaryFile(delete=False, suffix=f".{extension}") as handle:
            handle.write(await file.read())
            temp_path = handle.name

        if mode == "docling":
            parsed = parse_cv_with_docling_llm(temp_path)
        else:
            cv_text, _ = process_file(temp_path)
            if not cv_text:
                return StandardResponse(success=False, message="Could not extract text from file.")
            parsed = parse_cv_with_llm(cv_text)

        return StandardResponse(success=True, data=parsed)
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))
    finally:
        if temp_path and os.path.exists(temp_path):
            os.remove(temp_path)


@router.post("/cv/parse-text", response_model=StandardResponse, summary="Parse raw CV text")
async def parse_cv_text(req: CVParseTextRequest):
    try:
        parsed = parse_cv_with_llm(req.resume_text)
        return StandardResponse(success=True, data=parsed)
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.post("/cv/ats-score", response_model=StandardResponse, summary="Get ATS score for CV text")
async def ats_score(req: ATSRequest):
    try:
        parsed = parse_cv_with_llm(req.resume_text)
        ats_result = analyze_ats_with_llm(parsed, req.resume_text)
        return StandardResponse(success=True, data=ats_result)
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.post("/cv/improvements", response_model=StandardResponse, summary="Generate CV improvements")
async def cv_improvements(req: ImprovementRequest):
    try:
        parsed = parse_cv_with_llm(req.resume_text)
        ats_result = analyze_ats_with_llm(parsed, req.resume_text)
        improvements = generate_improvements_with_llm(parsed, ats_result)
        return StandardResponse(success=True, data=improvements)
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.post("/cv/full-analysis", response_model=StandardResponse, summary="Run full notebook-equivalent CV pipeline")
async def full_cv_analysis(req: CVFullAnalysisRequest):
    try:
        parsed = parse_cv_with_llm(req.resume_text)
        ai_result = get_ai_detector().analyze_text(req.resume_text)
        ats_result = analyze_ats_with_llm(parsed, req.resume_text)

        data = {
            "parsed_cv": parsed,
            "ai_detection": ai_result,
            "ats_result": ats_result,
            "job_matches": JobMatcher().match_jobs_from_db(parsed, n_results=req.job_match_limit),
        }

        if req.include_improvements:
            data["improvements"] = generate_improvements_with_llm(parsed, ats_result)

        return StandardResponse(success=True, data=data)
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.post("/embeddings/candidate", response_model=StandardResponse)
async def create_candidate_embedding(req: CandidateEmbeddingRequest):
    try:
        document = json.dumps(req.profile_data)
        store.candidates_col.upsert(
            documents=[document],
            metadatas=[{"candidate_id": req.candidate_id}],
            ids=[str(req.candidate_id)],
        )
        return StandardResponse(
            success=True,
            message="Candidate embedding created.",
            data={"embedding_id": req.candidate_id},
        )
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.put("/embeddings/candidate/{candidate_id}", response_model=StandardResponse)
async def update_candidate_embedding(candidate_id: str, req: CandidateEmbeddingRequest):
    try:
        store.candidates_col.update(
            ids=[candidate_id],
            documents=[json.dumps(req.profile_data)],
            metadatas=[{"candidate_id": req.candidate_id}],
        )
        return StandardResponse(success=True, message="Candidate embedding updated.")
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.delete("/embeddings/candidate/{candidate_id}", response_model=StandardResponse)
async def delete_candidate_embedding(candidate_id: str):
    try:
        store.candidates_col.delete(ids=[candidate_id])
        return StandardResponse(success=True, message="Candidate embedding deleted.")
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.post("/embeddings/job", response_model=StandardResponse)
async def create_job_embedding(req: JobEmbeddingRequest):
    try:
        job_id = _normalize_job_embedding_id(str(req.job_id))
        store.internal_jobs_col.upsert(
            documents=[json.dumps(req.job_data)],
            metadatas=[
                {
                    "job_id": req.job_id,
                    "source": "Internal API",
                    "title": req.job_data.get("title", ""),
                    "company": req.job_data.get("company", ""),
                    "location": req.job_data.get("location", ""),
                    "json_detailed": json.dumps(req.job_data),
                }
            ],
            ids=[job_id],
        )
        return StandardResponse(success=True, message="Job embedding created.", data={"embedding_id": job_id})
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.put("/embeddings/job/{job_id}", response_model=StandardResponse)
async def update_job_embedding(job_id: str, req: JobEmbeddingRequest):
    try:
        normalized_job_id = _normalize_job_embedding_id(job_id)
        store.internal_jobs_col.update(
            ids=[normalized_job_id],
            documents=[json.dumps(req.job_data)],
            metadatas=[{"job_id": req.job_id, "json_detailed": json.dumps(req.job_data)}],
        )
        return StandardResponse(success=True, message="Job embedding updated.")
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.delete("/embeddings/job/{job_id}", response_model=StandardResponse)
async def delete_job_embedding(job_id: str):
    try:
        store.internal_jobs_col.delete(ids=[_normalize_job_embedding_id(job_id)])
        return StandardResponse(success=True, message="Job embedding deleted.")
    except Exception as exc:
        return StandardResponse(success=False, message=str(exc))


@router.get("/recommendations/jobs/{candidate_id}", response_model=ListResponse)
async def recommend_jobs(candidate_id: int, limit: int = 10):
    try:
        data = recommend_jobs_for_candidate(candidate_id, limit=limit)
        if not data:
            return ListResponse(success=False, message="Candidate not found or no matches.")
        return ListResponse(success=True, data=data)
    except Exception as exc:
        return ListResponse(success=False, message=str(exc))


@router.get("/recommendations/candidates/{job_id}", response_model=ListResponse)
async def recommend_candidates(job_id: str, limit: int = 50, min_score: float = 0.3):
    try:
        data = recommend_candidates_for_job(job_id, limit=limit, min_score=min_score)
        if not data:
            return ListResponse(success=False, message="Job not found or no candidates match criteria.")
        return ListResponse(success=True, data=data)
    except Exception as exc:
        return ListResponse(success=False, message=str(exc))


@router.post("/recommendations/match-from-text", response_model=ListResponse)
async def match_from_cv_text(req: CVParseTextRequest, limit: int = 5):
    try:
        parsed = parse_cv_with_llm(req.resume_text)
        matches = JobMatcher().match_jobs_from_db(parsed, n_results=limit)
        return ListResponse(success=True, data=matches)
    except Exception as exc:
        return ListResponse(success=False, message=str(exc))
```

### `routers/__init__.py`
```py

```

