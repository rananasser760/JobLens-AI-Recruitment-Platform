import os
import logging
import torch
from nemo.collections.asr.models import ASRModel

# --- 1. Suppress NeMo's excessive logging ---
logging.getLogger("nemo_logger").setLevel(logging.ERROR)

# --- 2. Configuration ---
MODEL_NAME = "nvidia/parakeet-tdt-0.6b-v2"
device = "cuda" if torch.cuda.is_available() else "cpu"

print(f"[stt] Loading STT model ({device})... This might take a minute.")
if torch.cuda.is_available():
    print(f"[stt] Using GPU: {torch.cuda.get_device_name(0)}")

# --- 3. Load Model Globally ---
try:
    model = ASRModel.from_pretrained(MODEL_NAME).to(device)
    model.eval()
    print("[stt] STT model loaded successfully.")
except Exception as e:
    print(f"[stt] Failed to load NeMo model: {e}")
    raise e

def speech_to_text(audio_file_path: str) -> str:
    """
    Transcribes a WAV file using NVIDIA Parakeet.
    Returns ONLY the text string.
    """
    abs_path = os.path.abspath(audio_file_path)
    
    if not os.path.exists(abs_path):
        print(f"[stt] Error: Audio file not found at {abs_path}")
        return ""

    try:
        # Transcribe returns a list of Hypothesis objects
        transcriptions = model.transcribe([abs_path])
        
        # Handle tuple return type if it occurs
        if isinstance(transcriptions, tuple):
            transcriptions = transcriptions[0]
            
        if transcriptions and len(transcriptions) > 0:
            # --- THE FIX IS HERE ---
            # We must access the .text attribute of the Hypothesis object
            result_object = transcriptions[0]
            
            # Check if it has a .text attribute (newer NeMo versions)
            if hasattr(result_object, 'text'):
                return result_object.text
            # Fallback for simple string returns
            elif isinstance(result_object, str):
                return result_object
            else:
                return str(result_object)
        else:
            return ""
            
    except Exception as e:
        print(f"[stt] Transcription error: {e}")
        return ""