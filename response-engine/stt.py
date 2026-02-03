import torch
from nemo.collections.asr.models import ASRModel

MODEL = "nvidia/parakeet-tdt-0.6b-v2"
device = "cuda" if torch.cuda.is_available() else "cpu"
print("Using device:", torch.cuda.get_device_name(0) if torch.cuda.is_available() else "CPU")

model = ASRModel.from_pretrained(MODEL).to(device)
model.eval()

def speech_to_text(audio_file):
   """
   Gets an Audio file from front-end
   And returns the transcript
   :param audio_file: Audio file to transcript
   """
   transcript = model.transcribe([audio_file])[0].text
   return transcript