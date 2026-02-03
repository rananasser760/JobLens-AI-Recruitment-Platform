import os
import shutil
import uuid
import urllib.parse
import subprocess
from typing import List, Dict

from fastapi import FastAPI, UploadFile, File, Form, HTTPException, BackgroundTasks
from fastapi.responses import FileResponse
from fastapi.middleware.cors import CORSMiddleware

# --- NEW: Robust FFmpeg Wrapper ---
# This library guarantees a working binary on Windows/Mac/Linux
import imageio_ffmpeg 

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
    Converts audio using the guaranteed imageio-ffmpeg binary.
    """
    # 1. Get the path to the static executable
    ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
    
    # 2. Force Absolute Paths (Fixes 'File not found' errors)
    abs_input = os.path.abspath(input_path)
    abs_output = os.path.abspath(output_path)

    # 3. Validation
    if not os.path.exists(abs_input):
        raise RuntimeError(f"Input file missing: {abs_input}")
    
    if os.path.getsize(abs_input) == 0:
        raise RuntimeError("Input file is empty (0 bytes). Upload failed.")

    # 4. Command
    # -y: Overwrite
    # -v error: Only print errors
    command = [
        ffmpeg_exe,
        "-y",               
        "-v", "error",      
        "-i", abs_input,   
        "-ar", "16000",     
        "-ac", "1",         
        abs_output         
    ]

    try:
        # 5. Run it
        # capture_output=True allows us to see the error message if it fails
        subprocess.run(
            command, 
            check=True, 
            capture_output=True, 
            text=True
        )
        print(f"DEBUG: Converted {abs_input} -> {abs_output}")
        
    except subprocess.CalledProcessError as e:
        # If it fails, e.stderr will now contain the actual reason
        raise RuntimeError(f"FFmpeg Error:\n{e.stderr}")

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
    
    # --- HANDLING FILE EXTENSIONS ---
    # We grab the extension from the original filename to help FFmpeg
    original_ext = os.path.splitext(file.filename)[1]
    if not original_ext:
        original_ext = ".webm" # Fallback for browser blobs

    unique_id = uuid.uuid4()
    raw_input_filename = f"temp_raw_{unique_id}{original_ext}" 
    clean_wav_filename = f"temp_clean_{unique_id}.wav"
    output_audio_path = None

    try:
        # 2. Save File
        with open(raw_input_filename, "wb") as buffer:
            shutil.copyfileobj(file.file, buffer)

        # 3. Convert (Now using the robust binary)
        convert_to_wav(raw_input_filename, clean_wav_filename)

        # 4. STT
        user_transcript = speech_to_text(clean_wav_filename)
        print(f"User ({session_id}): {user_transcript}")

        # Update History
        session_data["history"].append({"role": "user", "content": user_transcript})

        # 5. LLM
        ai_response_text = generate_interview_response(
            current_transcript=user_transcript,
            chat_history=session_data["history"],
            cv_text=session_data["cv_text"]
        )
        
        # Update History
        session_data["history"].append({"role": "assistant", "content": ai_response_text})

        # 6. TTS
        output_audio_path = text_to_speech_file(ai_response_text)

        # Headers
        safe_response = urllib.parse.quote(ai_response_text)
        safe_transcript = urllib.parse.quote(user_transcript)

        # 7. Cleanup
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
        cleanup_files(raw_input_filename, clean_wav_filename, output_audio_path)
        print(f"Error processing request: {e}")
        # Return 500 but include the error detail so you can see it in Postman/Frontend
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    import uvicorn
    # reload=False prevents double-loading models
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)