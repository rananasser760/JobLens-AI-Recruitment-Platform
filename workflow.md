==================================================
SYSTEM OVERVIEW
==================================================

This platform is an AI-powered recruiting and hiring system.

It supports two primary user roles:
1. Candidate / Client
2. Recruiter / Company

The system has these core capabilities:
- Candidate registration and profile management
- CV upload and CV parsing
- Job creation and job scraping
- Embedding generation for candidates and jobs
- Job recommendations for candidates
- Candidate recommendations for recruiters
- ATS scoring
- AI interview simulation
- Video/audio-based cheating detection during interviews
- Interview transcript and report generation
- Background jobs for scraping, cleanup, and recommendation refresh

==================================================
HIGH-LEVEL ARCHITECTURE
==================================================

The architecture is:

Frontend (Angular)
        |
        v
ASP.NET Core Backend
        |
        +-----------------------------+
        |                             |
        v                             v
   Sql Server Server                  FastAPI AI Backend
        |                             |
        |                             v
        |                         ChromaDB
        |
        v
Business data, applications, users, jobs, interviews, reports, cached recommendations

The .NET backend is the single source of truth for business operations and orchestration.
The FastAPI backend is a specialized AI service layer.

==================================================
DATABASE STRATEGY
==================================================

There are two databases and they have different responsibilities.

1) Sql Server Server
This stores all traditional application data and must be the main persistent store for:
- users
- roles
- candidates
- recruiters
- companies
- jobs
- job applications
- interview sessions
- interview answers
- interview transcripts
- cheating events
- ATS scores
- final reports
- recommendation cache
- job metadata
- CV metadata
- audit/log data if needed
- references to AI objects such as embedding IDs or vector IDs

2) ChromaDB
This stores only AI/vector-related data:
- candidate embeddings
- job embeddings
- parsed CV vector representations
- scraped job vector representations
- similarity search data
- metadata needed for vector search

ChromaDB must not be used for business logic or transactional application data.

Important:
- Every candidate/job may exist in Sql Server as the canonical business record.
- The corresponding embedding exists in ChromaDB.
- Sql Server should store the reference to the embedding/vector record so the two systems stay connected.
- Embeddings must not be regenerated every time. They should be generated once and reused through stored IDs/references.

==================================================
CORE DESIGN PRINCIPLE
==================================================

The system should work like this:

- Sql Server stores the full business object.
- ChromaDB stores the vector/embedding representation.
- FastAPI is responsible for generating and querying embeddings.
- ASP.NET Core handles orchestration, persistence, authorization, and business workflows.
- The frontend only talks to .NET.
- All AI features are exposed to the frontend only indirectly through .NET endpoints.

==================================================
USER ROLES AND PERMISSIONS
==================================================

1) Candidate / Client
Can:
- register and log in
- upload CV
- edit profile
- view parsed CV data
- search jobs
- receive recommendations
- apply to jobs
- join interviews
- view interview reports and feedback
- track application status

2) Recruiter / Company
Can:
- register and log in
- create and manage jobs
- view applicants
- see recommended candidates
- invite candidates to interviews
- view interview reports
- see top matching candidates
- see ATS-related insights

3) Admin or system operator if needed
Can:
- manage platform settings
- monitor jobs
- monitor scraping and AI operations
- inspect logs and errors
- view system metrics

==================================================
MAIN BACKEND RESPONSIBILITIES (.NET)
==================================================

The ASP.NET Core backend is the main orchestrator.

It must handle:
- authentication and authorization
- user management
- role-based access control
- job management
- application management
- interview session management
- report storage and retrieval
- recommendation caching
- storage of canonical business data
- web socket/session coordination for interviews
- background jobs
- calling AI backend APIs through a clean integration layer
- error handling and resilience around AI calls

The .NET backend must expose all endpoints used by the frontend.

The frontend must never know that FastAPI exists.

==================================================
FASTAPI AI BACKEND RESPONSIBILITIES
==================================================

The AI backend handles all heavy AI/ML-related features, such as:
- CV parsing
- embedding generation
- job embeddings
- candidate embeddings
- job scraping
- recommendation similarity search
- ATS scoring
- interview simulation using LLM
- speech-to-text
- text-to-speech
- cheating detection from video frames
- interview analysis
- report generation

The FastAPI backend is a specialized AI engine and should not be exposed directly to the frontend.

==================================================
EMBEDDING AND VECTOR DATA LIFECYCLE
==================================================

This is a critical part of the system.

Candidate flow:
1. Candidate registers in the .NET backend.
2. Candidate uploads CV.
3. .NET stores the CV file and candidate metadata in Sql Server.
4. .NET calls the FastAPI CV parsing API.
5. FastAPI parses the CV.
6. FastAPI generates the candidate embedding.
7. FastAPI stores the embedding in ChromaDB.
8. FastAPI returns parsed structured data and an embedding/vector identifier.
9. .NET stores parsed data and embedding reference in Sql Server.
10. Future recommendation requests reuse the stored embedding reference.

Job flow:
1. Recruiter creates a job in the .NET backend.
2. .NET stores the job in Sql Server.
3. .NET calls FastAPI to generate the job embedding.
4. FastAPI stores the job embedding in ChromaDB.
5. FastAPI returns a vector/embedding identifier.
6. .NET stores the embedding reference in Sql Server.

Important:
- Do not regenerate embeddings repeatedly.
- Do not store embeddings in Sql Server as large binary data unless absolutely necessary.
- Always store and reuse references/IDs.
- The embedding generation process should be triggered only when the source content changes.

==================================================
JOB SCRAPING WORKFLOW
==================================================

The system must support periodic job scraping.

Scraping flow:
1. A background job in .NET runs on a schedule or manually by admin .
2. .NET calls the FastAPI scraping endpoint.
3. FastAPI scrapes jobs from external sources.
4. FastAPI may also generate embeddings for scraped jobs and store them in ChromaDB.
5. FastAPI returns job data and vector references.
6. .NET stores the scraped jobs in Sql Server.
7. .NET avoids duplicates using source URL, job external ID, title, company, and post date.
8. Scraped jobs are treated as external opportunities.
9. A candidate applying to a scraped job may be redirected to the original external application URL if the system is not hosting the application itself.

The scraping background job should:
- run periodically
- be configurable by interval
- deduplicate results
- track source and timestamp
- update existing records if necessary
- store scraped data in Sql Server
- preserve AI/vector references in ChromaDB

==================================================
OLD JOB CLEANUP WORKFLOW
==================================================

Jobs lose relevance over time, so a cleanup workflow is required.

This background job should:
- run periodically
- inspect job age
- use job post date and internal created date
- remove, archive, or deactivate stale jobs
- optionally remove jobs that are no longer relevant or already expired
- keep business rules configurable by days or date thresholds

The cleanup process must ensure:
- old jobs do not clutter search results
- recommendations remain fresh
- database size stays manageable

==================================================
RECOMMENDATION WORKFLOW
==================================================

Recommendations are a core system feature.

There are two recommendation directions:

1) Candidate → Jobs
The system recommends jobs to the candidate based on:
- candidate profile
- candidate embedding
- CV embedding
- skills
- experience
- ATS scoring
- job similarity

2) Job → Candidates
The system recommends candidates to recruiters based on:
- job description
- job embedding
- skills match
- experience match
- similarity in ChromaDB
- ATS scoring
- candidate profile data

Recommendation flow:
1. .NET requests recommendations from FastAPI.
2. FastAPI queries ChromaDB for nearest vector matches.
3. FastAPI returns ranked results.
4. .NET stores top recommendations in Sql Server for caching.
5. .NET uses cached recommendations for fast dashboard loading.
6. Background jobs can refresh recommendations after new scraping or major profile/job changes.

Recommendations should be refreshed:
- after job scraping
- after candidate CV upload/update
- after job creation/update
- periodically on a schedule

Recommendations should be cached and should not force full recomputation on every frontend request.

==================================================
ATS SCORING WORKFLOW
==================================================

When a candidate applies to a job, the system should perform ATS scoring.

Flow:
1. Candidate applies to a job.
2. .NET sends the candidate CV data and job description to FastAPI.
3. FastAPI computes ATS score.
4. FastAPI may also return missing skills, matching summary, and improvement suggestions.
5. .NET stores the score in Sql Server.
6. The score is used to decide whether the candidate passes first filtration.
7. If the candidate passes, the interview stage can begin.

ATS scoring should be part of the filtering workflow for both candidate and recruiter views.

==================================================
AI INTERVIEW WORKFLOW
==================================================

This is one of the most important workflows in the system.

The interview should be simulated by AI and coordinated by .NET.

Interview inputs from the frontend not based on the rule may include:
- job description 
- required skills
- candidate CV
- number of questions
- interview settings
- interview mode
- session identifiers

Real-time interview flow:
1. Candidate opens the interview page in the frontend.
2. Frontend connects only to .NET, usually via WebSocket or a real-time mechanism.
3. The frontend streams audio/video chunks to the .NET backend.
4. The .NET backend orchestrates the session and forwards the necessary parts to the FastAPI AI backend.
5. FastAPI runs the AI interview simulation.
6. FastAPI may use:
   - speech-to-text
   - text-to-speech
   - LLM-generated follow-up questions
   - answer evaluation
   - scoring
7. The AI interview continues dynamically based on user responses.
8. .NET stores all important data in Sql Server:
   - transcript
   - questions
   - candidate answers
   - session status
   - timestamps
   - scores
   - final evaluation
9. At the end, .NET requests or receives a final report from FastAPI.
10. The report becomes available to the candidate and recruiter.

The interview experience should support:
- live question/answer flow
- session tracking
- transcript logging
- answer storage
- final scoring
- post-interview report generation

==================================================
CHEATING DETECTION WORKFLOW
==================================================

During interviews, the system must detect cheating or suspicious behavior using video analysis.

Examples of cheating events:
- looking away too often
- suspicious eye movement
- multiple faces in the frame
- phone usage
- another person entering the frame
- unusual behavior patterns
- camera off or blocked
- abnormal posture or repeated suspicious movements

Cheating detection flow:
1. Frontend streams video or frame data during interview.
2. .NET forwards the relevant data to FastAPI.
3. FastAPI uses computer vision / media processing to analyze the frames.
4. FastAPI returns cheating events or suspicious activity events.
5. .NET stores these events in Sql Server.
6. These events appear later in the report and the recruiter dashboard.

Important:
- Cheating events must be persisted.
- Summaries should be available in reports.
- Both raw event logs and normalized event summaries can be stored.

==================================================
REPORT GENERATION WORKFLOW
==================================================

After the interview is completed, the system must generate a comprehensive report.

The report should include:
- candidate identity
- job information
- interview session details
- transcript
- candidate answers
- AI-generated questions
- skills evaluation
- ATS score
- cheating events
- suspicious behavior summary
- final score
- recruiter-facing evaluation
- candidate-facing feedback if allowed

Workflow:
1. Interview ends.
2. FastAPI compiles final AI evaluation.
3. .NET stores the final report in Sql Server.
4. The report becomes available in the candidate dashboard and recruiter dashboard.
5. If needed, the report can also be exported or viewed in a web page without PDF generation.
6. PDF export is optional, but the system should support a report-friendly structure.

==================================================
RECOMMENDATION AND SEARCH EXPERIENCE
==================================================

Candidate dashboard:
- show recommended jobs
- show application history
- show ATS insights
- show interview history
- show reports

Recruiter dashboard:
- show recommended candidates
- show applicants per job
- show job performance
- show interview reports
- show top candidates for each job

Search and filter:
- jobs can be searched and filtered
- candidates can be searched and filtered
- recommendations can be ranked and refreshed

The system should feel intelligent, not just database-driven.

==================================================
BACKGROUND JOBS
==================================================

The system requires scheduled or periodic background jobs.

At minimum implement:
1. Job scraping job
2. Old job cleanup job
3. Recommendation refresh job
4. Optional embedding refresh job if source content changes
5. Optional cache cleanup job

Background jobs should be implemented using a suitable .NET background execution strategy such as:
- Hangfire
- Quartz.NET
- BackgroundService
- or an equivalent production-ready mechanism

These jobs should:
- run on schedules
- be configurable
- be retryable
- log failures
- avoid duplicate work
- update Sql Server after calling FastAPI
- trigger recommendation refresh when appropriate



==================================================
PERFORMANCE REQUIREMENTS
==================================================

The solution must be high-performance and scalable.

Important performance rules:
- do not regenerate embeddings unnecessarily
- cache recommendation results
- store references to ChromaDB entries in Sql Server
- batch work when possible
- use async processing
- keep the frontend decoupled from the AI backend
- minimize duplicate calls to FastAPI
- use background jobs for periodic or heavy tasks
- keep Sql Server and ChromaDB responsibilities clearly separated

==================================================
MAPPING BETWEEN Sql Server AND CHROMADB
==================================================

The system must maintain a clean relationship between records in Sql Server and vector objects in ChromaDB.

For example:
- candidate record in Sql Server → candidate_embedding_id in ChromaDB
- job record in Sql Server → job_embedding_id in ChromaDB
- parsed CV in Sql Server → parsed_vector_reference in ChromaDB
- scraped job in Sql Server → scraped_embedding_reference in ChromaDB

The Sql Server record should store:
- the object ID
- the embedding/vector reference
- timestamps
- source metadata
- status fields
- any business-related state

ChromaDB should store:
- vector data
- similarity metadata
- AI search payloads
