import torch
from nemo.collections.asr.models import ASRModel

device = "cuda" if torch.cuda.is_available() else "cpu"
print("Using device:", torch.cuda.get_device_name(0))

print("Loading v2...")
model_v2 = ASRModel.from_pretrained("nvidia/parakeet-tdt-0.6b-v2").to(device)


print("Loading v3...")
model_v3 = ASRModel.from_pretrained("nvidia/parakeet-tdt-0.6b-v3").to(device)


print("Models loaded successfully!")