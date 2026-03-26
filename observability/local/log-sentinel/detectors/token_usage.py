"""Detect high token usage, expensive sessions, and low cache efficiency."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class TokenUsageDetector(BaseDetector):
    name = "token_usage"
    category = "ops"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        tokens = cache.get("claude_tokens")

        for line in tokens:
            session_id = line.get("session_id", "unknown")
            cost = _safe_float(line.get("cost_usd"))
            output_tokens = _safe_int(line.get("total_output_tokens"))
            input_tokens = _safe_int(line.get("total_input_tokens"))
            cache_read = _safe_int(line.get("total_cache_read_tokens"))

            # Cost thresholds (check expensive first to avoid duplicate)
            if cost > 5.0:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Expensive session: ${cost:.2f}",
                    summary=f"Session {session_id} cost ${cost:.2f} (>$5 threshold)",
                    category=self.category,
                    evidence={
                        "session_id": session_id,
                        "cost_usd": cost,
                        "total_output_tokens": output_tokens,
                        "total_input_tokens": input_tokens,
                    },
                    escalate_to_t2=True,
                    logql_query='{app="claude-token-metrics"} | json',
                ))
            elif cost > 1.0:
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"High-cost session: ${cost:.2f}",
                    summary=f"Session {session_id} cost ${cost:.2f} (>$1 threshold)",
                    category=self.category,
                    evidence={
                        "session_id": session_id,
                        "cost_usd": cost,
                        "total_output_tokens": output_tokens,
                        "total_input_tokens": input_tokens,
                    },
                    logql_query='{app="claude-token-metrics"} | json',
                ))

            # Token-heavy session
            if output_tokens > 100_000:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title="Token-heavy session",
                    summary=f"Session {session_id} produced {output_tokens:,} output tokens",
                    category=self.category,
                    evidence={
                        "session_id": session_id,
                        "total_output_tokens": output_tokens,
                    },
                    logql_query='{app="claude-token-metrics"} | json',
                ))

            # Cache efficiency
            denominator = max(input_tokens, 1)
            cache_ratio = cache_read / denominator
            if cache_ratio < 0.3 and input_tokens > 0:
                pct = round(cache_ratio * 100, 1)
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"Low cache hit rate ({pct}%)",
                    summary=f"Session {session_id}: cache read {cache_read:,} / input {input_tokens:,} = {pct}%",
                    category=self.category,
                    evidence={
                        "session_id": session_id,
                        "total_cache_read_tokens": cache_read,
                        "total_input_tokens": input_tokens,
                        "cache_hit_pct": pct,
                    },
                    logql_query='{app="claude-token-metrics"} | json',
                ))

        return findings


def _safe_float(val) -> float:
    try:
        return float(val)
    except (TypeError, ValueError):
        return 0.0


def _safe_int(val) -> int:
    try:
        return int(val)
    except (TypeError, ValueError):
        return 0
