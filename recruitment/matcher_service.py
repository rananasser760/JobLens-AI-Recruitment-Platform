from __future__ import annotations

import json
from functools import lru_cache
from typing import Dict, List, Optional

import numpy as np
from sklearn.metrics.pairwise import cosine_similarity
from sentence_transformers import SentenceTransformer

from .config import get_recruitment_settings
from .cv_service import _clean_json
from .llm_client import get_llm_client
from .vector_store import store


@lru_cache(maxsize=1)
def get_cv_embedding_model() -> SentenceTransformer:
    settings = get_recruitment_settings()
    return SentenceTransformer(settings.cv_embedding_model)


def _safe_json_load(value: str):
    try:
        return json.loads(value)
    except Exception:
        return value


def _to_builtin_types(value):
    if isinstance(value, dict):
        return {key: _to_builtin_types(item) for key, item in value.items()}

    if isinstance(value, list):
        return [_to_builtin_types(item) for item in value]

    if isinstance(value, tuple):
        return tuple(_to_builtin_types(item) for item in value)

    if isinstance(value, np.generic):
        return value.item()

    return value


class JobMatcher:
    """Matches parsed CV data against scraped jobs stored in ChromaDB."""

    WEIGHTS = {
        "job_title": 0.25,
        "skills": 0.30,
        "experience": 0.25,
        "summary": 0.10,
        "projects": 0.05,
        "certifications": 0.05,
    }

    def __init__(self) -> None:
        self.model = get_cv_embedding_model()

    def _cv_components(self, cv: Dict) -> Dict[str, str]:
        components = {}
        components["job_title"] = f"Target Role: {cv.get('job_title', '')}"

        skills_data = cv.get("skills", {})
        all_skills = []
        if isinstance(skills_data, dict):
            for skills in skills_data.values():
                if isinstance(skills, list):
                    all_skills.extend(skills)
        components["skills"] = f"Skills: {', '.join(all_skills)}"

        exp_parts = []
        for exp in cv.get("experience", []):
            parts = [exp.get("title", ""), f"at {exp.get('company', '')}"]
            if isinstance(exp.get("responsibilities"), list):
                parts += exp["responsibilities"]
            if isinstance(exp.get("achievements"), list):
                parts += exp["achievements"]
            if exp.get("description"):
                parts.append(exp["description"])
            exp_parts.append(" ".join(parts))

        components["experience"] = " ".join(exp_parts)
        components["summary"] = cv.get("summary", "")
        components["projects"] = " ".join(project.get("description", "") for project in cv.get("projects", []))
        components["certifications"] = " ".join(
            f"Certified in {cert.get('name', '')}" for cert in cv.get("certifications", [])
        )
        return components

    @staticmethod
    def _job_components(job: Dict) -> Dict[str, str]:
        skills = job.get("required_skills", [])
        if isinstance(skills, list):
            skills = ", ".join(skills)

        return {
            "job_title": job.get("title", ""),
            "skills": skills,
            "experience": job.get("description", ""),
            "summary": job.get("description", ""),
            "projects": "",
            "certifications": job.get("preferred_qualifications", "") or "",
        }

    def _weighted_sim(self, cv_components: Dict, job_components: Dict) -> float:
        total = 0.0
        weight_sum = 0.0

        for key, weight in self.WEIGHTS.items():
            cv_text = cv_components.get(key, "")
            job_text = job_components.get(key, "")
            if cv_text and job_text:
                similarity = cosine_similarity(
                    self.model.encode([cv_text]),
                    self.model.encode([job_text]),
                )[0][0]
                total += similarity * weight
                weight_sum += weight

        return (total / weight_sum * 100) if weight_sum > 0 else 0.0

    def _llm_match(self, cv: Dict, job: Dict) -> Optional[Dict]:
        settings = get_recruitment_settings()
        llm_client = get_llm_client()

        prompt = f"""You are a recruitment matching expert.

CANDIDATE CV: {json.dumps(cv, indent=2)}
JOB POSITION: {json.dumps(job, indent=2)}

Return EXACTLY this JSON (no markdown):
{{
  "match_score": 0,
  "match_level": "Excellent/Good/Fair/Poor",
  "recommendation": "",
  "detailed_scores": {{"skills_match":0,"experience_match":0,"education_match":0,
                       "cultural_fit":0,"career_trajectory":0}},
  "matched_requirements": [],
  "missing_requirements": [{{"requirement":"","importance":"","can_learn":true,"time_to_acquire":""}}],
  "strengths_for_role": [],
  "concerns": [],
  "differentiators": [],
  "interview_talking_points": [],
  "likelihood_of_offer": "High/Medium/Low",
  "application_strategy": ""
}}"""

        try:
            response = llm_client.chat.completions.create(
                model=settings.scoring_model,
                messages=[
                    {"role": "system", "content": "Recruitment expert. Return only valid JSON."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=2000,
                temperature=0.3,
            )
            return json.loads(_clean_json(response.choices[0].message.content.strip()))
        except Exception:
            return None

    def match_jobs_from_db(self, parsed_cv: Dict, n_results: int = 5) -> List[Dict]:
        total_jobs = store.jobs_col.count()
        if total_jobs == 0:
            return []

        cv_components = self._cv_components(parsed_cv)
        query_text = " ".join(
            filter(
                None,
                [
                    cv_components.get("job_title", ""),
                    cv_components.get("skills", ""),
                    cv_components.get("experience", "")[:500],
                ],
            )
        )

        n_fetch = min(n_results * 3, total_jobs)
        db_results = store.jobs_col.query(query_texts=[query_text], n_results=n_fetch)

        candidates = []
        for index in range(len(db_results["ids"][0])):
            metadata = db_results["metadatas"][0][index]
            try:
                detailed = json.loads(metadata.get("json_detailed", "{}"))
            except Exception:
                detailed = {}

            job_doc = {
                "title": metadata.get("title", "N/A"),
                "company": metadata.get("company", "N/A"),
                "location": metadata.get("location", "N/A"),
                "source": metadata.get("source", "N/A"),
                "apply_link": metadata.get("apply_link", metadata.get("job_page_link", "#")),
                "description": detailed.get("description", ""),
                "required_skills": detailed.get("skills", []),
                "experience_level": metadata.get("experience_level", ""),
                "employment_type": metadata.get("employment_type", ""),
            }

            similarity = self._weighted_sim(cv_components, self._job_components(job_doc))
            candidates.append((similarity, job_doc))

        candidates.sort(key=lambda item: item[0], reverse=True)
        top_jobs = candidates[:n_results]

        result = []
        for similarity, job in top_jobs:
            llm_analysis = self._llm_match(parsed_cv, job)
            entry = {
                "job_title": job["title"],
                "company": job["company"],
                "location": job["location"],
                "source": job["source"],
                "apply_link": job["apply_link"],
                "semantic_similarity": float(round(similarity, 2)),
            }

            if llm_analysis:
                entry.update(_to_builtin_types(llm_analysis))
            else:
                entry.update(
                    {
                        "match_score": float(round(similarity, 2)),
                        "match_level": self._match_level(similarity),
                        "recommendation": "Based on semantic similarity only.",
                    }
                )

            result.append(_to_builtin_types(entry))

        result.sort(key=lambda item: item.get("match_score", 0), reverse=True)
        return result

    @staticmethod
    def _match_level(score: float) -> str:
        if score >= 85:
            return "Excellent"
        if score >= 70:
            return "Good"
        if score >= 55:
            return "Fair"
        return "Poor"


def recommend_jobs_for_candidate(candidate_id: int, limit: int = 10) -> List[Dict]:
    candidate = store.candidates_col.get(ids=[str(candidate_id)])
    if not candidate.get("documents"):
        return []

    result = store.jobs_col.query(query_texts=[candidate["documents"][0]], n_results=limit)

    matches = []
    for index in range(len(result["ids"][0])):
        raw_preview = result["documents"][0][index]
        matches.append(
            {
                "job_id": result["ids"][0][index],
                "match_score": float(round(max(0.0, 1.0 - result["distances"][0][index]), 2)),
                "job_preview": _safe_json_load(raw_preview),
            }
        )

    return matches


def recommend_candidates_for_job(job_id: str, limit: int = 50, min_score: float = 0.3) -> List[Dict]:
    normalized_job_id = str(job_id)
    candidate_ids = [normalized_job_id]
    if not normalized_job_id.startswith("job_"):
        candidate_ids.append(f"job_{normalized_job_id}")

    job = store.jobs_col.get(ids=candidate_ids)
    if not job.get("documents"):
        return []

    result = store.candidates_col.query(query_texts=[job["documents"][0]], n_results=limit)

    matches = []
    if result.get("distances"):
        for index in range(len(result["ids"][0])):
            score = max(0.0, 1.0 - result["distances"][0][index])
            if score < min_score:
                continue

            raw_preview = result["documents"][0][index]
            matches.append(
                {
                    "candidate_id": result["ids"][0][index],
                    "score": float(round(score, 2)),
                    "candidate_preview": _safe_json_load(raw_preview),
                }
            )

    matches.sort(key=lambda item: item["score"], reverse=True)
    return matches
