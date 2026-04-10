from __future__ import annotations

from threading import Lock

import chromadb

from .config import get_recruitment_settings


class RecruitmentVectorStore:
    def __init__(self) -> None:
        self._lock = Lock()
        self._client = None
        self._jobs_col = None
        self._candidates_col = None

    def _ensure(self) -> None:
        if self._client is not None:
            return

        with self._lock:
            if self._client is not None:
                return

            settings = get_recruitment_settings()
            self._client = chromadb.PersistentClient(path=settings.chroma_path)
            self._jobs_col = self._client.get_or_create_collection(name="job_listings")
            self._candidates_col = self._client.get_or_create_collection(name="candidates")

    @property
    def client(self):
        self._ensure()
        return self._client

    @property
    def jobs_col(self):
        self._ensure()
        return self._jobs_col

    @property
    def candidates_col(self):
        self._ensure()
        return self._candidates_col

    def stats(self) -> dict:
        self._ensure()
        return {
            "chroma_path": get_recruitment_settings().chroma_path,
            "job_count": self._jobs_col.count(),
            "candidate_count": self._candidates_col.count(),
        }


store = RecruitmentVectorStore()
