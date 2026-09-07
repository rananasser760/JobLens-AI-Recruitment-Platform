from __future__ import annotations

import logging
import os
import threading
from typing import Any

import torch

logger = logging.getLogger("joblens.stt")
logging.getLogger("nemo_logger").setLevel(logging.ERROR)

MODEL_NAME = os.getenv("JOBLENS_STT_MODEL", "nvidia/parakeet-tdt-0.6b-v2")

_model: Any = None
_model_lock = threading.Lock()
_device: str = "cuda" if torch.cuda.is_available() else "cpu"


def _get_model() -> Any:
    """Thread-safe lazy initialization of NVIDIA NeMo ASR model."""
    global _model
    if _model is not None:
        return _model

    with _model_lock:
        if _model is not None:
            return _model

        logger.info(f"Loading NeMo STT model ({MODEL_NAME}) on device: {_device}")
        try:
            from nemo.collections.asr.models import ASRModel

            loaded = ASRModel.from_pretrained(MODEL_NAME).to(_device)
            loaded.eval()
            _model = loaded
            logger.info("NeMo STT model loaded successfully.")
            return _model
        except Exception as exc:
            logger.exception(f"Failed to load NeMo STT model: {exc}")
            raise RuntimeError(f"Could not load STT model '{MODEL_NAME}': {exc}") from exc


def speech_to_text(audio_file_path: str) -> str:
    """
    Transcribes a WAV file using NVIDIA Parakeet TDT.
    Returns plain text only.
    """
    if not audio_file_path:
        return ""

    abs_path = os.path.abspath(audio_file_path)
    if not os.path.isfile(abs_path):
        logger.error(f"Audio file not found: {abs_path}")
        return ""

    if os.path.getsize(abs_path) == 0:
        logger.warning(f"Audio file is empty (0 bytes): {abs_path}")
        return ""

    try:
        model = _get_model()
        with torch.inference_mode():
            transcriptions = model.transcribe([abs_path])

        if isinstance(transcriptions, tuple):
            transcriptions = transcriptions[0]

        if not transcriptions:
            return ""

        result = transcriptions[0]

        # Extract text from NeMo Hypothesis or string
        if hasattr(result, "text"):
            text_val = result.text
        elif isinstance(result, str):
            text_val = result
        elif isinstance(result, dict) and "text" in result:
            text_val = result["text"]
        else:
            text_val = str(result)

        return (text_val or "").strip()

    except Exception as exc:
        logger.exception(f"Inference error during speech transcription: {exc}")
        return ""