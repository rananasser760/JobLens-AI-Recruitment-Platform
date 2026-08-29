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