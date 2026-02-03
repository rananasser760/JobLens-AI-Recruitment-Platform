import os
import shutil
import uuid
import urllib.parse
from fastapi import FastAPI, UploadFile, File, Form, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from fastapi.middleware.cors import CORSMiddleware
from typing import List, Dict

# --- NEW: Audio Processing Import ---
from pydub import AudioSegment

# Import your modules
from response import generate_interview_response
from tts import text_to_speech_file
from stt import speech_to_text 

app = FastAPI()

# --- CORS ---
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["X-Text-Response", "X-Transcript"]
)

# --- Session Storage ---
SESSIONS: Dict[str, Dict] = {}

# --- Helpers ---
def cleanup_files(*paths):
    """Deletes temporary files to save space."""
    for path in paths:
        if path and os.path.exists(path):
            try:
                os.remove(path)
            except Exception as e:
                print(f"Warning: Could not delete {path}: {e}")

def convert_to_wav(input_path: str, output_path: str):
    """
    Converts any input audio (WebM, MP3, Ogg) to 16kHz Mono WAV.
    This format is required for optimal performance with NeMo/Parakeet.
    """
    try:
        # Load audio (pydub auto-detects format)
        audio = AudioSegment.from_file(input_path)
        
        # Set parameters: 16000Hz sample rate, Mono channel
        audio = audio.set_frame_rate(16000).set_channels(1)
        
        # Export as clean WAV
        audio.export(output_path, format="wav")
    except Exception as e:
        raise RuntimeError(f"FFmpeg conversion failed. Is FFmpeg installed? Error: {e}")

# --- Endpoints ---

@app.post("/start")
async def start_session(cv_text: str = Form(...)):
    session_id = str(uuid.uuid4())
    SESSIONS[session_id] = {
        "cv_text": cv_text,
        "history": [] 
    }
    return {"session_id": session_id, "message": "Session started."}

@app.post("/interview/{session_id}/reply")
async def handle_audio_reply(
    session_id: str, 
    background_tasks: BackgroundTasks,
    file: UploadFile = File(...)
):
    # 1. Validate Session
    if session_id not in SESSIONS:
        raise HTTPException(status_code=404, detail="Session not found.")
    
    session_data = SESSIONS[session_id]
    
    # Define temp file paths
    unique_id = uuid.uuid4()
    raw_input_filename = f"temp_raw_{unique_id}"  # No extension yet, pydub will figure it out
    clean_wav_filename = f"temp_clean_{unique_id}.wav"
    output_audio_path = None

    try:
        # 2. Save Uploaded Raw Audio (WebM/Ogg from browser)
        with open(raw_input_filename, "wb") as buffer:
            shutil.copyfileobj(file.file, buffer)

        # 3. Convert to 16kHz WAV (CRITICAL STEP)
        convert_to_wav(raw_input_filename, clean_wav_filename)

        # 4. STT: Process the Clean WAV
        user_transcript = speech_to_text(clean_wav_filename)
        print(f"User ({session_id}): {user_transcript}")

        # Update History
        session_data["history"].append({"role": "user", "content": user_transcript})

        # 5. LLM: Generate Response
        ai_response_text = generate_interview_response(
            current_transcript=user_transcript,
            chat_history=session_data["history"],
            cv_text=session_data["cv_text"]
        )
        
        # Update History
        session_data["history"].append({"role": "assistant", "content": ai_response_text})

        # 6. TTS: Generate Audio
        output_audio_path = text_to_speech_file(ai_response_text)

        # Prepare Headers
        safe_response = urllib.parse.quote(ai_response_text)
        safe_transcript = urllib.parse.quote(user_transcript)

        # 7. Cleanup & Return
        # We clean input files immediately. Output file is cleaned after response is sent.
        cleanup_files(raw_input_filename, clean_wav_filename)
        background_tasks.add_task(cleanup_files, output_audio_path)

        return FileResponse(
            path=output_audio_path,
            media_type="audio/wav",
            filename="reply.wav",
            headers={
                "X-Text-Response": safe_response,
                "X-Transcript": safe_transcript
            }
        )

    except Exception as e:
        # Cleanup on error
        cleanup_files(raw_input_filename, clean_wav_filename, output_audio_path)
        print(f"Error processing request: {e}")
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)