# ══════════════════════════════════════════════════════════════════════════════
#  routers/interview.py
#  AI interview endpoints.
#  Ported from interview_main.py — uses shared INTERVIEW_SESSIONS from
#  session_store.py and writes final summary back to DBSession via models.py.
# ══════════════════════════════════════════════════════════════════════════════

import json
import os
import subprocess
import uuid
from typing import Optional

import imageio_ffmpeg
from fastapi import APIRouter, Form, HTTPException, WebSocket, WebSocketDisconnect

from models import DBSession, DBSessionLocal
from session_store import (
    INTERVIEW_SESSIONS,
    push_log,
    link_integrity_to_interview,
)

# These three modules stay in the project root (unchanged)
from response import generate_interview_response, generate_interview_summary
from tts import text_to_speech_file
from stt import speech_to_text

router = APIRouter(prefix="/interview", tags=["interview"])


# ══════════════════════════════════════════════════════════════════════════════
#  HELPERS
# ══════════════════════════════════════════════════════════════════════════════

def _cleanup(*paths):
    for p in paths:
        if p and os.path.exists(p):
            try: os.remove(p)
            except Exception as e: print(f"cleanup warning: {e}")


def _to_wav(src: str, dst: str) -> None:
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    if not os.path.exists(src):
        raise RuntimeError(f"Input missing: {src}")
    if os.path.getsize(src) == 0:
        raise RuntimeError("Input file is empty (0 bytes)")
    result = subprocess.run(
        [ffmpeg, "-y", "-v", "error", "-i", src, "-ar", "16000", "-ac", "1", dst],
        capture_output=True, text=True
    )
    if result.returncode != 0:
        raise RuntimeError(f"FFmpeg error: {result.stderr}")


def _persist_summary(interview_sid: str, summary: dict) -> None:
    """
    Write the interview summary + score back to the linked DBSession row
    so the unified report can include it.
    """
    db = DBSessionLocal()
    try:
        row = db.query(DBSession).filter(
            DBSession.interview_session_id == interview_sid
        ).first()
        if row:
            score = None
            # try to extract a numeric score from the summary dict
            for key in ("score", "overall_score", "total_score", "rating"):
                if key in summary:
                    try: score = float(summary[key])
                    except: pass
                    break
            row.interview_summary_json = json.dumps(summary, ensure_ascii=False)
            if score is not None:
                row.interview_score = score
            db.commit()
    except Exception as e:
        print(f"[interview] persist summary error: {e}")
        db.rollback()
    finally:
        db.close()


# ══════════════════════════════════════════════════════════════════════════════
#  ENDPOINTS
# ══════════════════════════════════════════════════════════════════════════════

@router.post("/start")
async def start_interview(
    cv_text: str = Form(...),
    max_questions: int = Form(5),
    evaluation_criteria: str = Form(
        "Technical accuracy, clarity, and relevance to the CV"
    ),
    candidate_name: Optional[str] = Form(None),
    candidate_id:   Optional[str] = Form(None),
    # Pass the DB session id if integrity monitoring was already started
    integrity_db_session_id: Optional[int] = Form(None),
):
    """
    Create a new interview session.

    Flow:
    1.  Frontend calls POST /api/sessions/start  → gets integrity db_session_id
    2.  Frontend calls POST /interview/start      → passes integrity_db_session_id
    3.  This endpoint links the two and returns interview_session_id
    """
    sid = str(uuid.uuid4())
    INTERVIEW_SESSIONS[sid] = {
        "cv_text":        cv_text,
        "history":        [],
        "turn_count":     0,
        "max_questions":  max_questions,
        "criteria":       evaluation_criteria,
        "summary":        None,
        "candidate_name": candidate_name,
        "candidate_id":   candidate_id,
        "integrity_id":   integrity_db_session_id,
    }

    # Persist the interview_session_id on the DBSession row so the unified
    # report endpoint can join on it later.
    if integrity_db_session_id is not None:
        link_integrity_to_interview(sid, integrity_db_session_id)
        db = DBSessionLocal()
        try:
            row = db.query(DBSession).filter(
                DBSession.id == integrity_db_session_id
            ).first()
            if row:
                row.interview_session_id = sid
                if candidate_name: row.candidate_name = candidate_name
                if candidate_id:   row.candidate_id   = candidate_id
                db.commit()
        except Exception as e:
            print(f"[interview] link error: {e}"); db.rollback()
        finally:
            db.close()

    push_log("[INTERVIEW] session started",
             {"sid": sid, "integrity_id": integrity_db_session_id})

    return {
        "interview_session_id": sid,
        "max_questions":        max_questions,
        "message":              f"Session started. Limit: {max_questions} questions.",
    }


@router.websocket("/ws/{session_id}")
async def ws_interview(websocket: WebSocket, session_id: str):
    """
    Bidirectional WebSocket for the live interview.

    Client sends  : raw audio bytes  (webm)
    Server sends  :
        1. JSON  {"type":"transcript","user":"…","ai":"…","is_complete":bool}
        2. bytes  TTS audio (mp3 / wav)
    """
    await websocket.accept()

    if session_id not in INTERVIEW_SESSIONS:
        await websocket.close(code=1008)
        return

    sess = INTERVIEW_SESSIONS[session_id]

    try:
        while True:
            audio_bytes = await websocket.receive_bytes()

            uid        = uuid.uuid4()
            raw_path   = f"temp_raw_{uid}.webm"
            wav_path   = f"temp_clean_{uid}.wav"
            audio_out  = None

            try:
                with open(raw_path, "wb") as f:
                    f.write(audio_bytes)

                _to_wav(raw_path, wav_path)
                transcript = speech_to_text(wav_path)
                print(f"[interview] user ({session_id}): {transcript}")

                sess["history"].append({"role": "user", "content": transcript})
                sess["turn_count"] += 1
                is_complete = sess["turn_count"] >= sess["max_questions"]

                if is_complete:
                    summary = generate_interview_summary(
                        chat_history=sess["history"],
                        cv_text=sess["cv_text"],
                        criteria=sess["criteria"],
                    )
                    sess["summary"] = summary
                    _persist_summary(session_id, summary)
                    ai_text = (
                        "Thank you for your time. This concludes our interview. "
                        "Please check your screen for your review and final score."
                    )
                    push_log("[INTERVIEW] completed",
                             {"sid": session_id, "turns": sess["turn_count"]})
                else:
                    ai_text = generate_interview_response(
                        current_transcript=transcript,
                        chat_history=sess["history"],
                        cv_text=sess["cv_text"],
                    )

                sess["history"].append({"role": "assistant", "content": ai_text})

                # 1. send transcript JSON first
                await websocket.send_text(json.dumps({
                    "type":        "transcript",
                    "user":        transcript,
                    "ai":          ai_text,
                    "is_complete": is_complete,
                }))

                # 2. send TTS audio bytes
                audio_out = text_to_speech_file(ai_text)
                with open(audio_out, "rb") as f:
                    await websocket.send_bytes(f.read())

            finally:
                _cleanup(raw_path, wav_path, audio_out)

            if is_complete:
                await websocket.close()
                break

    except WebSocketDisconnect:
        print(f"[interview] client disconnected: {session_id}")
    except Exception as e:
        print(f"[interview] WS error: {e}")
        try: await websocket.close(code=1011)
        except: pass


@router.get("/{session_id}/summary")
def get_summary(session_id: str):
    if session_id not in INTERVIEW_SESSIONS:
        raise HTTPException(status_code=404, detail="Session not found")
    summary = INTERVIEW_SESSIONS[session_id].get("summary")
    if not summary:
        return {"status": "in_progress", "summary": None}
    return {"status": "completed", "summary": summary}


@router.get("/{session_id}/history")
def get_history(session_id: str):
    """Return the full conversation history (useful for HR review)."""
    if session_id not in INTERVIEW_SESSIONS:
        raise HTTPException(status_code=404, detail="Session not found")
    sess = INTERVIEW_SESSIONS[session_id]
    return {
        "session_id":    session_id,
        "turn_count":    sess["turn_count"],
        "max_questions": sess["max_questions"],
        "history":       sess["history"],
        "is_complete":   sess["turn_count"] >= sess["max_questions"],
    }
