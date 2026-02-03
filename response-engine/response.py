import os
import openai
from typing import List, Dict

# --- CONFIGURATION ---
# ⚠️ REPLACE THIS WITH YOUR REAL KEY ⚠️
OPENROUTER_API_KEY = "sk-or-v1-.." 
OPENROUTER_MODEL_NAME = "mistralai/mistral-7b-instruct-v0.2" 

# Configure the OpenAI client to use OpenRouter API
openrouter_client = openai.OpenAI(
    base_url="https://openrouter.ai/api/v1",
    api_key=OPENROUTER_API_KEY,
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

    try:
        response = openrouter_client.chat.completions.create(
            model=OPENROUTER_MODEL_NAME,
            messages=messages,
            temperature=0.7,
            max_tokens=100,
        )
        
        # --- NEW: Post-processing cleanup ---
        raw_content = response.choices[0].message.content
        
        # Remove asterisks (*) and other markdown symbols that mess up TTS
        clean_content = raw_content.replace("*", "").replace("#", "").strip()
        
        return clean_content

    except Exception as e:
        print(f"Error generating response with OpenRouter: {e}")

        return "I'm sorry, I'm having trouble connecting to my brain right now. Could you repeat that?"
