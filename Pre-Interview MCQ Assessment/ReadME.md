## Pre-Interview MCQ Assessment

To enhance candidate evaluation before the live AI interview, JobLens includes an AI-powered Pre-Interview MCQ Assessment module.

### Overview

Before starting the interview, the system automatically generates a short multiple-choice assessment tailored to the specific job role.

The assessment is dynamically created using the selected Job Description and Recruiter Criteria, ensuring that questions focus on the most relevant technical and professional skills required for the position.

### Workflow

1. Candidate applies for a job.
2. The system retrieves the corresponding Job Description.
3. Required skills are extracted automatically.
4. An LLM generates 10 role-specific multiple-choice questions.
5. The candidate completes the assessment.
6. The system calculates:
   - MCQ Score
   - Correct Answer Count
   - Weak Skill Areas
   - Strong Skill Areas
7. Results are stored and forwarded to the AI Interview Agent.
8. The interview adapts its questions based on detected weak skills.
9. MCQ performance contributes to the final hiring evaluation report.

### Key Features

- Dynamic question generation from Job Descriptions
- Role-specific assessments for any job category
- Automatic skill extraction
- Adaptive interview preparation
- Weak-skill identification
- Strong-skill identification
- Automated scoring
- Integrated with the AI Interviewing Agent
- Fully customizable through recruiter criteria

### Example Generated Question

**Question**

Which HTTP method is typically used to update an existing resource in a REST API?

**Options**
- GET
- POST
- PUT
- DELETE

**Correct Answer**
- PUT

### Assessment Output

```json
{
  "score": 80,
  "correct_answers": 8,
  "total_questions": 10,
  "weak_skills": [
    "Docker",
    "SQL"
  ],
  "strong_skills": [
    "Python",
    "FastAPI"
  ]
}
```

### .ENV 
```
DATABASE_URL=sqlite:///joblens_mcq.db
OPENROUTER_API_KEY=your_openrouter_api_key_here
```

### Interview Adaptation

The generated assessment results are used to guide the AI Interview Agent.

For example, if a candidate performs poorly in Docker-related questions, the interview system may generate additional Docker-focused questions to verify practical knowledge and identify skill gaps.

### Benefits
- Filters candidates before the interview stage
- Reduces recruiter screening effor
- Provides objective skill measurements 
- Enables adaptive and personalized interviews
- Improves hiring decision accuracy
- Creates a more comprehensive candidate evaluation pipeline
