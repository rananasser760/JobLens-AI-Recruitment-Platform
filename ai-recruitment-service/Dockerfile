FROM python:3.11-slim

WORKDIR /app

# Prevent uv lock timeouts and filesystem link warnings
ENV UV_LINK_MODE=copy \
    UV_HTTP_TIMEOUT=600 \
    UV_LOCK_TIMEOUT=1200

# Install system utilities, font rendering, and Playwright Chromium dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    curl \
    ca-certificates \
    git \
    fonts-liberation \
    libnss3 \
    libatk-bridge2.0-0 \
    libdrm2 \
    libxkbcommon0 \
    libgbm1 \
    libasound2 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-cache-dir uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,id=joblens-uv-cache,target=/root/.cache/uv,sharing=locked \
    UV_TORCH_BACKEND=cpu uv pip install --system --no-build-isolation -r requirements.txt

# Install Playwright browser binary
RUN playwright install chromium --with-deps

COPY . .

# Ensure storage directories exist
RUN mkdir -p /root/.cache/chroma /root/.cache/huggingface /root/.cache/torch

EXPOSE 8000

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]