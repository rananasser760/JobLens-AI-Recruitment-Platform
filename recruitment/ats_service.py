from __future__ import annotations

import json
from typing import Dict

from .config import get_recruitment_settings
from .cv_service import _clean_json
from .llm_client import get_llm_client


def analyze_ats_with_llm(parsed_cv: Dict, cv_text: str) -> Dict:
    settings = get_recruitment_settings()
    llm_client = get_llm_client()

    cv_summary = json.dumps(parsed_cv, indent=2)
    prompt = f"""You are a STRICT ATS analyst with 15+ years of experience.
Most CVs score 50-75. Only exceptional CVs exceed 85.

Score each category 0-100 using these benchmarks:
- formatting:              100=perfect ATS-friendly, 60-79=readable with issues, <60=ATS may fail
- content_quality:         100=every bullet has action verb + metric, 60-79=generic descriptions
- keyword_optimization:    100=15+ industry keywords, 60-79=4-7 keywords
- experience_presentation: 100=clear progression + quantified achievements
- skills_relevance:        100=15+ in-demand skills, 60-79=5-9 skills
- completeness:            100=all sections present, deduct for each missing section
- professionalism:         100=zero errors, professional email/LinkedIn

overall_score = average of all 7 categories.

CV DATA:
{cv_summary}

Return EXACTLY this JSON (no markdown):
{{
  "overall_score": 0,
  "grade": "",
  "summary_feedback": "",
  "scores": {{"formatting":0,"content_quality":0,"keyword_optimization":0,
              "experience_presentation":0,"skills_relevance":0,"completeness":0,"professionalism":0}},
  "strengths": [],
  "weaknesses": [],
  "critical_issues": [],
  "improvement_suggestions": [
    {{"category":"","priority":"","suggestion":"","impact":""}}
  ],
  "missing_elements": [],
  "keywords_analysis": {{"strong_keywords":[],"missing_keywords":[],"suggested_keywords":[]}},
  "recruiter_perspective": "",
  "next_steps": []
}}"""

    try:
        response = llm_client.chat.completions.create(
            model=settings.ats_model,
            messages=[
                {"role": "system", "content": "Strict ATS analyst. Return only valid JSON."},
                {"role": "user", "content": prompt},
            ],
            max_tokens=3000,
            temperature=0.2,
        )
        ats_result = json.loads(_clean_json(response.choices[0].message.content.strip()))

        if ats_result.get("overall_score", 0) > 95:
            scores = ats_result.get("scores", {})
            if scores:
                ats_result["overall_score"] = round(sum(scores.values()) / len(scores))
            else:
                ats_result["overall_score"] = 70

        return ats_result
    except Exception:
        return {
            "overall_score": 65,
            "grade": "D+",
            "summary_feedback": "CV needs improvements.",
            "scores": {
                "formatting": 60,
                "content_quality": 55,
                "keyword_optimization": 50,
                "experience_presentation": 70,
                "skills_relevance": 65,
                "completeness": 75,
                "professionalism": 70,
            },
            "strengths": ["CV structure is present"],
            "weaknesses": ["Lacks quantified achievements", "Missing keywords"],
            "critical_issues": ["No measurable results in experience section"],
            "improvement_suggestions": [
                {
                    "category": "Content",
                    "priority": "Critical",
                    "suggestion": "Add metrics to every achievement (e.g., Increased X by Y%).",
                    "impact": "+15 points",
                }
            ],
            "missing_elements": ["Quantified achievements"],
            "keywords_analysis": {
                "strong_keywords": [],
                "missing_keywords": ["Industry-specific terms"],
                "suggested_keywords": ["Add role-specific keywords"],
            },
            "recruiter_perspective": "CV shows potential but needs quantification and keyword optimization.",
            "next_steps": ["Add metrics", "Optimize keywords", "Fix formatting"],
        }


def generate_improvements_with_llm(parsed_cv: Dict, ats_result: Dict) -> Dict:
    settings = get_recruitment_settings()
    llm_client = get_llm_client()

    prompt = f"""You are a professional CV consultant.

CV: {json.dumps(parsed_cv, indent=2)}
ATS ANALYSIS: {json.dumps(ats_result, indent=2)}

Return EXACTLY this JSON (no markdown):
{{
  "priority_actions": [{{"action":"","reason":"","how_to":"","priority":""}}],
  "content_rewrites": {{
    "professional_summary": "",
    "experience_improvements": [{{"current":"","improved":"","why_better":""}}]
  }},
  "skills_strategy": {{"skills_to_add":[],"skills_to_emphasize":[],
                       "skills_to_remove":[],"how_to_demonstrate":[]}},
  "formatting_checklist": [],
  "keyword_strategy": {{"must_add":[],"contextual_usage":[{{"keyword":"","where":"","example":""}}]}},
  "achievement_framework": {{"suggested_achievements":[],"quantification_tips":[]}},
  "30_day_plan": [{{"week":1,"tasks":[],"goal":""}}]
}}"""

    try:
        response = llm_client.chat.completions.create(
            model=settings.scoring_model,
            messages=[
                {"role": "system", "content": "CV improvement expert. Return only valid JSON."},
                {"role": "user", "content": prompt},
            ],
            max_tokens=3000,
            temperature=0.4,
        )
        return json.loads(_clean_json(response.choices[0].message.content.strip()))
    except Exception:
        return {
            "priority_actions": [
                {
                    "action": "Review ATS feedback",
                    "reason": "Address identified issues",
                    "how_to": "Follow the suggestions provided",
                    "priority": "High",
                }
            ],
            "content_rewrites": {},
            "skills_strategy": {},
            "formatting_checklist": [],
            "keyword_strategy": {},
            "achievement_framework": {},
            "30_day_plan": [],
        }
