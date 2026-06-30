from __future__ import annotations

from functools import lru_cache

from openai import OpenAI

from .config import get_recruitment_settings


@lru_cache(maxsize=1)
def get_llm_client() -> OpenAI:
    settings = get_recruitment_settings()
    provider = settings.provider.lower().strip()

    if provider == "openrouter":
        if not settings.openrouter_api_key:
            raise RuntimeError("OPENROUTER_API_KEY is missing. Set it in your environment.")
        return OpenAI(
            api_key=settings.openrouter_api_key,
            base_url="https://openrouter.ai/api/v1",
        )

    if provider == "groq":
        if not settings.groq_api_key:
            raise RuntimeError("GROQ_API_KEY is missing. Set it in your environment.")
        return OpenAI(
            api_key=settings.groq_api_key,
            base_url="https://api.groq.com/openai/v1",
        )

    raise RuntimeError(f"Unsupported JOBLENS_LLM_PROVIDER: {settings.provider}")
