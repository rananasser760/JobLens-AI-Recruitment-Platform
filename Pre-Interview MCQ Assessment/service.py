"""
service.py
==========
All MCQ business logic:
  • LLM skill extraction  (OpenRouter / Llama 3.3-70B)
  • LLM question generation (10 MCQs per job)
  • DB question caching
  • Answer scoring + weak/strong skill analysis
  • Interview context builder (passes data to AI Interview Agent)
"""

from __future__ import annotations

import json
import logging
import re
from collections import defaultdict
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

import httpx
from sqlalchemy.orm import Session

from config import settings
from models import (
    Candidate, Job,
    MCQAnswer, MCQQuestion, MCQResult, MCQSession, MCQSessionStatus,
)
from schemas import CandidateAnswer, MCQGenerateResponse, MCQQuestionOut, MCQSubmitResponse, SkillBreakdown

logger = logging.getLogger(__name__)

OPENROUTER_URL = "https://openrouter.ai/api/v1/chat/completions"
LLM_MODEL      = "meta-llama/llama-3.3-70b-instruct"
MCQ_COUNT      = 10
PASS_THRESHOLD = 50.0

# ─────────────────────────────────────────────────────────────────────────────
# Prompts
# ─────────────────────────────────────────────────────────────────────────────

_SKILL_PROMPT = """
You are a senior technical recruiter.
Extract the top 5-7 core technical skills required by this job description.
Return ONLY a raw JSON array of skill name strings. No markdown. No explanation.
Example: ["Python", "FastAPI", "PostgreSQL", "Docker"]

Job Description:
{job_description}

Recruiter Criteria:
{criteria}
"""

_MCQ_PROMPT = """
You are an expert technical assessment designer.

Create exactly {count} multiple-choice questions testing a candidate's knowledge
of these skills: {skills}

Job context: {job_description}

Rules:
- Each question has exactly 4 options (A, B, C, D). ONE correct answer only.
- Cover ALL skills — do not repeat the same concept twice.
- Mix difficulty: 30% easy, 50% medium, 20% hard.
- Return ONLY a raw JSON array. No markdown. No explanation. Nothing else.

JSON format:
[
  {{
    "question": "...",
    "options": ["A. ...", "B. ...", "C. ...", "D. ..."],
    "correct_answer": "B",
    "related_skill": "Python"
  }}
]
"""

# Appended to the AI Interview Agent system prompt
INTERVIEW_MCQ_SECTION = """
## Pre-Interview MCQ Results
The candidate just completed a 10-question knowledge assessment.

MCQ Score   : {mcq_score}/100
Strong Skills: {strong_skills}
Weak Skills  : {weak_skills}

Instructions:
- Probe weak skills ({weak_skills}) with 1-2 deeper questions to verify real gaps.
- For strong skills ({strong_skills}), ask advanced questions to validate expertise.
- Do NOT reveal the MCQ score or which questions the candidate got wrong.
"""


# ─────────────────────────────────────────────────────────────────────────────
# LLM helpers
# ─────────────────────────────────────────────────────────────────────────────

async def _call_llm(prompt: str, max_tokens: int = 3000) -> str:
    headers = {
        "Authorization": f"Bearer {settings.OPENROUTER_API_KEY}",
        "Content-Type":  "application/json",
        "HTTP-Referer":  "https://joblens.ai",
        "X-Title":       "JobLens-MCQ",
    }
    payload = {
        "model":       LLM_MODEL,
        "max_tokens":  max_tokens,
        "temperature": 0.4,
        "messages":    [{"role": "user", "content": prompt}],
    }
    async with httpx.AsyncClient(timeout=60.0) as client:
        r = await client.post(OPENROUTER_URL, headers=headers, json=payload)
        r.raise_for_status()
    return r.json()["choices"][0]["message"]["content"]


def _parse_json(raw: str) -> Any:
    cleaned = re.sub(r"^```(?:json)?|```$", "", raw.strip(), flags=re.MULTILINE).strip()
    try:
        return json.loads(cleaned)
    except json.JSONDecodeError as exc:
        logger.error("Invalid JSON from LLM:\n%.400s", cleaned)
        raise ValueError(f"LLM returned invalid JSON: {exc}") from exc


# ─────────────────────────────────────────────────────────────────────────────
# Skill extraction
# ─────────────────────────────────────────────────────────────────────────────

async def extract_skills(job_description: str, criteria: str = "") -> List[str]:
    raw    = await _call_llm(_SKILL_PROMPT.format(
        job_description=job_description[:4000],
        criteria=criteria[:1000],
    ), max_tokens=300)
    skills = _parse_json(raw)
    if not isinstance(skills, list):
        raise ValueError("Skill extraction returned non-list JSON.")
    seen, out = set(), []
    for s in skills:
        name = str(s).strip()
        if name and name.lower() not in seen:
            seen.add(name.lower())
            out.append(name)
    logger.info("Extracted skills: %s", out)
    return out


# ─────────────────────────────────────────────────────────────────────────────
# Question generation
# ─────────────────────────────────────────────────────────────────────────────

async def _generate_raw_questions(
    job_description: str,
    skills: List[str],
    count: int = MCQ_COUNT,
) -> List[Dict[str, Any]]:
    raw  = await _call_llm(_MCQ_PROMPT.format(
        count=count,
        skills=", ".join(skills),
        job_description=job_description[:2000],
    ), max_tokens=4000)
    data = _parse_json(raw)
    if not isinstance(data, list):
        raise ValueError("MCQ generation returned non-list JSON.")

    valid = []
    for i, q in enumerate(data[:count], 1):
        missing = {"question", "options", "correct_answer", "related_skill"} - q.keys()
        if missing:
            logger.warning("Q%d missing keys %s – skipped.", i, missing); continue
        opts = q["options"]
        if not isinstance(opts, list) or len(opts) != 4:
            logger.warning("Q%d wrong option count – skipped.", i); continue
        ans = str(q["correct_answer"]).strip().upper()
        if ans not in ("A", "B", "C", "D"):
            logger.warning("Q%d invalid answer '%s' – skipped.", i, ans); continue

        clean = [re.sub(r"^[A-D][.):\s]+", "", str(o)).strip() for o in opts]
        valid.append({
            "question_text":  str(q["question"]).strip(),
            "option_a": clean[0], "option_b": clean[1],
            "option_c": clean[2], "option_d": clean[3],
            "correct_answer": ans,
            "related_skill":  str(q["related_skill"]).strip(),
        })

    if len(valid) < count:
        logger.warning("Only %d/%d valid questions generated.", len(valid), count)
    return valid


# ─────────────────────────────────────────────────────────────────────────────
# DB helpers
# ─────────────────────────────────────────────────────────────────────────────

def _get_cached_questions(db: Session, job_id: int) -> List[MCQQuestion]:
    return (
        db.query(MCQQuestion)
        .filter(MCQQuestion.job_id == job_id)
        .order_by(MCQQuestion.question_index)
        .all()
    )


def _save_questions(db: Session, job_id: int, raw: List[Dict]) -> List[MCQQuestion]:
    db.query(MCQQuestion).filter(MCQQuestion.job_id == job_id).delete()
    objs = []
    for idx, q in enumerate(raw, 1):
        obj = MCQQuestion(job_id=job_id, question_index=idx, **q)
        db.add(obj)
        objs.append(obj)
    db.commit()
    for o in objs:
        db.refresh(o)
    return objs


def _get_or_create_session(
    db: Session, candidate_id: int, job_id: int
) -> MCQSession:
    existing = (
        db.query(MCQSession)
        .filter(
            MCQSession.candidate_id == candidate_id,
            MCQSession.job_id       == job_id,
            MCQSession.status       == MCQSessionStatus.PENDING,
        )
        .first()
    )
    if existing:
        return existing
    s = MCQSession(candidate_id=candidate_id, job_id=job_id,
                   status=MCQSessionStatus.PENDING, started_at=datetime.utcnow())
    db.add(s); db.commit(); db.refresh(s)
    return s


# ─────────────────────────────────────────────────────────────────────────────
# Scoring
# ─────────────────────────────────────────────────────────────────────────────

def _score(
    answers: List[CandidateAnswer],
    questions: List[MCQQuestion],
) -> Tuple[float, int, List[str], List[str], Dict[str, Dict]]:
    q_map  = {q.id: q for q in questions}
    stats: Dict[str, Dict[str, int]] = defaultdict(lambda: {"correct": 0, "total": 0})
    correct = 0

    for a in answers:
        q = q_map.get(a.question_id)
        if not q:
            continue
        stats[q.related_skill]["total"] += 1
        if a.selected_option.upper() == q.correct_answer:
            correct += 1
            stats[q.related_skill]["correct"] += 1

    score         = (correct / MCQ_COUNT) * 100
    weak_skills   = [s for s, v in stats.items() if v["correct"] / v["total"] < 0.6]
    strong_skills = [s for s, v in stats.items() if v["correct"] / v["total"] >= 0.6]
    return score, correct, weak_skills, strong_skills, dict(stats)


# ─────────────────────────────────────────────────────────────────────────────
# Public functions
# ─────────────────────────────────────────────────────────────────────────────

async def generate_mcq(
    db: Session,
    job_id: Optional[int],
    job_description: str,
    criteria: str = "",
    force_regenerate: bool = False,
) -> MCQGenerateResponse:
    """Generate (or return cached) 10 MCQ questions for a job."""
    if job_id and not force_regenerate:
        cached = _get_cached_questions(db, job_id)
        if len(cached) >= MCQ_COUNT:
            return MCQGenerateResponse(
                job_id=job_id,
                questions=[MCQQuestionOut(**q.to_candidate_dict()) for q in cached],
                generated=False, total=len(cached),
            )

    skills = await extract_skills(job_description, criteria)
    if not skills:
        raise ValueError("Could not extract skills from the job description.")

    raw_qs = await _generate_raw_questions(job_description, skills)
    if not raw_qs:
        raise ValueError("LLM failed to generate valid questions.")

    saved = _save_questions(db, job_id, raw_qs) if job_id else []

    if saved:
        out_qs = [MCQQuestionOut(**q.to_candidate_dict()) for q in saved]
    else:
        out_qs = [
            MCQQuestionOut(
                id=i, question=q["question_text"],
                options=[q["option_a"], q["option_b"], q["option_c"], q["option_d"]],
                related_skill=q["related_skill"],
            )
            for i, q in enumerate(raw_qs, 1)
        ]

    return MCQGenerateResponse(
        job_id=job_id, questions=out_qs, generated=True, total=len(out_qs)
    )


async def submit_mcq(
    db: Session,
    candidate_id: int,
    job_id: int,
    answers: List[CandidateAnswer],
    session_id: Optional[int] = None,
) -> MCQSubmitResponse:
    """Score submitted answers, persist result, return structured response."""
    if session_id:
        mcq_session = db.get(MCQSession, session_id)
        if not mcq_session:
            raise ValueError(f"MCQSession {session_id} not found.")
    else:
        mcq_session = _get_or_create_session(db, candidate_id, job_id)

    questions = _get_cached_questions(db, job_id)
    if not questions:
        raise ValueError(
            f"No questions for job_id={job_id}. Call POST /api/mcq/generate first."
        )

    score, correct, weak, strong, breakdown = _score(answers, questions)

    q_map = {q.id: q for q in questions}
    for a in answers:
        q = q_map.get(a.question_id)
        if not q:
            continue
        db.add(MCQAnswer(
            session_id=mcq_session.id,
            question_id=a.question_id,
            selected_option=a.selected_option.upper(),
            is_correct=(a.selected_option.upper() == q.correct_answer),
            related_skill=q.related_skill,
        ))

    db.query(MCQResult).filter(MCQResult.session_id == mcq_session.id).delete()
    db_result = MCQResult(
        session_id=mcq_session.id, score=score,
        correct_answers=correct, total_questions=MCQ_COUNT,
        weak_skills=weak, strong_skills=strong, skill_breakdown=breakdown,
    )
    db.add(db_result)
    mcq_session.status       = MCQSessionStatus.COMPLETED
    mcq_session.submitted_at = datetime.utcnow()
    db.commit()
    db.refresh(db_result)

    logger.info("MCQ submitted  candidate=%d  job=%d  score=%.1f  %d/%d correct",
                candidate_id, job_id, score, correct, MCQ_COUNT)

    return MCQSubmitResponse(
        session_id=mcq_session.id, score=round(score, 1),
        correct_answers=correct, total_questions=MCQ_COUNT,
        weak_skills=weak, strong_skills=strong,
        skill_breakdown={k: SkillBreakdown(**v) for k, v in breakdown.items()},
        passed=score >= PASS_THRESHOLD,
    )


def get_mcq_result(db: Session, session_id: int) -> Optional[MCQResult]:
    return db.query(MCQResult).filter(MCQResult.session_id == session_id).first()


def build_interview_context(mcq_result: Optional[MCQResult]) -> Dict:
    """
    Returns the dict passed to the AI Interview Agent before the interview starts.
    Safe empty defaults when MCQ was not completed.
    """
    if not mcq_result:
        return {"mcq_score": None, "weak_skills": [], "strong_skills": [], "skill_breakdown": {}}
    return {
        "mcq_score":       round(mcq_result.score, 1),
        "weak_skills":     mcq_result.weak_skills,
        "strong_skills":   mcq_result.strong_skills,
        "skill_breakdown": mcq_result.skill_breakdown,
    }


def build_interview_prompt_section(mcq_result: Optional[MCQResult]) -> str:
    """
    Returns the text snippet appended to the AI Interview Agent system prompt.
    Empty string when MCQ was not run.
    """
    if not mcq_result:
        return ""
    return INTERVIEW_MCQ_SECTION.format(
        mcq_score=round(mcq_result.score, 1),
        strong_skills=", ".join(mcq_result.strong_skills) or "None",
        weak_skills=", ".join(mcq_result.weak_skills)    or "None",
    )
