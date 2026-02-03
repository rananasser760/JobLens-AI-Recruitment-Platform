from torchaudio.datasets import LIBRISPEECH

dataset = LIBRISPEECH("./Comparing Parakeet v2 vs v3/librispeech_data", url="test-clean", download=False)
waveform, sample_rate, transcript, speaker_id, chapter_id, utterance_id = dataset[0]

print("Transcript:", transcript)
print("Sample rate:", sample_rate)
