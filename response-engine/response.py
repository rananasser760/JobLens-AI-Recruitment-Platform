import os
import google.generativeai as genai
from typing import List, Dict

# --- CONFIGURATION ---
# PASTE YOUR KEY HERE
GEMINI_API_KEY = "GEMINI_KEY_HERE"

# Configure the library
genai.configure(api_key=GEMINI_API_KEY)

# UPDATED: Using the fastest model from your specific list
model = genai.GenerativeModel('gemini-3-flash-preview')

def generate_interview_response(
    current_transcript: str, 
    chat_history: List[Dict[str, str]], 
    cv_text: str
) -> str:
    """
    Generates a follow-up interview question using Google Gemini.
    """
    
    # 1. Construct the System Prompt (Context)
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
    """

    # 2. Build the Chat Session
    # We manually format the history to ensure compatibility
    history_text = ""
    for msg in chat_history:
        role = "Interviewer" if msg['role'] == 'assistant' else "Candidate"
        history_text += f"{role}: {msg['content']}\n"

    # Create the final prompt sent to Gemini
    full_prompt = f"""
    {system_prompt}

    --- Conversation History ---
    {history_text}
    
    --- Current Reply ---
    Candidate: {current_transcript}
    
    Interviewer (You):
    """

    try:
        response = model.generate_content(full_prompt)
        return response.text.strip()
        
    except Exception as e:
        print(f"Error generating response with Gemini: {e}")
        return "Could you please repeat that? I didn't quite catch it."