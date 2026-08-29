from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import os
import subprocess
import tempfile
import uuid
from datetime import datetime, timezone
from typing import Any

import cv2
import imageio_ffmpeg
import numpy as np
from fastapi import APIRouter
from pydantic import BaseModel, Field

from recruitment.ats_service import analyze_ats_with_llm
from recruitment.cv_service import parse_cv_with_llm
from recruitment.matcher_service import JobMatcher, recommend_candidates_for_job, recommend_jobs_for_candidate
from recruitment.scraper_service import run_scraper
from recruitment.vector_store import store
from request_context import get_request_id
from response import InterviewProviderError, generate_interview_response, generate_interview_summary
from stt import speech_to_text
from session_store import INTERVIEW_SESSIONS
from tts import text_to_speech_file
from routers.integrity import start_session, end_session, _active_get, StartSessionRequest

router = APIRouter(prefix="/internal/v1", tags=["internal"])

VIDEO_DEDUP_WINDOW_SECONDS = 1.0
VIDEO_MIN_INTERVAL_SECONDS = 0.45
_VIDEO_FRAME_STATE: dict[str, tuple[str, datetime, int]] = {}

_EXTERNAL_SCRAPED_SOURCE_TOKENS = {"wuzzuf", "linkedin", "scraped", "external"}
_EXTERNAL_SCRAPED_HOST_TOKENS = ("wuzzuf.net", "linkedin.com")
DEFAULT_TTS_TIMEOUT_SECONDS = 12.0
TTS_TIMEOUT_ENV_VAR = "JOBLENS_TTS_TIMEOUT_SECONDS"


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


class VideoAnalysisRequest(BaseModel):
    interviewSessionId: str
    base64Frame: str
    sequence: int = Field(default=1, ge=1)


class TranscriptEntryRequest(BaseModel):
    sequence: int
    speaker: str
    content: str
    occurredAtUtc: datetime | None = None


class FinalizeInterviewRequest(BaseModel):
    interviewSessionId: str
    integritySessionId: str
    transcript: list[TranscriptEntryRequest] = Field(default_factory=list)


def _envelope_ok(data: Any) -> dict[str, Any]:
    request_id = get_request_id() or uuid.uuid4().hex
    return {
        "requestId": request_id,
        "success": True,
        "data": data,
        "error": None,
    }


def _envelope_error(code: str, message: str, details: str | None = None) -> dict[str, Any]:
    request_id = get_request_id() or uuid.uuid4().hex
    return {
        "requestId": request_id,
        "success": False,
        "data": None,
        "error": {
            "code": code,
            "message": message,
            "details": details,
        },
    }


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


def _resolve_tts_timeout_seconds() -> float:
    raw = str(os.getenv(TTS_TIMEOUT_ENV_VAR, "") or "").strip()
    if not raw:
        return DEFAULT_TTS_TIMEOUT_SECONDS

    try:
        parsed = float(raw)
    except Exception:
        return DEFAULT_TTS_TIMEOUT_SECONDS

    return max(2.0, min(parsed, 120.0))


def _to_target_id(value: Any) -> int:
    if isinstance(value, int):
        return value if value > 0 else 0

    text = str(value or "").strip()
    if not text:
        return 0

    if text.isdigit():
        return int(text)

    lowered = text.lower()
    if lowered.startswith("job_"):
        suffix = text[4:]
        return int(suffix) if suffix.isdigit() else 0

    if lowered.startswith("candidate_"):
        suffix = text[10:]
        return int(suffix) if suffix.isdigit() else 0

    return 0


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
    if value is None:
        return None

    if isinstance(value, datetime):
        dt = value if value.tzinfo else value.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")

    text = str(value).strip()
    if not text:
        return None

    try:
        dt = datetime.fromisoformat(text.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    except Exception:
        return None


def _decode_data_uri_base64(value: str) -> bytes:
    payload = value.split(",", 1)[1] if "," in value else value
    return base64.b64decode(payload)


def _is_external_scraped_record(source: str, source_url: str, redirect_url: str) -> bool:
    normalized_source = source.strip().lower()
    if normalized_source in _EXTERNAL_SCRAPED_SOURCE_TOKENS:
        return True

    links = f"{source_url} {redirect_url}".lower()
    return any(token in links for token in _EXTERNAL_SCRAPED_HOST_TOKENS)


def _to_wav(src_path: str, dst_path: str) -> None:
    ffmpeg_path = imageio_ffmpeg.get_ffmpeg_exe()
    result = subprocess.run(
        [ffmpeg_path, "-y", "-v", "error", "-i", src_path, "-ar", "16000", "-ac", "1", dst_path],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr or "ffmpeg failed")


def _decode_frame(base64_frame: str) -> np.ndarray | None:
    frame_bytes = _decode_data_uri_base64(base64_frame)
    frame_array = np.frombuffer(frame_bytes, dtype=np.uint8)
    return cv2.imdecode(frame_array, cv2.IMREAD_COLOR)


def _get_face_cascade() -> cv2.CascadeClassifier:
    cascade_path = os.path.join(cv2.data.haarcascades, "haarcascade_frontalface_default.xml")
    return cv2.CascadeClassifier(cascade_path)


@router.post("/resumes/parse-text")
async def parse_resume_text(request: ParseResumeTextRequest):
    try:
        parsed = parse_cv_with_llm(request.resumeText)
        data = {
            "fullName": str(parsed.get("full_name", "") or ""),
            "email": str(parsed.get("email", "") or ""),
            "phone": str(parsed.get("phone", "") or ""),
            "skills": _flatten_skills(parsed.get("skills")),
            "structuredJson": _safe_json(parsed),
        }
        return _envelope_ok(data)
    except Exception as exc:
        return _envelope_error("ResumeParseFailed", "Could not parse resume text.", str(exc))


@router.post("/resumes/extract-text")
async def extract_resume_text(request: ExtractResumeTextRequest):
    try:
        from recruitment.cv_service import process_file

        base_name = request.fileName or "resume.bin"
        extension = ".bin"
        if "." in base_name:
            extension = "." + base_name.rsplit(".", 1)[1].strip().lower()

        payload = request.base64Content.strip()
        if not payload:
            return _envelope_ok({"text": ""})

        file_bytes = _decode_data_uri_base64(payload)
        with tempfile.NamedTemporaryFile(delete=False, suffix=extension) as handle:
            handle.write(file_bytes)
            temp_path = handle.name

        try:
            text, _ = process_file(temp_path)
        except Exception:
            text = ""
        finally:
            try:
                os.remove(temp_path)
            except Exception:
                pass

        return _envelope_ok({"text": text or ""})
    except Exception as exc:
        return _envelope_error("ResumeTextExtractFailed", "Could not extract resume text.", str(exc))


@router.post("/resumes/score-ats")
async def score_ats(request: ScoreAtsRequest):
    try:
        parsed = parse_cv_with_llm(request.resumeText)
        ats_result = analyze_ats_with_llm(parsed, request.jobDescription or request.resumeText)

        suggestions: list[str] = []
        for suggestion in ats_result.get("improvement_suggestions", []):
            if isinstance(suggestion, dict):
                text = str(suggestion.get("suggestion", "") or "").strip()
                if text:
                    suggestions.append(text)
            else:
                text = str(suggestion).strip()
                if text:
                    suggestions.append(text)

        for step in ats_result.get("next_steps", []):
            text = str(step).strip()
            if text:
                suggestions.append(text)

        keywords = ats_result.get("keywords_analysis", {})
        missing_skills = []
        if isinstance(keywords, dict):
            missing_skills = [str(item).strip() for item in keywords.get("missing_keywords", []) if str(item).strip()]

        data = {
            "score": round(_to_float(ats_result.get("overall_score")), 2),
            "summary": str(ats_result.get("summary_feedback", "") or ""),
            "missingSkills": list(dict.fromkeys(missing_skills)),
            "suggestions": list(dict.fromkeys(suggestions)),
        }
        return _envelope_ok(data)
    except Exception as exc:
        return _envelope_error("AtsScoreFailed", "Could not score resume text.", str(exc))


@router.post("/vectors/candidates/upsert")
async def upsert_candidate_vector(request: CandidateVectorSyncRequest):
    try:
        vector_id = str(request.candidateId)

        if request.contentHash:
            existing = store.candidates_col.get(ids=[vector_id], include=["metadatas"])
            metadatas = existing.get("metadatas", []) if isinstance(existing, dict) else []
            metadata = metadatas[0] if metadatas else None
            if isinstance(metadata, dict) and str(metadata.get("content_hash", "")) == request.contentHash:
                return _envelope_ok(
                    {
                        "vectorId": vector_id,
                        "collection": "candidates",
                        "model": "chroma",
                    }
                )

        store.candidates_col.upsert(
            documents=[_safe_json(request.profileData)],
            metadatas=[
                {
                    "candidate_id": request.candidateId,
                    "content_hash": request.contentHash or "",
                }
            ],
            ids=[vector_id],
        )

        return _envelope_ok(
            {
                "vectorId": vector_id,
                "collection": "candidates",
                "model": "chroma",
            }
        )
    except Exception as exc:
        return _envelope_error("CandidateVectorUpsertFailed", "Could not upsert candidate vector.", str(exc))


@router.post("/vectors/jobs/upsert")
async def upsert_job_vector(request: JobVectorSyncRequest):
    try:
        normalized_id = f"job_{request.jobId}"

        if request.contentHash:
            existing = store.internal_jobs_col.get(ids=[normalized_id], include=["metadatas"])
            metadatas = existing.get("metadatas", []) if isinstance(existing, dict) else []
            metadata = metadatas[0] if metadatas else None
            if isinstance(metadata, dict) and str(metadata.get("content_hash", "")) == request.contentHash:
                return _envelope_ok(
                    {
                        "vectorId": normalized_id,
                        "collection": "job_listings_internal",
                        "model": "chroma",
                    }
                )

        store.internal_jobs_col.upsert(
            documents=[_safe_json(request.jobData)],
            metadatas=[
                {
                    "job_id": request.jobId,
                    "source": "Internal API",
                    "title": request.jobData.get("title", ""),
                    "company": request.jobData.get("company", ""),
                    "location": request.jobData.get("location", ""),
                    "json_detailed": _safe_json(request.jobData),
                    "content_hash": request.contentHash or "",
                }
            ],
            ids=[normalized_id],
        )

        return _envelope_ok(
            {
                "vectorId": normalized_id,
                "collection": "job_listings_internal",
                "model": "chroma",
            }
        )
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
        matches = []
        if request.candidateId > 0:
            matches = recommend_jobs_for_candidate(request.candidateId, limit=request.limit)

        if not matches:
            parsed = parse_cv_with_llm(request.resumeText)
            matches = JobMatcher().match_jobs_from_db(parsed, n_results=request.limit)

        result = []
        for match in matches:
            target_id = _to_target_id(match.get("db_id") or match.get("job_id") or match.get("external_job_id"))
            if target_id <= 0:
                continue

            result.append(
                {
                    "targetId": target_id,
                    "targetType": "Job",
                    "score": round(_to_float(match.get("match_score") or match.get("semantic_similarity")), 2),
                    "reason": str(match.get("recommendation") or match.get("match_level") or "Matched by AI."),
                    "previewJson": _safe_json(match),
                }
            )

        return _envelope_ok(result)
    except Exception as exc:
        return _envelope_error("JobRecommendationFailed", "Could not generate job recommendations.", str(exc))


@router.post("/recommendations/candidates")
async def recommend_candidates(request: CandidateRecommendationRequest):
    try:
        matches = recommend_candidates_for_job(str(request.jobId), limit=request.limit, min_score=0.0)
        result = []
        for match in matches:
            score = _to_float(match.get("score"))
            if 0 <= score <= 1:
                score *= 100

            result.append(
                {
                    "targetId": _to_target_id(match.get("candidate_id")),
                    "targetType": "Candidate",
                    "score": round(score, 2),
                    "reason": "Recommended based on profile similarity.",
                    "previewJson": _safe_json(match.get("candidate_preview", {})),
                }
            )

        return _envelope_ok(result)
    except Exception as exc:
        return _envelope_error("CandidateRecommendationFailed", "Could not generate candidate recommendations.", str(exc))


@router.post("/scrape/jobs")
async def scrape_jobs(request: ScrapeJobsRequest):
    try:
        scrape_result = await run_scraper(max_categories=request.maxCategories)

        stored = store.scraped_jobs_col.get()
        ids = stored.get("ids", [])
        metadatas = stored.get("metadatas", [])

        jobs = []
        for index, metadata in enumerate(metadatas):
            detail: dict[str, Any] = {}
            try:
                detail = json.loads(metadata.get("json_detailed", "{}"))
            except Exception:
                detail = {}

            source = str(metadata.get("source", "") or detail.get("source", "") or "")
            source_url = str(metadata.get("job_page_link", "") or detail.get("job_page_link", "") or "")
            redirect_url = str(metadata.get("apply_link", "") or detail.get("apply_link", "") or source_url)
            if not _is_external_scraped_record(source, source_url, redirect_url):
                continue

            external_job_id = str(ids[index]) if index < len(ids) else str(uuid.uuid4().hex)
            location = str(detail.get("location", "") or metadata.get("location", "") or "")
            city = str(detail.get("city", "") or metadata.get("city", "") or "")
            country = str(detail.get("country", "") or metadata.get("country", "") or "")
            enrichment_source = str(metadata.get("enrichment_source", "") or detail.get("_enrichment_source", "") or "")

            skill_values = detail.get("skills") if isinstance(detail.get("skills"), list) else []
            skills = [str(skill).strip() for skill in skill_values if str(skill).strip()]

            requirements = str(
                detail.get("requirements", "")
                or metadata.get("requirements_snippet", "")
                or ""
            )
            responsibilities = str(
                detail.get("responsibilities", "")
                or metadata.get("responsibilities_snippet", "")
                or ""
            )
            employment_type = str(
                detail.get("employment_type", "")
                or metadata.get("employment_type", "")
                or ""
            )
            experience_level = str(
                detail.get("experience_level", "")
                or metadata.get("experience_level", "")
                or ""
            )

            jobs.append(
                {
                    "source": source,
                    "externalJobId": external_job_id,
                    "sourceUrl": source_url,
                    "redirectUrl": redirect_url,
                    "title": str(metadata.get("title", "") or ""),
                    "company": str(metadata.get("company", "") or ""),
                    "location": location,
                    "city": city,
                    "country": country,
                    "description": str(detail.get("description", "") or metadata.get("description_snippet", "") or ""),
                    "requirements": requirements,
                    "responsibilities": responsibilities,
                    "employmentType": employment_type,
                    "experienceLevel": experience_level,
                    "enrichmentSource": enrichment_source,
                    "skills": skills,
                    "postedAtUtc": _normalize_iso_datetime(metadata.get("posted_time")),
                    "metadata": detail,
                }
            )

        payload = {
            "processedCategories": int(scrape_result.get("processed_categories", 0)),
            "upsertedJobs": int(scrape_result.get("upserted_jobs", 0)),
            "totalJobs": int(scrape_result.get("total_jobs", len(jobs))),
            "jobs": jobs,
        }

        stats = scrape_result.get("stats")
        if isinstance(stats, dict):
            payload["stats"] = stats

        warning = scrape_result.get("warning")
        if isinstance(warning, str) and warning.strip():
            payload["warning"] = warning.strip()

        return _envelope_ok(payload)
    except Exception as exc:
        return _envelope_error("ScrapeJobsFailed", "Could not scrape jobs.", str(exc))


@router.post("/interviews/sessions")
async def start_interview_session(request: StartInterviewSessionRequest):
    try:
        interview_session_id = str(uuid.uuid4())
        
        # Start true proctoring session
        integrity_resp = start_session(StartSessionRequest(
             candidate_name=request.candidateName,
             candidate_id=request.candidateId,
             interview_session_id=interview_session_id
        ))
        integrity_session_id = str(integrity_resp.get("session_id"))

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
            "integrity_session_id": integrity_session_id,
        }

        candidate_name = request.candidateName.strip() or "candidate"
        return _envelope_ok(
            {
                "interviewSessionId": interview_session_id,
                "integritySessionId": integrity_session_id,
                "maxQuestions": request.maxQuestions,
                "welcomeMessage": f"Welcome {candidate_name}. Let's start the interview.",
            }
        )
    except Exception as exc:
        return _envelope_error("InterviewStartFailed", "Could not initialize interview session.", str(exc))


@router.post("/interviews/analyze-audio")
async def analyze_audio(request: AudioAnalysisRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        flags: list[str] = []
        transcript = ""
        reply_audio_base64: str | None = None
        reply_audio_mime_type: str | None = None
        temp_raw_path = None
        temp_wav_path = None

        try:
            audio_bytes = _decode_data_uri_base64(request.base64Audio)
            with tempfile.NamedTemporaryFile(delete=False, suffix=".webm") as raw_file:
                raw_file.write(audio_bytes)
                temp_raw_path = raw_file.name

            with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as wav_file:
                temp_wav_path = wav_file.name

            _to_wav(temp_raw_path, temp_wav_path)
            transcript = (speech_to_text(temp_wav_path) or "").strip()
            if not transcript:
                flags.append("empty_transcript")
        except Exception:
            flags.append("audio_processing_error")
        finally:
            for path in [temp_raw_path, temp_wav_path]:
                if path and os.path.exists(path):
                    try:
                        os.remove(path)
                    except Exception:
                        pass

        if not transcript:
            transcript = "Could not transcribe candidate response clearly."

        history = session.get("history", [])
        history.append({"role": "user", "content": transcript})
        session["turn_count"] = int(session.get("turn_count", 0)) + 1

        try:
            reply = generate_interview_response(
                current_transcript=transcript,
                chat_history=history,
                cv_text=session.get("cv_text", ""),
                job_description=session.get("job_description", ""),
            )
        except InterviewProviderError as exc:
            return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

        history.append({"role": "assistant", "content": reply})

        tts_path: str | None = None
        tts_timeout_seconds = _resolve_tts_timeout_seconds()
        if reply.strip():
            try:
                tts_path = await asyncio.wait_for(
                    asyncio.to_thread(text_to_speech_file, reply),
                    timeout=tts_timeout_seconds,
                )
                with open(tts_path, "rb") as audio_file:
                    audio_bytes = audio_file.read()

                if audio_bytes:
                    reply_audio_base64 = base64.b64encode(audio_bytes).decode("utf-8")
                else:
                    flags.append("tts_empty_audio")

                if reply_audio_base64 and tts_path.lower().endswith(".mp3"):
                    reply_audio_mime_type = "audio/mpeg"
                elif reply_audio_base64:
                    reply_audio_mime_type = "audio/wav"
            except asyncio.TimeoutError:
                flags.append("tts_timeout")
                print(
                    f"[internal_api] TTS timeout after {tts_timeout_seconds:.1f}s "
                    f"for interview session {request.interviewSessionId}"
                )
            except Exception as exc:
                flags.append("tts_generation_error")
                print(
                    "[internal_api] TTS generation failed for interview session "
                    f"{request.interviewSessionId}: {exc}"
                )
            finally:
                if tts_path and os.path.exists(tts_path):
                    try:
                        os.remove(tts_path)
                    except Exception:
                        pass

        if reply.strip() and not reply_audio_base64:
            flags.append("tts_unavailable")

        max_questions = int(session.get("max_questions", 5))
        is_complete = int(session.get("turn_count", 0)) >= max_questions

        score = None
        if is_complete:
            try:
                summary = generate_interview_summary(
                    chat_history=history,
                    cv_text=session.get("cv_text", ""),
                    job_description=session.get("job_description", ""),
                    criteria=session.get("criteria", ""),
                )
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

            session["summary"] = summary
            score = _to_float(summary.get("score")) if isinstance(summary, dict) else None

        return _envelope_ok(
            {
                "transcript": transcript,
                "reply": reply,
                "isComplete": is_complete,
                "score": score,
                "flags": flags,
                "replyAudioBase64": reply_audio_base64,
                "replyAudioMimeType": reply_audio_mime_type,
            }
        )
    except Exception as exc:
        return _envelope_error("InterviewAudioAnalysisFailed", "Could not analyze interview audio.", str(exc))


@router.post("/interviews/analyze-video")
async def analyze_video(request: VideoAnalysisRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        now_utc = datetime.now(timezone.utc)
        frame_hash = hashlib.sha1(request.base64Frame.encode("utf-8")).hexdigest()
        last_state = _VIDEO_FRAME_STATE.get(request.interviewSessionId)
        if last_state is not None:
            last_hash, last_at, last_sequence = last_state
            elapsed = (now_utc - last_at).total_seconds()

            if request.sequence <= last_sequence:
                return _envelope_ok(
                    {
                        "events": [],
                        "skipped": True,
                        "reason": "sequence_not_advanced",
                    }
                )

            if frame_hash == last_hash and elapsed < VIDEO_DEDUP_WINDOW_SECONDS:
                return _envelope_ok(
                    {
                        "events": [],
                        "skipped": True,
                        "reason": "duplicate_frame",
                    }
                )

            if elapsed < VIDEO_MIN_INTERVAL_SECONDS:
                return _envelope_ok(
                    {
                        "events": [],
                        "skipped": True,
                        "reason": "throttled",
                    }
                )

        _VIDEO_FRAME_STATE[request.interviewSessionId] = (frame_hash, now_utc, request.sequence)

        try:
            integrity_id_str = session.get("integrity_session_id")
            if not integrity_id_str:
                return _envelope_ok({"events": [], "skipped": True, "reason": "no_integrity_session"})
                
            proc = _active_get(int(integrity_id_str))
            if not proc:
                return _envelope_ok({"events": [], "skipped": True, "reason": "processor_not_found"})

            frame = _decode_frame(request.base64Frame)
            if frame is not None:
                proc.push_frame(frame)
                
            state = proc.get_state()
            
            events: list[dict[str, Any]] = []
            alert = state.get("current_alert")
            yolo_alert = state.get("yolo_alert")
            
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
                # Fallback for robustness
                if alert: consumed.append(alert)
                if yolo_alert and yolo_alert != alert: consumed.append(yolo_alert)
            
            for a in consumed:
                if a in alert_map:
                    desc, sev = alert_map[a]
                    source = "yolo" if "YOLO" in desc else "vision"
                    events.append({
                        "eventType": a,
                        "severity": sev,
                        "source": source,
                        "description": desc,
                        "mediaReference": None,
                    })

            # Append the state to reason for debugging if needed, but return events clearly
            reason_str = None
            if not events and state.get("calibrated") is False:
                reason_str = "calibrating"

            return _envelope_ok(
                {
                    "events": events,
                    "skipped": False,
                    "reason": reason_str,
                }
            )

        except Exception as inner_exc:
            return _envelope_ok(
                {
                    "events": [],
                    "skipped": True,
                    "reason": f"processing_error: {str(inner_exc)}",
                }
            )

    except Exception as exc:
        return _envelope_error("InterviewVideoAnalysisFailed", "Could not analyze video frame.", str(exc))


@router.post("/interviews/finalize")
async def finalize_interview(request: FinalizeInterviewRequest):
    try:
        if request.integritySessionId:
            try:
                end_session(int(request.integritySessionId))
            except Exception as e:
                print(f"Error finalizing integrity session: {e}")
                
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        summary = session.get("summary")
        if not isinstance(summary, dict):
            history = session.get("history", [])
            if not history and request.transcript:
                sorted_transcript = sorted(request.transcript, key=lambda item: item.sequence)
                history = [
                    {
                        "role": "assistant" if item.speaker.lower() in ("assistant", "interviewer") else "user",
                        "content": item.content,
                    }
                    for item in sorted_transcript
                ]

            try:
                summary = generate_interview_summary(
                    chat_history=history,
                    cv_text=session.get("cv_text", ""),
                    job_description=session.get("job_description", ""),
                    criteria=session.get("criteria", ""),
                )
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

            session["summary"] = summary

        final_score = round(_to_float(summary.get("score")) if isinstance(summary, dict) else 0.0, 2)
        verdict = str(summary.get("recommendation") if isinstance(summary, dict) else "Requires manual review")
        recruiter_report_json = _safe_json(summary)
        candidate_feedback_json = _safe_json(
            {
                "review": summary.get("review", "") if isinstance(summary, dict) else "",
                "strengths": summary.get("strengths", []) if isinstance(summary, dict) else [],
                "weaknesses": summary.get("weaknesses", []) if isinstance(summary, dict) else [],
                "recommendation": verdict,
            }
        )

        return _envelope_ok(
            {
                "finalScore": final_score,
                "verdict": verdict,
                "recruiterReportJson": recruiter_report_json,
                "candidateFeedbackJson": candidate_feedback_json,
            }
        )
    except Exception as exc:
        return _envelope_error("InterviewFinalizeFailed", "Could not finalize interview.", str(exc))