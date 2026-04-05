import os
import sys

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

import torch
from TTS.api import TTS
import uuid

# 1. Select device (GPU is highly recommended for TTS)
device = "cuda" if torch.cuda.is_available() else "cpu"

# 2. Initialize the model ONCE globally.
# "tts_models/en/ljspeech/vits" is a good balance of speed and quality.
print(f"Loading TTS model on {device}...")
tts = TTS("tts_models/en/ljspeech/vits").to(device)
print("TTS Model loaded.")

def text_to_speech_file(text: str) -> str:
    """
    Converts text to audio and saves it as a temporary .wav file.
    Returns the file path.
    """
    # Generate a unique filename to avoid collisions between users
    filename = f"response_{uuid.uuid4()}.wav"
    output_path = os.path.join("temp_audio", filename)
    
    # Ensure directory exists
    os.makedirs("temp_audio", exist_ok=True)

    # Generate audio
    tts.tts_to_file(
        text=text, 
        file_path=output_path
    )
    
    return output_path