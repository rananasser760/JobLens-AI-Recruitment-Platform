import torch
import gc
from nemo.collections.asr.models import ASRModel

print("Checking GPU...")
device = "cuda" if torch.cuda.is_available() else "cpu"
print("Using device:", torch.cuda.get_device_name(0))

def validate_model(model_name):
    print(f"\nLoading {model_name}...")
    model = ASRModel.from_pretrained(model_name).to(device)
    print(f"{model_name} loaded successfully!")

    # Free VRAM
    del model
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

    print(f"{model_name} unloaded and VRAM cleared.")

validate_model("nvidia/parakeet-tdt-0.6b-v2")
validate_model("nvidia/parakeet-tdt-0.6b-v3")

print("\nAll models validated successfully!")