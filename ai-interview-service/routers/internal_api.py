from __future__ import annotations

import asyncio
import base64
import json
import logging
import os
import subprocess
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
logger = logging.getLogger(__name__)

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


def _decode_data_uri_base64(value: str) -> bytes:
    payload = value.split(",", 1)[1] if "," in value else value
    return base64.b64decode(payload)


def _to_wav(src_path: str, dst_path: str) -> None:
    ffmpeg_path = imageio_ffmpeg.get_ffmpeg_exe()
    subprocess.run(
        [ffmpeg_path, "-y", "-v", "error", "-i", src_path, "-ar", "16000", "-ac", "1", dst_path],
        capture_output=True,
        text=True,
        check=True,
    )


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
            "integrity_id": interview_session_id,
            "lock": asyncio.Lock(),
        }
        return _envelope_ok({
            "interviewSessionId": interview_session_id,
            "integritySessionId": interview_session_id,
            "maxQuestions": request.maxQuestions,
            "welcomeMessage": f"Welcome {request.candidateName.strip() or 'candidate'}. Let's start the interview.",
        })
    except Exception as exc:
        logger.exception("Failed to start interview session")
        return _envelope_error("InterviewStartFailed", "Could not initialize interview session.", str(exc))


@router.post("/interviews/analyze-audio")
async def analyze_audio(request: AudioAnalysisRequest):
    session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
    if session is None:
        return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

    session_lock: asyncio.Lock = session.setdefault("lock", asyncio.Lock())

    async with session_lock:
        try:
            flags: list[str] = []
            transcript = ""
            reply_audio_base64 = None
            reply_audio_mime_type = None
            temp_raw_path = None
            temp_wav_path = None

            try:
                with tempfile.NamedTemporaryFile(delete=False, suffix=".bin") as raw_file:
                    raw_file.write(_decode_data_uri_base64(request.base64Audio))
                    temp_raw_path = raw_file.name

                with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as wav_file:
                    temp_wav_path = wav_file.name

                await asyncio.to_thread(_to_wav, temp_raw_path, temp_wav_path)
                stt_raw = await asyncio.to_thread(speech_to_text, temp_wav_path)
                transcript = (stt_raw or "").strip()
                if not transcript:
                    flags.append("empty_transcript")
            except Exception as exc:
                logger.warning(f"Audio processing failure: {exc}")
                flags.append("audio_processing_error")
            finally:
                for path in (temp_raw_path, temp_wav_path):
                    if path and os.path.exists(path):
                        try:
                            os.remove(path)
                        except OSError:
                            pass

            if "audio_processing_error" in flags or "empty_transcript" in flags:
                return _envelope_ok({
                    "transcript": "",
                    "reply": "I couldn't hear your response clearly. Could you please repeat that?",
                    "isComplete": False,
                    "score": None,
                    "flags": flags,
                    "replyAudioBase64": None,
                    "replyAudioMimeType": None,
                })

            session.setdefault("history", []).append({"role": "user", "content": transcript})
            session["turn_count"] = int(session.get("turn_count", 0)) + 1

            is_complete = session["turn_count"] >= int(session.get("max_questions", 5))
            score = None

            if is_complete:
                reply = "Thank you for answering our questions. That concludes our interview today."
                session["history"].append({"role": "assistant", "content": reply})
                try:
                    summary = await asyncio.to_thread(
                        generate_interview_summary,
                        session["history"],
                        session.get("cv_text", ""),
                        session.get("job_description", ""),
                        session.get("criteria", ""),
                    )
                    session["summary"] = summary
                    score = float(summary.get("score", 0.0)) if isinstance(summary, dict) else None
                except InterviewProviderError as exc:
                    return _envelope_error(exc.code, str(exc))
            else:
                try:
                    reply = await asyncio.to_thread(
                        generate_interview_response,
                        transcript,
                        session["history"][:-1],
                        session.get("cv_text", ""),
                        session.get("job_description", ""),
                    )
                except InterviewProviderError as exc:
                    return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

                session["history"].append({"role": "assistant", "content": reply})

            if reply.strip():
                tts_path = None
                try:
                    timeout_val = float(os.getenv(TTS_TIMEOUT_ENV_VAR, DEFAULT_TTS_TIMEOUT_SECONDS))
                    tts_path = await asyncio.wait_for(
                        asyncio.to_thread(text_to_speech_file, reply),
                        timeout=timeout_val,
                    )
                    with open(tts_path, "rb") as f:
                        reply_audio_base64 = base64.b64encode(f.read()).decode("utf-8")
                    reply_audio_mime_type = "audio/mpeg" if tts_path.lower().endswith(".mp3") else "audio/wav"
                except Exception as exc:
                    logger.warning(f"TTS synthesis failure: {exc}")
                    flags.append("tts_generation_error")
                finally:
                    if tts_path and os.path.exists(tts_path):
                        try:
                            os.remove(tts_path)
                        except OSError:
                            pass

            return _envelope_ok({
                "transcript": transcript,
                "reply": reply,
                "isComplete": is_complete,
                "score": score,
                "flags": flags,
                "replyAudioBase64": reply_audio_base64,
                "replyAudioMimeType": reply_audio_mime_type,
            })
        except Exception as exc:
            logger.exception("Unexpected error in analyze_audio")
            return _envelope_error("InterviewAudioAnalysisFailed", "Could not analyze interview audio.", str(exc))


@router.post("/interviews/finalize")
async def finalize_interview(request: FinalizeInterviewRequest):
    session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
    if session is None:
        return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

    session_lock: asyncio.Lock = session.setdefault("lock", asyncio.Lock())

    async with session_lock:
        try:
            summary = session.get("summary")

            if request.transcript:
                history = [
                    {
                        "role": "assistant" if item.speaker.lower() in ("assistant", "interviewer") else "user",
                        "content": item.content,
                    }
                    for item in sorted(request.transcript, key=lambda i: i.sequence)
                ]
                try:
                    summary = await asyncio.to_thread(
                        generate_interview_summary,
                        history,
                        session.get("cv_text", ""),
                        session.get("job_description", ""),
                        session.get("criteria", ""),
                    )
                    session["summary"] = summary
                except InterviewProviderError as exc:
                    return _envelope_error(exc.code, str(exc))

            elif not isinstance(summary, dict):
                history = session.get("history", [])
                try:
                    summary = await asyncio.to_thread(
                        generate_interview_summary,
                        history,
                        session.get("cv_text", ""),
                        session.get("job_description", ""),
                        session.get("criteria", ""),
                    )
                    session["summary"] = summary
                except InterviewProviderError as exc:
                    return _envelope_error(exc.code, str(exc))

            return _envelope_ok({
                "finalScore": float(summary.get("score", 0.0)),
                "verdict": str(summary.get("recommendation", "Requires manual review")),
                "recruiterReportJson": json.dumps(summary, ensure_ascii=False),
                "candidateFeedbackJson": json.dumps(
                    {
                        "review": summary.get("review", ""),
                        "strengths": summary.get("strengths", []),
                        "weaknesses": summary.get("weaknesses", []),
                        "recommendation": summary.get("recommendation", ""),
                    },
                    ensure_ascii=False,
                ),
            })
        except Exception as exc:
            logger.exception("Unexpected error in finalize_interview")
            return _envelope_error("InterviewFinalizeFailed", "Could not finalize interview.", str(exc))