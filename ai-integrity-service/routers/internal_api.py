from __future__ import annotations

import asyncio
import base64
import hashlib
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Any, Union

import cv2
import numpy as np
from fastapi import APIRouter
from pydantic import BaseModel, Field

from models import DBSession, DBSessionLocal
from request_context import get_request_id
from routers.integrity import WebSessionProcessor

router = APIRouter(prefix="/internal/v1", tags=["internal"])

VIDEO_DEDUP_WINDOW_SECONDS = 1.0
VIDEO_MIN_INTERVAL_SECONDS = 0.45
PROCESSOR_IDLE_TIMEOUT_SECONDS = 20 * 60
_STATE_LOCK = threading.Lock()
_VIDEO_FRAME_STATE: dict[str, tuple[str, datetime, int]] = {}
_PROCESSORS_LOCK = threading.Lock()
_INTERNAL_PROCESSORS: dict[str, WebSessionProcessor] = {}
_LAST_FRAME_AT: dict[str, float] = {}

ALERT_MAP: dict[str, tuple[str, str]] = {
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


class EndIntegritySessionRequest(BaseModel):
    integritySessionId: str


class VideoAnalysisRequest(BaseModel):
    integritySessionId: Union[str, int, Any] = Field(default="1", alias="integritySessionId")
    base64Frame: str = Field(alias="base64Frame")
    sequence: int = Field(default=1, ge=1)

    class Config:
        populate_by_name = True
        allow_population_by_field_name = True


def _envelope_ok(data: Any) -> dict[str, Any]:
    return {
        "requestId": get_request_id() or uuid.uuid4().hex,
        "success": True,
        "data": data,
        "error": None,
    }


def _envelope_error(code: str, message: str, details: str | None = None) -> dict[str, Any]:
    return {
        "requestId": get_request_id() or uuid.uuid4().hex,
        "success": False,
        "data": None,
        "error": {"code": code, "message": message, "details": details},
    }


def _processor_alive(proc: WebSessionProcessor) -> bool:
    thread = getattr(proc, "_thread", None)
    return bool(getattr(proc, "_running", False)) and thread is not None and thread.is_alive()


def _create_processor(session_key: str) -> WebSessionProcessor:
    db = DBSessionLocal()
    try:
        record = DBSession(interview_session_id=session_key[:100])
        db.add(record)
        db.commit()
        db.refresh(record)
        proc = WebSessionProcessor(record.id)
    finally:
        db.close()

    proc.auto_abandon_enabled = False
    proc.start()
    return proc


def _finalize_processor(proc: WebSessionProcessor) -> None:
    db = DBSessionLocal()
    try:
        proc.finalize(db)
    except Exception:
        pass
    finally:
        db.close()


def _get_or_create_processor(session_key: str) -> WebSessionProcessor:
    with _PROCESSORS_LOCK:
        proc = _INTERNAL_PROCESSORS.get(session_key)
        if proc is not None and _processor_alive(proc):
            _LAST_FRAME_AT[session_key] = time.time()
            return proc

        proc = _create_processor(session_key)
        _INTERNAL_PROCESSORS[session_key] = proc
        _LAST_FRAME_AT[session_key] = time.time()
        return proc


def _pop_processor(session_key: str) -> WebSessionProcessor | None:
    with _PROCESSORS_LOCK:
        _LAST_FRAME_AT.pop(session_key, None)
        return _INTERNAL_PROCESSORS.pop(session_key, None)


def _sweep_idle_processors() -> None:
    cutoff = time.time() - PROCESSOR_IDLE_TIMEOUT_SECONDS
    with _PROCESSORS_LOCK:
        idle_keys = [key for key, last in _LAST_FRAME_AT.items() if last < cutoff]
    for key in idle_keys:
        proc = _pop_processor(key)
        if proc is not None:
            _finalize_processor(proc)
    with _STATE_LOCK:
        for key in idle_keys:
            _VIDEO_FRAME_STATE.pop(key, None)


def _decode_frame(base64_frame: str) -> np.ndarray | None:
    try:
        payload = base64_frame.split(",", 1)[1] if "," in base64_frame else base64_frame
        raw_bytes = base64.b64decode(payload.strip())
        frame_array = np.frombuffer(raw_bytes, dtype=np.uint8)
        return cv2.imdecode(frame_array, cv2.IMREAD_COLOR)
    except Exception:
        return None


@router.post("/integrity/analyze-video")
async def analyze_video(request: VideoAnalysisRequest):
    try:
        now_utc = datetime.now(timezone.utc)
        frame_hash = hashlib.sha1(request.base64Frame.encode("utf-8")).hexdigest()
        state_key = str(request.integritySessionId)

        with _STATE_LOCK:
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

            if len(_VIDEO_FRAME_STATE) > 1000:
                cutoff = now_utc.timestamp() - 3600
                expired_keys = [k for k, v in _VIDEO_FRAME_STATE.items() if v[1].timestamp() < cutoff]
                for k in expired_keys:
                    _VIDEO_FRAME_STATE.pop(k, None)

        await asyncio.to_thread(_sweep_idle_processors)
        proc = await asyncio.to_thread(_get_or_create_processor, state_key)

        frame = await asyncio.to_thread(_decode_frame, request.base64Frame)
        if frame is not None:
            proc.push_frame(frame)

        state = proc.get_state()
        consumed = proc.consume_alerts() if hasattr(proc, "consume_alerts") else []
        if not consumed:
            if state.get("current_alert"):
                consumed.append(state["current_alert"])
            if state.get("yolo_alert") and state["yolo_alert"] != state.get("current_alert"):
                consumed.append(state["yolo_alert"])

        events = []
        for a in consumed:
            if a in ALERT_MAP:
                desc, severity = ALERT_MAP[a]
                events.append({
                    "eventType": a,
                    "severity": severity,
                    "source": "yolo" if "YOLO" in desc else "vision",
                    "description": desc,
                    "mediaReference": None,
                })

        return _envelope_ok({
            "events": events,
            "skipped": False,
            "reason": "calibrating" if not events and not state.get("calibrated") else None,
        })

    except Exception as exc:
        return _envelope_error("InterviewVideoAnalysisFailed", "Could not analyze video frame.", str(exc))


@router.post("/integrity/sessions/end")
async def end_integrity_session(request: EndIntegritySessionRequest):
    try:
        key = str(request.integritySessionId)
        proc = _pop_processor(key)
        with _STATE_LOCK:
            _VIDEO_FRAME_STATE.pop(key, None)
        if proc is not None:
            await asyncio.to_thread(_finalize_processor, proc)
        return _envelope_ok(True)
    except Exception as exc:
        return _envelope_error("IntegritySessionEndFailed", "Could not close integrity session.", str(exc))
