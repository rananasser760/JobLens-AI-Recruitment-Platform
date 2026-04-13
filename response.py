import os
import openai
from typing import List, Dict
import json

# --- CONFIGURATION ---
# ⚠️ REPLACE THIS WITH YOUR REAL KEY ⚠️
OPENROUTER_API_KEY = "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" 
OPENROUTER_MODEL_NAME = "mistralai/mistral-small-3.2-24b-instruct" 

# Configure the OpenAI client to use OpenRouter API
openrouter_client = openai.OpenAI(
    base_url="https://openrouter.ai/api/v1",
    api_key=OPENROUTER_API_KEY,
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

    try:
        response = openrouter_client.chat.completions.create(
            model=OPENROUTER_MODEL_NAME,
            messages=messages,
            temperature=0.7,
            max_tokens=100,
        )
        
        raw_content = response.choices[0].message.content
        clean_content = raw_content.replace("*", "").replace("#", "").strip()
        return clean_content

    except Exception as e:
        print(f"Error generating response with OpenRouter: {e}")
        return "I'm sorry, I'm having trouble connecting to my brain right now. Could you repeat that?"

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

    try:
        response = openrouter_client.chat.completions.create(
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