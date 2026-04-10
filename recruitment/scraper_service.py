from __future__ import annotations

import asyncio
import hashlib
import json
import random
import re
import urllib.parse
from datetime import datetime, timedelta
from functools import lru_cache
from typing import Dict, List, Optional

from playwright.async_api import async_playwright

try:
    from playwright_stealth import stealth_async

    STEALTH_AVAILABLE = True
except Exception:
    STEALTH_AVAILABLE = False

from sentence_transformers import SentenceTransformer

from .config import get_recruitment_settings
from .vector_store import store


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


def parse_description(full_text: str) -> tuple[str, str]:
    lower = full_text.lower()
    req_start = lower.find("requirements") if "requirements" in lower else lower.find("qualifications")
    resp_start = lower.find("responsibilities") if "responsibilities" in lower else lower.find("duties")

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


class JobLensScraper:
    """Scrapes Wuzzuf and LinkedIn, then stores results in the jobs collection."""

    def __init__(self) -> None:
        self.collection = store.jobs_col
        self.model = get_scraper_embedding_model()

        existing = self.collection.get()
        self.existing_ids = set(existing["ids"]) if existing and existing.get("ids") else set()

    @staticmethod
    def normalize_text(text: str) -> str:
        return re.sub(r"[^a-z0-9]", "", text.lower()) if text else ""

    def generate_job_id(self, title: str, company: str) -> str:
        raw = f"{self.normalize_text(title)}|{self.normalize_text(company)}"
        return hashlib.md5(raw.encode()).hexdigest()

    def save_jobs(self, jobs: List[Dict]) -> int:
        if not jobs:
            return 0

        batch_dedup: Dict[str, Dict] = {}

        for job in jobs:
            job_id = self.generate_job_id(job["title"], job["company"])

            if job_id in self.existing_ids:
                rec = self.collection.get(ids=[job_id])
                if rec and rec.get("metadatas"):
                    old_meta = rec["metadatas"][0]
                    if job.get("posted_time", "") <= old_meta.get("posted_time", ""):
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
                "job_page_link": job["job_page_link"],
                "apply_link": job.get("apply_link", job["job_page_link"]),
                "posted_time": job.get("posted_time", str(datetime.now().date())),
                "experience_level": job.get("experience_level", "Not Specified"),
                "employment_type": job.get("employment_type", "Not Specified"),
                "skills_list": skills_str,
                "description_snippet": job.get("description", "")[:200],
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
        except Exception:
            pass

    return page


async def get_job_details(context, link: str, source: str) -> Dict:
    if not link:
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
        "apply_link": link,
    }

    try:
        await page.goto(link, timeout=15000)

        if source == "Wuzzuf":
            try:
                element = await page.wait_for_selector(".css-1uobp1k", timeout=3000)
                data["description"] = await element.inner_text()
            except Exception:
                data["description"] = "Description not found."

            try:
                tags = await page.query_selector_all(".css-158idm4, a.css-o171kl")
                data["skills"] = [(await tag.inner_text()).strip() for tag in tags]
            except Exception:
                pass

            try:
                career = await page.query_selector("span:has-text('Career Level:') + span")
                if career:
                    data["experience_level"] = await career.inner_text()

                employment = await page.query_selector("span:has-text('Job Type:') + span")
                if employment:
                    data["employment_type"] = await employment.inner_text()
            except Exception:
                pass

        elif source == "LinkedIn":
            try:
                try:
                    await page.click("button.show-more-less-html__button", timeout=1000)
                except Exception:
                    pass

                element = await page.wait_for_selector(".show-more-less-html__markup, .description__text", timeout=3000)
                data["description"] = await element.inner_text()
            except Exception:
                data["description"] = "Description not found."

            try:
                button = await page.query_selector("a.top-card-layout__cta--primary, a.apply-button")
                if button:
                    href = await button.get_attribute("href")
                    if href and "linkedin.com" not in href:
                        data["apply_link"] = href
            except Exception:
                pass

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
                pass

        if data["description"]:
            requirements, responsibilities = parse_description(data["description"])
            data["requirements"] = requirements or "See Description"
            data["responsibilities"] = responsibilities or "See Description"

    except Exception:
        pass
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

    scraper = JobLensScraper()
    total_upserted = 0

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
                await page.goto(url, timeout=60000)

                try:
                    await page.wait_for_selector(".css-1gatmva, div.css-pkv5jc, .css-bjn8wh", timeout=15000)
                except Exception:
                    pass

                cards = await page.query_selector_all(".css-1gatmva")
                if not cards:
                    cards = await page.query_selector_all("div.css-pkv5jc")
                if not cards:
                    cards = await page.query_selector_all("div.css-bjn8wh")

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

                        basics.append(
                            {
                                "source": "Wuzzuf",
                                "title": title,
                                "company": company,
                                "location": location,
                                "posted_time": parse_relative_date(date_text),
                                "job_page_link": post_link,
                            }
                        )
                    except Exception:
                        continue

                await page.close()

                for index in range(0, len(basics), settings.concurrency_limit):
                    batch = basics[index : index + settings.concurrency_limit]
                    details = await asyncio.gather(
                        *[get_job_details(context, item["job_page_link"], "Wuzzuf") for item in batch]
                    )
                    for detail_index, detail in enumerate(details):
                        batch[detail_index].update(detail)
                    total_upserted += scraper.save_jobs(batch)
            except Exception:
                pass

            try:
                page = await _new_page(context)
                url = (
                    f"https://www.linkedin.com/jobs/search?keywords={encoded}"
                    f"&location=Egypt&position=1&pageNum=0"
                )
                await page.goto(url, timeout=60000)

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

                            basics.append(
                                {
                                    "source": "LinkedIn",
                                    "title": (await title_el.inner_text()).strip(),
                                    "company": (await company_el.inner_text()).strip() if company_el else "N/A",
                                    "location": (await location_el.inner_text()).strip() if location_el else "Egypt",
                                    "posted_time": date_attr or str(datetime.now().date()),
                                    "job_page_link": await link_el.get_attribute("href"),
                                }
                            )
                    except Exception:
                        continue

                await page.close()

                for index in range(0, len(basics), settings.concurrency_limit):
                    batch = basics[index : index + settings.concurrency_limit]
                    details = await asyncio.gather(
                        *[get_job_details(context, item["job_page_link"], "LinkedIn") for item in batch]
                    )
                    for detail_index, detail in enumerate(details):
                        batch[detail_index].update(detail)
                    total_upserted += scraper.save_jobs(batch)
            except Exception:
                pass

            await asyncio.sleep(random.uniform(25, 45))

        await browser.close()

    return {
        "processed_categories": len(target_categories),
        "upserted_jobs": total_upserted,
        "total_jobs": store.jobs_col.count(),
    }
