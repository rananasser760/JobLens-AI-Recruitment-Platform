FROM python:3.11-slim

WORKDIR /app

# Prevent uv lock timeouts and filesystem link warnings
ENV PHONEMIZER_ESPEAK_LIBRARY=/usr/lib/x86_64-linux-gnu/libespeak-ng.so.1 \
    UV_LINK_MODE=copy \
    UV_HTTP_TIMEOUT=600 \
    UV_LOCK_TIMEOUT=1200

# Install build dependencies, ffmpeg for audio transcoding, and espeak-ng for Coqui TTS
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    curl \
    ffmpeg \
    espeak-ng \
    libespeak-ng-dev \
    git \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

RUN --mount=type=cache,target=/root/.cache/pip \
    pip install --no-cache-dir uv "cython<3.0" setuptools wheel

RUN --mount=type=cache,id=joblens-uv-cache,target=/root/.cache/uv,sharing=locked \
    UV_TORCH_BACKEND=cpu uv pip install --system --no-build-isolation -r requirements.txt

RUN --mount=type=cache,id=joblens-uv-cache,target=/root/.cache/uv,sharing=locked \
    TORCH_MINOR=$(python -c "import torch; print('.'.join(torch.__version__.split('+')[0].split('.')[:2]))") && \
    case "$TORCH_MINOR" in \
        2.8) CODEC_SPEC="torchcodec>=0.6,<0.8" ;; \
        2.9) CODEC_SPEC="torchcodec>=0.8,<0.10" ;; \
        2.10) CODEC_SPEC="torchcodec>=0.10,<0.11" ;; \
        2.11) CODEC_SPEC="torchcodec>=0.11" ;; \
        *) CODEC_SPEC="torchcodec" ;; \
    esac && \
    echo "torch $TORCH_MINOR -> $CODEC_SPEC" && \
    UV_TORCH_BACKEND=cpu uv pip install --system --no-deps "$CODEC_SPEC" six && \
    python -c "import torchcodec; print('torchcodec', torchcodec.__version__)" && \
    python -c "from TTS.api import TTS; print('coqui-tts import ok')"

COPY . .

# Ensure audio exchange directory exists
RUN mkdir -p /app/temp_audio

EXPOSE 8000

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]