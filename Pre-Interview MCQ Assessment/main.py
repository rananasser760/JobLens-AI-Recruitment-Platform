"""
main.py
=======
FastAPI entry point for the standalone MCQ Assessment service.

Run:
    uvicorn main:app --reload --port 8001

HTML UI:
    http://localhost:8001/
    
Docs:
    http://localhost:8001/docs
"""

from __future__ import annotations

from datetime import datetime
from typing import Optional

from fastapi import Depends, FastAPI, HTTPException, status
from fastapi.responses import FileResponse
from fastapi.responses import HTMLResponse

from fastapi.middleware.cors import CORSMiddleware
from sqlalchemy.orm import Session

from database import create_tables, get_db
from models import (
    Candidate, Job,
    MCQSession, MCQSessionStatus,
)
from schemas import (
    CandidateCreate, CandidateOut,
    FinalEvaluationReport,
    JobCreate, JobOut,
    MCQGenerateRequest, MCQGenerateResponse,
    MCQQuestionOut, MCQSessionOut,
    MCQSubmitRequest, MCQSubmitResponse,
    SkillBreakdown,
)
from service import (
    build_interview_context,
    build_interview_prompt_section,
    generate_mcq,
    get_mcq_result,
    submit_mcq,
)

# ── App setup ─────────────────────────────────────────────────────────────────

app = FastAPI(
    title="JobLens MCQ Assessment API",
    description=(
        "Standalone Pre-Interview MCQ Assessment service.\n\n"
        "Workflow: create job → generate questions → start session → "
        "submit answers → get score → fetch interview context"
    ),
    version="1.0.0",
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
def startup():
    create_tables()

@app.get("/", include_in_schema=False)
def serve_html():
    """Serve the main HTML file for the JobLens MCQ interface."""
    return FileResponse("joblens_mcq.html")


# ═══════════════════════════════════════════════════════════════════════════════
# Seed endpoints  (Jobs & Candidates)
# ═══════════════════════════════════════════════════════════════════════════════
@app.post("/api/jobs", response_model=JobOut, status_code=201, tags=["Setup"])
def create_job(body: JobCreate, db: Session = Depends(get_db)):
    """Create a job posting so you can generate MCQs for it."""
    job = Job(**body.model_dump())
    db.add(job); db.commit(); db.refresh(job)
    return job


@app.get("/api/jobs/{job_id}", response_model=JobOut, tags=["Setup"])
def get_job(job_id: int, db: Session = Depends(get_db)):
    job = db.get(Job, job_id)
    if not job:
        raise HTTPException(404, f"Job {job_id} not found.")
    return job


@app.post("/api/candidates", response_model=CandidateOut, status_code=201, tags=["Setup"])
def create_candidate(body: CandidateCreate, db: Session = Depends(get_db)):
    """Create a candidate record."""
    c = Candidate(**body.model_dump())
    db.add(c); db.commit(); db.refresh(c)
    return c


# ═══════════════════════════════════════════════════════════════════════════════
# MCQ Core Endpoints
# ═══════════════════════════════════════════════════════════════════════════════

@app.post(
    "/api/mcq/generate",
    response_model=MCQGenerateResponse,
    status_code=201,
    tags=["MCQ"],
    summary="Generate 10 MCQ questions for a job",
)
async def generate_mcq_endpoint(
    body: MCQGenerateRequest,
    db: Session = Depends(get_db),
) -> MCQGenerateResponse:
    """
    Extracts required skills from the job description using an LLM,
    then generates exactly 10 MCQ questions.
    Questions are cached per job_id — subsequent calls return the cache.

    **Request examples:**

    Using job_id (fetches description from DB):
    ```json
    { "job_id": 1, "force_regenerate": false }
    ```

    Using raw text (no DB needed):
    ```json
    {
      "job_description": "We need a Python backend developer with FastAPI...",
      "criteria": "Strong OOP skills, REST API experience"
    }
    ```
    """
    job_description = body.job_description or ""

    if body.job_id and not job_description:
        job = db.get(Job, body.job_id)
        if not job:
            raise HTTPException(404, f"Job {body.job_id} not found.")
        job_description = (
            f"{job.title}\n\n{job.description}"
            + (f"\n\nRequirements:\n{job.requirements}" if job.requirements else "")
        )

    if not job_description.strip():
        raise HTTPException(422, "job_description is empty.")

    try:
        return await generate_mcq(
            db=db,
            job_id=body.job_id,
            job_description=job_description,
            criteria=body.criteria or "",
            force_regenerate=body.force_regenerate,
        )
    except ValueError as exc:
        raise HTTPException(502, f"MCQ generation failed: {exc}")


@app.get(
    "/api/mcq/session/{session_id}",
    response_model=MCQSessionOut,
    tags=["MCQ"],
    summary="Get quiz session + questions (candidate view)",
)
def get_session(session_id: int, db: Session = Depends(get_db)):
    """
    Returns the session status and all 10 questions for the candidate UI.
    **Correct answers are never included.**
    """
    from models import MCQQuestion

    session = db.get(MCQSession, session_id)
    if not session:
        raise HTTPException(404, f"Session {session_id} not found.")

    questions = (
        db.query(MCQQuestion)
        .filter(MCQQuestion.job_id == session.job_id)
        .order_by(MCQQuestion.question_index)
        .all()
    )
    return MCQSessionOut(
        session_id=session.id,
        job_id=session.job_id,
        candidate_id=session.candidate_id,
        status=session.status.value,
        questions=[MCQQuestionOut(**q.to_candidate_dict()) for q in questions],
    )


@app.post(
    "/api/mcq/submit",
    response_model=MCQSubmitResponse,
    tags=["MCQ"],
    summary="Submit answers — returns score + skill breakdown",
)
async def submit_mcq_endpoint(
    body: MCQSubmitRequest,
    db: Session = Depends(get_db),
) -> MCQSubmitResponse:
    """
    Submit answers for a quiz. Returns:
    - total score (0–100)
    - number of correct answers
    - weak skills (< 60% correct rate)
    - strong skills (≥ 60% correct rate)
    - per-skill breakdown

    **Request example:**
    ```json
    {
      "candidate_id": 1,
      "job_id": 1,
      "answers": [
        { "question_id": 1, "selected_option": "B" },
        { "question_id": 2, "selected_option": "A" }
      ]
    }
    ```
    """
    try:
        return await submit_mcq(
            db=db,
            candidate_id=body.candidate_id,
            job_id=body.job_id,
            answers=body.answers,
            session_id=body.session_id,
        )
    except ValueError as exc:
        raise HTTPException(400, str(exc))


@app.get(
    "/api/mcq/result/{session_id}",
    tags=["MCQ"],
    summary="Get stored MCQ result for a session",
)
def get_result(session_id: int, db: Session = Depends(get_db)):
    """
    Returns the full scored result for a completed session.
    Used by the HR dashboard and the AI Interview Agent.
    """
    result = get_mcq_result(db, session_id)
    if not result:
        raise HTTPException(404, f"No result for session {session_id}.")
    return result.to_dict()


@app.get(
    "/api/mcq/interview-context/{session_id}",
    tags=["MCQ"],
    summary="Get MCQ context to inject into the AI Interview Agent",
)
def interview_context(session_id: int, db: Session = Depends(get_db)):
    """
    Returns the structured context dict that the AI Interview Agent needs
    to adapt its questions based on the candidate's MCQ performance.

    Also returns the ready-made system prompt snippet to append.

    **Response example:**
    ```json
    {
      "mcq_score": 70.0,
      "weak_skills": ["Docker", "SQL"],
      "strong_skills": ["Python", "FastAPI"],
      "skill_breakdown": { "Python": { "correct": 2, "total": 2 } },
      "system_prompt_addition": "## Pre-Interview MCQ Results\\n..."
    }
    ```
    """
    result = get_mcq_result(db, session_id)
    if not result:
        raise HTTPException(404, f"No MCQ result for session {session_id}.")

    context = build_interview_context(result)
    context["system_prompt_addition"] = build_interview_prompt_section(result)
    return context


@app.get(
    "/api/mcq/report/{session_id}",
    response_model=FinalEvaluationReport,
    tags=["MCQ"],
    summary="Full evaluation report for HR dashboard",
)
def final_report(session_id: int, db: Session = Depends(get_db)):
    """
    Returns the complete MCQ evaluation report for a session,
    ready to be displayed on the HR dashboard or merged with the
    interview score to compute the final candidate score.
    """
    session = db.get(MCQSession, session_id)
    if not session:
        raise HTTPException(404, f"Session {session_id} not found.")

    result = get_mcq_result(db, session_id)
    if not result:
        raise HTTPException(404, f"No result yet for session {session_id}. Submit answers first.")

    candidate = db.get(Candidate, session.candidate_id)
    context   = build_interview_context(result)

    return FinalEvaluationReport(
        candidate_id=session.candidate_id,
        candidate_name=candidate.full_name if candidate else "Unknown",
        job_id=session.job_id,
        mcq_session_id=session_id,
        score=round(result.score, 1),
        correct_answers=result.correct_answers,
        total_questions=result.total_questions,
        weak_skills=result.weak_skills,
        strong_skills=result.strong_skills,
        skill_breakdown={k: SkillBreakdown(**v) for k, v in result.skill_breakdown.items()},
        passed=result.score >= 50.0,
        mcq_score=round(result.score, 1),
        interview_context=context,
    )


# ── Health check ──────────────────────────────────────────────────────────────

@app.get("/health", tags=["Health"])
def health():
    return {"status": "ok", "service": "JobLens MCQ Assessment", "time": datetime.utcnow().isoformat()}
