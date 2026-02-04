"""
Enhanced JobLens Scraper with Category-Based Scraping
Focus: Egypt market with specialized categories
"""

import asyncio
import json
import hashlib
import re
import random
from datetime import datetime, timedelta
from typing import List, Dict, Optional
import chromadb
from playwright.async_api import async_playwright
from playwright_stealth import stealth_async
from sentence_transformers import SentenceTransformer

# ==================== CONFIGURATION ====================
CHROMA_PATH = "./joblens_db"
EMBEDDING_MODEL = 'all-MiniLM-L6-v2'

# Job Categories for Egypt Market (2026)
JOB_CATEGORIES = {
    "IT & Software Development": {
        "keywords": [
            "web developer", "frontend developer", "backend developer", "full stack developer",
            "mobile developer", "ios developer", "android developer", "react native",
            "devops engineer", "cloud engineer", "azure", "aws", "gcp",
            "qa engineer", "quality assurance", "software tester",
            "ai engineer", "machine learning", "data scientist", "nlp engineer"
        ],
        "egypt_variations": [
            "برمجة", "مطور ويب", "مهندس برمجيات"
        ]
    },
    "Engineering - Mech/Elec": {
        "keywords": [
            "mechanical engineer", "electrical engineer", "mechatronics engineer",
            "electric vehicle engineer", "e-mobility", "ev engineer",
            "maintenance engineer", "production engineer", "manufacturing engineer",
            "power systems engineer", "automation engineer"
        ],
        "egypt_variations": [
            "مهندس ميكانيكا", "مهندس كهرباء", "مهندس انتاج"
        ]
    },
    "Engineering - Civ/Arch": {
        "keywords": [
            "civil engineer", "site engineer", "technical office engineer",
            "architect", "interior designer", "bim coordinator", "bim modeler",
            "planning engineer", "structural engineer", "construction engineer"
        ],
        "egypt_variations": [
            "مهندس مدني", "مهندس معماري", "مكتب فني"
        ]
    },
    "Sales & Retail": {
        "keywords": [
            "sales engineer", "sales representative", "b2b sales", "account manager",
            "business development", "real estate agent", "property consultant",
            "telesales", "inside sales", "sales executive"
        ],
        "egypt_variations": [
            "مبيعات", "تطوير أعمال", "عقارات"
        ]
    },
    "Marketing / PR / Ads": {
        "keywords": [
            "marketing specialist", "digital marketing", "growth marketing",
            "seo specialist", "sem specialist", "content creator", "copywriter",
            "social media manager", "community manager", "e-commerce manager",
            "performance marketing", "brand manager"
        ],
        "egypt_variations": [
            "تسويق", "تسويق رقمي", "سوشيال ميديا"
        ]
    },
    "Accounting & Finance": {
        "keywords": [
            "accountant", "financial analyst", "treasury specialist",
            "auditor", "tax specialist", "accounts payable", "accounts receivable",
            "fintech operations", "financial controller", "cost accountant"
        ],
        "egypt_variations": [
            "محاسب", "محلل مالي", "مراجع حسابات"
        ]
    },
    "Logistics / Supply Chain": {
        "keywords": [
            "logistics coordinator", "supply chain specialist", "procurement officer",
            "warehouse manager", "inventory controller", "distribution manager",
            "shipping coordinator", "customs clearance", "import export specialist"
        ],
        "egypt_variations": [
            "لوجستيات", "مشتريات", "سلسلة التوريد"
        ]
    },
    "Customer Service": {
        "keywords": [
            "customer service representative", "call center agent", "technical support",
            "customer success specialist", "help desk", "support engineer",
            "english customer service", "german customer service", "french customer service"
        ],
        "egypt_variations": [
            "خدمة عملاء", "كول سنتر", "دعم فني"
        ]
    },
    "Human Resources (HR)": {
        "keywords": [
            "hr specialist", "recruiter", "talent acquisition", "hr generalist",
            "personnel specialist", "learning and development", "l&d specialist",
            "hr operations", "compensation and benefits", "payroll specialist"
        ],
        "egypt_variations": [
            "موارد بشرية", "توظيف", "تدريب وتطوير"
        ]
    },
    "Data & AI": {
        "keywords": [
            "data scientist", "data engineer", "data analyst", "business intelligence",
            "machine learning engineer", "ai researcher", "deep learning engineer",
            "mlops engineer", "data architect"
        ],
        "egypt_variations": [
            "عالم بيانات", "ذكاء اصطناعي"
        ]
    },
    "Renewable Energy": {
        "keywords": [
            "solar engineer", "wind energy engineer", "sustainability consultant",
            "renewable energy specialist", "energy analyst", "green building consultant"
        ],
        "egypt_variations": [
            "طاقة متجددة", "طاقة شمسية"
        ]
    },
    "Cybersecurity": {
        "keywords": [
            "security analyst", "cybersecurity specialist", "penetration tester",
            "security engineer", "privacy manager", "soc analyst", "incident response"
        ],
        "egypt_variations": [
            "أمن سيبراني", "أمن معلومات"
        ]
    }
}

# Egyptian Job Boards and Their Configurations
EGYPT_JOB_SOURCES = {
    "wuzzuf": {
        "base_url": "https://wuzzuf.net/search/jobs/",
        "location_code": "egypt",
        "enabled": True
    },
    "forasna": {
        "base_url": "https://www.forasna.com/jobs/",
        "location_code": "egypt",
        "enabled": True
    },
    "linkedin": {
        "base_url": "https://www.linkedin.com/jobs/search/",
        "location_code": "Egypt",
        "enabled": True
    }
}


class CategoryBasedJobScraper:
    """Enhanced scraper with category-based search for Egypt market"""
    
    def __init__(self):
        self.chroma_client = chromadb.PersistentClient(path=CHROMA_PATH)
        self.collection = self.chroma_client.get_or_create_collection(name="job_listings")
        print("📚 Loading AI Model...")
        self.model = SentenceTransformer(EMBEDDING_MODEL)
        
        # Load existing jobs
        existing_data = self.collection.get()
        self.existing_ids = set(existing_data['ids'])
        print(f"✅ Loaded {len(self.existing_ids)} existing jobs from database.")
    
    def normalize_text(self, text: str) -> str:
        """Normalize text for comparison"""
        if not text:
            return ""
        return re.sub(r'[^a-z0-9]', '', text.lower())
    
    def generate_job_id(self, title: str, company: str, source: str = "") -> str:
        """Generate unique job ID"""
        raw_sig = f"{self.normalize_text(title)}|{self.normalize_text(company)}|{source}"
        return hashlib.md5(raw_sig.encode()).hexdigest()
    
    def is_duplicate(self, job_id: str) -> bool:
        """Check if job already exists"""
        return job_id in self.existing_ids
    
    def extract_salary(self, text: str) -> Optional[str]:
        """Extract salary information from text"""
        if not text:
            return None
        
        # Common Egyptian salary patterns
        patterns = [
            r'EGP\s*[\d,]+\s*-\s*EGP\s*[\d,]+',
            r'[\d,]+\s*-\s*[\d,]+\s*EGP',
            r'£\s*[\d,]+\s*-\s*£\s*[\d,]+',
            r'\$\s*[\d,]+\s*-\s*\$\s*[\d,]+',
            r'[\d,]+\s*جنيه'
        ]
        
        for pattern in patterns:
            match = re.search(pattern, text, re.IGNORECASE)
            if match:
                return match.group(0)
        
        return None
    
    def categorize_job(self, title: str, description: str) -> Optional[str]:
        """Determine job category based on title and description"""
        text_to_check = f"{title} {description}".lower()
        
        category_scores = {}
        for category, data in JOB_CATEGORIES.items():
            score = 0
            for keyword in data["keywords"]:
                if keyword.lower() in text_to_check:
                    score += 1
            category_scores[category] = score
        
        # Return category with highest score (if any matches)
        max_score = max(category_scores.values())
        if max_score > 0:
            return max(category_scores, key=category_scores.get)
        
        return None
    
    def save_jobs(self, jobs: List[Dict], category: str = None):
        """Save jobs to ChromaDB with category information"""
        if not jobs:
            return
        
        new_ids, new_docs, new_metas, new_embeddings = [], [], [], []
        
        print(f"   📊 Processing {len(jobs)} jobs for category: {category or 'General'}...")
        
        for job in jobs:
            job_id = self.generate_job_id(
                job['title'], 
                job['company'], 
                job.get('source', '')
            )
            
            if self.is_duplicate(job_id):
                continue
            
            # Auto-categorize if not provided
            if not category:
                category = self.categorize_job(
                    job['title'], 
                    job.get('description', '')
                )
            
            # Rich embedding text
            skills_str = ", ".join(job.get('skills', []))
            text_for_embedding = (
                f"{job['title']} at {job['company']}. "
                f"Category: {category or 'General'}. "
                f"Location: {job.get('location', 'Egypt')}. "
                f"Skills: {skills_str}. "
                f"{job.get('description', '')}"
            )
            
            # Complete metadata
            metadata = {
                # Core fields
                "source": job.get('source', 'Unknown'),
                "title": job['title'],
                "company": job['company'],
                "location": job.get('location', 'Egypt'),
                
                # Category information
                "category": category or "Uncategorized",
                "subcategory": job.get('subcategory', ''),
                
                # Job details
                "experience_level": job.get('experience_level', 'Not Specified'),
                "employment_type": job.get('employment_type', 'Not Specified'),
                "salary_range": job.get('salary_range', 'Not Specified'),
                
                # Skills and requirements
                "skills_list": skills_str,
                "requirements": job.get('requirements', '')[:500],  # Truncate for metadata
                "responsibilities": job.get('responsibilities', '')[:500],
                
                # Links
                "job_page_link": job.get('job_page_link', ''),
                "apply_link": job.get('apply_link', ''),
                
                # Timestamps
                "posted_time": job.get('posted_time', ''),
                "scraped_at": datetime.now().isoformat(),
                
                # Preview
                "description_snippet": job.get('description', '')[:300],
                
                # Full data
                "json_detailed": json.dumps(job, ensure_ascii=False)
            }
            
            new_ids.append(job_id)
            new_docs.append(text_for_embedding)
            new_metas.append(metadata)
            new_embeddings.append(self.model.encode(text_for_embedding).tolist())
            self.existing_ids.add(job_id)
        
        if new_ids:
            self.collection.upsert(
                ids=new_ids,
                embeddings=new_embeddings,
                metadatas=new_metas,
                documents=new_docs
            )
            print(f"   ✅ Added {len(new_ids)} NEW jobs to database")
        else:
            print("   ⚠️  All jobs were duplicates - skipped")


# ==================== SCRAPER FUNCTIONS ====================

async def scrape_wuzzuf_by_category(
    context, 
    category: str, 
    keywords: List[str], 
    max_pages: int = 3
) -> List[Dict]:
    """Scrape Wuzzuf.net by category"""
    all_jobs = []
    
    print(f"\n🔍 Scraping Wuzzuf for category: {category}")
    
    for keyword in keywords[:5]:  # Limit keywords per category
        try:
            print(f"   Searching: {keyword}")
            
            # Build search URL
            search_url = f"https://wuzzuf.net/search/jobs/?q={keyword.replace(' ', '%20')}&a=hpb"
            
            page = await context.new_page()
            await stealth_async(page)
            await page.goto(search_url, wait_until="domcontentloaded", timeout=30000)
            await asyncio.sleep(random.uniform(2, 4))
            
            # Extract job listings
            job_cards = await page.query_selector_all("div.css-1gatmva")
            
            for card in job_cards[:20]:  # Limit per keyword
                try:
                    title_el = await card.query_selector("h2.css-m604qf a")
                    company_el = await card.query_selector("a.css-17s97q8")
                    location_el = await card.query_selector("span.css-5wys0k")
                    time_el = await card.query_selector("div.css-4c4ojb")
                    link_el = await card.query_selector("h2.css-m604qf a")
                    
                    if not (title_el and company_el):
                        continue
                    
                    title = await title_el.inner_text()
                    company = await company_el.inner_text()
                    location = await location_el.inner_text() if location_el else "Egypt"
                    posted_time = await time_el.inner_text() if time_el else "Recently"
                    job_link = await link_el.get_attribute("href") if link_el else ""
                    
                    if job_link and not job_link.startswith("http"):
                        job_link = f"https://wuzzuf.net{job_link}"
                    
                    job_data = {
                        "title": title.strip(),
                        "company": company.strip(),
                        "location": location.strip(),
                        "posted_time": posted_time.strip(),
                        "job_page_link": job_link,
                        "apply_link": job_link,
                        "source": "wuzzuf",
                        "category": category,
                        "description": "",  # Will be filled by detail scraper
                        "skills": [],
                        "experience_level": "Not Specified",
                        "employment_type": "Not Specified",
                        "salary_range": "Not Specified"
                    }
                    
                    all_jobs.append(job_data)
                    
                except Exception as e:
                    print(f"      Error extracting job card: {e}")
                    continue
            
            await page.close()
            await asyncio.sleep(random.uniform(3, 5))
            
        except Exception as e:
            print(f"   ❌ Error scraping keyword '{keyword}': {e}")
            continue
    
    print(f"   Found {len(all_jobs)} jobs for {category}")
    return all_jobs


async def scrape_linkedin_egypt_by_category(
    context,
    category: str,
    keywords: List[str],
    max_pages: int = 2
) -> List[Dict]:
    """Scrape LinkedIn jobs in Egypt by category"""
    all_jobs = []
    
    print(f"\n🔍 Scraping LinkedIn for category: {category}")
    
    for keyword in keywords[:3]:  # Limit keywords
        try:
            print(f"   Searching: {keyword}")
            
            # LinkedIn Egypt search
            search_url = (
                f"https://www.linkedin.com/jobs/search/?"
                f"keywords={keyword.replace(' ', '%20')}"
                f"&location=Egypt"
                f"&f_TPR=r604800"  # Past week
            )
            
            page = await context.new_page()
            await stealth_async(page)
            await page.goto(search_url, wait_until="domcontentloaded", timeout=30000)
            await asyncio.sleep(random.uniform(3, 5))
            
            # Scroll to load jobs
            for _ in range(3):
                await page.evaluate("window.scrollBy(0, 1000)")
                await asyncio.sleep(1)
            
            # Extract jobs
            job_cards = await page.query_selector_all("div.base-card")
            
            for card in job_cards[:15]:
                try:
                    title_el = await card.query_selector("h3.base-search-card__title")
                    company_el = await card.query_selector("h4.base-search-card__subtitle")
                    location_el = await card.query_selector("span.job-search-card__location")
                    time_el = await card.query_selector("time")
                    link_el = await card.query_selector("a.base-card__full-link")
                    
                    if not (title_el and company_el):
                        continue
                    
                    title = await title_el.inner_text()
                    company = await company_el.inner_text()
                    location = await location_el.inner_text() if location_el else "Egypt"
                    posted_time = await time_el.get_attribute("datetime") if time_el else ""
                    job_link = await link_el.get_attribute("href") if link_el else ""
                    
                    job_data = {
                        "title": title.strip(),
                        "company": company.strip(),
                        "location": location.strip(),
                        "posted_time": posted_time,
                        "job_page_link": job_link,
                        "apply_link": job_link,
                        "source": "linkedin",
                        "category": category,
                        "description": "",
                        "skills": [],
                        "experience_level": "Not Specified",
                        "employment_type": "Not Specified",
                        "salary_range": "Not Specified"
                    }
                    
                    all_jobs.append(job_data)
                    
                except Exception as e:
                    print(f"      Error extracting LinkedIn job: {e}")
                    continue
            
            await page.close()
            await asyncio.sleep(random.uniform(4, 6))
            
        except Exception as e:
            print(f"   ❌ Error scraping LinkedIn keyword '{keyword}': {e}")
            continue
    
    print(f"   Found {len(all_jobs)} LinkedIn jobs for {category}")
    return all_jobs


async def get_job_details_enhanced(context, job: Dict) -> Dict:
    """Get detailed job information including description and structured data"""
    if not job.get('job_page_link'):
        return job
    
    try:
        await asyncio.sleep(random.uniform(2, 4))
        page = await context.new_page()
        await stealth_async(page)
        await page.goto(job['job_page_link'], wait_until="domcontentloaded", timeout=30000)
        await asyncio.sleep(random.uniform(2, 3))
        
        # Wuzzuf-specific selectors
        if 'wuzzuf' in job['source']:
            # Description
            desc_el = await page.query_selector("div.css-1uobp1k")
            if desc_el:
                job['description'] = await desc_el.inner_text()
            
            # Skills
            skills_els = await page.query_selector_all("a.css-o171kl")
            job['skills'] = [await el.inner_text() for el in skills_els]
            
            # Experience level
            exp_el = await page.query_selector("span.css-4xky9y:has-text('Experience')")
            if exp_el:
                parent = await exp_el.query_selector("..")
                job['experience_level'] = await parent.inner_text()
            
            # Employment type
            type_el = await page.query_selector("span.css-4xky9y:has-text('Job Type')")
            if type_el:
                parent = await type_el.query_selector("..")
                job['employment_type'] = await parent.inner_text()
            
            # Salary
            salary_el = await page.query_selector("span.css-4xky9y:has-text('Salary')")
            if salary_el:
                parent = await salary_el.query_selector("..")
                job['salary_range'] = await parent.inner_text()
        
        # LinkedIn-specific selectors
        elif 'linkedin' in job['source']:
            desc_el = await page.query_selector("div.show-more-less-html__markup")
            if desc_el:
                job['description'] = await desc_el.inner_text()
            
            # Extract skills from description
            desc_text = job.get('description', '')
            common_skills = [
                'Python', 'Java', 'JavaScript', 'React', 'Node.js', 'SQL',
                'AWS', 'Azure', 'Docker', 'Kubernetes', 'Git', 'Agile'
            ]
            job['skills'] = [skill for skill in common_skills if skill.lower() in desc_text.lower()]
        
        await page.close()
        
    except Exception as e:
        print(f"      Error getting details for {job.get('title', 'Unknown')}: {e}")
    
    return job


# ==================== MAIN SCRAPING ORCHESTRATOR ====================

async def run_category_based_scraper(
    categories: List[str] = None,
    sources: List[str] = None,
    max_pages_per_category: int = 2,
    get_details: bool = True
):
    """Main scraping function with category-based approach"""
    
    scraper = CategoryBasedJobScraper()
    
    # Default to all categories if none specified
    if not categories:
        categories = list(JOB_CATEGORIES.keys())
    
    # Default to all sources
    if not sources:
        sources = ["wuzzuf", "linkedin"]
    
    print(f"\n{'='*60}")
    print(f"🚀 Starting Category-Based Job Scraping")
    print(f"📍 Location: Egypt")
    print(f"📂 Categories: {len(categories)}")
    print(f"🌐 Sources: {', '.join(sources)}")
    print(f"{'='*60}\n")
    
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        context = await browser.new_context(
            viewport={'width': 1920, 'height': 1080},
            user_agent='Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        )
        
        total_jobs_scraped = 0
        
        for category in categories:
            print(f"\n{'='*60}")
            print(f"📂 Processing Category: {category}")
            print(f"{'='*60}")
            
            category_data = JOB_CATEGORIES.get(category, {})
            keywords = category_data.get('keywords', [])
            
            if not keywords:
                print(f"   ⚠️  No keywords defined for {category}")
                continue
            
            category_jobs = []
            
            # Scrape from each source
            if "wuzzuf" in sources:
                wuzzuf_jobs = await scrape_wuzzuf_by_category(
                    context, 
                    category, 
                    keywords, 
                    max_pages_per_category
                )
                category_jobs.extend(wuzzuf_jobs)
            
            if "linkedin" in sources:
                linkedin_jobs = await scrape_linkedin_egypt_by_category(
                    context,
                    category,
                    keywords,
                    max_pages_per_category
                )
                category_jobs.extend(linkedin_jobs)
            
            # Get detailed information if requested
            if get_details and category_jobs:
                print(f"\n   📄 Fetching detailed information for {len(category_jobs)} jobs...")
                detailed_jobs = []
                for job in category_jobs[:50]:  # Limit details fetching
                    detailed_job = await get_job_details_enhanced(context, job)
                    detailed_jobs.append(detailed_job)
                    if len(detailed_jobs) % 10 == 0:
                        print(f"      Processed {len(detailed_jobs)} job details...")
                
                category_jobs = detailed_jobs
            
            # Save jobs
            if category_jobs:
                scraper.save_jobs(category_jobs, category=category)
                total_jobs_scraped += len(category_jobs)
            
            print(f"\n   ✅ Category {category} complete: {len(category_jobs)} jobs")
            
            # Delay between categories
            await asyncio.sleep(random.uniform(5, 8))
        
        await browser.close()
    
    print(f"\n{'='*60}")
    print(f"✅ SCRAPING COMPLETE")
    print(f"📊 Total jobs scraped: {total_jobs_scraped}")
    print(f"💾 Database size: {len(scraper.existing_ids)} total jobs")
    print(f"{'='*60}\n")
    
    return total_jobs_scraped


# ==================== HELPER FUNCTIONS ====================

def get_categories_by_priority() -> List[str]:
    """Get categories ordered by priority for Egypt market"""
    priority_categories = [
        "IT & Software Development",
        "Data & AI",
        "Engineering - Civ/Arch",
        "Sales & Retail",
        "Customer Service",
        "Marketing / PR / Ads",
        "Engineering - Mech/Elec",
        "Accounting & Finance",
        "Human Resources (HR)",
        "Logistics / Supply Chain",
        "Cybersecurity",
        "Renewable Energy"
    ]
    return priority_categories


def get_high_demand_categories() -> List[str]:
    """Get high-demand categories for 2026"""
    return [
        "Data & AI",
        "IT & Software Development",
        "Cybersecurity",
        "Renewable Energy",
        "Engineering - Mech/Elec"  # E-Mobility
    ]


# ==================== MAIN EXECUTION ====================

if __name__ == "__main__":
    # Example: Scrape high-demand categories
    high_demand = get_high_demand_categories()
    asyncio.run(run_category_based_scraper(
        categories=high_demand,
        sources=["wuzzuf", "linkedin"],
        max_pages_per_category=3,
        get_details=True
    ))
