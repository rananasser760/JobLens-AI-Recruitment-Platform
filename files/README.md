# 🎯 JobLens - Enhanced Job Scraper for Egypt Market

A category-based job scraping system focused on the Egyptian job market with AI-powered matching capabilities.

## 🌟 Features

### Core Capabilities
- ✅ **Category-Based Scraping**: Specialized scraping for 12+ job categories
- ✅ **Egypt Market Focus**: Optimized for Egyptian job boards (Wuzzuf, Forasna, LinkedIn Egypt)
- ✅ **Multi-Source Aggregation**: Scrapes from multiple job boards simultaneously
- ✅ **AI-Powered Matching**: Uses sentence transformers for semantic job matching
- ✅ **Structured Data Extraction**: Extracts skills, experience level, salary, employment type
- ✅ **Duplicate Detection**: Smart deduplication based on title, company, and source
- ✅ **RESTful API**: Complete FastAPI implementation with comprehensive endpoints

### Supported Categories (2026 Focus)

1. **IT & Software Development**
   - Web, Mobile, DevOps, Cloud, QA, AI/ML

2. **Data & AI** ⭐ High Demand
   - Data Scientists, Data Engineers, ML Engineers

3. **Engineering - Mech/Elec**
   - Mechatronics, E-Mobility, Maintenance, Production

4. **Engineering - Civ/Arch**
   - Technical Office, Site Engineering, BIM, Planning

5. **Sales & Retail**
   - B2B Sales, Account Management, Real Estate, Business Development

6. **Marketing / PR / Ads**
   - Growth Marketing, SEO/SEM, Content, Social Media

7. **Accounting & Finance**
   - Treasury, Auditing, Tax, Financial Analysis, FinTech

8. **Logistics / Supply Chain**
   - Procurement, Warehouse, Distribution, Customs

9. **Customer Service**
   - Call Center (English/German/French), Technical Support

10. **Human Resources (HR)**
    - Recruitment, Personnel, L&D, HR Operations

11. **Renewable Energy** ⭐ High Demand
    - Solar/Wind Engineers, Sustainability Consultants

12. **Cybersecurity** ⭐ High Demand
    - Security Analysts, Privacy Managers

## 📋 Prerequisites

- Python 3.10+
- 8GB+ RAM (for AI models)
- Internet connection
- Windows/Linux/macOS

## 🚀 Installation

### 1. Clone or Download Files

Place all files in your project directory:
```
joblens/
├── job_scraper_enhanced.py
├── api_server.py
├── requirements.txt
└── README.md
```

### 2. Create Virtual Environment

```bash
# Create virtual environment
python -m venv venv

# Activate (Windows)
venv\Scripts\activate

# Activate (Linux/Mac)
source venv/bin/activate
```

### 3. Install Dependencies

```bash
pip install -r requirements.txt
```

### 4. Install Playwright Browsers

```bash
playwright install chromium
```

## 💻 Usage

### Method 1: Direct Scraping (Python Script)

```python
import asyncio
from job_scraper_enhanced import run_category_based_scraper, get_high_demand_categories

# Scrape high-demand categories
high_demand = get_high_demand_categories()
asyncio.run(run_category_based_scraper(
    categories=high_demand,
    sources=["wuzzuf", "linkedin"],
    max_pages_per_category=3,
    get_details=True
))
```

### Method 2: API Server (Recommended)

#### Start the API Server

```bash
python api_server.py
```

The server will start at: `http://127.0.0.1:8000`

API Documentation: `http://127.0.0.1:8000/docs`

## 📡 API Endpoints

### 1. Get Scraped Jobs

**Endpoint:** `GET /api/scraping/jobs`

**Query Parameters:**
- `keyword` (optional): Job title or skill keyword
- `location` (optional): Location filter
- `source` (optional): Source filter (wuzzuf, linkedin, forasna)
- `category` (optional): Category filter
- `experience_level` (optional): Experience level filter
- `employment_type` (optional): Employment type filter
- `limit` (optional): Max results (default: 50)
- `posted_within_days` (optional): Only recent jobs

**Example Requests:**

```bash
# Get all Python jobs
curl "http://127.0.0.1:8000/api/scraping/jobs?keyword=python&limit=50"

# Get IT jobs in Cairo
curl "http://127.0.0.1:8000/api/scraping/jobs?category=IT%20%26%20Software%20Development&location=Cairo"

# Get recent data science jobs
curl "http://127.0.0.1:8000/api/scraping/jobs?category=Data%20%26%20AI&posted_within_days=7"

# Get Wuzzuf jobs only
curl "http://127.0.0.1:8000/api/scraping/jobs?source=wuzzuf&limit=100"
```

**Response Example:**

```json
{
  "success": true,
  "data": [
    {
      "external_job_id": "abc123def456",
      "title": "Senior Python Developer",
      "description": "We are seeking a talented Python developer...",
      "requirements": "- 5+ years Python experience\n- FastAPI knowledge\n- Docker/Kubernetes",
      "responsibilities": "- Design and implement APIs\n- Mentor junior developers",
      "location": "Cairo, Egypt",
      "salary_range": "25,000 - 35,000 EGP",
      "employment_type": "Full-time",
      "experience_level": "Senior",
      "external_url": "https://wuzzuf.net/jobs/...",
      "external_source": "wuzzuf",
      "company_name": "TechCorp Egypt",
      "posted_at": "2 days ago",
      "scraped_at": "2026-02-04T10:30:00",
      "skills": ["Python", "FastAPI", "PostgreSQL", "Docker", "Kubernetes"],
      "category": "IT & Software Development"
    }
  ],
  "total_count": 150,
  "message": "Found 150 jobs matching 'python'"
}
```

### 2. Trigger Job Scraping (Admin)

**Endpoint:** `POST /api/scraping/trigger`

**Request Body:**

```json
{
  "sources": ["wuzzuf", "linkedin"],
  "categories": [
    "IT & Software Development",
    "Data & AI",
    "Cybersecurity"
  ],
  "max_pages": 3,
  "get_details": true
}
```

**Response (202 Accepted):**

```json
{
  "success": true,
  "message": "Scraping job queued successfully",
  "data": {
    "job_id": "scrape_abc123",
    "estimated_time_minutes": 25,
    "status_endpoint": "/api/scraping/status/scrape_abc123",
    "categories": [
      "IT & Software Development",
      "Data & AI",
      "Cybersecurity"
    ],
    "sources": ["wuzzuf", "linkedin"]
  }
}
```

**Example cURL:**

```bash
curl -X POST "http://127.0.0.1:8000/api/scraping/trigger" \
  -H "Content-Type: application/json" \
  -d '{
    "sources": ["wuzzuf", "linkedin"],
    "categories": ["Data & AI", "IT & Software Development"],
    "max_pages": 2,
    "get_details": true
  }'
```

### 3. Check Scraping Status

**Endpoint:** `GET /api/scraping/status/{job_id}`

```bash
curl "http://127.0.0.1:8000/api/scraping/status/scrape_abc123"
```

**Response:**

```json
{
  "success": true,
  "data": {
    "job_id": "scrape_abc123",
    "status": "completed",
    "created_at": "2026-02-04T10:00:00",
    "started_at": "2026-02-04T10:00:05",
    "completed_at": "2026-02-04T10:25:30",
    "total_jobs_scraped": 342,
    "config": {
      "sources": ["wuzzuf", "linkedin"],
      "categories": ["Data & AI", "IT & Software Development"]
    }
  }
}
```

### 4. Get Job Statistics

**Endpoint:** `GET /api/scraping/jobs/stats`

```bash
curl "http://127.0.0.1:8000/api/scraping/jobs/stats"
```

**Response:**

```json
{
  "success": true,
  "data": {
    "total_jobs": 5420,
    "by_source": {
      "wuzzuf": 3200,
      "linkedin": 1850,
      "forasna": 370
    },
    "by_category": {
      "IT & Software Development": 1850,
      "Data & AI": 620,
      "Engineering - Civ/Arch": 580,
      "Sales & Retail": 520
    },
    "by_location": {
      "Cairo": 2100,
      "Giza": 890,
      "Alexandria": 450,
      "Remote": 780
    },
    "last_updated": "2026-02-04T12:00:00"
  }
}
```

### 5. Get Available Categories

**Endpoint:** `GET /api/categories`

```bash
curl "http://127.0.0.1:8000/api/categories"
```

## 🎯 Advanced Features

### Category-Based Scraping Strategy

The scraper uses an intelligent category-based approach:

1. **Keyword Expansion**: Each category has multiple keyword variations
2. **Multi-Language Support**: Includes Arabic keywords for Egyptian market
3. **Priority Scraping**: High-demand categories scraped first
4. **Smart Deduplication**: Prevents duplicate jobs across sources

### Data Extraction

For each job, the scraper extracts:

- ✅ **Title & Company**: Job title and company name
- ✅ **Description**: Full job description
- ✅ **Requirements**: Extracted requirements section
- ✅ **Responsibilities**: Extracted responsibilities
- ✅ **Skills**: Parsed skill list
- ✅ **Experience Level**: Entry, Mid, Senior, Expert
- ✅ **Employment Type**: Full-time, Part-time, Contract, Freelance
- ✅ **Salary Range**: When available
- ✅ **Location**: City/region in Egypt
- ✅ **Posted Date**: Original posting date
- ✅ **Apply Link**: Direct application URL

### AI-Powered Matching

Uses SentenceTransformers for semantic search:

```python
# Search for similar jobs
results = await get_scraped_jobs(
    keyword="machine learning engineer with python experience",
    limit=20
)
# Returns semantically similar jobs even if exact keywords don't match
```

## 🔧 Configuration

### Customize Categories

Edit `job_scraper_enhanced.py`:

```python
JOB_CATEGORIES = {
    "Your Custom Category": {
        "keywords": [
            "keyword1", "keyword2", "keyword3"
        ],
        "egypt_variations": [
            "كلمة مفتاحية بالعربية"
        ]
    }
}
```

### Add New Job Sources

```python
EGYPT_JOB_SOURCES = {
    "your_source": {
        "base_url": "https://example.com/jobs/",
        "location_code": "egypt",
        "enabled": True
    }
}
```

## 📊 Database Structure

Jobs are stored in ChromaDB with:

**Vector Embeddings**: AI-generated embeddings for semantic search  
**Metadata Fields**:
- `source`: Job board source
- `title`: Job title
- `company`: Company name
- `location`: Job location
- `category`: Job category
- `experience_level`: Required experience
- `employment_type`: Employment type
- `skills_list`: Comma-separated skills
- `salary_range`: Salary information
- `posted_time`: Original posting time
- `scraped_at`: When job was scraped
- `job_page_link`: Link to original posting
- `json_detailed`: Full job data in JSON

## 🐛 Troubleshooting

### Issue: Playwright browser not found
```bash
playwright install chromium
```

### Issue: Timeout errors
- Increase timeout in scraper configuration
- Check internet connection
- Some sites may be blocking automated access

### Issue: No jobs found
- Verify job boards are accessible
- Check if category keywords match current market
- Try different sources or categories

### Issue: Memory errors
- Reduce `max_pages_per_category`
- Scrape fewer categories at once
- Close other applications

## 📈 Performance Tips

1. **Start Small**: Begin with 1-2 categories and 2 pages
2. **Monitor Progress**: Watch console output for errors
3. **Respect Rate Limits**: Built-in delays prevent blocking
4. **Regular Updates**: Run scraping daily or weekly
5. **Database Cleanup**: Periodically remove old jobs

## 🔒 Best Practices

1. **Ethical Scraping**:
   - Respect robots.txt
   - Use appropriate delays
   - Don't overload servers
   - Follow terms of service

2. **Data Privacy**:
   - Don't scrape personal information
   - Store data securely
   - Follow GDPR/local regulations

3. **Production Deployment**:
   - Use environment variables for config
   - Implement proper logging
   - Add authentication to API
   - Use Redis for caching
   - Deploy with Docker

## 📝 Example Integration

### Python Client

```python
import requests

# Get Data Science jobs in Cairo
response = requests.get(
    "http://127.0.0.1:8000/api/scraping/jobs",
    params={
        "category": "Data & AI",
        "location": "Cairo",
        "limit": 50
    }
)

jobs = response.json()["data"]
for job in jobs:
    print(f"{job['title']} at {job['company_name']}")
    print(f"Skills: {', '.join(job['skills'])}")
    print(f"Link: {job['external_url']}\n")
```

### JavaScript/Node.js Client

```javascript
const axios = require('axios');

async function getJobs() {
  const response = await axios.get('http://127.0.0.1:8000/api/scraping/jobs', {
    params: {
      keyword: 'python developer',
      location: 'Egypt',
      limit: 30
    }
  });
  
  return response.data.data;
}

getJobs().then(jobs => {
  jobs.forEach(job => {
    console.log(`${job.title} - ${job.company_name}`);
  });
});
```

## 🌐 Deployment

### Docker (Recommended)

```dockerfile
FROM python:3.10-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install -r requirements.txt
RUN playwright install chromium

COPY . .

EXPOSE 8000

CMD ["python", "api_server.py"]
```

### Build and Run

```bash
docker build -t joblens .
docker run -p 8000:8000 -v ./joblens_db:/app/joblens_db joblens
```

## 📚 Additional Resources

- **FastAPI Documentation**: https://fastapi.tiangolo.com
- **Playwright Documentation**: https://playwright.dev
- **ChromaDB Documentation**: https://docs.trychroma.com
- **Sentence Transformers**: https://www.sbert.net

## 🤝 Support

For issues or questions:
1. Check the troubleshooting section
2. Review API documentation at `/docs`
3. Check console logs for detailed errors

## 📄 License

This project is provided as-is for educational and commercial use.

## 🎉 Acknowledgments

Built with:
- FastAPI
- Playwright
- ChromaDB
- SentenceTransformers
- BeautifulSoup4

---

**Happy Scraping! 🚀**
