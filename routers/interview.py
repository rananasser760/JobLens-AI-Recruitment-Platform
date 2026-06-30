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
import asyncio
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


def _is_integrity_abandoned(integrity_id: Optional[int]) -> bool:
    if integrity_id is None:
        return False

    db = DBSessionLocal()
    try:
        row = db.query(DBSession).filter(DBSession.id == integrity_id).first()
        return bool(row and row.recommendation == "ABANDONED")
    except Exception:
        return False
    finally:
        db.close()


def _is_ws_authorized(websocket: WebSocket) -> bool:
    expected_key = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()
    if not expected_key:
        return True

    provided_key = (websocket.headers.get("x-api-key") or websocket.query_params.get("api_key") or "").strip()
    return provided_key == expected_key


# ══════════════════════════════════════════════════════════════════════════════
#  ENDPOINTS
# ══════════════════════════════════════════════════════════════════════════════

@router.post("/start")
async def start_interview(
    cv_text: str = Form(...),
    job_description: str = Form(...),
    evaluation_criteria: str = Form(...),
    max_questions: int = Form(5),
    candidate_name: Optional[str] = Form(None),
    candidate_id:   Optional[str] = Form(None),
    integrity_db_session_id: Optional[int] = Form(None),
):
    """
    Create a new interview session with Job Description and Criteria.
    """
    sid = str(uuid.uuid4())
    INTERVIEW_SESSIONS[sid] = {
        "cv_text":         cv_text,
        "job_description": job_description,
        "criteria":        evaluation_criteria,
        "history":         [],
        "turn_count":      0,
        "max_questions":   max_questions,
        "summary":         None,
        "candidate_name":  candidate_name,
        "candidate_id":    candidate_id,
        "integrity_id":    integrity_db_session_id,
    }

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
    """
    if not _is_ws_authorized(websocket):
        await websocket.accept()
        await websocket.close(code=1008)
        return

    await websocket.accept()

    if session_id not in INTERVIEW_SESSIONS:
        await websocket.close(code=1008)
        return

    sess = INTERVIEW_SESSIONS[session_id]
    integrity_id = sess.get("integrity_id")

    try:
        while True:
            if _is_integrity_abandoned(integrity_id):
                ai_text = (
                    "This interview session has been stopped because the linked integrity "
                    "monitoring session was abandoned."
                )
                try:
                    await websocket.send_text(json.dumps({
                        "type":        "transcript",
                        "user":        "(System)",
                        "ai":          ai_text,
                        "is_complete": True,
                    }))
                except Exception:
                    pass
                push_log("[INTERVIEW] stopped due to abandoned integrity session", {"sid": session_id})
                await websocket.close(code=1000)
                break

            try:
                audio_bytes = await asyncio.wait_for(websocket.receive_bytes(), timeout=2.0)
            except asyncio.TimeoutError:
                continue

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

                if not transcript or not transcript.strip():
                    print("[interview] STT returned empty. Asking user to repeat.")
                    ai_text = "I'm sorry, I didn't quite catch that. Could you please repeat your answer?"
                    
                    await websocket.send_text(json.dumps({
                        "type":        "transcript",
                        "user":        "(Silence / Inaudible)",
                        "ai":          ai_text,
                        "is_complete": False,
                    }))

                    try:
                        audio_out = text_to_speech_file(ai_text)
                        with open(audio_out, "rb") as f:
                            await websocket.send_bytes(f.read())
                    except Exception as tts_exc:
                        print(f"[interview] TTS warning: {tts_exc}")
                        audio_out = None
                        
                    continue 

                sess["history"].append({"role": "user", "content": transcript})
                sess["turn_count"] += 1
                is_complete = sess["turn_count"] >= sess["max_questions"]

                if is_complete:
                    summary = generate_interview_summary(
                        chat_history=sess["history"],
                        cv_text=sess["cv_text"],
                        job_description=sess["job_description"],
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
                        job_description=sess["job_description"],
                    )

                sess["history"].append({"role": "assistant", "content": ai_text})

                await websocket.send_text(json.dumps({
                    "type":        "transcript",
                    "user":        transcript,
                    "ai":          ai_text,
                    "is_complete": is_complete,
                }))

                # 2. send TTS audio bytes
                try:
                    audio_out = text_to_speech_file(ai_text)
                    with open(audio_out, "rb") as f:
                        await websocket.send_bytes(f.read())
                except Exception as tts_exc:
                    print(f"[interview] TTS warning: {tts_exc}")
                    audio_out = None

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