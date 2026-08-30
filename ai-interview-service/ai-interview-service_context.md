# Repository Snapshot

This document contains the directory structure and file contents of the project. It is formatted specifically for AI context ingestion.

## Directory Structure
```text
/ai-interview-service
    - .env
    - .env.example
    - Dockerfile
    - main.py
    - request_context.py
    - requirements.txt
    - response.py
    - session_store.py
    - stt.py
    - tts.py
    /routers
        - internal_api.py
        - __init__.py
```

## File Contents

### `Dockerfile`
```text
FROM python:3.11-slim

WORKDIR /app

RUN apt-get update && apt-get install -y \
    build-essential \
    libgl1 \
    libglib2.0-0 \
    curl \
    gnupg \
    ffmpeg \
    espeak-ng \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-build-isolation uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,target=/root/.cache/uv \
    uv pip install --system --no-build-isolation -r requirements.txt

COPY . .

EXPOSE 8000 8001

CMD ["python", "main.py"]
```

### `main.py`
```py
import os
import sys
import subprocess
import atexit
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse, JSONResponse
from dotenv import load_dotenv

load_dotenv()

RUNTIME_ENV = os.getenv("JOBLENS_ENV", "development").strip().lower()
INTERNAL_API_KEY = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()

from routers.internal_api import router as internal_router
from request_context import set_request_id

@asynccontextmanager
async def lifespan(app: FastAPI):
    if RUNTIME_ENV not in {"development", "dev", "local", "test"} and not INTERNAL_API_KEY:
        raise RuntimeError("JOBLENS_INTERNAL_API_KEY must be configured outside development environments")
    yield

def _docs_enabled() -> bool:
    return os.getenv("JOBLENS_ENABLE_DOCS", "true").strip().lower() == "true"

app = FastAPI(
    title="JobLens AI - Interview Service",
    description="Dedicated microservice for AI-driven audio interviews (STT, TTS, LLM)",
    version="2.0.0",
    docs_url="/docs" if _docs_enabled() else None,
    redoc_url="/redoc" if _docs_enabled() else None,
    openapi_url="/openapi.json" if _docs_enabled() else None,
    lifespan=lifespan,
)

cors_origins = [
    origin.strip()
    for origin in os.getenv("JOBLENS_CORS_ORIGINS", "http://localhost:4200,http://localhost:5245").split(",")
    if origin.strip()
] or ["http://localhost:4200", "http://localhost:5245"]

app.add_middleware(
    CORSMiddleware,
    allow_origins=cors_origins,
    allow_methods=["*"],
    allow_headers=["*"],
)

@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    correlation_id = request.headers.get("X-Correlation-Id", "").strip() or uuid.uuid4().hex
    request.state.correlation_id = correlation_id
    set_request_id(correlation_id)
    response = await call_next(request)
    response.headers["X-Correlation-Id"] = correlation_id
    return response

@app.middleware("http")
async def internal_api_key_guard(request: Request, call_next):
    if INTERNAL_API_KEY:
        path = request.url.path
        if path.startswith("/internal/v1/"):
            provided_key = request.headers.get("X-API-Key", "")
            if provided_key != INTERNAL_API_KEY:
                return JSONResponse(
                    status_code=401,
                    content={"detail": "Invalid or missing X-API-Key"},
                )
    return await call_next(request)

app.include_router(internal_router)

if os.path.isdir("static"):
    app.mount("/static", StaticFiles(directory="static"), name="static")

@app.get("/health")
def health():
    provider = os.getenv("JOBLENS_LLM_PROVIDER", "openrouter").lower().strip()
    llm_ok = bool(os.getenv("OPENROUTER_API_KEY")) if provider != "groq" else bool(os.getenv("GROQ_API_KEY"))
    return {
        "status": "healthy",
        "version": app.version,
        "environment": RUNTIME_ENV,
        "services": {
            "llm": "configured" if llm_ok else "missing_api_key",
            "stt_tts": "active"
        },
    }

@app.get("/")
def serve_home():
    return FileResponse("index.html")

def launch_mcq_server():
    mcq_dir = os.path.join(os.getcwd(), "Pre-Interview MCQ Assessment")
    if not os.path.exists(mcq_dir):
        return
    mcq_process = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8001"],
        cwd=mcq_dir
    )
    def cleanup():
        mcq_process.terminate()
        mcq_process.wait()
    atexit.register(cleanup)

if __name__ == "__main__":
    import uvicorn
    launch_mcq_server()
    uvicorn.run("main:app", host="0.0.0.0", port=8000, reload=False)
```

### `request_context.py`
```py
from contextvars import ContextVar

_request_id_ctx: ContextVar[str] = ContextVar("request_id", default="")


def set_request_id(value: str) -> None:
    _request_id_ctx.set((value or "").strip())


def get_request_id() -> str:
    return (_request_id_ctx.get() or "").strip()
```

### `requirements.txt`
```txt
fastapi
uvicorn
python-multipart
requests
websockets
python-dotenv
openai
nemo_toolkit[asr]<2.0.0
coqui-tts>=0.24.0
torch
torchaudio
torchvision
pydub
ffmpeg-python
imageio-ffmpeg
scipy
gTTS
transformers==4.40.2
huggingface-hub<0.23.0
```

### `response.py`
```py
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
```

### `session_store.py`
```py
from typing import Dict, Any

# ── Interview sessions ────────────────────────────────────────────────────────
# Key  : session_id  (str UUID, set by interview router at /interview/start)
INTERVIEW_SESSIONS: Dict[str, Dict[str, Any]] = {}

def get_interview_session(session_id: str) -> Dict[str, Any] | None:
    return INTERVIEW_SESSIONS.get(session_id)
```

### `stt.py`
```py
import os
import logging
import torch
from nemo.collections.asr.models import ASRModel

# --- 1. Suppress NeMo's excessive logging ---
logging.getLogger("nemo_logger").setLevel(logging.ERROR)

# --- 2. Configuration ---
MODEL_NAME = "nvidia/parakeet-tdt-0.6b-v2"
device = "cuda" if torch.cuda.is_available() else "cpu"

print(f"[stt] Loading STT model ({device})... This might take a minute.")
if torch.cuda.is_available():
    print(f"[stt] Using GPU: {torch.cuda.get_device_name(0)}")

# --- 3. Load Model Globally ---
try:
    model = ASRModel.from_pretrained(MODEL_NAME).to(device)
    model.eval()
    print("[stt] STT model loaded successfully.")
except Exception as e:
    print(f"[stt] Failed to load NeMo model: {e}")
    raise e

def speech_to_text(audio_file_path: str) -> str:
    """
    Transcribes a WAV file using NVIDIA Parakeet.
    Returns ONLY the text string.
    """
    abs_path = os.path.abspath(audio_file_path)
    
    if not os.path.exists(abs_path):
        print(f"[stt] Error: Audio file not found at {abs_path}")
        return ""

    try:
        # Transcribe returns a list of Hypothesis objects
        transcriptions = model.transcribe([abs_path])
        
        # Handle tuple return type if it occurs
        if isinstance(transcriptions, tuple):
            transcriptions = transcriptions[0]
            
        if transcriptions and len(transcriptions) > 0:
            # --- THE FIX IS HERE ---
            # We must access the .text attribute of the Hypothesis object
            result_object = transcriptions[0]
            
            # Check if it has a .text attribute (newer NeMo versions)
            if hasattr(result_object, 'text'):
                return result_object.text
            # Fallback for simple string returns
            elif isinstance(result_object, str):
                return result_object
            else:
                return str(result_object)
        else:
            return ""
            
    except Exception as e:
        print(f"[stt] Transcription error: {e}")
        return ""
```

### `tts.py`
```py
import os
import threading
import wave
from typing import Any

# --- FIX: REGISTER ESPEAK IN SYSTEM PATH ---
# 1. Define the exact path to the eSpeak installation FOLDER (not the .exe)
espeak_folder = r"C:\Program Files\eSpeak NG"

# 2. Add this folder to the system PATH environment variable
# This ensures Python can find 'libespeak-ng.dll'
if espeak_folder not in os.environ["PATH"]:
    os.environ["PATH"] += os.pathsep + espeak_folder

# 3. Explicitly tell the phonemizer where the library is
os.environ["PHONEMIZER_ESPEAK_LIBRARY"] = os.path.join(espeak_folder, "libespeak-ng.dll")
# --- FIX END ---

import uuid

import torch

# 1. Select device (GPU is highly recommended for TTS)
device = "cuda" if torch.cuda.is_available() else "cpu"

TTS_MODEL_NAME = os.getenv("TTS_MODEL_NAME", "tts_models/en/ljspeech/vits")
_tts = None
_tts_error = None
_tts_lock = threading.Lock()
ALLOW_SILENT_TTS_FALLBACK = (
    str(os.getenv("JOBLENS_ALLOW_SILENT_TTS_FALLBACK", "false") or "")
    .strip()
    .lower()
    in {"1", "true", "yes", "on"}
)


def _get_tts_model() -> Any:
    """Lazy-load Coqui TTS model; returns None when unavailable."""
    global _tts, _tts_error

    if _tts is not None:
        return _tts
    if _tts_error is not None:
        return None

    with _tts_lock:
        if _tts is not None:
            return _tts
        if _tts_error is not None:
            return None

        try:
            from TTS.api import TTS

            print(f"Loading TTS model on {device}...")
            _tts = TTS(TTS_MODEL_NAME).to(device)
            print("TTS Model loaded.")
            return _tts
        except Exception as exc:
            _tts_error = (
                "TTS initialization failed. Ensure espeak-ng is installed or set "
                "PHONEMIZER_ESPEAK_LIBRARY to libespeak-ng.dll. "
                f"Original error: {exc}"
            )
            print(f"[tts] {_tts_error}")
            return None


def _synthesize_with_gtts(text: str, output_path: str) -> bool:
    """Fallback cloud TTS if Coqui is unavailable."""
    try:
        from gtts import gTTS

        gTTS(text=text, lang="en", slow=False).save(output_path)
        return True
    except Exception as exc:
        print(f"[tts] gTTS fallback failed: {exc}")
        return False


def _write_silent_wav(output_path: str, seconds: float = 0.5, sample_rate: int = 16000) -> str:
    """Last-resort fallback so WebSocket flow keeps running without crashing."""
    frame_count = int(seconds * sample_rate)
    with wave.open(output_path, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b"\x00\x00" * frame_count)
    return output_path

def text_to_speech_file(text: str) -> str:
    """
    Converts text to audio and saves it as a temporary .wav file.
    Returns the file path.
    """
    # Ensure directory exists
    os.makedirs("temp_audio", exist_ok=True)

    uid = str(uuid.uuid4())

    # 1) Try primary Coqui TTS output (wav)
    coqui_path = os.path.join("temp_audio", f"response_{uid}.wav")
    tts_model = _get_tts_model()
    if tts_model is not None:
        try:
            tts_model.tts_to_file(text=text, file_path=coqui_path)
            return coqui_path
        except Exception as exc:
            print(f"[tts] Coqui synthesis failed: {exc}")

    # 2) Fallback to gTTS output (mp3)
    gtts_path = os.path.join("temp_audio", f"response_{uid}.mp3")
    if _synthesize_with_gtts(text, gtts_path):
        return gtts_path

    # 3) Optional final fallback: short silent wav (disabled by default)
    if ALLOW_SILENT_TTS_FALLBACK:
        print("[tts] Falling back to silent audio because JOBLENS_ALLOW_SILENT_TTS_FALLBACK is enabled.")
        silent_path = os.path.join("temp_audio", f"response_{uid}_silent.wav")
        return _write_silent_wav(silent_path)

    raise RuntimeError(
        "TTS synthesis unavailable: Coqui and gTTS generation both failed. "
        "Set JOBLENS_ALLOW_SILENT_TTS_FALLBACK=true to allow silent fallback output."
    )
```

### `routers/internal_api.py`
```py
import asyncio
import base64
import json
import os
import tempfile
import uuid
from datetime import datetime, timezone
from typing import Any

import imageio_ffmpeg
from fastapi import APIRouter
from pydantic import BaseModel, Field

from request_context import get_request_id
from response import InterviewProviderError, generate_interview_response, generate_interview_summary
from stt import speech_to_text
from session_store import INTERVIEW_SESSIONS
from tts import text_to_speech_file

router = APIRouter(prefix="/internal/v1", tags=["internal"])

DEFAULT_TTS_TIMEOUT_SECONDS = 12.0
TTS_TIMEOUT_ENV_VAR = "JOBLENS_TTS_TIMEOUT_SECONDS"

class StartInterviewSessionRequest(BaseModel):
    candidateName: str
    candidateId: str
    resumeText: str
    jobDescription: str
    evaluationCriteria: str
    maxQuestions: int = Field(default=5, ge=1, le=20)

class AudioAnalysisRequest(BaseModel):
    interviewSessionId: str
    base64Audio: str
    sequence: int = Field(default=1, ge=1)

class TranscriptEntryRequest(BaseModel):
    sequence: int
    speaker: str
    content: str
    occurredAtUtc: datetime | None = None

class FinalizeInterviewRequest(BaseModel):
    interviewSessionId: str
    integritySessionId: str | None = None
    transcript: list[TranscriptEntryRequest] = Field(default_factory=list)

def _envelope_ok(data: Any) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": True, "data": data, "error": None}

def _envelope_error(code: str, message: str, details: str | None = None) -> dict[str, Any]:
    return {"requestId": get_request_id() or uuid.uuid4().hex, "success": False, "data": None, "error": {"code": code, "message": message, "details": details}}

def _decode_data_uri_base64(value: str) -> bytes:
    payload = value.split(",", 1)[1] if "," in value else value
    return base64.b64decode(payload)

def _to_wav(src_path: str, dst_path: str) -> None:
    ffmpeg_path = imageio_ffmpeg.get_ffmpeg_exe()
    subprocess.run([ffmpeg_path, "-y", "-v", "error", "-i", src_path, "-ar", "16000", "-ac", "1", dst_path], capture_output=True, text=True, check=True)

@router.post("/interviews/sessions")
async def start_interview_session(request: StartInterviewSessionRequest):
    try:
        interview_session_id = str(uuid.uuid4())
        INTERVIEW_SESSIONS[interview_session_id] = {
            "cv_text": request.resumeText,
            "job_description": request.jobDescription,
            "criteria": request.evaluationCriteria,
            "history": [],
            "turn_count": 0,
            "max_questions": request.maxQuestions,
            "summary": None,
            "candidate_name": request.candidateName,
            "candidate_id": request.candidateId,
            "integrity_id": None,
        }
        return _envelope_ok({
            "interviewSessionId": interview_session_id,
            "maxQuestions": request.maxQuestions,
            "welcomeMessage": f"Welcome {request.candidateName.strip() or 'candidate'}. Let's start the interview.",
        })
    except Exception as exc:
        return _envelope_error("InterviewStartFailed", "Could not initialize interview session.", str(exc))

@router.post("/interviews/analyze-audio")
async def analyze_audio(request: AudioAnalysisRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        flags, transcript, reply_audio_base64, reply_audio_mime_type = [], "", None, None
        temp_raw_path, temp_wav_path = None, None

        try:
            with tempfile.NamedTemporaryFile(delete=False, suffix=".webm") as raw_file:
                raw_file.write(_decode_data_uri_base64(request.base64Audio))
                temp_raw_path = raw_file.name

            with tempfile.NamedTemporaryFile(delete=False, suffix=".wav") as wav_file:
                temp_wav_path = wav_file.name

            _to_wav(temp_raw_path, temp_wav_path)
            transcript = (speech_to_text(temp_wav_path) or "").strip()
            if not transcript: flags.append("empty_transcript")
        except Exception:
            flags.append("audio_processing_error")
        finally:
            for p in [temp_raw_path, temp_wav_path]:
                if p and os.path.exists(p): os.remove(p)

        if not transcript: transcript = "Could not transcribe candidate response clearly."

        session.setdefault("history", []).append({"role": "user", "content": transcript})
        session["turn_count"] = int(session.get("turn_count", 0)) + 1

        try:
            reply = generate_interview_response(transcript, session["history"], session.get("cv_text", ""), session.get("job_description", ""))
        except InterviewProviderError as exc:
            return _envelope_error(exc.code, str(exc), f"retryable={str(exc.retryable).lower()}")

        session["history"].append({"role": "assistant", "content": reply})

        if reply.strip():
            try:
                tts_path = await asyncio.wait_for(asyncio.to_thread(text_to_speech_file, reply), timeout=float(os.getenv(TTS_TIMEOUT_ENV_VAR, DEFAULT_TTS_TIMEOUT_SECONDS)))
                with open(tts_path, "rb") as f:
                    reply_audio_base64 = base64.b64encode(f.read()).decode("utf-8")
                reply_audio_mime_type = "audio/mpeg" if tts_path.lower().endswith(".mp3") else "audio/wav"
                os.remove(tts_path)
            except Exception as exc:
                flags.append("tts_generation_error")

        is_complete = session["turn_count"] >= int(session.get("max_questions", 5))
        score = None
        if is_complete:
            try:
                summary = generate_interview_summary(session["history"], session.get("cv_text", ""), session.get("job_description", ""), session.get("criteria", ""))
                session["summary"] = summary
                score = float(summary.get("score", 0.0)) if isinstance(summary, dict) else None
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc))

        return _envelope_ok({
            "transcript": transcript, "reply": reply, "isComplete": is_complete,
            "score": score, "flags": flags, "replyAudioBase64": reply_audio_base64, "replyAudioMimeType": reply_audio_mime_type
        })
    except Exception as exc:
        return _envelope_error("InterviewAudioAnalysisFailed", "Could not analyze interview audio.", str(exc))

@router.post("/interviews/finalize")
async def finalize_interview(request: FinalizeInterviewRequest):
    try:
        session = INTERVIEW_SESSIONS.get(request.interviewSessionId)
        if session is None:
            return _envelope_error("InterviewSessionNotFound", "Interview session not found.")

        summary = session.get("summary")
        if not isinstance(summary, dict):
            history = session.get("history", [])
            if not history and request.transcript:
                history = [{"role": "assistant" if item.speaker.lower() in ("assistant", "interviewer") else "user", "content": item.content} for item in sorted(request.transcript, key=lambda i: i.sequence)]
            try:
                summary = generate_interview_summary(history, session.get("cv_text", ""), session.get("job_description", ""), session.get("criteria", ""))
                session["summary"] = summary
            except InterviewProviderError as exc:
                return _envelope_error(exc.code, str(exc))

        return _envelope_ok({
            "finalScore": float(summary.get("score", 0.0)),
            "verdict": str(summary.get("recommendation", "Requires manual review")),
            "recruiterReportJson": json.dumps(summary, ensure_ascii=False),
            "candidateFeedbackJson": json.dumps({"review": summary.get("review", ""), "strengths": summary.get("strengths", []), "weaknesses": summary.get("weaknesses", []), "recommendation": summary.get("recommendation", "")}, ensure_ascii=False),
        })
    except Exception as exc:
        return _envelope_error("InterviewFinalizeFailed", "Could not finalize interview.", str(exc))
```

### `routers/__init__.py`
```py

```

