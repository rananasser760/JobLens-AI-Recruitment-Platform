"""
JobLens FastAPI - Complete Implementation
Includes Job Scraping APIs with Category Support
"""

from fastapi import FastAPI, HTTPException, Query, BackgroundTasks
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
from typing import List, Optional, Dict
from datetime import datetime, timedelta
from enum import Enum
import asyncio
import uuid
import chromadb
from sentence_transformers import SentenceTransformer
import json

# Import the enhanced scraper
from job_scraper_enhanced import (
    run_category_based_scraper,
    JOB_CATEGORIES,
    get_high_demand_categories,
    get_categories_by_priority,
    CategoryBasedJobScraper
)

# ==================== CONFIGURATION ====================
CHROMA_PATH = "./joblens_db"
EMBEDDING_MODEL = 'all-MiniLM-L6-v2'

# ==================== FASTAPI APP ====================
app = FastAPI(
    title="JobLens API",
    description="Job Scraping and Matching API with Category-Based Egypt Market Focus",
    version="2.0.0"
)

# CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# ==================== INITIALIZATION ====================
print("⏳ Initializing JobLens API...")
chroma_client = chromadb.PersistentClient(path=CHROMA_PATH)
collection = chroma_client.get_or_create_collection(name="job_listings")
model = SentenceTransformer(EMBEDDING_MODEL)
print("✅ JobLens API Ready")

# Background job tracking
scraping_jobs = {}


# ==================== ENUMS ====================
class JobSource(str, Enum):
    linkedin = "linkedin"
    wuzzuf = "wuzzuf"
    forasna = "forasna"
    indeed = "indeed"
    glassdoor = "glassdoor"


class JobCategory(str, Enum):
    it_software = "IT & Software Development"
    engineering_mech = "Engineering - Mech/Elec"
    engineering_civ = "Engineering - Civ/Arch"
    sales_retail = "Sales & Retail"
    marketing = "Marketing / PR / Ads"
    accounting = "Accounting & Finance"
    logistics = "Logistics / Supply Chain"
    customer_service = "Customer Service"
    hr = "Human Resources (HR)"
    data_ai = "Data & AI"
    renewable_energy = "Renewable Energy"
    cybersecurity = "Cybersecurity"


# ==================== REQUEST/RESPONSE MODELS ====================

class ScrapingTriggerRequest(BaseModel):
    sources: List[JobSource] = Field(
        default=["wuzzuf", "linkedin"],
        description="Job boards to scrape"
    )
    keywords: Optional[List[str]] = Field(
        default=None,
        description="Specific keywords to search (overrides categories)"
    )
    categories: Optional[List[JobCategory]] = Field(
        default=None,
        description="Job categories to scrape. If empty, scrapes high-demand categories"
    )
    locations: List[str] = Field(
        default=["Egypt"],
        description="Locations to search"
    )
    max_pages: int = Field(
        default=2,
        ge=1,
        le=10,
        description="Maximum pages to scrape per category"
    )
    get_details: bool = Field(
        default=True,
        description="Whether to fetch full job details"
    )


class ScrapingTriggerResponse(BaseModel):
    success: bool
    message: str
    data: Dict


class JobResponse(BaseModel):
    external_job_id: str
    title: str
    description: str
    requirements: Optional[str] = None
    responsibilities: Optional[str] = None
    location: str
    salary_range: Optional[str] = None
    employment_type: str
    experience_level: str
    external_url: str
    external_source: str
    company_name: str
    posted_at: str
    scraped_at: Optional[str] = None
    skills: List[str]
    category: Optional[str] = None


class JobListResponse(BaseModel):
    success: bool
    data: List[JobResponse]
    total_count: int
    message: Optional[str] = None


class JobData(BaseModel):
    title: str
    description: str
    requirements: Optional[str] = "Not Specified"
    responsibilities: Optional[str] = "Not Specified"
    skills: List[str] = []
    location: str
    experience_level: str = "Not Specified"
    employment_type: str = "Not Specified"
    company_name: str
    category: Optional[str] = None


class JobEmbeddingRequest(BaseModel):
    job_id: int
    job_data: JobData


# ==================== BACKGROUND TASKS ====================

async def run_scraping_task(job_id: str, request: ScrapingTriggerRequest):
    """Background task to run scraping"""
    try:
        print(f"\n🔄 Starting scraping job {job_id}")
        scraping_jobs[job_id]["status"] = "running"
        scraping_jobs[job_id]["started_at"] = datetime.now().isoformat()
        
        # Determine categories to scrape
        if request.categories:
            categories = [cat.value for cat in request.categories]
        elif request.keywords:
            # If keywords provided, use general scraping
            categories = None
        else:
            # Default to high-demand categories
            categories = get_high_demand_categories()
        
        sources = [src.value for src in request.sources]
        
        # Run the scraper
        total_scraped = await run_category_based_scraper(
            categories=categories,
            sources=sources,
            max_pages_per_category=request.max_pages,
            get_details=request.get_details
        )
        
        scraping_jobs[job_id]["status"] = "completed"
        scraping_jobs[job_id]["completed_at"] = datetime.now().isoformat()
        scraping_jobs[job_id]["total_jobs_scraped"] = total_scraped
        
        print(f"✅ Scraping job {job_id} completed: {total_scraped} jobs scraped")
        
    except Exception as e:
        print(f"❌ Scraping job {job_id} failed: {e}")
        scraping_jobs[job_id]["status"] = "failed"
        scraping_jobs[job_id]["error"] = str(e)


# ==================== HELPER FUNCTIONS ====================

def parse_job_metadata(metadata: Dict) -> JobResponse:
    """Convert ChromaDB metadata to JobResponse"""
    try:
        # Try to get full details from json_detailed
        if "json_detailed" in metadata:
            full_job = json.loads(metadata["json_detailed"])
        else:
            full_job = metadata
        
        return JobResponse(
            external_job_id=metadata.get("external_job_id", ""),
            title=metadata.get("title", "N/A"),
            description=full_job.get("description", ""),
            requirements=full_job.get("requirements", "Not Specified"),
            responsibilities=full_job.get("responsibilities", "Not Specified"),
            location=metadata.get("location", "Egypt"),
            salary_range=metadata.get("salary_range", "Not Specified"),
            employment_type=metadata.get("employment_type", "Not Specified"),
            experience_level=metadata.get("experience_level", "Not Specified"),
            external_url=metadata.get("job_page_link", ""),
            external_source=metadata.get("source", "Unknown"),
            company_name=metadata.get("company", "N/A"),
            posted_at=metadata.get("posted_time", ""),
            scraped_at=metadata.get("scraped_at", ""),
            skills=metadata.get("skills_list", "").split(", ") if metadata.get("skills_list") else [],
            category=metadata.get("category", None)
        )
    except Exception as e:
        print(f"Error parsing metadata: {e}")
        # Return minimal response
        return JobResponse(
            external_job_id="",
            title=metadata.get("title", "N/A"),
            description="",
            location=metadata.get("location", "Egypt"),
            salary_range="Not Specified",
            employment_type="Not Specified",
            experience_level="Not Specified",
            external_url="",
            external_source=metadata.get("source", "Unknown"),
            company_name=metadata.get("company", "N/A"),
            posted_at="",
            skills=[]
        )


def calculate_posted_within_filter(days: int) -> str:
    """Calculate date string for filtering"""
    cutoff_date = datetime.now() - timedelta(days=days)
    return cutoff_date.isoformat()


# ==================== API ENDPOINTS ====================

@app.get("/")
async def root():
    """API Health Check"""
    return {
        "status": "online",
        "service": "JobLens API",
        "version": "2.0.0",
        "features": [
            "Category-based scraping",
            "Egypt market focus",
            "Multi-source aggregation",
            "AI-powered matching"
        ]
    }


@app.get("/api/categories")
async def get_categories():
    """Get all available job categories"""
    return {
        "success": True,
        "data": {
            "all_categories": list(JOB_CATEGORIES.keys()),
            "high_demand_categories": get_high_demand_categories(),
            "priority_categories": get_categories_by_priority(),
            "category_details": {
                name: {
                    "keywords": data["keywords"][:5],  # First 5 keywords
                    "total_keywords": len(data["keywords"])
                }
                for name, data in JOB_CATEGORIES.items()
            }
        }
    }


@app.get("/api/scraping/jobs", response_model=JobListResponse)
async def get_scraped_jobs(
    keyword: Optional[str] = Query(None, description="Job title or skill keyword"),
    location: Optional[str] = Query(None, description="Job location filter"),
    source: Optional[JobSource] = Query(None, description="Source filter"),
    category: Optional[JobCategory] = Query(None, description="Category filter"),
    experience_level: Optional[str] = Query(None, description="Experience level filter"),
    employment_type: Optional[str] = Query(None, description="Employment type filter"),
    limit: int = Query(50, ge=1, le=200, description="Maximum results"),
    posted_within_days: Optional[int] = Query(None, ge=1, description="Only jobs posted within N days")
):
    """
    Fetch jobs scraped from external job boards with advanced filtering
    
    **Enhanced Features:**
    - Category-based filtering
    - Multi-criteria search
    - Semantic similarity search
    - Egypt market specialization
    """
    try:
        # Build filter
        where_filter = {}
        
        if location:
            where_filter["location"] = {"$contains": location}
        
        if source:
            where_filter["source"] = source.value
        
        if category:
            where_filter["category"] = category.value
        
        if experience_level:
            where_filter["experience_level"] = {"$contains": experience_level}
        
        if employment_type:
            where_filter["employment_type"] = {"$contains": employment_type}
        
        # Handle posted_within_days filter
        if posted_within_days:
            cutoff_date = calculate_posted_within_filter(posted_within_days)
            where_filter["scraped_at"] = {"$gte": cutoff_date}
        
        # Use None if no filters
        if len(where_filter) == 0:
            where_filter = None
        
        # Execute query
        if keyword:
            # Semantic search
            query_vec = model.encode(keyword).tolist()
            results = collection.query(
                query_embeddings=[query_vec],
                n_results=limit,
                where=where_filter
            )
            ids = results['ids'][0]
            metas = results['metadatas'][0]
            distances = results['distances'][0] if 'distances' in results else None
        else:
            # List all matching filter
            results = collection.get(
                limit=limit,
                where=where_filter
            )
            ids = results['ids']
            metas = results['metadatas']
            distances = None
        
        # Format response
        jobs = []
        for i, job_id in enumerate(ids):
            meta = metas[i]
            meta["external_job_id"] = job_id
            
            job_response = parse_job_metadata(meta)
            jobs.append(job_response)
        
        return JobListResponse(
            success=True,
            data=jobs,
            total_count=len(jobs),
            message=f"Found {len(jobs)} jobs" + (f" matching '{keyword}'" if keyword else "")
        )
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error fetching jobs: {str(e)}")


@app.post("/api/scraping/trigger", status_code=202, response_model=ScrapingTriggerResponse)
async def trigger_scraping_job(
    payload: ScrapingTriggerRequest,
    background_tasks: BackgroundTasks
):
    """
    Manually trigger a scraping job (Admin endpoint)
    
    **Features:**
    - Category-based scraping
    - Multi-source support
    - Background processing
    - Job status tracking
    
    **Example Request:**
    ```json
    {
        "sources": ["wuzzuf", "linkedin"],
        "categories": ["IT & Software Development", "Data & AI"],
        "max_pages": 3,
        "get_details": true
    }
    ```
    """
    try:
        # Generate unique job ID
        job_id = f"scrape_{uuid.uuid4().hex[:12]}"
        
        # Estimate time based on configuration
        estimated_minutes = len(payload.sources) * (len(payload.categories) if payload.categories else 5) * payload.max_pages * 2
        
        # Initialize job tracking
        scraping_jobs[job_id] = {
            "job_id": job_id,
            "status": "queued",
            "created_at": datetime.now().isoformat(),
            "config": payload.dict(),
            "estimated_time_minutes": estimated_minutes
        }
        
        # Add background task
        background_tasks.add_task(run_scraping_task, job_id, payload)
        
        return ScrapingTriggerResponse(
            success=True,
            message="Scraping job queued successfully",
            data={
                "job_id": job_id,
                "estimated_time_minutes": estimated_minutes,
                "status_endpoint": f"/api/scraping/status/{job_id}",
                "categories": [cat.value for cat in payload.categories] if payload.categories else "high-demand defaults",
                "sources": [src.value for src in payload.sources]
            }
        )
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error triggering scraping: {str(e)}")


@app.get("/api/scraping/status/{job_id}")
async def get_scraping_status(job_id: str):
    """
    Get status of a scraping job
    """
    if job_id not in scraping_jobs:
        raise HTTPException(status_code=404, detail="Job not found")
    
    job_info = scraping_jobs[job_id]
    
    return {
        "success": True,
        "data": job_info
    }


@app.get("/api/scraping/jobs/stats")
async def get_job_statistics():
    """
    Get statistics about scraped jobs
    """
    try:
        # Get all jobs
        all_jobs = collection.get()
        total_jobs = len(all_jobs['ids'])
        
        # Count by source
        source_counts = {}
        category_counts = {}
        location_counts = {}
        
        for meta in all_jobs['metadatas']:
            # Count by source
            source = meta.get('source', 'Unknown')
            source_counts[source] = source_counts.get(source, 0) + 1
            
            # Count by category
            category = meta.get('category', 'Uncategorized')
            category_counts[category] = category_counts.get(category, 0) + 1
            
            # Count by location
            location = meta.get('location', 'Unknown')
            location_counts[location] = location_counts.get(location, 0) + 1
        
        return {
            "success": True,
            "data": {
                "total_jobs": total_jobs,
                "by_source": source_counts,
                "by_category": category_counts,
                "by_location": location_counts,
                "last_updated": datetime.now().isoformat()
            }
        }
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error getting statistics: {str(e)}")


# ==================== JOB EMBEDDING ENDPOINTS ====================

@app.post("/api/embeddings/job")
async def create_job_embedding(payload: JobEmbeddingRequest):
    """
    Create embedding for a new job posting (Internal API)
    """
    try:
        job_data = payload.job_data
        job_id = payload.job_id
        
        # Create rich text for embedding
        skills_str = ", ".join(job_data.skills)
        full_text = (
            f"{job_data.title} at {job_data.company_name}. "
            f"Location: {job_data.location}. "
            f"Category: {job_data.category or 'General'}. "
            f"Level: {job_data.experience_level}. "
            f"Skills: {skills_str}. "
            f"Description: {job_data.description} "
            f"Requirements: {job_data.requirements}"
        )
        
        # Generate embedding
        embedding = model.encode(full_text).tolist()
        
        # Store in ChromaDB
        embedding_id = f"job_{job_id}"
        
        metadata = {
            "original_job_id": job_id,
            "title": job_data.title,
            "company": job_data.company_name,
            "location": job_data.location,
            "experience_level": job_data.experience_level,
            "employment_type": job_data.employment_type,
            "category": job_data.category or "General",
            "source": "Internal API",
            "skills_list": skills_str,
            "description_snippet": job_data.description[:300],
            "scraped_at": datetime.now().isoformat()
        }
        
        collection.upsert(
            ids=[embedding_id],
            embeddings=[embedding],
            documents=[full_text],
            metadatas=[metadata]
        )
        
        return {
            "success": True,
            "message": "Job embedding created successfully",
            "data": {"id": embedding_id}
        }
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error creating embedding: {str(e)}")


@app.put("/api/embeddings/job/{job_id}")
async def update_job_embedding(job_id: int, payload: JobEmbeddingRequest):
    """
    Update embedding for an existing job
    """
    try:
        if job_id != payload.job_id:
            raise HTTPException(status_code=400, detail="URL job_id does not match payload")
        
        # Reuse create logic (upsert handles updates)
        return await create_job_embedding(payload)
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error updating embedding: {str(e)}")


@app.delete("/api/embeddings/job/{job_id}")
async def delete_job_embedding(job_id: int):
    """
    Delete job embedding
    """
    try:
        embedding_id = f"job_{job_id}"
        collection.delete(ids=[embedding_id])
        
        return {
            "success": True,
            "message": "Job embedding deleted successfully"
        }
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Error deleting embedding: {str(e)}")


# ==================== MAIN ====================

if __name__ == "__main__":
    import uvicorn
    
    print("\n" + "="*60)
    print("🚀 Starting JobLens API Server")
    print("="*60)
    print(f"📍 Egypt Market Focus")
    print(f"📂 Categories: {len(JOB_CATEGORIES)}")
    print(f"🌐 API Docs: http://127.0.0.1:8000/docs")
    print("="*60 + "\n")
    
    uvicorn.run(
        app,
        host="0.0.0.0",
        port=8000,
        log_level="info"
    )
