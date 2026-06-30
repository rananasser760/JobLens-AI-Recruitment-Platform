from __future__ import annotations

from typing import Any, Dict, List, Optional

from pydantic import BaseModel


class CVParseTextRequest(BaseModel):
    resume_text: str


class ATSRequest(BaseModel):
    resume_text: str
    job_description: Optional[str] = None


class ImprovementRequest(BaseModel):
    resume_text: str


class CandidateEmbeddingRequest(BaseModel):
    candidate_id: int
    profile_data: Dict[str, Any]


class JobEmbeddingRequest(BaseModel):
    job_id: int
    job_data: Dict[str, Any]


class CVFullAnalysisRequest(BaseModel):
    resume_text: str
    include_improvements: bool = True
    job_match_limit: int = 5


class StandardResponse(BaseModel):
    success: bool
    data: Optional[Dict[str, Any]] = None
    message: Optional[str] = None


class ListResponse(BaseModel):
    success: bool
    data: Optional[List[Dict[str, Any]]] = None
    message: Optional[str] = None
