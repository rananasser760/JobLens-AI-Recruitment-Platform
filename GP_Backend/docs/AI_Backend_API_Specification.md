# JobLens AI Backend API Specification

## FastAPI Python Backend for AI/ML Services

This document outlines all the APIs required from the Python FastAPI backend that handles LLMs, ChromaDB vector database, and AI-related functionality for the JobLens AI Recruitment Platform.

---

## Table of Contents

1. [Overview](#overview)
2. [Base Configuration](#base-configuration)
3. [CV Parsing APIs](#cv-parsing-apis)
4. [ATS Scoring APIs](#ats-scoring-apis)
5. [Embeddings & Vector Database APIs](#embeddings--vector-database-apis)
6. [Recommendations APIs](#recommendations-apis)
7. [Job Scraping APIs](#job-scraping-apis)
8. [Interview AI APIs](#interview-ai-apis)
9. [Audio Processing APIs](#audio-processing-apis)
10. [Error Handling](#error-handling)
11. [Models & Schemas](#models--schemas)

---

## Overview

### Technology Stack (Recommended)

| Component | Technology |
|-----------|------------|
| Framework | FastAPI |
| LLM | OpenAI GPT-4 / Gemini / Llama |
| Vector Database | ChromaDB |
| Embeddings | OpenAI text-embedding-3-small / sentence-transformers |
| Speech-to-Text | OpenAI Whisper |
| Text-to-Speech | OpenAI TTS / gTTS |
| CV Parsing | Custom LLM prompts + PyPDF2/python-docx |
| Job Scraping | BeautifulSoup + Selenium |

### Base URL

```
http://localhost:8000
```

### Authentication

All endpoints should accept an API key header for internal service-to-service communication:

```
X-API-Key: <your-internal-api-key>
```

---

## Base Configuration

### Health Check

```http
GET /health
```

**Response:**
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "services": {
    "llm": "connected",
    "chromadb": "connected",
    "whisper": "available"
  }
}
```

---

## CV Parsing APIs

### 1. Parse CV from File

Extracts structured information from a CV/Resume file (PDF, DOCX).

```http
POST /api/cv/parse
Content-Type: multipart/form-data
```

**Request:**
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| file | File | Yes | CV file (PDF, DOCX, DOC) |

**Response (200 OK):**
```json
{
  "success": true,
  "data": {
    "full_name": "John Doe",
    "email": "john.doe@email.com",
    "phone": "+1-234-567-8900",
    "location": "New York, NY",
    "summary": "Experienced software engineer with 5+ years...",
    "skills": [
      "Python",
      "Machine Learning",
      "TensorFlow",
      "SQL",
      "Docker"
    ],
    "experience": [
      {
        "job_title": "Senior Software Engineer",
        "company": "Tech Corp",
        "start_date": "2020-01",
        "end_date": "Present",
        "description": "Led development of ML pipeline..."
      },
      {
        "job_title": "Software Engineer",
        "company": "StartupXYZ",
        "start_date": "2018-06",
        "end_date": "2019-12",
        "description": "Developed RESTful APIs..."
      }
    ],
    "education": [
      {
        "degree": "Master of Science",
        "institution": "MIT",
        "graduation_year": "2018",
        "field_of_study": "Computer Science"
      }
    ],
    "confidence": 0.92
  },
  "message": null
}
```

**Implementation Notes:**
- Use PyPDF2 or pdfplumber for PDF extraction
- Use python-docx for DOCX extraction
- Send extracted text to LLM with structured extraction prompt
- Return confidence score based on extraction quality

---

### 2. Parse CV from Text

Extracts structured information from raw CV text.

```http
POST /api/cv/parse-text
Content-Type: application/json
```

**Request:**
```json
{
  "resume_text": "John Doe\nSenior Software Engineer\n..."
}
```

**Response:** Same as Parse CV from File

---

## ATS Scoring APIs

### 3. Get ATS Score

Analyzes resume for ATS (Applicant Tracking System) compatibility.

```http
POST /api/cv/ats-score
Content-Type: application/json
```

**Request:**
```json
{
  "resume_text": "John Doe\nSenior Software Engineer...",
  "job_description": "We are looking for a Python developer..." 
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| resume_text | string | Yes | Full text of the resume |
| job_description | string | No | Job description to match against |

**Response (200 OK):**
```json
{
  "success": true,
  "data": {
    "overall_score": 78,
    "is_friendly": true,
    "recommendations": [
      "Add more quantifiable achievements",
      "Include relevant keywords from job description",
      "Use standard section headers (Experience, Education, Skills)",
      "Avoid tables and complex formatting"
    ],
    "category_scores": {
      "keywords": 85,
      "format": 70,
      "experience_match": 80,
      "skills_match": 75,
      "education": 80
    },
    "missing_keywords": [
      "agile",
      "scrum",
      "kubernetes"
    ],
    "matched_keywords": [
      "python",
      "machine learning",
      "sql"
    ]
  },
  "message": null
}
```

**Implementation Notes:**
- Compare resume keywords with job description
- Check for ATS-friendly formatting
- Use LLM to generate improvement recommendations
- Score categories: Keywords, Format, Experience, Skills, Education

---

## Embeddings & Vector Database APIs

### 4. Create Candidate Embedding

Creates a vector embedding for a candidate profile and stores in ChromaDB.

```http
POST /api/embeddings/candidate
Content-Type: application/json
```

**Request:**
```json
{
  "candidate_id": 123,
  "profile_data": {
    "full_name": "John Doe",
    "current_title": "Senior Software Engineer",
    "summary": "5+ years experience in...",
    "skills": ["Python", "ML", "Docker"],
    "experience_years": 5,
    "location": "New York",
    "education": "MS Computer Science",
    "resume_text": "Full resume text..."
  }
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Candidate embedding created successfully",
  "data": {
    "candidate_id": 123,
    "embedding_id": "cand_123"
  }
}
```

**Implementation Notes:**
- Combine profile fields into a single text representation
- Generate embedding using OpenAI text-embedding-3-small or sentence-transformers
- Store in ChromaDB collection `candidates`
- Include metadata: candidate_id, skills, location, experience_years

---

### 5. Update Candidate Embedding

```http
PUT /api/embeddings/candidate/{candidate_id}
Content-Type: application/json
```

**Request:** Same as Create Candidate Embedding

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Candidate embedding updated successfully"
}
```

---

### 6. Delete Candidate Embedding

```http
DELETE /api/embeddings/candidate/{candidate_id}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Candidate embedding deleted successfully"
}
```

---

### 7. Create Job Embedding

Creates a vector embedding for a job posting and stores in ChromaDB.

```http
POST /api/embeddings/job
Content-Type: application/json
```

**Request:**
```json
{
  "job_id": 456,
  "job_data": {
    "title": "Senior Python Developer",
    "description": "We are looking for...",
    "requirements": "5+ years Python experience...",
    "responsibilities": "Design and implement...",
    "skills": ["Python", "FastAPI", "PostgreSQL"],
    "location": "Remote",
    "experience_level": "Senior",
    "employment_type": "FullTime",
    "company_name": "TechCorp"
  }
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Job embedding created successfully",
  "data": {
    "job_id": 456,
    "embedding_id": "job_456"
  }
}
```

**Implementation Notes:**
- Combine job fields into a single text representation
- Generate embedding using same model as candidates
- Store in ChromaDB collection `jobs`
- Include metadata: job_id, required_skills, location, experience_level

---

### 8. Update Job Embedding

```http
PUT /api/embeddings/job/{job_id}
Content-Type: application/json
```

**Request:** Same as Create Job Embedding

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Job embedding updated successfully"
}
```

---

### 9. Delete Job Embedding

```http
DELETE /api/embeddings/job/{job_id}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Job embedding deleted successfully"
}
```

---

## Recommendations APIs

### 10. Get Job Recommendations for Candidate

Finds the most relevant jobs for a candidate using vector similarity search.

```http
GET /api/recommendations/jobs/{candidate_id}?limit=10
```

**Query Parameters:**
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| limit | int | 10 | Maximum number of recommendations |
| min_score | float | 0.5 | Minimum similarity score (0-1) |

**Response (200 OK):**
```json
{
  "success": true,
  "data": [
    {
      "job_id": 456,
      "title": "Senior Python Developer",
      "company_name": "TechCorp",
      "location": "Remote",
      "match_score": 0.92,
      "matching_skills": ["Python", "FastAPI", "PostgreSQL"],
      "match_reason": "Strong alignment with Python and API development experience"
    },
    {
      "job_id": 789,
      "title": "ML Engineer",
      "company_name": "AI Startup",
      "location": "New York",
      "match_score": 0.85,
      "matching_skills": ["Python", "Machine Learning"],
      "match_reason": "ML background matches job requirements"
    }
  ],
  "message": null
}
```

**Implementation Notes:**
- Query candidate embedding from ChromaDB
- Perform similarity search against jobs collection
- Use cosine similarity
- Optionally use LLM to generate match_reason

---

### 11. Get Candidate Rankings for Job

Finds and ranks the most suitable candidates for a job.

```http
GET /api/recommendations/candidates/{job_id}?limit=50
```

**Query Parameters:**
| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| limit | int | 50 | Maximum number of candidates to return |
| min_score | float | 0.3 | Minimum similarity score (0-1) |

**Response (200 OK):**
```json
{
  "success": true,
  "data": [
    {
      "candidate_id": 123,
      "score": 0.94,
      "reason": "Excellent match - 5+ years Python experience with FastAPI expertise"
    },
    {
      "candidate_id": 456,
      "score": 0.87,
      "reason": "Strong Python background, some ML experience"
    }
  ],
  "message": null
}
```

**Implementation Notes:**
- Query job embedding from ChromaDB
- Perform similarity search against candidates collection
- Return sorted by score descending
- Optionally use LLM to generate ranking reasons

---

## Job Scraping APIs

### 12. Get Scraped Jobs

Fetches jobs scraped from external job boards.

```http
GET /api/scraping/jobs?keyword=python&location=remote&limit=50
```

**Query Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| keyword | string | No | Job title or skill keyword |
| location | string | No | Job location filter |
| source | string | No | Source filter (linkedin, indeed, glassdoor) |
| limit | int | No | Maximum results (default: 50) |
| posted_within_days | int | No | Only jobs posted within N days |

**Response (200 OK):**
```json
{
  "success": true,
  "data": [
    {
      "external_job_id": "linkedin_12345",
      "title": "Python Developer",
      "description": "We are seeking a talented Python developer...",
      "requirements": "- 3+ years Python experience\n- FastAPI knowledge",
      "location": "Remote",
      "salary_range": "$120,000 - $150,000",
      "employment_type": "Full-time",
      "external_url": "https://linkedin.com/jobs/view/12345",
      "external_source": "linkedin",
      "company_name": "TechCorp Inc.",
      "posted_at": "2025-01-30T10:00:00Z",
      "skills": ["Python", "FastAPI", "PostgreSQL", "Docker"]
    }
  ],
  "total_count": 150,
  "message": null
}
```

**Implementation Notes:**
- Implement web scrapers for LinkedIn, Indeed, Glassdoor
- Use BeautifulSoup for HTML parsing
- Use Selenium for JavaScript-rendered content
- Store scraped jobs in cache (Redis) with TTL
- Extract skills using NLP/LLM

---

### 13. Trigger Job Scraping (Admin)

Manually triggers a scraping job.

```http
POST /api/scraping/trigger
Content-Type: application/json
```

**Request:**
```json
{
  "sources": ["linkedin", "indeed"],
  "keywords": ["python developer", "machine learning engineer"],
  "locations": ["remote", "new york"],
  "max_pages": 5
}
```

**Response (202 Accepted):**
```json
{
  "success": true,
  "message": "Scraping job queued",
  "data": {
    "job_id": "scrape_abc123",
    "estimated_time_minutes": 15
  }
}
```

---

## Interview AI APIs

### 14. Generate Interview Questions

Generates AI-powered interview questions based on job requirements.

```http
POST /api/interview/generate-questions
Content-Type: application/json
```

**Request:**
```json
{
  "job_id": 456,
  "job_title": "Senior Python Developer",
  "job_description": "We are looking for a Python developer...",
  "job_requirements": "5+ years Python experience...",
  "required_skills": ["Python", "FastAPI", "PostgreSQL"],
  "agent_type": "mixed",
  "question_count": 10,
  "difficulty_distribution": {
    "easy": 2,
    "medium": 5,
    "hard": 3
  }
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| job_id | long | Yes | Job ID for context |
| job_title | string | Yes | Job title |
| job_description | string | Yes | Full job description |
| job_requirements | string | No | Job requirements |
| required_skills | array | Yes | List of required skills |
| agent_type | string | Yes | "technical", "behavioral", or "mixed" |
| question_count | int | No | Number of questions (default: 10) |
| difficulty_distribution | object | No | Distribution of question difficulties |

**Response (200 OK):**
```json
{
  "success": true,
  "data": [
    {
      "question_text": "Explain the difference between list and tuple in Python. When would you use each?",
      "category": "Technical - Python Basics",
      "difficulty": "easy",
      "expected_answer": "Lists are mutable, tuples are immutable. Use tuples for fixed collections, lists for dynamic data.",
      "max_duration_seconds": 120
    },
    {
      "question_text": "Design a rate limiter for a REST API. What data structures and algorithms would you use?",
      "category": "Technical - System Design",
      "difficulty": "hard",
      "expected_answer": "Token bucket or sliding window algorithm. Use Redis for distributed rate limiting...",
      "max_duration_seconds": 300
    },
    {
      "question_text": "Tell me about a time when you had to deal with a difficult team member. How did you handle it?",
      "category": "Behavioral - Teamwork",
      "difficulty": "medium",
      "expected_answer": null,
      "max_duration_seconds": 180
    }
  ],
  "message": null
}
```

**Agent Types:**
| Type | Description |
|------|-------------|
| technical | Focus on coding, algorithms, system design |
| behavioral | Focus on soft skills, teamwork, leadership |
| mixed | Combination of both (recommended for most roles) |

**Implementation Notes:**
- Use LLM (GPT-4/Gemini) with role-specific prompts
- Include job context for relevance
- Generate expected answers for technical questions
- Vary difficulty levels
- Include time estimates per question

---

### 15. Evaluate Interview Answer

Evaluates a candidate's answer using AI.

```http
POST /api/interview/evaluate-answer
Content-Type: application/json
```

**Request:**
```json
{
  "question": "Explain the difference between list and tuple in Python.",
  "answer": "Lists are mutable sequences that can be modified after creation, while tuples are immutable. I would use tuples for fixed data like coordinates or database records, and lists when I need to add or remove items.",
  "expected_answer": "Lists are mutable, tuples are immutable. Use tuples for fixed collections, lists for dynamic data.",
  "category": "Technical - Python Basics",
  "difficulty": "easy",
  "job_context": "Senior Python Developer position requiring 5+ years experience"
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "data": {
    "score": 8.5,
    "feedback": "Excellent answer demonstrating clear understanding of Python data structures. The candidate correctly identified the mutability difference and provided practical use cases.",
    "strong_points": [
      "Correctly explained mutability difference",
      "Provided practical examples (coordinates, database records)",
      "Clear and concise communication"
    ],
    "improvement_areas": [
      "Could mention memory efficiency of tuples",
      "Could discuss hashability for dictionary keys"
    ],
    "category_score": {
      "accuracy": 9,
      "completeness": 7,
      "communication": 9
    }
  },
  "message": null
}
```

**Scoring Scale:** 0-10 where:
- 0-3: Poor/Incorrect
- 4-5: Below expectations
- 6-7: Meets expectations
- 8-9: Exceeds expectations
- 10: Exceptional

**Implementation Notes:**
- Use LLM with evaluation rubric
- Consider job level (junior vs senior)
- Provide actionable feedback
- Score multiple dimensions

---

### 16. Generate Interview Report

Generates a comprehensive interview report.

```http
POST /api/interview/generate-report
Content-Type: application/json
```

**Request:**
```json
{
  "session_id": 789,
  "candidate_name": "John Doe",
  "job_title": "Senior Python Developer",
  "interview_duration_minutes": 45,
  "qa_list": [
    {
      "question": "Explain Python decorators",
      "answer": "Decorators are functions that modify other functions...",
      "score": 8.0,
      "category": "Technical"
    },
    {
      "question": "Tell me about a challenging project",
      "answer": "I led a migration project...",
      "score": 9.0,
      "category": "Behavioral"
    }
  ],
  "overall_score": 8.2,
  "cheating_detected": false,
  "browser_events": {
    "tab_switches": 2,
    "focus_losses": 1
  }
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "data": {
    "report_html": "<html>...</html>",
    "report_markdown": "# Interview Report\n\n## Candidate: John Doe\n...",
    "summary": "John Doe demonstrated strong technical skills with an overall score of 8.2/10. He showed excellent understanding of Python concepts and provided thoughtful behavioral responses.",
    "recommendation": "STRONGLY_RECOMMEND",
    "key_strengths": [
      "Strong Python fundamentals",
      "Excellent communication skills",
      "Good problem-solving approach"
    ],
    "areas_for_development": [
      "System design experience could be deeper",
      "Could improve on time management during answers"
    ],
    "hiring_decision_factors": {
      "technical_competency": "High",
      "cultural_fit": "Strong",
      "experience_level": "Appropriate for role",
      "red_flags": "None identified"
    }
  },
  "message": null
}
```

**Recommendation Values:**
- `STRONGLY_RECOMMEND`
- `RECOMMEND`
- `NEUTRAL`
- `NOT_RECOMMEND`
- `STRONGLY_NOT_RECOMMEND`

---

## Audio Processing APIs

### 17. Transcribe Audio (Speech-to-Text)

Converts audio to text using Whisper.

```http
POST /api/audio/transcribe
Content-Type: multipart/form-data
```

**Request:**
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| file | File | Yes | Audio file (WAV, MP3, WebM, M4A) |
| language | string | No | Language code (default: "en") |

**Response (200 OK):**
```json
{
  "success": true,
  "data": {
    "text": "I believe the main difference between lists and tuples in Python is that lists are mutable while tuples are immutable.",
    "confidence": 0.95,
    "duration_seconds": 15.5,
    "language": "en"
  },
  "message": null
}
```

**Implementation Notes:**
- Use OpenAI Whisper API or local whisper model
- Support common audio formats
- Handle long audio by chunking
- Return confidence score

---

### 18. Text-to-Speech (Synthesize)

Converts text to speech for AI interviewer.

```http
POST /api/audio/synthesize
Content-Type: application/json
```

**Request:**
```json
{
  "text": "Please explain the difference between a list and a tuple in Python.",
  "voice": "professional_female",
  "speed": 1.0
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| text | string | Yes | Text to synthesize |
| voice | string | No | Voice type (default: "professional_female") |
| speed | float | No | Speech speed 0.5-2.0 (default: 1.0) |

**Response (200 OK):**
```
Content-Type: audio/mpeg
[Binary audio data]
```

**Voice Options:**
- `professional_female`
- `professional_male`
- `friendly_female`
- `friendly_male`

**Implementation Notes:**
- Use OpenAI TTS or gTTS
- Cache common phrases
- Return audio stream for real-time playback

---

## Error Handling

### Standard Error Response

All endpoints return errors in this format:

```json
{
  "success": false,
  "data": null,
  "message": "Detailed error message",
  "error_code": "VALIDATION_ERROR",
  "details": {
    "field": "resume_text",
    "issue": "Field is required"
  }
}
```

### HTTP Status Codes

| Code | Description |
|------|-------------|
| 200 | Success |
| 201 | Created |
| 202 | Accepted (async operation queued) |
| 400 | Bad Request (validation error) |
| 401 | Unauthorized |
| 404 | Not Found |
| 422 | Unprocessable Entity |
| 429 | Too Many Requests (rate limited) |
| 500 | Internal Server Error |
| 503 | Service Unavailable (LLM/ChromaDB down) |

### Error Codes

| Code | Description |
|------|-------------|
| VALIDATION_ERROR | Request validation failed |
| LLM_ERROR | LLM service error |
| CHROMADB_ERROR | Vector database error |
| FILE_PROCESSING_ERROR | File parsing error |
| AUDIO_PROCESSING_ERROR | Audio transcription/synthesis error |
| RATE_LIMIT_EXCEEDED | Too many requests |
| EMBEDDING_NOT_FOUND | Candidate/Job embedding not found |

---

## Models & Schemas

### Pydantic Models (Python)

```python
from pydantic import BaseModel, Field
from typing import List, Optional, Dict
from datetime import datetime
from enum import Enum

# Enums
class AgentType(str, Enum):
    TECHNICAL = "technical"
    BEHAVIORAL = "behavioral"
    MIXED = "mixed"

class Difficulty(str, Enum):
    EASY = "easy"
    MEDIUM = "medium"
    HARD = "hard"

class Recommendation(str, Enum):
    STRONGLY_RECOMMEND = "STRONGLY_RECOMMEND"
    RECOMMEND = "RECOMMEND"
    NEUTRAL = "NEUTRAL"
    NOT_RECOMMEND = "NOT_RECOMMEND"
    STRONGLY_NOT_RECOMMEND = "STRONGLY_NOT_RECOMMEND"

# CV Parsing
class ParsedExperience(BaseModel):
    job_title: Optional[str] = None
    company: Optional[str] = None
    start_date: Optional[str] = None
    end_date: Optional[str] = None
    description: Optional[str] = None

class ParsedEducation(BaseModel):
    degree: Optional[str] = None
    institution: Optional[str] = None
    graduation_year: Optional[str] = None
    field_of_study: Optional[str] = None

class ParsedCVResponse(BaseModel):
    full_name: Optional[str] = None
    email: Optional[str] = None
    phone: Optional[str] = None
    location: Optional[str] = None
    summary: Optional[str] = None
    skills: List[str] = []
    experience: List[ParsedExperience] = []
    education: List[ParsedEducation] = []
    confidence: float = 0.0

# ATS Scoring
class ATSScoreRequest(BaseModel):
    resume_text: str
    job_description: Optional[str] = None

class ATSScoreResponse(BaseModel):
    overall_score: int = Field(ge=0, le=100)
    is_friendly: bool
    recommendations: List[str] = []
    category_scores: Dict[str, int] = {}
    missing_keywords: List[str] = []
    matched_keywords: List[str] = []

# Embeddings
class CandidateProfileData(BaseModel):
    full_name: Optional[str] = None
    current_title: Optional[str] = None
    summary: Optional[str] = None
    skills: List[str] = []
    experience_years: Optional[int] = None
    location: Optional[str] = None
    education: Optional[str] = None
    resume_text: Optional[str] = None

class CreateCandidateEmbeddingRequest(BaseModel):
    candidate_id: int
    profile_data: CandidateProfileData

class JobData(BaseModel):
    title: str
    description: str
    requirements: Optional[str] = None
    responsibilities: Optional[str] = None
    skills: List[str] = []
    location: Optional[str] = None
    experience_level: Optional[str] = None
    employment_type: Optional[str] = None
    company_name: Optional[str] = None

class CreateJobEmbeddingRequest(BaseModel):
    job_id: int
    job_data: JobData

# Recommendations
class JobRecommendation(BaseModel):
    job_id: int
    title: str
    company_name: Optional[str] = None
    location: Optional[str] = None
    match_score: float = Field(ge=0, le=1)
    matching_skills: List[str] = []
    match_reason: Optional[str] = None

class CandidateRanking(BaseModel):
    candidate_id: int
    score: float = Field(ge=0, le=1)
    reason: Optional[str] = None

# Job Scraping
class ScrapedJob(BaseModel):
    external_job_id: str
    title: str
    description: str
    requirements: Optional[str] = None
    location: Optional[str] = None
    salary_range: Optional[str] = None
    employment_type: Optional[str] = None
    external_url: str
    external_source: str
    company_name: Optional[str] = None
    posted_at: datetime
    skills: List[str] = []

# Interview
class GenerateQuestionsRequest(BaseModel):
    job_id: int
    job_title: str
    job_description: str
    job_requirements: Optional[str] = None
    required_skills: List[str]
    agent_type: AgentType = AgentType.MIXED
    question_count: int = Field(default=10, ge=1, le=30)
    difficulty_distribution: Optional[Dict[str, int]] = None

class GeneratedQuestion(BaseModel):
    question_text: str
    category: Optional[str] = None
    difficulty: Difficulty = Difficulty.MEDIUM
    expected_answer: Optional[str] = None
    max_duration_seconds: int = 180

class EvaluateAnswerRequest(BaseModel):
    question: str
    answer: str
    expected_answer: Optional[str] = None
    category: Optional[str] = None
    difficulty: Optional[str] = None
    job_context: Optional[str] = None

class AnswerEvaluation(BaseModel):
    score: float = Field(ge=0, le=10)
    feedback: str
    strong_points: List[str] = []
    improvement_areas: List[str] = []
    category_score: Optional[Dict[str, int]] = None

class QuestionAnswerPair(BaseModel):
    question: str
    answer: str
    score: float
    category: Optional[str] = None

class GenerateReportRequest(BaseModel):
    session_id: int
    candidate_name: str
    job_title: str
    interview_duration_minutes: int
    qa_list: List[QuestionAnswerPair]
    overall_score: float
    cheating_detected: bool = False
    browser_events: Optional[Dict[str, int]] = None

class InterviewReport(BaseModel):
    report_html: str
    report_markdown: str
    summary: str
    recommendation: Recommendation
    key_strengths: List[str] = []
    areas_for_development: List[str] = []
    hiring_decision_factors: Dict[str, str] = {}

# Audio
class TranscriptionResponse(BaseModel):
    text: str
    confidence: float
    duration_seconds: float
    language: str

class SynthesizeRequest(BaseModel):
    text: str
    voice: str = "professional_female"
    speed: float = Field(default=1.0, ge=0.5, le=2.0)

# Generic Response Wrapper
class ApiResponse(BaseModel):
    success: bool
    data: Optional[any] = None
    message: Optional[str] = None
    error_code: Optional[str] = None
    details: Optional[Dict] = None
```

---

## Environment Variables

```env
# LLM Configuration
OPENAI_API_KEY=sk-xxx
LLM_MODEL=gpt-4-turbo-preview
EMBEDDING_MODEL=text-embedding-3-small

# ChromaDB
CHROMADB_HOST=localhost
CHROMADB_PORT=8001
CHROMADB_PERSIST_DIR=./chroma_data

# Audio Processing
WHISPER_MODEL=base
TTS_PROVIDER=openai

# API Security
API_KEY=your-internal-api-key
CORS_ORIGINS=http://localhost:3000,http://localhost:5000

# Rate Limiting
RATE_LIMIT_REQUESTS=100
RATE_LIMIT_PERIOD=60

# Scraping
SCRAPING_ENABLED=true
SCRAPING_INTERVAL_HOURS=6
```

---

## Directory Structure (Recommended)

```
ai_backend/
??? app/
?   ??? __init__.py
?   ??? main.py                 # FastAPI app entry
?   ??? config.py               # Settings & configuration
?   ??? dependencies.py         # Dependency injection
?   ?
?   ??? api/
?   ?   ??? __init__.py
?   ?   ??? v1/
?   ?   ?   ??? __init__.py
?   ?   ?   ??? cv.py           # CV parsing endpoints
?   ?   ?   ??? embeddings.py   # Embeddings CRUD
?   ?   ?   ??? recommendations.py
?   ?   ?   ??? interview.py    # Interview AI endpoints
?   ?   ?   ??? audio.py        # STT/TTS endpoints
?   ?   ?   ??? scraping.py     # Job scraping
?   ?   ??? router.py
?   ?
?   ??? services/
?   ?   ??? __init__.py
?   ?   ??? llm_service.py      # LLM interactions
?   ?   ??? embedding_service.py
?   ?   ??? chromadb_service.py
?   ?   ??? cv_parser.py
?   ?   ??? ats_scorer.py
?   ?   ??? question_generator.py
?   ?   ??? answer_evaluator.py
?   ?   ??? report_generator.py
?   ?   ??? audio_service.py
?   ?   ??? scraper_service.py
?   ?
?   ??? models/
?   ?   ??? __init__.py
?   ?   ??? schemas.py          # Pydantic models
?   ?   ??? enums.py
?   ?
?   ??? utils/
?       ??? __init__.py
?       ??? prompts.py          # LLM prompts
?       ??? file_utils.py
?
??? scrapers/
?   ??? __init__.py
?   ??? base_scraper.py
?   ??? linkedin_scraper.py
?   ??? indeed_scraper.py
?   ??? glassdoor_scraper.py
?
??? tests/
??? requirements.txt
??? Dockerfile
??? docker-compose.yml
```

---

## Quick Start

```bash
# Install dependencies
pip install fastapi uvicorn openai chromadb python-multipart pydantic

# Run the server
uvicorn app.main:app --host 0.0.0.0 --port 8000 --reload
```

---

## Integration with .NET Backend

The .NET backend calls these APIs using `HttpClient`. Example configuration in `appsettings.json`:

```json
{
  "AIBackend": {
    "BaseUrl": "http://localhost:8000",
    "ApiKey": "your-internal-api-key",
    "TimeoutSeconds": 300
  }
}
```

All endpoints are consumed by `AIBackendService.cs` in the .NET project.

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0.0 | 2025-02-01 | Initial API specification |

---

*Document generated for JobLens AI Recruitment Platform*
