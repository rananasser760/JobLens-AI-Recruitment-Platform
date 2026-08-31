import os
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from dotenv import load_dotenv

from routers.internal_api import router as internal_router
from routers.recruitment import router as recruitment_router
from recruitment.scheduler import start_scheduler, stop_scheduler
from request_context import set_request_id

load_dotenv()

RUNTIME_ENV = os.getenv("JOBLENS_ENV", "development").strip().lower()
INTERNAL_API_KEY = os.getenv("JOBLENS_INTERNAL_API_KEY", "").strip()

_public_routes_flag = os.getenv("JOBLENS_ENABLE_PUBLIC_ROUTES")
if _public_routes_flag is None:
    ALLOW_PUBLIC_ROUTES = RUNTIME_ENV in {"development", "dev", "local", "test"}
else:
    ALLOW_PUBLIC_ROUTES = _public_routes_flag.strip().lower() == "true"

@asynccontextmanager
async def lifespan(app: FastAPI):
    if RUNTIME_ENV not in {"development", "dev", "local", "test"} and not INTERNAL_API_KEY:
        raise RuntimeError("JOBLENS_INTERNAL_API_KEY must be configured outside development environments")
        
    try:
        app.state.recruitment_scheduler = start_scheduler()
    except Exception as exc:
        app.state.recruitment_scheduler = {"enabled": False, "running": False, "error": str(exc)}
    yield
    try:
        stop_scheduler()
    except Exception:
        pass

app = FastAPI(
    title="JobLens AI - Recruitment Service",
    description="CV parsing, scraping, and matching platform",
    version="2.0.0",
    docs_url="/docs",
    redoc_url="/redoc",
    lifespan=lifespan,
)

cors_origins = [origin.strip() for origin in os.getenv("JOBLENS_CORS_ORIGINS", "http://localhost:4200,http://localhost:5245").split(",") if origin.strip()] or ["http://localhost:4200", "http://localhost:5245"]

app.add_middleware(CORSMiddleware, allow_origins=cors_origins, allow_methods=["*"], allow_headers=["*"])

@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    correlation_id = request.headers.get("X-Correlation-Id", "").strip() or uuid.uuid4().hex
    set_request_id(correlation_id)
    response = await call_next(request)
    response.headers["X-Correlation-Id"] = correlation_id
    return response

@app.middleware("http")
async def internal_api_key_guard(request: Request, call_next):
    if INTERNAL_API_KEY:
        path = request.url.path
        protected = path.startswith("/internal/v1/") or path.startswith("/api/")
        if protected and request.headers.get("X-API-Key", "") != INTERNAL_API_KEY:
            return JSONResponse(status_code=401, content={"detail": "Invalid or missing X-API-Key"})
    return await call_next(request)

if ALLOW_PUBLIC_ROUTES:
    app.include_router(recruitment_router)
app.include_router(internal_router)

@app.get("/health")
def health():
    return {"status": "healthy", "version": app.version, "environment": RUNTIME_ENV}

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", "8000"))
    uvicorn.run("main:app", host="0.0.0.0", port=port, reload=False)