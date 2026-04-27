"""Shared LLM output parsing and normalization helpers."""

import json


def _parse_json(text: str) -> dict:
    """Extract and parse the first JSON object or array from text."""
    if not text:
        return {}
    text = text.strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    for start_char, end_char in [('{', '}'), ('[', ']')]:
        start = text.find(start_char)
        end = text.rfind(end_char)
        if start != -1 and end > start:
            try:
                return json.loads(text[start:end + 1])
            except json.JSONDecodeError:
                pass
    return {}


def _normalize_confidence(v: str) -> str:
    return v if v in ("high", "medium", "low") else "low"


def _normalize_issue_type(v: str) -> str:
    valid = ("error_spike", "config", "regression", "user_behavior", "infra", "unknown")
    return v if v in valid else "unknown"


def _normalize_evidence_quality(v: str) -> str:
    return v if v in ("complete", "partial", "insufficient") else "partial"


def _valid_logql(q: str) -> bool:
    q = q.strip()
    return bool(q) and q.startswith("{") and "|" in q
