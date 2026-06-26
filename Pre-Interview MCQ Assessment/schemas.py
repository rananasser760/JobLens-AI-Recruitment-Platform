"""
schemas.py
==========
All Pydantic v2 schemas for request validation and response serialisation.
"""

from __future__ import annotations

from typing import Dict, List, Literal, Optional
from pydantic import BaseModel, Field, field_validator, model_validator


# ── Shared ────────────────────────────────────────────────────────────────────

class MCQQuestionOut(BaseModel):
    """Sent to the candidate — correct_answer is never exposed."""
    id:            int
    question:      str
    options:       List[str] = Field(..., min_length=4, max_length=4)
    related_skill: str
    model_config = {"from_attributes": True}


class CandidateAnswer(BaseModel):
    question_id:     int = Field(..., gt=0)
    selected_option: Literal["A", "B", "C", "D"]


class SkillBreakdown(BaseModel):
    correct: int
    total:   int


# ── Jobs / Candidates (for seeding) ──────────────────────────────────────────

class JobCreate(BaseModel):
    title:        str = Field(..., min_length=2)
    description:  str = Field(..., min_length=10)
    requirements: str = ""
    criteria:     str = ""


class JobOut(BaseModel):
    id:          int
    title:       str
    description: str
    model_config = {"from_attributes": True}


class CandidateCreate(BaseModel):
    full_name: str = Field(..., min_length=2)
    email:     Optional[str] = None


class CandidateOut(BaseModel):
    id:        int
    full_name: str
    model_config = {"from_attributes": True}


# ── POST /api/mcq/generate ───────────────────────────────────────────────────

class MCQGenerateRequest(BaseModel):
    job_id:           Optional[int] = Field(None, gt=0)
    job_description:  Optional[str] = Field(None, min_length=20)
    criteria:         Optional[str] = None
    force_regenerate: bool          = False

    @model_validator(mode="after")
    def require_source(self) -> "MCQGenerateRequest":
        if self.job_id is None and self.job_description is None:
            raise ValueError("Provide either job_id or job_description.")
        return self


class MCQGenerateResponse(BaseModel):
    job_id:    Optional[int]
    questions: List[MCQQuestionOut]
    generated: bool = True
    total:     int  = 10


# ── POST /api/mcq/submit ─────────────────────────────────────────────────────

class MCQSubmitRequest(BaseModel):
    candidate_id: int                   = Field(..., gt=0)
    job_id:       int                   = Field(..., gt=0)
    session_id:   Optional[int]         = Field(None, gt=0)
    answers:      List[CandidateAnswer] = Field(..., min_length=1, max_length=10)

    @field_validator("answers")
    @classmethod
    def no_duplicate_question_ids(cls, v: List[CandidateAnswer]) -> List[CandidateAnswer]:
        ids = [a.question_id for a in v]
        if len(ids) != len(set(ids)):
            raise ValueError("Duplicate question_id in answers.")
        return v


class MCQSubmitResponse(BaseModel):
    session_id:      int
    score:           float = Field(..., ge=0, le=100)
    correct_answers: int
    total_questions: int
    weak_skills:     List[str]
    strong_skills:   List[str]
    skill_breakdown: Dict[str, SkillBreakdown]
    passed:          bool


# ── GET /api/mcq/session/{id} ────────────────────────────────────────────────

class MCQSessionOut(BaseModel):
    session_id:   int
    job_id:       int
    candidate_id: int
    status:       str
    questions:    List[MCQQuestionOut]


# ── GET /api/mcq/report/{session_id} ─────────────────────────────────────────

class FinalEvaluationReport(BaseModel):
    candidate_id:    int
    candidate_name:  str
    job_id:          int
    mcq_session_id:  int
    score:           float
    correct_answers: int
    total_questions: int
    weak_skills:     List[str]
    strong_skills:   List[str]
    skill_breakdown: Dict[str, SkillBreakdown]
    passed:          bool
    # Fields ready to be merged with interview score later
    mcq_score:       float
    interview_context: dict   # what gets passed to the AI Interview Agent
