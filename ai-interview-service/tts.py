from __future__ import annotations

import logging
import os
import threading
import time
import uuid
import wave
from typing import Any, Optional

import torch

logger = logging.getLogger("joblens.tts")

device: str = "cuda" if torch.cuda.is_available() else "cpu"
TTS_MODEL_NAME = os.getenv("TTS_MODEL_NAME", "tts_models/en/ljspeech/vits")

_tts: Any = None
_tts_error: Optional[str] = None
_tts_lock = threading.Lock()

ALLOW_SILENT_TTS_FALLBACK: bool = (
    str(os.getenv("JOBLENS_ALLOW_SILENT_TTS_FALLBACK", "false") or "")
    .strip()
    .lower()
    in {"1", "true", "yes", "on"}
)


def _get_tts_model() -> Any:
    """Lazy-loads Coqui TTS with thread safety and error caching."""
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

            logger.info(f"Loading Coqui TTS model '{TTS_MODEL_NAME}' on {device}...")
            loaded = TTS(TTS_MODEL_NAME).to(device)
            logger.info("Coqui TTS model successfully loaded.")
            _tts = loaded
            return _tts
        except Exception as exc:
            _tts_error = (
                f"Coqui TTS initialization failed: {exc}. "
                "Ensure espeak-ng is installed and configured in system path."
            )
            logger.error(_tts_error)
            return None


def _synthesize_with_coqui(model: Any, text: str, output_path: str) -> bool:
    try:
        with _tts_lock:
            model.tts_to_file(text=text, file_path=output_path)
        return os.path.isfile(output_path) and os.path.getsize(output_path) > 0
    except Exception as exc:
        logger.warning(f"Coqui synthesis failure: {exc}")
        return False


def _synthesize_with_gtts(text: str, output_path: str) -> bool:
    try:
        from gtts import gTTS

        tts = gTTS(text=text, lang="en", slow=False)
        tts.save(output_path)
        return os.path.isfile(output_path) and os.path.getsize(output_path) > 0
    except Exception as exc:
        logger.warning(f"gTTS fallback failure: {exc}")
        return False


def _write_silent_wav(output_path: str, seconds: float = 0.5, sample_rate: int = 16000) -> str:
    frame_count = int(seconds * sample_rate)
    with wave.open(output_path, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(sample_rate)
        wav_file.writeframes(b"\x00\x00" * frame_count)
    return output_path


def _purge_stale_audio(target_dir: str, max_age_seconds: float = 600.0) -> None:
    cutoff = time.time() - max_age_seconds
    try:
        for name in os.listdir(target_dir):
            if not name.startswith("response_"):
                continue
            path = os.path.join(target_dir, name)
            try:
                if os.path.isfile(path) and os.path.getmtime(path) < cutoff:
                    os.remove(path)
            except OSError:
                continue
    except OSError:
        pass


def text_to_speech_file(text: str) -> str:
    """
    Converts text to an audio file (.wav or .mp3) and returns its absolute path.
    Tries Coqui TTS -> gTTS -> optional silent audio fallback.
    """
    clean_text = (text or "").strip()
    if not clean_text:
        clean_text = "Thank you."

    target_dir = os.path.abspath("temp_audio")
    os.makedirs(target_dir, exist_ok=True)
    _purge_stale_audio(target_dir)
    uid = uuid.uuid4().hex

    # 1. Primary Coqui synthesis (.wav)
    tts_model = _get_tts_model()
    if tts_model is not None:
        coqui_path = os.path.join(target_dir, f"response_{uid}.wav")
        if _synthesize_with_coqui(tts_model, clean_text, coqui_path):
            return coqui_path

    # 2. Fallback Google TTS (.mp3)
    gtts_path = os.path.join(target_dir, f"response_{uid}.mp3")
    if _synthesize_with_gtts(clean_text, gtts_path):
        return gtts_path

    # 3. Optional emergency silent WAV
    if ALLOW_SILENT_TTS_FALLBACK:
        logger.warning("All TTS generators failed; using silent WAV fallback.")
        silent_path = os.path.join(target_dir, f"response_{uid}_silent.wav")
        return _write_silent_wav(silent_path)

    raise RuntimeError(
        "TTS synthesis unavailable: Both Coqui and gTTS engines failed to generate audio. "
        "Set JOBLENS_ALLOW_SILENT_TTS_FALLBACK=true if headless execution is intended."
    )