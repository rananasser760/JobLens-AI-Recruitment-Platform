FROM python:3.11-slim

WORKDIR /app

# Removed heavy SQL and OpenCV OS dependencies
RUN apt-get update && apt-get install -y \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-build-isolation uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,target=/root/.cache/uv \
    uv pip install --system --no-build-isolation -r requirements.txt

RUN playwright install chromium --with-deps

COPY . .

EXPOSE 8000 8001

CMD ["python", "main.py"]