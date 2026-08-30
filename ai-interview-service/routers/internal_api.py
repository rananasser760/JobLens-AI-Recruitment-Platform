import asyncio
import base64
import json
import os
import tempfile
import uuid
from datetime import datetime, timezone
from typing import Any

import imageio_ffmpeg
from fastapi import APIRouter
from pydantic import BaseModel, Field

from request_context import get_request_id
from response import InterviewProviderError, generate_interview_response, generate_interview_summary
from stt import speech_to_text
from session_store import INTERVIEW_SESSIONS
from tts import text_to_speech_file

router = APIRouter(prefix="/internal/v1", tags=["internal"])

DEFAULT_TTS_TIMEOUT_SECONDS = 12.0
TTS_TIMEOUT_ENV_VAR = "JOBLENS_TTS_TIMEOUT_SECONDS"

class StartInterviewSessionRequest(BaseModel):
    candidateName: str
    candidateId: str
    resumeText: str
    jobDescription: str
    evaluationCriteria: str
    maxQuestions: int = Field(default=5, ge=1, le=20)

class AudioAnalysisRequest(BaseModel):
    interviewSessionId: str
    base64Audio: str
    sequence: int = Field(default=1, ge=1)

class TranscriptEntryRequest(BaseModel):
    sequence: int
    speaker: str
    content: str
    occurredAtUtc: datetime | None = None

class FinalizeInterviewRequest(BaseModel):
    interviewSessionId: str
    integritySessionId: str | None = None
    transcript: list[TranscriptEntryRequest] = Field(default_factory=list)

def _envelope_ok(data: Any) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": True, "data": data, "error": None}

def _envelope_error(code: str, message: str, details: str | None = None) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": False, "data": None, "error": {"code": code, "message": message, "details": details}}

def _decode_data_uri_base64(value: str) -> bytes:
    payload = value.split(",", 1)[1] if "," in value else value
    return base64.b64decode(payload)

def _to_wav(src_path: str, dst_path: str) -> None:
    ffmpeg_path = imageio_ffmpeg.get_ffmpeg_exe()
    subprocess.run([ffmpeg_path, "-y", "-v", "error", "-i", src_path, "-ar", "16000", "-ac", "1", dst_path], capture_output=True, text=True, check=True)

@router.post("/interviews/sessions")
async def start_interview_session(request: StartInterviewSessionRequest):
    try:
        interview_session_id = str(uuid.uuid4())
        INTERVIEW_SESSIONS[interview_session_id] = {
            "cv_text": request.resumeText,
            "job_description": request.jobDescription,
            "criteria": request.evaluationCriteria,
            "history": [],
            "turn_count": 0,
            "max_questions": request.maxQuestions,
            "summary": None,
            "candidate_name": request.candidateName,
            "candidate_id": request.candidateId,
            "integrity_id": None,
        }
        return _envelope_ok({
            "interviewSessionId": interview_session_id,
            "maxQuestions": request.maxQuestions,
            "welcomeMessage": f"Welcome {request.candidateName.strip() or 'candidate'}. Let's start the interview.",
        })
    except Exception as exc:
        return _envelope_error("InterviewStartFailed", "Could not initialize interview session.", str(exc))

@router.post("/interviews/analyze-audio")
async def analyze_audio(request: AudioAnalysisRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        flags, transcript, reply_audio_base64, reply_audio_mime_type = [], "", None, None
        temp_raw_path, temp_wav_path = None, None

        try:
            with tempfile.NamedTemporaryFile(delete=False, suffix=".webm") as raw_file:
                raw_file.write(_decode_data_uri_base64(request.base64Audio))
                temp_raw_path = raw_file.name

            with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as wav_file:
                temp_wav_path = wav_file.name

            _to_wav(temp_raw_path, temp_wav_path)
            transcript = (speech_to_text(temp_wav_path) or "").strip()
            if not transcript: flags.append("empty_transcript")
        except Exception:
            flags.append("audio_processing_error")
        finally:
            for p in [temp_raw_path, temp_wav_path]:
                if p and os.path.exists(p): os.remove(p)

        if not transcript: transcript = "Could not transcribe candidate response clearly."

        session.setdefault("history", []).append({"role": "user", "content": transcript})
        session["turn_count"] = int(session.get("turn_count", 0)) + 1

        try:
            reply = generate_interview_response(transcript, session["history"], session.get("cv_text", ""), session.get("job_description", ""))
        except InterviewProviderError as exc:
            return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

        session["history"].append({"role": "assistant", "content": reply})

        if reply.strip():
            try:
                tts_path = await asyncio.wait_for(asyncio.to_thread(text_to_speech_file, reply), timeout=float(os.getenv(TTS_TIMEOUT_ENV_VAR, DEFAULT_TTS_TIMEOUT_SECONDS)))
                with open(tts_path, "rb") as f:
                    reply_audio_base64 = base64.b64encode(f.read()).decode("utf-8")
                reply_audio_mime_type = "audio/mpeg" if tts_path.lower().endswith(".mp3") else "audio/wav"
                os.remove(tts_path)
            except Exception as exc:
                flags.append("tts_generation_error")

        is_complete = session["turn_count"] >= int(session.get("max_questions", 5))
        score = None
        if is_complete:
            try:
                summary = generate_interview_summary(session["history"], session.get("cv_text", ""), session.get("job_description", ""), session.get("criteria", ""))
                session["summary"] = summary
                score = float(summary.get("score", 0.0)) if isinstance(summary, dict) else None
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc))

        return _envelope_ok({
            "transcript": transcript, "reply": reply, "isComplete": is_complete,
            "score": score, "flags": flags, "replyAudioBase64": reply_audio_base64, "replyAudioMimeType": reply_audio_mime_type
        })
    except Exception as exc:
        return _envelope_error("InterviewAudioAnalysisFailed", "Could not analyze interview audio.", str(exc))

@router.post("/interviews/finalize")
async def finalize_interview(request: FinalizeInterviewRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        summary = session.get("summary")
        if not isinstance(summary, dict):
            history = session.get("history", [])
            if not history and request.transcript:
                history = [{"role": "assistant" if item.speaker.lower() in ("assistant", "interviewer") else "user", "content": item.content} for item in sorted(request.transcript, key=lambda i: i.sequence)]
            try:
                summary = generate_interview_summary(history, session.get("cv_text", ""), session.get("job_description", ""), session.get("criteria", ""))
                session["summary"] = summary
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc))

        return _envelope_ok({
            "finalScore": float(summary.get("score", 0.0)),
            "verdict": str(summary.get("recommendation", "Requires manual review")),
            "recruiterReportJson": json.dumps(summary, ensure_ascii=False),
            "candidateFeedbackJson": json.dumps({"review": summary.get("review", ""), "strengths": summary.get("strengths", []), "weaknesses": summary.get("weaknesses", []), "recommendation": summary.get("recommendation", "")}, ensure_ascii=False),
        })
    except Exception as exc:
        return _envelope_error("InterviewFinalizeFailed", "Could not finalize interview.", str(exc))