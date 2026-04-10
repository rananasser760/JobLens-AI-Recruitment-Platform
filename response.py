import os
import openai
from typing import List, Dict
import json
import re

# --- CONFIGURATION ---
OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY", "")
OPENROUTER_MODEL_NAME = os.getenv(
    "JOBLENS_INTERVIEW_MODEL",
    "mistralai/mistral-small-3.2-24b-instruct",
)

# Configure the OpenAI client to use OpenRouter API
openrouter_client = (
    openai.OpenAI(
        base_url="https://openrouter.ai/api/v1",
        api_key=OPENROUTER_API_KEY,
    )
    if OPENROUTER_API_KEY
    else None
)

def generate_interview_response(
    current_transcript: str,
    chat_history: List[Dict[str, str]],
    cv_text: str
) -> str:
    """
    Generates a follow-up interview question using OpenRouter compatible LLM.
    """

    # 1. Construct the System Prompt (Context)
    # UPDATED: Added instruction #5 to forbid markdown/asterisks
    system_prompt = f"""
    You are a professional, encouraging, yet thorough technical recruiter.
    You are interviewing a candidate.

    Context from Candidate's CV:
    {cv_text}

    Your Goal:
    1. Analyze the candidate's last response.
    2. If the response is vague, ask for clarification.
    3. If the response is good, move to the next relevant topic based on the CV.
    4. Keep your responses concise (under 2-3 sentences) so they are easy to listen to via audio.
    5. CRITICAL: Do NOT use markdown formatting (like **bold** or *italics*). Do not use asterisks. Output plain text only.
    """

    # 2. Build the Chat History for OpenRouter format
    messages = [{"role": "system", "content": system_prompt}]
    for msg in chat_history:
        messages.append({"role": msg["role"], "content": msg["content"]})
    messages.append({"role": "user", "content": current_transcript})

    if openrouter_client is None:
        return "Interview model is not configured. Please set OPENROUTER_API_KEY."

    try:
        response = openrouter_client.chat.completions.create(
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
        return "I'm sorry, I'm having trouble connecting to my brain right now. Could you repeat that?"

def generate_interview_summary(
    chat_history: List[Dict[str, str]],
    cv_text: str,
    criteria: str 
) -> dict: 
    """
    Evaluates the completed interview based on custom criteria and returns a structured JSON object.
    """
    
    transcript_lines = []
    for msg in chat_history:
        role = "Interviewer" if msg["role"] == "assistant" else "Candidate"
        transcript_lines.append(f"{role}: {msg['content']}")
    
    full_transcript = "\n\n".join(transcript_lines)

    # --- NEW: Strict prompt asking for JSON structure and using custom criteria ---
    user_prompt = f"""
    You are an expert technical recruiter evaluating a candidate after a brief audio interview.

    Context from Candidate's CV:
    {cv_text}

    Below is the full transcript of the interview:
    --------------------------------------------------
    {full_transcript}
    --------------------------------------------------

    Your Goal: Review the transcript and evaluate the candidate strictly based on these criteria provided by the hiring manager:
    "{criteria}"

    CRITICAL INSTRUCTION:
    You MUST output your evaluation strictly as a raw JSON object. Do not use Markdown formatting, do not use asterisks, and do not add any conversational filler text. 
    Use the exact following JSON structure:
    {{
        "review": "A brief paragraph summarizing their performance based on the specific criteria.",
        "strengths": ["strength 1", "strength 2"],
        "weaknesses": ["weakness 1", "weakness 2"],
        "score": <integer between 0 and 100>,
        "recommendation": "A brief final recommendation."
    }}
    """

    messages = [{"role": "user", "content": user_prompt}]

    if openrouter_client is None:
        return {
            "review": "Interview model is not configured.",
            "strengths": [],
            "weaknesses": [],
            "score": 0,
            "recommendation": "Set OPENROUTER_API_KEY to enable summary generation.",
        }

    try:
        response = openrouter_client.chat.completions.create(
            model=OPENROUTER_MODEL_NAME,
            messages=messages,
            temperature=0.3, # Lowered temperature makes the AI strictly follow JSON formatting
            max_tokens=600,
            response_format={"type": "json_object"} # Hints to the API to enforce JSON
        )
        
        raw_content = response.choices[0].message.content.strip()
        
        # Failsafe: Strip markdown code blocks if the model stubbornly includes them
        if raw_content.startswith("```json"):
            raw_content = raw_content[7:]
        if raw_content.endswith("```"):
            raw_content = raw_content[:-3]
            
        # Parse the string into an actual Python dictionary
        return json.loads(raw_content.strip())

    except json.JSONDecodeError as e:
        print(f"Failed to parse JSON from LLM: {raw_content}")
        return {
            "review": "Error parsing the AI's response.", 
            "strengths": [], 
            "weaknesses": [], 
            "score": 0, 
            "recommendation": "N/A"
        }
    except Exception as e:
        print(f"Error generating summary: {e}")
        return {"error": "Error generating interview summary."}