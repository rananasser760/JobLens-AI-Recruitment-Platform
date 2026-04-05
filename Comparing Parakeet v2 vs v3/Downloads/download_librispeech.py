import os
import torchaudio

current_dir = os.path.dirname(os.path.abspath(__file__))
dataset_path = os.path.join(current_dir, "librispeech_data")
os.makedirs(dataset_path, exist_ok=True)

print("Downloading LibriSpeech test-clean...")
torchaudio.datasets.LIBRISPEECH(
    root=dataset_path,
    url="test-clean",
    download=True
)

print("Downloading LibriSpeech test-other...")
torchaudio.datasets.LIBRISPEECH(
    root=dataset_path,
    url="test-other",
    download=True
)

print("Download complete!")
