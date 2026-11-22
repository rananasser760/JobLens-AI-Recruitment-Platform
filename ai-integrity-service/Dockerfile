FROM python:3.11-slim

WORKDIR /app

# Prevent uv lock timeouts and filesystem link warnings
ENV UV_LINK_MODE=copy \
    UV_HTTP_TIMEOUT=600 \
    UV_LOCK_TIMEOUT=1200

# Install system utilities, OpenCV headless dependencies, and Microsoft ODBC 18 driver
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libgl1 \
    libglib2.0-0 \
    libsm6 \
    libxext6 \
    curl \
    gnupg \
    unixodbc-dev \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y msodbcsql18 \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-cache-dir uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,id=joblens-uv-cache,target=/root/.cache/uv,sharing=locked \
    UV_TORCH_BACKEND=cpu uv pip install --system --no-build-isolation -r requirements.txt

COPY . .

# Ensure outputs directories exist
RUN mkdir -p outputs/alert_frames

EXPOSE 8000

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]