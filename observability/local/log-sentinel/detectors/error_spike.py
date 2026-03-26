"""Tier 1 detector: error rate spikes."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward", level="ERROR"} | json'


class ErrorSpikeDetector(BaseDetector):
    name = "error_spike"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        count = len(lines)
        if count < 3:
            return []

        # Collect top 5 error messages as evidence
        msg_counts: dict[str, int] = {}
        for line in lines:
            msg = line.get("message", "<no message>")
            msg_counts[msg] = msg_counts.get(msg, 0) + 1
        top_msgs = sorted(msg_counts.items(), key=lambda x: x[1], reverse=True)[:5]

        severity = "critical" if count >= 10 else "warn"
        escalate = count >= 5

        return [
            Finding(
                detector=self.name,
                severity=severity,
                title=f"Error spike: {count} errors in {window.duration_sec}s",
                summary=(
                    f"{count} ERROR-level log lines detected in the last "
                    f"{window.duration_sec}s. Top message: {top_msgs[0][0][:120]}"
                ),
                evidence={
                    "error_count": count,
                    "window_sec": window.duration_sec,
                    "top_messages": [
                        {"message": msg[:200], "count": c} for msg, c in top_msgs
                    ],
                },
                escalate_to_t2=escalate,
                logql_query=QUERY,
            )
        ]
