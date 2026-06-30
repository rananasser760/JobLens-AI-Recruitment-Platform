from __future__ import annotations

import re
from functools import lru_cache
from typing import Dict

import numpy as np
import torch
from transformers import GPT2LMHeadModel, GPT2TokenizerFast


class AIDetector:
    """Perplexity + burstiness detector ported from notebook logic."""

    def __init__(self) -> None:
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        model_id = "gpt2"
        self.model = GPT2LMHeadModel.from_pretrained(model_id).to(self.device)
        self.tokenizer = GPT2TokenizerFast.from_pretrained(model_id)

    def calculate_perplexity(self, text: str) -> float:
        encoded = self.tokenizer(text, return_tensors="pt")
        input_ids = encoded.input_ids.to(self.device)
        with torch.no_grad():
            output = self.model(input_ids, labels=input_ids)
        return torch.exp(output.loss).item()

    def analyze_text(self, text: str) -> Dict:
        text = re.sub(r"\n+", " ", text).strip()
        if not text:
            return {
                "ai_probability_score": 0,
                "verdict": "Empty text",
                "avg_perplexity": 0,
                "burstiness_score": 0,
                "sentence_count": 0,
            }

        sentences = [sentence.strip() for sentence in text.split(".") if len(sentence.strip()) > 10]
        if len(sentences) < 3:
            ppl = self.calculate_perplexity(text)
            return {
                "ai_probability_score": 50,
                "verdict": "Insufficient Data",
                "avg_perplexity": round(ppl, 2),
                "burstiness_score": 0,
                "sentence_count": len(sentences),
            }

        perplexities = []
        for sentence in sentences[:20]:
            try:
                perplexities.append(self.calculate_perplexity(sentence))
            except Exception:
                continue

        if not perplexities:
            return {
                "ai_probability_score": 0,
                "verdict": "Error",
                "avg_perplexity": 0,
                "burstiness_score": 0,
                "sentence_count": 0,
            }

        avg_ppl = np.mean(perplexities)
        burstiness = np.std(perplexities)

        score = 0
        if avg_ppl < 40:
            score += 60
        elif avg_ppl < 70:
            score += 40
        elif avg_ppl < 100:
            score += 20

        if burstiness < 15:
            score += 40
        elif burstiness < 30:
            score += 20

        score = min(score, 99)

        if score > 80:
            verdict = "Likely AI Generated"
        elif score > 50:
            verdict = "Mixed or Suspicious"
        else:
            verdict = "Likely Human Written"

        return {
            "ai_probability_score": score,
            "verdict": verdict,
            "avg_perplexity": round(avg_ppl, 2),
            "burstiness_score": round(burstiness, 2),
            "sentence_count": len(sentences),
        }


@lru_cache(maxsize=1)
def get_ai_detector() -> AIDetector:
    return AIDetector()
