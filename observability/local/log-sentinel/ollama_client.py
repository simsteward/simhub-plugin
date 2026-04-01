"""Ollama HTTP client with qwen3 /think and /no_think mode support."""

import re
import time
import logging

import requests

logger = logging.getLogger("sentinel.ollama")

_THINK_STRIP = re.compile(r"<think>.*?</think>", re.DOTALL)


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
    ) -> tuple[str, int]:
        """
        Call Ollama /api/generate. Returns (response_text, duration_ms).
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

        raw = resp.json().get("response", "")
        cleaned = _THINK_STRIP.sub("", raw).strip()
        return cleaned, duration_ms

    def is_available(self) -> bool:
        """Quick availability check — HEAD /api/tags."""
        try:
            resp = requests.get(f"{self.base_url}/api/tags", timeout=5)
            return resp.status_code == 200
        except Exception:
            return False
