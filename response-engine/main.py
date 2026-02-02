from fastapi import FastAPI, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from pydantic import BaseModel
from typing import List, Dict
import os
import urllib.parse  # <--- NEW IMPORT

# Import your modules
from response import generate_interview_response
from tts import text_to_speech_file

app = FastAPI()

# --- Data Models ---
class InterviewRequest(BaseModel):
    transcript: str
    chat_history: List[Dict[str, str]] # [{'role': 'assistant', 'content': '...'}, ...]
    cv_text: str

# --- Helpers ---
def cleanup_file(path: str):
    """Deletes the audio file after it is sent to save space."""
    if os.path.exists(path):
        os.remove(path)

# --- Endpoints ---

@app.post("/interview/reply")
async def get_agent_reply(request: InterviewRequest, background_tasks: BackgroundTasks):
    """
    1. Receives transcription.
    2. Generates AI text response.
    3. Converts text to audio.
    4. Returns audio file.
    """
    try:
        # Step 1: Generate Text Response
        ai_response_text = generate_interview_response(
            current_transcript=request.transcript,
            chat_history=request.chat_history,
            cv_text=request.cv_text
        )
        
        # Step 2: Generate Audio
        # This will block the thread slightly; in production, consider Celery/Redis queue
        audio_file_path = text_to_speech_file(ai_response_text)
        
        # Step 3: Return Audio
        # We use background_tasks to delete the file after it's sent
        background_tasks.add_task(cleanup_file, audio_file_path)

        # --- FIX: Encode the text for the header ---
        # Headers cannot contain special chars (like curly apostrophes or emojis).
        # We use quote() to make it safe (e.g., "That's great" -> "That%27s%20great")
        safe_header_text = urllib.parse.quote(ai_response_text)
        
        return FileResponse(
            path=audio_file_path, 
            media_type="audio/wav", 
            filename="response.wav",
            headers={"X-Text-Response": safe_header_text} # Send encoded text back
        )

    except Exception as e:
        # It's helpful to print the error to the console for debugging
        print(f"Error processing request: {e}")
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)