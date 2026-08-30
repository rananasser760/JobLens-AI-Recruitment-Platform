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
