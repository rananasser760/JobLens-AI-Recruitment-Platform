FROM python:3.11-slim

WORKDIR /app

# Added curl and ca-certificates for Playwright binary downloads
RUN apt-get update && apt-get install -y \
    build-essential \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-build-isolation uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,target=/root/.cache/uv \
    uv pip install --system --no-build-isolation -r requirements.txt

# Playwright can now safely fetch its dependencies
RUN playwright install chromium --with-deps

COPY . .

EXPOSE 8000 8001

CMD ["python", "main.py"]