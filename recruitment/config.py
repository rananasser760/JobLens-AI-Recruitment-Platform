from __future__ import annotations

import os
from functools import lru_cache
from pydantic import BaseModel, Field


def _int_env(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except ValueError:
        return default


def _bool_env(name: str, default: bool) -> bool:
    fallback = "true" if default else "false"
    return os.getenv(name, fallback).lower() in {"1", "true", "yes", "on"}


class RecruitmentSettings(BaseModel):
    chroma_path: str = Field(default_factory=lambda: os.getenv("JOBLENS_CHROMA_PATH", "./joblens_db"))
    provider: str = Field(default_factory=lambda: os.getenv("JOBLENS_LLM_PROVIDER", "openrouter"))

    openrouter_api_key: str = Field(default_factory=lambda: os.getenv("OPENROUTER_API_KEY", ""))
    groq_api_key: str = Field(default_factory=lambda: os.getenv("GROQ_API_KEY", ""))

    parsing_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_PARSING_MODEL", "meta-llama/llama-3.3-70b-instruct"))
    parsing_fallback_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_PARSING_FALLBACK_MODEL", "meta-llama/llama-3.1-70b-instruct"))
    ats_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_ATS_MODEL", "meta-llama/llama-3.3-70b-instruct"))
    scoring_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_SCORING_MODEL", "meta-llama/llama-3.3-70b-instruct"))
    ocr_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_OCR_MODEL", "meta-llama/llama-3.2-90b-vision-preview"))

    scraper_embedding_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_SCRAPER_EMBEDDING_MODEL", "all-MiniLM-L6-v2"))
    cv_embedding_model: str = Field(default_factory=lambda: os.getenv("JOBLENS_CV_EMBEDDING_MODEL", "sentence-transformers/all-mpnet-base-v2"))

    concurrency_limit: int = Field(default_factory=lambda: _int_env("JOBLENS_SCRAPER_CONCURRENCY", 5))
    scheduled_scrape_hours: int = Field(default_factory=lambda: _int_env("JOBLENS_SCRAPER_INTERVAL_HOURS", 6))
    scheduled_scrape_enabled: bool = Field(default_factory=lambda: _bool_env("JOBLENS_SCRAPER_SCHEDULED", False))

    target_categories: list[str] = Field(
        default=[
            "Data Science",
            "Engineering",
            "Information Technology",
            "Software Development",
            "Web Development",
            "Mobile Development",
            "Data Engineering",
            "Data Analytics",
            "Artificial Intelligence",
            "Machine Learning Engineering",
            "Cloud Computing",
            "DevOps Engineering",
            "Site Reliability Engineering (SRE)",
            "Cybersecurity",
            "Information Security",
            "Network Engineering",
            "System Administration",
            "Database Administration",
            "Big Data Engineering",
            "Blockchain Development",
            "Embedded Systems Engineering",
            "Game Development",
            "IT Support / Technical Support",
            "IT Infrastructure",
            "Automation Engineering",
            "Quality Engineering (Software Testing)",
            "UI/UX Design",
            "Product Engineering",
            "Robotics Engineering",
            "AR/VR Development",
            "Platform Engineering",
            "API Development",
            "Technical Architecture",
            "Product Management",
            "Finance",
            "Marketing",
            "Sales",
            "Business Development",
        ]
    )


@lru_cache(maxsize=1)
def get_recruitment_settings() -> RecruitmentSettings:
    return RecruitmentSettings()
