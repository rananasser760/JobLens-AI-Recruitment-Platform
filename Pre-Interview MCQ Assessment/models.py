"""
models.py
=========
All database tables for the standalone MCQ service.

Tables
------
  jobs            – minimal job record (title + description + criteria)
  candidates      – minimal candidate record
  mcq_questions   – 10 LLM-generated questions per job (cached)
  mcq_sessions    – one quiz attempt per candidate + job
  mcq_answers     – one row per submitted answer
  mcq_results     – aggregated score, weak/strong skills per session
"""

from __future__ import annotations

import enum
from datetime import datetime

from sqlalchemy import (
    BigInteger, Boolean, Column, DateTime,
    Enum as SAEnum, Float, ForeignKey,
    Integer, JSON, String, Text, UniqueConstraint,
)
from sqlalchemy.orm import relationship

from database import Base


# ── Minimal Job ───────────────────────────────────────────────────────────────

class Job(Base):
    __tablename__ = "jobs"

    id           = Column(Integer, primary_key=True, autoincrement=True)
    title        = Column(String(255), nullable=False)
    description  = Column(Text,        nullable=False)
    requirements = Column(Text,        nullable=True,  default="")
    criteria     = Column(Text,        nullable=True,  default="")
    created_at   = Column(DateTime,    default=datetime.utcnow)

    mcq_questions = relationship("MCQQuestion", back_populates="job",
                                  cascade="all, delete-orphan")
    mcq_sessions  = relationship("MCQSession",  back_populates="job",
                                  cascade="all, delete-orphan")


# ── Minimal Candidate ─────────────────────────────────────────────────────────

class Candidate(Base):
    __tablename__ = "candidates"

    id         = Column(Integer, primary_key=True, autoincrement=True)
    full_name  = Column(String(255), nullable=False)
    email      = Column(String(255), nullable=True)
    created_at = Column(DateTime,    default=datetime.utcnow)

    mcq_sessions = relationship("MCQSession", back_populates="candidate",
                                 cascade="all, delete-orphan")


# ── MCQ Enums ─────────────────────────────────────────────────────────────────

class MCQSessionStatus(str, enum.Enum):
    PENDING   = "PENDING"
    COMPLETED = "COMPLETED"
    EXPIRED   = "EXPIRED"


# ── MCQQuestion ───────────────────────────────────────────────────────────────

class MCQQuestion(Base):
    __tablename__ = "mcq_questions"

    id             = Column(Integer, primary_key=True, autoincrement=True)
    job_id         = Column(Integer, ForeignKey("jobs.id", ondelete="CASCADE"),
                            nullable=False, index=True)
    question_text  = Column(Text,        nullable=False)
    option_a       = Column(String(512), nullable=False)
    option_b       = Column(String(512), nullable=False)
    option_c       = Column(String(512), nullable=False)
    option_d       = Column(String(512), nullable=False)
    correct_answer = Column(String(1),   nullable=False)   # A | B | C | D
    related_skill  = Column(String(128), nullable=False)
    question_index = Column(Integer,     nullable=False, default=1)
    created_at     = Column(DateTime,    default=datetime.utcnow)

    job     = relationship("Job",       back_populates="mcq_questions")
    answers = relationship("MCQAnswer", back_populates="question",
                           cascade="all, delete-orphan")

    def to_candidate_dict(self) -> dict:
        """Safe public view — correct_answer NOT included."""
        return {
            "id":            self.id,
            "question":      self.question_text,
            "options":       [self.option_a, self.option_b,
                              self.option_c, self.option_d],
            "related_skill": self.related_skill,
        }


# ── MCQSession ────────────────────────────────────────────────────────────────

class MCQSession(Base):
    __tablename__ = "mcq_sessions"
    __table_args__ = (
        UniqueConstraint("candidate_id", "job_id",
                         name="uq_mcq_session_candidate_job"),
    )

    id           = Column(Integer, primary_key=True, autoincrement=True)
    candidate_id = Column(Integer, ForeignKey("candidates.id", ondelete="CASCADE"),
                          nullable=False, index=True)
    job_id       = Column(Integer, ForeignKey("jobs.id", ondelete="CASCADE"),
                          nullable=False, index=True)
    status       = Column(SAEnum(MCQSessionStatus),
                          default=MCQSessionStatus.PENDING, nullable=False)
    started_at   = Column(DateTime, default=datetime.utcnow)
    submitted_at = Column(DateTime, nullable=True)

    candidate = relationship("Candidate",  back_populates="mcq_sessions")
    job       = relationship("Job",        back_populates="mcq_sessions")
    answers   = relationship("MCQAnswer",  back_populates="session",
                             cascade="all, delete-orphan")
    result    = relationship("MCQResult",  back_populates="session",
                             uselist=False, cascade="all, delete-orphan")


# ── MCQAnswer ─────────────────────────────────────────────────────────────────

class MCQAnswer(Base):
    __tablename__ = "mcq_answers"

    id              = Column(Integer, primary_key=True, autoincrement=True)
    session_id      = Column(Integer, ForeignKey("mcq_sessions.id",  ondelete="CASCADE"),
                             nullable=False, index=True)
    question_id     = Column(Integer, ForeignKey("mcq_questions.id", ondelete="CASCADE"),
                             nullable=False)
    selected_option = Column(String(1),   nullable=False)
    is_correct      = Column(Boolean,     nullable=False, default=False)
    related_skill   = Column(String(128), nullable=False)
    answered_at     = Column(DateTime,    default=datetime.utcnow)

    session  = relationship("MCQSession",  back_populates="answers")
    question = relationship("MCQQuestion", back_populates="answers")


# ── MCQResult ─────────────────────────────────────────────────────────────────

class MCQResult(Base):
    __tablename__ = "mcq_results"

    id              = Column(Integer, primary_key=True, autoincrement=True)
    session_id      = Column(Integer, ForeignKey("mcq_sessions.id", ondelete="CASCADE"),
                             nullable=False, unique=True, index=True)
    score           = Column(Float,   nullable=False)
    correct_answers = Column(Integer, nullable=False)
    total_questions = Column(Integer, nullable=False, default=10)
    weak_skills     = Column(JSON,    nullable=False, default=list)
    strong_skills   = Column(JSON,    nullable=False, default=list)
    skill_breakdown = Column(JSON,    nullable=False, default=dict)
    tab_switches    = Column(Integer, nullable=False, default=0)
    cam_violations  = Column(JSON,    nullable=False, default=dict)
    created_at      = Column(DateTime, default=datetime.utcnow)

    session = relationship("MCQSession", back_populates="result")

    def to_dict(self) -> dict:
        return {
            "session_id":      self.session_id,
            "score":           round(self.score, 1),
            "correct_answers": self.correct_answers,
            "total_questions": self.total_questions,
            "weak_skills":     self.weak_skills,
            "strong_skills":   self.strong_skills,
            "skill_breakdown": self.skill_breakdown,
            "tab_switches":    self.tab_switches,
            "cam_violations":  self.cam_violations,
        }
