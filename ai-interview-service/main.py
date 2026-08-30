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