import os
import shutil
import uuid
import json
import subprocess
from typing import List, Dict

from fastapi import FastAPI, Form, HTTPException, BackgroundTasks, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, HTMLResponse
from fastapi.middleware.cors import CORSMiddleware
import imageio_ffmpeg 

from response import generate_interview_response, generate_interview_summary
from tts import text_to_speech_file
from stt import speech_to_text 

app = FastAPI()

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# --- Session Storage ---
SESSIONS: Dict[str, Dict] = {}

# --- Helpers ---
def cleanup_files(*paths):
    for path in paths:
        if path and os.path.exists(path):
            try:
                os.remove(path)
            except Exception as e:
                print(f"Warning: Could not delete {path}: {e}")

def convert_to_wav(input_path: str, output_path: str):
    ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
    abs_input = os.path.abspath(input_path)
    abs_output = os.path.abspath(output_path)

    if not os.path.exists(abs_input):
        raise RuntimeError(f"Input file missing: {abs_input}")
    if os.path.getsize(abs_input) == 0:
        raise RuntimeError("Input file is empty (0 bytes). Upload failed.")

    command = [
        ffmpeg_exe, "-y", "-v", "error", "-i", abs_input, 
        "-ar", "16000", "-ac", "1", abs_output         
    ]

    try:
        subprocess.run(command, check=True, capture_output=True, text=True)
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"FFmpeg Error:\n{e.stderr}")

# --- Endpoints ---

@app.get("/", response_class=HTMLResponse)
async def serve_frontend():
    """Serves the test HTML file for the browser."""
    with open("index.html", "r", encoding="utf-8") as f:
        return f.read()

@app.post("/start")
async def start_session(
    cv_text: str = Form(...), 
    max_questions: int = Form(5),
    evaluation_criteria: str = Form("Technical accuracy, clarity, and relevance to the CV")
):
    session_id = str(uuid.uuid4())
    SESSIONS[session_id] = {
        "cv_text": cv_text,
        "history": [],
        "turn_count": 0,
        "max_questions": max_questions,
        "criteria": evaluation_criteria,
        "summary": None
    }
    return {
        "session_id": session_id, 
        "message": f"Session started. Limit: {max_questions} questions."
    }

@app.websocket("/ws/interview/{session_id}")
async def websocket_endpoint(websocket: WebSocket, session_id: str):
    """Handles the live audio stream back and forth."""
    await websocket.accept()
    
    if session_id not in SESSIONS:
        await websocket.close(code=1008) # Policy violation (not found)
        return
        
    session_data = SESSIONS[session_id]

    try:
        while True:
            # 1. Wait for user's audio bytes from the browser
            audio_bytes = await websocket.receive_bytes()
            
            unique_id = uuid.uuid4()
            raw_input_filename = f"temp_raw_{unique_id}.webm" 
            clean_wav_filename = f"temp_clean_{unique_id}.wav"
            output_audio_path = None

            try:
                # 2. Save and convert
                with open(raw_input_filename, "wb") as f:
                    f.write(audio_bytes)
                
                convert_to_wav(raw_input_filename, clean_wav_filename)

                # 3. Transcribe
                user_transcript = speech_to_text(clean_wav_filename)
                print(f"User ({session_id}): {user_transcript}")

                session_data["history"].append({"role": "user", "content": user_transcript})
                session_data["turn_count"] += 1
                is_complete = session_data["turn_count"] >= session_data["max_questions"]

                # 4. Generate AI Response or Summary
                if is_complete:
                    print(f"Session {session_id} finishing. Generating summary...")
                    summary_data = generate_interview_summary(
                        chat_history=session_data["history"],
                        cv_text=session_data["cv_text"],
                        criteria=session_data["criteria"]
                    )
                    session_data["summary"] = summary_data
                    ai_response_text = "Thank you for your time. This concludes our interview. Please check your screen for your review and final score."
                    session_data["history"].append({"role": "assistant", "content": ai_response_text})
                else:
                    ai_response_text = generate_interview_response(
                        current_transcript=user_transcript,
                        chat_history=session_data["history"],
                        cv_text=session_data["cv_text"]
                    )
                    session_data["history"].append({"role": "assistant", "content": ai_response_text})

                # 5. Send transcripts and state to frontend FIRST (as JSON text)
                await websocket.send_text(json.dumps({
                    "type": "transcript",
                    "user": user_transcript,
                    "ai": ai_response_text,
                    "is_complete": is_complete
                }))

                # 6. Generate TTS Audio
                output_audio_path = text_to_speech_file(ai_response_text)

                # 7. Send the resulting audio bytes back to the frontend SECOND
                with open(output_audio_path, "rb") as f:
                    ai_audio_bytes = f.read()
                await websocket.send_bytes(ai_audio_bytes)

            finally:
                cleanup_files(raw_input_filename, clean_wav_filename, output_audio_path)

            # If interview is over, break the loop and close
            if is_complete:
                await websocket.close()
                break

    except WebSocketDisconnect:
        print(f"Client disconnected gracefully: {session_id}")
    except Exception as e:
        print(f"WebSocket Error: {e}")
        try:
            await websocket.close(code=1011)
        except:
            pass


@app.get("/interview/{session_id}/summary")
async def get_summary(session_id: str):
    if session_id not in SESSIONS:
        raise HTTPException(status_code=404, detail="Session not found.")
    summary = SESSIONS[session_id].get("summary")
    if not summary:
        return {"status": "in_progress", "summary": "Interview has not concluded yet."}
    return {"status": "completed", "summary": summary}

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)