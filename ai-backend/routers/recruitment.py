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
