# ══════════════════════════════════════════════════════════════════════════════
#  models.py
#  Single source of truth for every DB table.
#  Both routers import engine / DBSessionLocal / all model classes from here.
# ══════════════════════════════════════════════════════════════════════════════

from datetime import datetime

from sqlalchemy import (
    create_engine, Column, Integer, Float, String,
    DateTime, ForeignKey, Text, LargeBinary
)
from sqlalchemy.ext.declarative import declarative_base
from sqlalchemy.orm import sessionmaker, relationship

# ── Engine ────────────────────────────────────────────────────────────────────
DATABASE_URL = "sqlite:///./joblens.db"
engine = create_engine(DATABASE_URL, connect_args={"check_same_thread": False})
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
    import sqlite3

    db_path = "./joblens.db"

    # (table, column, sql_type)
    migrations = [
        ("sessions",    "candidate_name",          "VARCHAR(200)"),
        ("sessions",    "candidate_id",             "VARCHAR(100)"),
        ("sessions",    "interview_session_id",     "VARCHAR(100)"),
        ("sessions",    "interview_score",          "FLOAT"),
        ("sessions",    "interview_summary_json",   "TEXT"),
        ("alerts",      "elapsed_secs",             "FLOAT DEFAULT 0.0"),
        ("yolo_alerts", "elapsed_secs",             "FLOAT DEFAULT 0.0"),
    ]

    try:
        conn   = sqlite3.connect(db_path)
        cursor = conn.cursor()

        for table, column, definition in migrations:
            cursor.execute(f"PRAGMA table_info({table})")
            existing = [row[1] for row in cursor.fetchall()]
            if column not in existing:
                cursor.execute(
                    f"ALTER TABLE {table} ADD COLUMN {column} {definition}"
                )
                print(f"[migration] added '{column}' to '{table}'")

        # keyframes table may not exist on very old installs
        cursor.execute("""
            CREATE TABLE IF NOT EXISTS keyframes (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id   INTEGER REFERENCES sessions(id),
                alert_type   VARCHAR(100),
                timestamp    DATETIME DEFAULT CURRENT_TIMESTAMP,
                elapsed_secs FLOAT DEFAULT 0.0,
                image_data   BLOB
            )
        """)

        conn.commit()
        conn.close()
        print("[migration] complete")

    except Exception as exc:
        print(f"[migration] warning: {exc}")


run_migrations()
