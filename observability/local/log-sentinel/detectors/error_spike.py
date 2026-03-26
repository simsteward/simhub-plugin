"""Detect error spikes in sim-steward logs."""

from collections import Counter

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class ErrorSpikeDetector(BaseDetector):
    name = "error_spike"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        errors = cache.get("ss_errors")
        count = len(errors)

        if count < 3:
            return findings

        messages = [e.get("message", "unknown") for e in errors]
        top_messages = Counter(messages).most_common(5)

        if count >= 10:
            severity = "critical"
        else:
            severity = "warn"

        escalate = count >= 5

        findings.append(Finding(
            detector=self.name,
            severity=severity,
            title=f"Error spike: {count} errors",
            summary=f"{count} errors detected in window. Top: {top_messages[0][0]!r} ({top_messages[0][1]}x)",
            category=self.category,
            evidence={
                "count": count,
                "top_messages": [{"message": m, "count": c} for m, c in top_messages],
            },
            escalate_to_t2=escalate,
            logql_query='{app="sim-steward", level="ERROR"} | json',
        ))

        return findings
