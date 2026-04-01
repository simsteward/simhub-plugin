"""Ollama HTTP client with qwen3 /think and /no_think mode support."""

import re
import time
import logging
from dataclasses import dataclass

import requests

logger = logging.getLogger("sentinel.ollama")

_THINK_STRIP = re.compile(r"<think>.*?</think>", re.DOTALL)


@dataclass
class OllamaResult:
    text: str
    duration_ms: int
    input_tokens: int
    output_tokens: int
    tokens_per_sec: float


class OllamaClient:
    def __init__(self, base_url: str, timeout: int = 300):
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

    def generate(
        self,
        model: str,
        prompt: str,
        think: bool = False,
        temperature: float = 0.1,
    ) -> OllamaResult:
        """
        Call Ollama /api/generate. Returns OllamaResult with text, timing, and token counts.
        Prepends /think or /no_think for qwen3 models.
        Strips <think>...</think> blocks from output before returning.
        Raises on failure so callers can handle via circuit breaker.
        """
        mode_prefix = "/think\n" if think else "/no_think\n"
        full_prompt = mode_prefix + prompt

        start = time.time()
        resp = requests.post(
            f"{self.base_url}/api/generate",
            json={
                "model": model,
                "prompt": full_prompt,
                "stream": False,
                "options": {
                    "temperature": temperature,
                    "num_predict": 2048,
                },
            },
            timeout=self.timeout,
        )
        duration_ms = int((time.time() - start) * 1000)

        if resp.status_code != 200:
            raise RuntimeError(f"Ollama {resp.status_code}: {resp.text[:200]}")

        body = resp.json()
        raw = body.get("response", "")
        cleaned = _THINK_STRIP.sub("", raw).strip()

        input_tokens = body.get("prompt_eval_count", 0) or 0
        output_tokens = body.get("eval_count", 0) or 0
        tokens_per_sec = (output_tokens / (duration_ms / 1000)) if duration_ms > 0 else 0.0

        return OllamaResult(
            text=cleaned,
            duration_ms=duration_ms,
            input_tokens=input_tokens,
            output_tokens=output_tokens,
            tokens_per_sec=round(tokens_per_sec, 2),
        )

    def is_available(self) -> bool:
        """Quick availability check — HEAD /api/tags."""
        try:
            resp = requests.get(f"{self.base_url}/api/tags", timeout=5)
            return resp.status_code == 200
        except Exception:
            return False
