from __future__ import annotations

import json
import re
import threading
from typing import Any, Dict, Iterable, List, Optional

from .config import get_recruitment_settings
from .llm_client import get_llm_client

FREE_ROUTER_MODEL = "openrouter/free"

_unavailable_models: set[str] = set()
_unavailable_lock = threading.Lock()


def _is_openrouter() -> bool:
    return get_recruitment_settings().provider.lower().strip() == "openrouter"


def candidate_models(*preferred: Optional[str]) -> List[str]:
    models: List[str] = []
    for model in preferred:
        cleaned = (model or "").strip()
        if cleaned and cleaned not in models and cleaned not in _unavailable_models:
            models.append(cleaned)
    if _is_openrouter() and FREE_ROUTER_MODEL not in models:
        models.append(FREE_ROUTER_MODEL)
    return models


def _status_code(exc: Exception) -> Optional[int]:
    status = getattr(exc, "status_code", None)
    return status if isinstance(status, int) else None


def is_model_unavailable(exc: Exception) -> bool:
    text = str(exc).lower()
    return (
        _status_code(exc) == 404
        or "unavailable for free" in text
        or "no endpoints found" in text
        or "not a valid model" in text
        or "model not found" in text
    )


def mark_unavailable(model: str) -> None:
    if model == FREE_ROUTER_MODEL:
        return
    with _unavailable_lock:
        _unavailable_models.add(model)
    print(f"[llm] Model '{model}' is no longer available; skipping it from now on.", flush=True)


def _strip_wrappers(text: str) -> str:
    cleaned = re.sub(r"<think>.*?</think>", "", text or "", flags=re.DOTALL | re.IGNORECASE)
    cleaned = re.sub(r"```(?:json)?", "", cleaned, flags=re.IGNORECASE)
    start = cleaned.find("{")
    if start == -1:
        return cleaned.strip()
    end = cleaned.rfind("}")
    return cleaned[start : end + 1] if end > start else cleaned[start:]


def _close_truncated(text: str) -> str:
    stack: List[str] = []
    in_string = False
    escaped = False
    last_safe = 0
    for index, char in enumerate(text):
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char in "{[":
            stack.append("}" if char == "{" else "]")
        elif char in "}]":
            if stack:
                stack.pop()
            last_safe = index + 1
        elif char == ",":
            last_safe = index
    if not stack and not in_string:
        return text
    body = text[:last_safe] if last_safe else text
    body = re.sub(r"[,:\s]+$", "", body)
    reopened: List[str] = []
    in_string = False
    escaped = False
    for char in body:
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char in "{[":
            reopened.append("}" if char == "{" else "]")
        elif char in "}]" and reopened:
            reopened.pop()
    if in_string:
        body += '"'
    return body + "".join(reversed(reopened))


def loads_json(text: str) -> Any:
    candidate = _strip_wrappers(text)
    if not candidate:
        raise ValueError("empty model response")
    attempts = [
        candidate,
        re.sub(r",\s*([}\]])", r"\1", candidate),
    ]
    attempts.append(re.sub(r",\s*([}\]])", r"\1", _close_truncated(attempts[1])))
    last_error: Optional[Exception] = None
    for attempt in attempts:
        try:
            return json.loads(attempt)
        except json.JSONDecodeError as exc:
            last_error = exc
    raise ValueError(f"model returned invalid JSON: {last_error}")


def chat_json(
    models: Iterable[Optional[str]],
    system_prompt: str,
    user_prompt: str,
    *,
    max_tokens: int = 6000,
    temperature: float = 0.0,
    label: str = "llm",
) -> Dict[str, Any]:
    client = get_llm_client()
    errors: List[str] = []
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]
    for model in candidate_models(*models):
        for structured in (True, False):
            kwargs: Dict[str, Any] = {
                "model": model,
                "messages": messages,
                "max_tokens": max_tokens,
                "temperature": temperature,
            }
            if structured:
                kwargs["response_format"] = {"type": "json_object"}
                if _is_openrouter():
                    kwargs["extra_body"] = {"reasoning": {"effort": "low", "exclude": True}}
            try:
                response = client.chat.completions.create(**kwargs)
            except Exception as exc:
                if is_model_unavailable(exc):
                    mark_unavailable(model)
                    errors.append(f"{model}: unavailable")
                    break
                if structured and _status_code(exc) == 400:
                    continue
                errors.append(f"{model}: {exc}")
                print(f"[{label}] Model '{model}' failed: {exc}", flush=True)
                break
            content = response.choices[0].message.content if response.choices else ""
            try:
                data = loads_json(content or "")
            except ValueError as exc:
                errors.append(f"{model}: {exc}")
                print(f"[{label}] Model '{model}' returned unusable JSON: {exc}", flush=True)
                break
            if isinstance(data, dict) and data:
                return data
            errors.append(f"{model}: empty JSON object")
            break
    raise RuntimeError("; ".join(errors) or "no language model is available")
