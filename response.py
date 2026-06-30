import os
import openai
from typing import List, Dict
import json
import re

# --- CONFIGURATION ---
OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY", "")
OPENROUTER_MODEL_NAME = os.getenv(
    "JOBLENS_INTERVIEW_MODEL",
    "google/gemini-2.5-flash:free",
)


class InterviewProviderError(RuntimeError):
    def __init__(self, code: str, message: str, retryable: bool) -> None:
        super().__init__(message)
        self.code = code
        self.retryable = retryable

_openrouter_client = None

def _get_openrouter_client():
    global _openrouter_client
    if _openrouter_client is not None:
        return _openrouter_client
    
    api_key = os.getenv("OPENROUTER_API_KEY", "").strip()
    if api_key.startswith('"') and api_key.endswith('"'):
        api_key = api_key[1:-1]
    if api_key.startswith("'") and api_key.endswith("'"):
        api_key = api_key[1:-1]

    if not api_key:
        return None
        
    _openrouter_client = openai.OpenAI(
        base_url="https://openrouter.ai/api/v1",
        api_key=api_key,
    )
    return _openrouter_client


def _raise_provider_error(exc: Exception, operation: str) -> None:
    status_code = getattr(exc, "status_code", None)
    message = str(exc)
    normalized = message.lower()

    if status_code == 429 or "error code: 429" in normalized or "rate limit" in normalized:
        raise InterviewProviderError(
            "ProviderRateLimited",
            "AI provider rate limit reached. Please retry shortly.",
            True,
        )

    if (
        status_code == 402
        or "error code: 402" in normalized
        or "spend limit" in normalized
        or "payment" in normalized
    ):
        raise InterviewProviderError(
            "ProviderPaymentRequired",
            "AI provider spending or payment limit reached.",
            False,
        )

    if "timeout" in normalized:
        raise InterviewProviderError(
            "ProviderTimeout",
            f"AI provider timed out while trying to {operation}.",
            True,
        )

    if "connection" in normalized or "temporarily" in normalized or "upstream" in normalized:
        raise InterviewProviderError(
            "ProviderUnavailable",
            f"AI provider is temporarily unavailable while trying to {operation}.",
            True,
        )

    raise InterviewProviderError(
        "ProviderUnexpectedError",
        f"Unexpected AI provider error while trying to {operation}.",
        False,
    )

def generate_interview_response(
    current_transcript: str,
    chat_history: List[Dict[str, str]],
    cv_text: str,
    job_description: str
) -> str:
    """
    Generates a follow-up interview question using OpenRouter compatible LLM.
    Now dynamically uses the Job Description to steer the questions.
    """

    system_prompt = f"""
    You are a professional, encouraging, yet thorough technical recruiter.
    You are interviewing a candidate for a specific role.

    Job Description / Role Requirements:
    {job_description}

    Context from Candidate's CV:
    {cv_text}

    Your Goal:
    1. Analyze the candidate's last response.
    2. If the response is vague, ask for clarification.
    3. If the response is good, move to the next relevant topic based on the Job Description and CV. Ensure your questions evaluate if they are a good fit for this specific role.
    4. Keep your responses concise (under 2-3 sentences) so they are easy to listen to via audio.
    5. CRITICAL: Do NOT use markdown formatting (like **bold** or *italics*). Do not use asterisks. Output plain text only.
    """

    messages = [{"role": "system", "content": system_prompt}]
    for msg in chat_history:
        messages.append({"role": msg["role"], "content": msg["content"]})
    messages.append({"role": "user", "content": current_transcript})

    client = _get_openrouter_client()
    if client is None:
        raise InterviewProviderError(
            "ProviderNotConfigured",
            "Interview model is not configured. Please set OPENROUTER_API_KEY.",
            False,
        )

    try:
        response = client.chat.completions.create(
            model=OPENROUTER_MODEL_NAME,
            messages=messages,
            temperature=0.7,
            max_tokens=100,
        )
        
        # Keep semantic content intact while removing markdown-only wrappers.
        raw_content = response.choices[0].message.content or ""
        clean_content = re.sub(r"^\s*[#]+\s*", "", raw_content, flags=re.MULTILINE)
        clean_content = clean_content.replace("**", "").strip()
        return clean_content

    except Exception as e:
        print(f"Error generating response with OpenRouter: {e}")
        _raise_provider_error(e, "generate interview response")

def generate_interview_summary(
    chat_history: List[Dict[str, str]],
    cv_text: str,
    job_description: str,
    criteria: str 
) -> dict: 
    """
    Evaluates the completed interview based on custom criteria and job description.
    """
    
    transcript_lines = []
    for msg in chat_history:
        role = "Interviewer" if msg["role"] == "assistant" else "Candidate"
        transcript_lines.append(f"{role}: {msg['content']}")
    
    full_transcript = "\n\n".join(transcript_lines)

    user_prompt = f"""
    You are an expert technical recruiter evaluating a candidate after a brief audio interview.

    Job Description:
    {job_description}

    Context from Candidate's CV:
    {cv_text}

    Below is the full transcript of the interview:
    --------------------------------------------------
    {full_transcript}
    --------------------------------------------------

    Your Goal: Review the transcript and evaluate the candidate strictly based on these criteria provided by the hiring manager, factoring in how well they fit the Job Description:
    "{criteria}"

    CRITICAL INSTRUCTION:
    You MUST output your evaluation strictly as a raw JSON object. Do not use Markdown formatting, do not use asterisks, and do not add any conversational filler text. 
    Use the exact following JSON structure:
    {{
        "review": "A brief paragraph summarizing their performance against the job description and criteria.",
        "strengths": ["strength 1", "strength 2"],
        "weaknesses": ["weakness 1", "weakness 2"],
        "score": <integer between 0 and 100>,
        "recommendation": "A brief final recommendation."
    }}
    """

    messages = [{"role": "user", "content": user_prompt}]

    client = _get_openrouter_client()
    if client is None:
        raise InterviewProviderError(
            "ProviderNotConfigured",
            "Interview model is not configured. Please set OPENROUTER_API_KEY.",
            False,
        )

    try:
        response = client.chat.completions.create(
            model=OPENROUTER_MODEL_NAME,
            messages=messages,
            temperature=0.3, 
            max_tokens=600,
            response_format={"type": "json_object"}
        )
        
        raw_content = response.choices[0].message.content.strip()
        
        if raw_content.startswith("```json"):
            raw_content = raw_content[7:]
        if raw_content.endswith("```"):
            raw_content = raw_content[:-3]
            
        return json.loads(raw_content.strip())

    except json.JSONDecodeError as e:
        print(f"Failed to parse JSON from LLM: {raw_content}")
        raise InterviewProviderError(
            "ProviderInvalidResponse",
            "AI provider returned malformed summary JSON.",
            False,
        ) from e
    except Exception as e:
        print(f"Error generating summary: {e}")
        _raise_provider_error(e, "generate interview summary")