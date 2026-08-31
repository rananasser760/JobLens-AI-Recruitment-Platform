from __future__ import annotations

import base64
import json
import os
import tempfile
import uuid
from datetime import datetime, timezone
from typing import Any

from fastapi import APIRouter
from pydantic import BaseModel, Field

from recruitment.ats_service import analyze_ats_with_llm
from recruitment.cv_service import parse_cv_with_llm, process_file
from recruitment.matcher_service import JobMatcher, recommend_candidates_for_job, recommend_jobs_for_candidate
from recruitment.scraper_service import run_scraper
from recruitment.vector_store import store
from request_context import get_request_id

router = APIRouter(prefix="/internal/v1", tags=["internal"])

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
    return base64.b64decode(payload.strip())

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
        ext = "." + request.fileName.rsplit(".", 1)[1].lower() if "." in (request.fileName or "") else ".bin"
        if not request.base64Content.strip(): return _envelope_ok({"text": ""})
        
        temp_path = None
        with tempfile.NamedTemporaryFile(delete=False, suffix=ext) as handle:
            handle.write(_decode_data_uri_base64(request.base64Content))
            temp_path = handle.name
            
        try:
            text, _ = process_file(temp_path)
        finally:
            if temp_path and os.path.exists(temp_path): os.remove(temp_path)
            
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
            
            if src.lower() in ["wuzzuf", "linkedin", "scraped", "external"] or any(t in f"{src_url} {red_url}".lower() for t in ["wuzzuf.net", "linkedin.com"]):
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