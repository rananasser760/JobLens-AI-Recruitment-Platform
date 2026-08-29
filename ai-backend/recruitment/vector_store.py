from __future__ import annotations

from threading import Lock
from typing import Any

import chromadb

from .config import get_recruitment_settings


LEGACY_JOBS_COLLECTION_NAME = "job_listings"
INTERNAL_JOBS_COLLECTION_NAME = "job_listings_internal"
SCRAPED_JOBS_COLLECTION_NAME = "job_listings_scraped"
CANDIDATES_COLLECTION_NAME = "candidates"

_EXTERNAL_SOURCE_TOKENS = {"wuzzuf", "linkedin", "scraped", "external"}
_EXTERNAL_HOST_TOKENS = ("wuzzuf.net", "linkedin.com")


class RecruitmentVectorStore:
    def __init__(self) -> None:
        self._lock = Lock()
        self._client = None
        self._legacy_jobs_col = None
        self._internal_jobs_col = None
        self._scraped_jobs_col = None
        self._jobs_col = None
        self._candidates_col = None
        self._legacy_migrated = False

    def _ensure(self) -> None:
        if self._client is not None:
            return

        with self._lock:
            if self._client is not None:
                return

            settings = get_recruitment_settings()
            self._client = chromadb.PersistentClient(path=settings.chroma_path)
            self._legacy_jobs_col = self._client.get_or_create_collection(name=LEGACY_JOBS_COLLECTION_NAME)
            self._internal_jobs_col = self._client.get_or_create_collection(name=INTERNAL_JOBS_COLLECTION_NAME)
            self._scraped_jobs_col = self._client.get_or_create_collection(name=SCRAPED_JOBS_COLLECTION_NAME)
            self._jobs_col = self._internal_jobs_col
            self._candidates_col = self._client.get_or_create_collection(name=CANDIDATES_COLLECTION_NAME)
            self._migrate_legacy_job_listings()

    @staticmethod
    def _is_external_job_metadata(metadata: dict[str, Any]) -> bool:
        source = str(metadata.get("source", "") or "").strip().lower()
        if source in _EXTERNAL_SOURCE_TOKENS:
            return True

        links = " ".join(
            [
                str(metadata.get("job_page_link", "") or ""),
                str(metadata.get("apply_link", "") or ""),
            ]
        ).lower()
        return any(token in links for token in _EXTERNAL_HOST_TOKENS)

    @staticmethod
    def _upsert_records(
        collection,
        ids: list[str],
        documents: list[str],
        metadatas: list[dict[str, Any]],
        embeddings: list[Any],
    ) -> None:
        if not ids:
            return

        payload: dict[str, Any] = {
            "ids": ids,
            "documents": documents,
            "metadatas": metadatas,
        }
        has_embeddings = len(embeddings) == len(ids) and all(item is not None for item in embeddings)
        if has_embeddings:
            payload["embeddings"] = embeddings

        collection.upsert(**payload)

    def _migrate_legacy_job_listings(self) -> None:
        if self._legacy_migrated:
            return
        self._legacy_migrated = True

        if self._legacy_jobs_col is None or self._internal_jobs_col is None or self._scraped_jobs_col is None:
            return

        legacy = self._legacy_jobs_col.get(include=["embeddings", "metadatas", "documents"])
        legacy_ids = [str(item) for item in legacy.get("ids", [])]
        if not legacy_ids:
            return

        legacy_documents = legacy.get("documents", [])
        legacy_metadatas = legacy.get("metadatas", [])
        legacy_embeddings = legacy.get("embeddings", [])

        existing_scraped_ids = {
            str(item)
            for item in (self._scraped_jobs_col.get().get("ids", []) if self._scraped_jobs_col.count() > 0 else [])
        }
        existing_internal_ids = {
            str(item)
            for item in (self._internal_jobs_col.get().get("ids", []) if self._internal_jobs_col.count() > 0 else [])
        }

        scraped_ids: list[str] = []
        scraped_docs: list[str] = []
        scraped_meta: list[dict[str, Any]] = []
        scraped_embeddings: list[Any] = []

        internal_ids: list[str] = []
        internal_docs: list[str] = []
        internal_meta: list[dict[str, Any]] = []
        internal_embeddings: list[Any] = []

        for index, vector_id in enumerate(legacy_ids):
            metadata = legacy_metadatas[index] if index < len(legacy_metadatas) and isinstance(legacy_metadatas[index], dict) else {}
            document = str(legacy_documents[index] if index < len(legacy_documents) else "")
            embedding = legacy_embeddings[index] if index < len(legacy_embeddings) else None

            if self._is_external_job_metadata(metadata):
                if vector_id in existing_scraped_ids:
                    continue
                scraped_ids.append(vector_id)
                scraped_docs.append(document)
                scraped_meta.append(metadata)
                scraped_embeddings.append(embedding)
                continue

            if vector_id in existing_internal_ids:
                continue

            internal_ids.append(vector_id)
            internal_docs.append(document)
            internal_meta.append(metadata)
            internal_embeddings.append(embedding)

        self._upsert_records(self._scraped_jobs_col, scraped_ids, scraped_docs, scraped_meta, scraped_embeddings)
        self._upsert_records(self._internal_jobs_col, internal_ids, internal_docs, internal_meta, internal_embeddings)

    @property
    def client(self):
        self._ensure()
        return self._client

    @property
    def jobs_col(self):
        self._ensure()
        return self._jobs_col

    @property
    def legacy_jobs_col(self):
        self._ensure()
        return self._legacy_jobs_col

    @property
    def internal_jobs_col(self):
        self._ensure()
        return self._internal_jobs_col

    @property
    def scraped_jobs_col(self):
        self._ensure()
        return self._scraped_jobs_col

    @property
    def candidates_col(self):
        self._ensure()
        return self._candidates_col

    def stats(self) -> dict:
        self._ensure()
        return {
            "chroma_path": get_recruitment_settings().chroma_path,
            "job_count": self._internal_jobs_col.count(),
            "internal_job_count": self._internal_jobs_col.count(),
            "scraped_job_count": self._scraped_jobs_col.count(),
            "legacy_job_count": self._legacy_jobs_col.count(),
            "candidate_count": self._candidates_col.count(),
        }


store = RecruitmentVectorStore()
