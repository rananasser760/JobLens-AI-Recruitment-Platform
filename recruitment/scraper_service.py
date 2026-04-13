from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import random
import re
import sys
import urllib.parse
from datetime import datetime, timedelta
from functools import lru_cache
from typing import Any, Dict, List, Optional, Tuple

from playwright.async_api import async_playwright

try:
    from playwright_stealth import stealth_async

    STEALTH_AVAILABLE = True
except Exception:
    STEALTH_AVAILABLE = False

from sentence_transformers import SentenceTransformer

from .config import get_recruitment_settings
from .llm_client import get_llm_client
from .vector_store import store


SOURCE_BASE_URLS = {
    "Wuzzuf": "https://wuzzuf.net",
    "LinkedIn": "https://www.linkedin.com",
}

PLACEHOLDER_TEXT_VALUES = {
    "description not found.",
    "see description",
    "n/a",
    "na",
    "not specified",
    "not available",
    "tbd",
    "tba",
}

EGYPT_LOCATION_TOKENS = {
    "egypt",
    "cairo",
    "giza",
    "alexandria",
    "aswan",
    "luxor",
    "mansoura",
    "tanta",
    "suez",
    "ismailia",
    "zagazig",
    "port said",
    "new cairo",
    "maadi",
    "nasr city",
    "october",
    "sheikh zayed",
    "smart village",
}

NON_EGYPT_LOCATION_TOKENS = {
    "dubai",
    "abu dhabi",
    "riyadh",
    "saudi",
    "uae",
    "united arab emirates",
    "qatar",
    "doha",
    "kuwait",
    "oman",
    "jordan",
    "lebanon",
    "morocco",
    "tunisia",
    "algeria",
    "turkey",
    "germany",
    "france",
    "uk",
    "united kingdom",
    "usa",
    "united states",
    "canada",
    "india",
    "pakistan",
}

logger = logging.getLogger(__name__)


async def _supports_async_subprocess() -> bool:
    """Playwright launches browser subprocesses; some Windows event loops cannot do that."""
    if os.name != "nt":
        return True

    try:
        proc = await asyncio.create_subprocess_exec(
            sys.executable,
            "-c",
            "print('ok')",
            stdout=asyncio.subprocess.DEVNULL,
            stderr=asyncio.subprocess.DEVNULL,
        )
        await proc.wait()
        return True
    except NotImplementedError:
        return False
    except Exception:
        # If subprocess support exists but the probe fails for another reason,
        # allow scraper to continue and let the normal error handling report it.
        return True


@lru_cache(maxsize=1)
def get_scraper_embedding_model() -> SentenceTransformer:
    settings = get_recruitment_settings()
    return SentenceTransformer(settings.scraper_embedding_model)


def parse_relative_date(text: str) -> str:
    if not text:
        return str(datetime.now().date())

    text = text.lower()
    today = datetime.now()

    try:
        if "hour" in text or "minute" in text or "just now" in text:
            delta = timedelta(days=0)
        elif "day" in text:
            delta = timedelta(days=int(re.search(r"\d+", text).group()))
        elif "week" in text:
            delta = timedelta(weeks=int(re.search(r"\d+", text).group()))
        elif "month" in text:
            delta = timedelta(days=int(re.search(r"\d+", text).group()) * 30)
        else:
            delta = timedelta(days=0)

        return (today - delta).strftime("%Y-%m-%d")
    except Exception:
        return str(today.date())


def parse_posted_time(value: str) -> str:
    text = (value or "").strip()
    if not text:
        return str(datetime.now().date())

    lowered = text.lower()
    if any(token in lowered for token in ["hour", "minute", "just now", "day", "week", "month", "ago"]):
        return parse_relative_date(lowered)

    iso_candidate = text.replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(iso_candidate)
        return parsed.date().isoformat()
    except Exception:
        pass

    for fmt in ("%Y-%m-%d", "%d-%m-%Y", "%d/%m/%Y", "%b %d, %Y", "%B %d, %Y"):
        try:
            return datetime.strptime(text, fmt).date().isoformat()
        except ValueError:
            continue

    return parse_relative_date(text)


def _normalize_text_value(value: str) -> str:
    return re.sub(r"\s+", " ", (value or "").strip()).lower()


def _is_placeholder_text(value: str) -> bool:
    normalized = _normalize_text_value(value)
    return not normalized or normalized in PLACEHOLDER_TEXT_VALUES


def parse_description(full_text: str) -> tuple[str, str]:
    if not full_text:
        return "", ""

    requirement_patterns = [
        r"\brequirements?\b",
        r"\bqualifications?\b",
        r"\bwhat\s+you\s+need\b",
        r"\bskills\s+required\b",
    ]
    responsibility_patterns = [
        r"\bresponsibilities\b",
        r"\bduties\b",
        r"\bwhat\s+you(?:'ll|\s+will)\s+do\b",
        r"\bkey\s+tasks?\b",
    ]

    def find_first_index(patterns: List[str]) -> int:
        indexes: List[int] = []
        for pattern in patterns:
            match = re.search(pattern, full_text, re.IGNORECASE)
            if match:
                indexes.append(match.start())
        return min(indexes) if indexes else -1

    req_start = find_first_index(requirement_patterns)
    resp_start = find_first_index(responsibility_patterns)

    requirements = ""
    responsibilities = ""

    if req_start != -1 and resp_start != -1:
        if req_start < resp_start:
            requirements = full_text[req_start:resp_start]
            responsibilities = full_text[resp_start:]
        else:
            responsibilities = full_text[resp_start:req_start]
            requirements = full_text[req_start:]
    elif req_start != -1:
        requirements = full_text[req_start:]
    elif resp_start != -1:
        responsibilities = full_text[resp_start:]

    return requirements.strip(), responsibilities.strip()


async def block_resources(route) -> None:
    if route.request.resource_type in ["image", "media", "font", "stylesheet"]:
        await route.abort()
    else:
        await route.continue_()


def normalize_job_url(url: str, source: str) -> str:
    text = (url or "").strip()
    if not text:
        return ""

    if text.startswith("//"):
        return f"https:{text}"

    base = SOURCE_BASE_URLS.get(source, "")
    return urllib.parse.urljoin(base, text) if base else text


def normalize_skills(skills: List[str]) -> List[str]:
    seen: set[str] = set()
    normalized: List[str] = []

    for skill in skills:
        value = re.sub(r"\s+", " ", str(skill or "").strip())
        if not value:
            continue

        key = value.lower()
        if key in seen:
            continue

        seen.add(key)
        normalized.append(value)

    return normalized


def _normalize_location_label(value: str) -> str:
    text = re.sub(r"\s+", " ", str(value or "").strip())
    text = text.replace("|", " ").replace("/", " ")
    text = re.sub(r"\s+,", ",", text)
    return text.strip(" -,")


def _is_egypt_location_label(location: str) -> bool:
    normalized = _normalize_text_value(location)
    if not normalized:
        return False

    if any(token in normalized for token in NON_EGYPT_LOCATION_TOKENS):
        return any(token in normalized for token in EGYPT_LOCATION_TOKENS)

    if any(token in normalized for token in EGYPT_LOCATION_TOKENS):
        return True

    # Common Egypt-specific variants seen on boards.
    if "eg" == normalized or normalized.endswith(" eg"):
        return True

    return False


def _clean_llm_json(raw: str) -> str:
    text = (raw or "").strip()
    if "```json" in text:
        text = text.split("```json", 1)[1].split("```", 1)[0]
    elif "```" in text:
        parts = text.split("```")
        text = parts[1] if len(parts) >= 2 else text

    text = text.strip()
    if not text.startswith("{"):
        start = text.find("{")
        end = text.rfind("}")
        if start != -1 and end != -1:
            text = text[start : end + 1]

    return text


def _skills_from_any(value: Any) -> List[str]:
    if isinstance(value, list):
        return normalize_skills([str(item).strip() for item in value if str(item).strip()])
    if isinstance(value, str):
        chunks = [chunk.strip() for chunk in re.split(r"[,|;\n]", value) if chunk.strip()]
        return normalize_skills(chunks)
    return []


async def goto_with_retry(page, url: str, timeout_ms: int, attempts: int = 3) -> bool:
    for attempt in range(1, attempts + 1):
        try:
            await page.goto(url, timeout=timeout_ms, wait_until="domcontentloaded")
            return True
        except Exception as exc:
            if attempt >= attempts:
                logger.warning("Scraper failed to load URL after %s attempts: %s (%s)", attempts, url, exc)
                return False

            backoff = min(5.0, attempt * 1.25) + random.uniform(0.1, 0.6)
            await asyncio.sleep(backoff)

    return False


class JobLensScraper:
    """Scrapes Wuzzuf and LinkedIn, then stores results in the jobs collection."""

    def __init__(self) -> None:
        self.collection = store.scraped_jobs_col
        self.model = get_scraper_embedding_model()
        self.settings = get_recruitment_settings()
        self._llm_client = None

        existing = self.collection.get()
        self.existing_ids = set(existing["ids"]) if existing and existing.get("ids") else set()

    @staticmethod
    def normalize_text(text: str) -> str:
        return re.sub(r"[^a-z0-9]", "", text.lower()) if text else ""

    def generate_job_id(self, title: str, company: str) -> str:
        raw = f"{self.normalize_text(title)}|{self.normalize_text(company)}"
        return hashlib.md5(raw.encode()).hexdigest()

    @staticmethod
    def _has_rich_job_details(job: Dict) -> bool:
        description = str(job.get("description", "") or "")
        requirements = str(job.get("requirements", "") or "")
        responsibilities = str(job.get("responsibilities", "") or "")
        skills = normalize_skills(job.get("skills", []) if isinstance(job.get("skills"), list) else [])

        has_text = any(not _is_placeholder_text(text) for text in [description, requirements, responsibilities])
        has_skills = len(skills) > 0
        return has_text or has_skills

    @staticmethod
    def _metadata_has_rich_details(metadata: Dict) -> bool:
        description = str(metadata.get("description_snippet", "") or "")
        requirements = str(metadata.get("requirements_snippet", "") or "")
        responsibilities = str(metadata.get("responsibilities_snippet", "") or "")
        skills_text = str(metadata.get("skills_list", "") or "")

        has_text = any(not _is_placeholder_text(text) for text in [description, requirements, responsibilities])
        skills = normalize_skills([value.strip() for value in skills_text.split(",") if value.strip()])
        return has_text or len(skills) > 0

    @staticmethod
    def _is_egypt_job(job: Dict) -> bool:
        location = str(job.get("location", "") or "")
        if _is_egypt_location_label(location):
            return True

        hints = " ".join(
            [
                str(job.get("location", "") or ""),
                str(job.get("title", "") or ""),
                str(job.get("job_page_link", "") or ""),
                str(job.get("apply_link", "") or ""),
            ]
        ).lower()

        if any(token in hints for token in NON_EGYPT_LOCATION_TOKENS):
            return False

        return any(token in hints for token in EGYPT_LOCATION_TOKENS)

    def _get_llm_client(self):
        if self._llm_client is None:
            self._llm_client = get_llm_client()
        return self._llm_client

    def _enrich_job_with_llm(self, job: Dict) -> Tuple[Dict, bool]:
        source = str(job.get("source", "") or "")
        job_page_link = normalize_job_url(str(job.get("job_page_link", "") or ""), source)
        apply_link = normalize_job_url(str(job.get("apply_link", "") or ""), source) or job_page_link

        payload = {
            "source": source,
            "title": str(job.get("title", "") or ""),
            "company": str(job.get("company", "") or ""),
            "location": str(job.get("location", "") or ""),
            "description": str(job.get("description", "") or ""),
            "requirements": str(job.get("requirements", "") or ""),
            "responsibilities": str(job.get("responsibilities", "") or ""),
            "skills": normalize_skills(job.get("skills", []) if isinstance(job.get("skills"), list) else []),
            "experience_level": str(job.get("experience_level", "") or ""),
            "employment_type": str(job.get("employment_type", "") or ""),
            "apply_link": apply_link,
            "job_page_link": job_page_link,
        }

        prompt = (
            "You are a recruitment data normalizer. Return ONLY valid JSON with these keys exactly: "
            "title, company, location, city, country, description, requirements, responsibilities, skills, "
            "experience_level, employment_type, apply_link. "
            "Rules: keep original meaning, do not invent facts, keep links absolute, set country to Egypt if location is Egyptian, "
            "skills must be an array of strings. If uncertain, keep existing values.\n\n"
            f"Input JSON:\n{json.dumps(payload, ensure_ascii=False)}"
        )

        try:
            response = self._get_llm_client().chat.completions.create(
                model=self.settings.scraper_enrichment_model,
                messages=[
                    {"role": "system", "content": "Return only valid JSON."},
                    {"role": "user", "content": prompt},
                ],
                max_tokens=self.settings.scraper_enrichment_max_tokens,
                temperature=self.settings.scraper_enrichment_temperature,
                timeout=self.settings.scraper_enrichment_timeout_seconds,
            )

            raw = response.choices[0].message.content or "{}"
            parsed = json.loads(_clean_llm_json(raw))
            if not isinstance(parsed, dict):
                return job, False

            normalized_location = _normalize_location_label(str(parsed.get("location") or payload["location"]))
            city = _normalize_location_label(str(parsed.get("city") or ""))
            country = _normalize_location_label(str(parsed.get("country") or ""))
            if not country and _is_egypt_location_label(normalized_location):
                country = "Egypt"

            normalized_apply_link = (
                normalize_job_url(str(parsed.get("apply_link") or ""), source) or apply_link or job_page_link
            )

            enriched = dict(job)
            enriched["title"] = str(parsed.get("title") or payload["title"]).strip() or payload["title"]
            enriched["company"] = str(parsed.get("company") or payload["company"]).strip() or payload["company"]
            enriched["location"] = normalized_location or payload["location"]
            enriched["city"] = city or str(job.get("city", "") or "")
            enriched["country"] = country or str(job.get("country", "") or "")
            enriched["description"] = str(parsed.get("description") or payload["description"]).strip() or payload["description"]
            enriched["requirements"] = str(parsed.get("requirements") or payload["requirements"]).strip() or payload["requirements"]
            enriched["responsibilities"] = (
                str(parsed.get("responsibilities") or payload["responsibilities"]).strip() or payload["responsibilities"]
            )
            enriched["skills"] = _skills_from_any(parsed.get("skills")) or payload["skills"]
            enriched["experience_level"] = (
                str(parsed.get("experience_level") or payload["experience_level"]).strip() or payload["experience_level"]
            )
            enriched["employment_type"] = (
                str(parsed.get("employment_type") or payload["employment_type"]).strip() or payload["employment_type"]
            )
            enriched["apply_link"] = normalized_apply_link
            enriched["_raw_title"] = payload["title"]
            enriched["_raw_company"] = payload["company"]
            enriched["_enrichment_source"] = "llm"
            return enriched, True
        except Exception as exc:
            logger.warning("LLM enrichment failed for %s: %s", job_page_link or payload["title"], exc)
            fallback = dict(job)
            fallback["_raw_title"] = payload["title"]
            fallback["_raw_company"] = payload["company"]
            fallback["_enrichment_source"] = "raw"
            return fallback, False

    async def enrich_job_async(self, job: Dict) -> Tuple[Dict, bool]:
        return await asyncio.to_thread(self._enrich_job_with_llm, job)

    def save_jobs(self, jobs: List[Dict], stats: Optional[Dict[str, int]] = None) -> int:
        if not jobs:
            return 0

        batch_dedup: Dict[str, Dict] = {}

        for job in jobs:
            source = str(job.get("source", "") or "")
            job_page_link = normalize_job_url(str(job.get("job_page_link", "") or ""), source)
            apply_link = normalize_job_url(str(job.get("apply_link", "") or ""), source)

            if not job_page_link:
                continue

            job["job_page_link"] = job_page_link
            job["apply_link"] = apply_link or job_page_link
            job["skills"] = normalize_skills(job.get("skills", []) if isinstance(job.get("skills"), list) else [])
            job["posted_time"] = parse_posted_time(str(job.get("posted_time", "") or ""))
            job["location"] = _normalize_location_label(str(job.get("location", "") or ""))

            if self.settings.strict_egypt_only and not self._is_egypt_job(job):
                if stats is not None:
                    stats["non_egypt_rejected"] = stats.get("non_egypt_rejected", 0) + 1
                logger.info(
                    "Skipping non-Egypt job '%s' at '%s' (%s)",
                    job.get("title", ""),
                    job.get("company", ""),
                    job.get("location", ""),
                )
                continue

            dedupe_title = str(job.get("_raw_title", "") or job.get("title", "") or "")
            dedupe_company = str(job.get("_raw_company", "") or job.get("company", "") or "")
            job_id = self.generate_job_id(dedupe_title, dedupe_company)
            incoming_posted_time = str(job.get("posted_time", "") or "")
            incoming_has_details = self._has_rich_job_details(job)

            if job_id in self.existing_ids:
                rec = self.collection.get(ids=[job_id])
                if rec and rec.get("metadatas"):
                    old_meta = rec["metadatas"][0]
                    existing_posted_time = str(old_meta.get("posted_time", "") or "")
                    existing_has_details = self._metadata_has_rich_details(old_meta)

                    if existing_has_details and not incoming_has_details:
                        continue

                    is_not_newer = (
                        incoming_posted_time <= existing_posted_time
                        if incoming_posted_time and existing_posted_time
                        else True
                    )

                    if is_not_newer and (existing_has_details or not incoming_has_details):
                        continue
            else:
                self.existing_ids.add(job_id)

            skills_str = ", ".join(job.get("skills", []))
            text_for_embed = (
                f"{job['title']} at {job['company']}. "
                f"Location: {job['location']}. "
                f"Skills: {skills_str}. "
                f"{job.get('description', '')}"
            )

            meta = {
                "source": job["source"],
                "title": job["title"],
                "company": job["company"],
                "location": job["location"],
                "city": str(job.get("city", "") or ""),
                "country": str(job.get("country", "") or ""),
                "job_page_link": job["job_page_link"],
                "apply_link": job.get("apply_link", job["job_page_link"]),
                "posted_time": job.get("posted_time", str(datetime.now().date())),
                "experience_level": job.get("experience_level", "Not Specified"),
                "employment_type": job.get("employment_type", "Not Specified"),
                "enrichment_source": str(job.get("_enrichment_source", "raw") or "raw"),
                "skills_list": skills_str,
                "description_snippet": job.get("description", "")[:200],
                "requirements_snippet": job.get("requirements", "")[:200],
                "responsibilities_snippet": job.get("responsibilities", "")[:200],
                "json_detailed": json.dumps(job),
            }

            batch_dedup[job_id] = {
                "doc": text_for_embed,
                "meta": meta,
                "embedding": self.model.encode(text_for_embed).tolist(),
            }

        if batch_dedup:
            self.collection.upsert(
                ids=list(batch_dedup.keys()),
                embeddings=[value["embedding"] for value in batch_dedup.values()],
                metadatas=[value["meta"] for value in batch_dedup.values()],
                documents=[value["doc"] for value in batch_dedup.values()],
            )

        return len(batch_dedup)


async def _new_page(context):
    page = await context.new_page()
    await page.route("**/*", block_resources)

    if STEALTH_AVAILABLE:
        try:
            await stealth_async(page)
        except Exception as exc:
            logger.debug("Failed to apply playwright stealth: %s", exc)

    return page


async def get_job_details(context, link: str, source: str) -> Dict:
    normalized_link = normalize_job_url(link, source)
    if not normalized_link:
        return {}

    await asyncio.sleep(random.uniform(0.5, 1.5))
    page = await _new_page(context)

    data = {
        "description": "",
        "requirements": "",
        "responsibilities": "",
        "skills": [],
        "experience_level": "Not Specified",
        "employment_type": "Not Specified",
        "apply_link": normalized_link,
        "_apply_link_strategy": "fallback",
    }

    try:
        if not await goto_with_retry(page, normalized_link, timeout_ms=15000, attempts=3):
            return data

        if source == "Wuzzuf":
            description_selectors = [
                ".css-1uobp1k",
                "[data-testid='job-description']",
                ".job-description",
            ]
            try:
                for selector in description_selectors:
                    element = await page.query_selector(selector)
                    if element:
                        text = (await element.inner_text()).strip()
                        if text:
                            data["description"] = text
                            break
            except Exception:
                logger.debug("Wuzzuf description selector lookup failed for %s", normalized_link)

            if not data["description"]:
                data["description"] = "Description not found."

            try:
                tags = await page.query_selector_all(".css-158idm4, a.css-o171kl")
                data["skills"] = normalize_skills([(await tag.inner_text()).strip() for tag in tags])
            except Exception:
                logger.debug("Wuzzuf skills selector lookup failed for %s", normalized_link)

            try:
                career = await page.query_selector("span:has-text('Career Level:') + span")
                if career:
                    data["experience_level"] = await career.inner_text()

                employment = await page.query_selector("span:has-text('Job Type:') + span")
                if employment:
                    data["employment_type"] = await employment.inner_text()
            except Exception:
                logger.debug("Wuzzuf experience/employment extraction failed for %s", normalized_link)

            try:
                apply_selectors = [
                    "a[href*='wuzzuf.net/jobs/careers']",
                    "a.css-1f5tw6x",
                    "a[data-testid='apply-button']",
                ]
                for selector in apply_selectors:
                    apply_button = await page.query_selector(selector)
                    if not apply_button:
                        continue

                    href = await apply_button.get_attribute("href")
                    normalized_apply_link = normalize_job_url(href or "", "Wuzzuf")
                    if normalized_apply_link:
                        data["apply_link"] = normalized_apply_link
                        data["_apply_link_strategy"] = "external"
                        break
            except Exception:
                logger.debug("Wuzzuf apply-link extraction failed for %s", normalized_link)

        elif source == "LinkedIn":
            description_selectors = [
                ".show-more-less-html__markup",
                ".description__text",
                "div.show-more-less-html",
            ]
            try:
                try:
                    await page.click("button.show-more-less-html__button", timeout=1000)
                except Exception:
                    logger.debug("LinkedIn expand-description button not available for %s", normalized_link)

                for selector in description_selectors:
                    element = await page.query_selector(selector)
                    if element:
                        text = (await element.inner_text()).strip()
                        if text:
                            data["description"] = text
                            break
            except Exception:
                logger.debug("LinkedIn description extraction failed for %s", normalized_link)

            if not data["description"]:
                data["description"] = "Description not found."

            try:
                apply_selectors = [
                    "a.top-card-layout__cta--primary",
                    "a.apply-button",
                    "a[data-tracking-control-name*='apply']",
                ]
                for selector in apply_selectors:
                    button = await page.query_selector(selector)
                    if not button:
                        continue

                    href = await button.get_attribute("href")
                    normalized_apply_link = normalize_job_url(href or "", "LinkedIn")
                    if normalized_apply_link and normalized_apply_link != normalized_link:
                        data["apply_link"] = normalized_apply_link
                        data["_apply_link_strategy"] = "external"
                        break
            except Exception:
                logger.debug("LinkedIn apply-link extraction failed for %s", normalized_link)

            try:
                criteria = await page.query_selector_all(".description__job-criteria-item")
                for item in criteria:
                    header = await item.query_selector("h3")
                    value = await item.query_selector("span")
                    if header and value:
                        header_text = (await header.inner_text()).lower()
                        value_text = (await value.inner_text()).strip()
                        if "seniority" in header_text:
                            data["experience_level"] = value_text
                        if "employment" in header_text:
                            data["employment_type"] = value_text
            except Exception:
                logger.debug("LinkedIn criteria extraction failed for %s", normalized_link)

        if data["description"]:
            requirements, responsibilities = parse_description(data["description"])
            data["requirements"] = requirements or "See Description"
            data["responsibilities"] = responsibilities or "See Description"

        if data.get("_apply_link_strategy") == "fallback":
            logger.debug("Apply-link fallback used for %s (%s)", source, normalized_link)

    except Exception as exc:
        logger.warning("Failed to fetch job details from %s: %s", normalized_link, exc)
    finally:
        await page.close()

    return data


async def run_scraper(
    categories: Optional[List[str]] = None,
    max_categories: Optional[int] = None,
) -> Dict:
    settings = get_recruitment_settings()
    target_categories = categories or settings.target_categories
    if max_categories is not None:
        target_categories = target_categories[:max_categories]

    if not await _supports_async_subprocess():
        return {
            "processed_categories": 0,
            "upserted_jobs": 0,
            "total_jobs": store.scraped_jobs_col.count(),
            "warning": "Playwright is unavailable on the active Windows asyncio event loop (subprocess unsupported).",
        }

    scraper = JobLensScraper()
    total_upserted = 0
    stats: Dict[str, int] = {
        "wuzzuf_cards": 0,
        "linkedin_cards": 0,
        "detail_attempted": 0,
        "detail_rich": 0,
        "detail_poor": 0,
        "llm_enriched": 0,
        "llm_enrichment_failed": 0,
        "apply_link_external": 0,
        "apply_link_fallback": 0,
        "non_egypt_rejected": 0,
        "category_errors": 0,
    }

    async def _enrich_batch(batch: List[Dict]) -> List[Dict]:
        enriched_batch: List[Dict] = []
        for item in batch:
            enriched = item
            ok = False
            if scraper.settings.scraper_enrichment_enabled:
                enriched, ok = await scraper.enrich_job_async(item)
                if ok:
                    stats["llm_enriched"] += 1
                else:
                    stats["llm_enrichment_failed"] += 1

            strategy = str(enriched.get("_apply_link_strategy", "fallback") or "fallback")
            if strategy == "external":
                stats["apply_link_external"] += 1
            else:
                stats["apply_link_fallback"] += 1

            enriched_batch.append(enriched)

        return enriched_batch

    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch(headless=True)
        context = await browser.new_context(
            user_agent=(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                "AppleWebKit/537.36 (KHTML, like Gecko) "
                "Chrome/121.0.0.0 Safari/537.36"
            )
        )

        for category in target_categories:
            encoded = urllib.parse.quote(category)

            try:
                page = await _new_page(context)
                url = (
                    f"https://wuzzuf.net/search/jobs/?q={encoded}"
                    f"&a=hpb&filters%5Bcountry%5D%5B0%5D=Egypt"
                )
                if not await goto_with_retry(page, url, timeout_ms=60000, attempts=3):
                    await page.close()
                    continue

                try:
                    await page.wait_for_selector(".css-1gatmva, div.css-pkv5jc, .css-bjn8wh", timeout=15000)
                except Exception:
                    pass

                cards = await page.query_selector_all(".css-1gatmva")
                if not cards:
                    cards = await page.query_selector_all("div.css-pkv5jc")
                if not cards:
                    cards = await page.query_selector_all("div.css-bjn8wh")

                stats["wuzzuf_cards"] += len(cards)

                basics = []
                for card in cards:
                    try:
                        title_el = await card.query_selector("h2") or await card.query_selector("h1")
                        title = (await title_el.inner_text()).strip() if title_el else "N/A"

                        company_el = await card.query_selector("a.css-p7pghv") or await card.query_selector("a.css-17s97q8")
                        company = (
                            (await company_el.inner_text()).strip().replace(" -", "") if company_el else "Confidential"
                        )

                        location_el = await card.query_selector("strong.css-1vlp604")
                        if location_el:
                            full_text = (await location_el.inner_text()).strip()
                            location = full_text.replace(company, "").replace("-", "").strip()
                        else:
                            old_location_el = await card.query_selector(".css-5wys0k")
                            location = (await old_location_el.inner_text()).strip() if old_location_el else "Egypt"

                        date_el = await card.query_selector(".css-do6t5g, .css-4c4ojb, .css-154erwh")
                        date_text = await date_el.inner_text() if date_el else ""

                        link_el = await card.query_selector("h2 a") or await card.query_selector("h1 a")
                        post_link = ""
                        if link_el:
                            post_link = await link_el.get_attribute("href")
                        elif company_el:
                            post_link = await company_el.get_attribute("href")

                        post_link = normalize_job_url(post_link or "", "Wuzzuf")
                        if not post_link:
                            continue

                        basics.append(
                            {
                                "source": "Wuzzuf",
                                "title": title,
                                "company": company,
                                "location": location,
                                "posted_time": parse_posted_time(date_text),
                                "job_page_link": post_link,
                            }
                        )
                    except Exception:
                        continue

                await page.close()

                for index in range(0, len(basics), settings.concurrency_limit):
                    batch = basics[index : index + settings.concurrency_limit]
                    stats["detail_attempted"] += len(batch)
                    details = await asyncio.gather(
                        *[get_job_details(context, item["job_page_link"], "Wuzzuf") for item in batch],
                        return_exceptions=True,
                    )
                    for detail_index, detail in enumerate(details):
                        if isinstance(detail, Exception):
                            stats["detail_poor"] += 1
                            logger.warning("Wuzzuf detail extraction exception for %s: %s", batch[detail_index].get("job_page_link", ""), detail)
                            continue

                        if scraper._has_rich_job_details(detail):
                            stats["detail_rich"] += 1
                        else:
                            stats["detail_poor"] += 1

                        batch[detail_index].update(detail)
                    enriched_batch = await _enrich_batch(batch)
                    total_upserted += scraper.save_jobs(enriched_batch, stats=stats)
            except Exception as exc:
                stats["category_errors"] += 1
                logger.warning("Wuzzuf scrape failed for category '%s': %s", category, exc)

            try:
                page = await _new_page(context)
                url = (
                    f"https://www.linkedin.com/jobs/search?keywords={encoded}"
                    f"&location=Egypt&position=1&pageNum=0"
                )
                if not await goto_with_retry(page, url, timeout_ms=60000, attempts=3):
                    await page.close()
                    continue

                previous_count = 0
                no_change = 0
                for _ in range(8):
                    await page.mouse.wheel(0, 1500)
                    await asyncio.sleep(1.5)
                    cards = await page.query_selector_all(".base-card")
                    if len(cards) == previous_count:
                        no_change += 1
                        if no_change >= 2:
                            break
                    else:
                        no_change = 0
                    previous_count = len(cards)

                cards = await page.query_selector_all(".base-card")
                stats["linkedin_cards"] += len(cards)

                basics = []
                for card in cards:
                    try:
                        title_el = await card.query_selector(".base-search-card__title")
                        company_el = await card.query_selector(".base-search-card__subtitle")
                        location_el = await card.query_selector(".job-search-card__location")
                        date_el = await card.query_selector("time")
                        link_el = await card.query_selector("a.base-card__full-link")

                        if title_el and link_el:
                            date_attr = await date_el.get_attribute("datetime") if date_el else ""
                            if not date_attr and date_el:
                                date_attr = (await date_el.inner_text()).strip()

                            job_link = normalize_job_url(await link_el.get_attribute("href") or "", "LinkedIn")
                            if not job_link:
                                continue

                            basics.append(
                                {
                                    "source": "LinkedIn",
                                    "title": (await title_el.inner_text()).strip(),
                                    "company": (await company_el.inner_text()).strip() if company_el else "N/A",
                                    "location": (await location_el.inner_text()).strip() if location_el else "Egypt",
                                    "posted_time": parse_posted_time(date_attr or ""),
                                    "job_page_link": job_link,
                                }
                            )
                    except Exception:
                        continue

                await page.close()

                for index in range(0, len(basics), settings.concurrency_limit):
                    batch = basics[index : index + settings.concurrency_limit]
                    stats["detail_attempted"] += len(batch)
                    details = await asyncio.gather(
                        *[get_job_details(context, item["job_page_link"], "LinkedIn") for item in batch],
                        return_exceptions=True,
                    )
                    for detail_index, detail in enumerate(details):
                        if isinstance(detail, Exception):
                            stats["detail_poor"] += 1
                            logger.warning("LinkedIn detail extraction exception for %s: %s", batch[detail_index].get("job_page_link", ""), detail)
                            continue

                        if scraper._has_rich_job_details(detail):
                            stats["detail_rich"] += 1
                        else:
                            stats["detail_poor"] += 1

                        batch[detail_index].update(detail)
                    enriched_batch = await _enrich_batch(batch)
                    total_upserted += scraper.save_jobs(enriched_batch, stats=stats)
            except Exception as exc:
                stats["category_errors"] += 1
                logger.warning("LinkedIn scrape failed for category '%s': %s", category, exc)

            await asyncio.sleep(random.uniform(25, 45))

        await browser.close()

    return {
        "processed_categories": len(target_categories),
        "upserted_jobs": total_upserted,
        "total_jobs": store.scraped_jobs_col.count(),
        "stats": stats,
    }
