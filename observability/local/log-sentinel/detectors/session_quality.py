"""Detect session quality issues from session_digest events."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class SessionQualityDetector(BaseDetector):
    name = "session_quality"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        digests = cache.get("ss_digests")

        if not digests:
            return findings

        for line in digests:
            fields = line.get("fields", {})
            track = fields.get("track_display_name", "unknown track")
            total_incidents = _to_int(fields.get("total_incidents", 0))
            action_failures = _to_int(fields.get("action_failures", 0))
            p95_latency = _to_float(fields.get("p95_action_latency_ms", 0))
            plugin_errors = _to_int(fields.get("plugin_errors", 0))

            # Always emit info for completed sessions
            findings.append(Finding(
                detector=self.name,
                severity="info",
                title=f"Session complete: {track}, {total_incidents} incidents",
                summary=f"Session digest for {track}: {total_incidents} total incidents",
                category=self.category,
                evidence={
                    "track": track,
                    "total_incidents": total_incidents,
                    "action_failures": action_failures,
                    "p95_action_latency_ms": p95_latency,
                    "plugin_errors": plugin_errors,
                },
                escalate_to_t2=False,
                logql_query='{app="sim-steward", event="session_digest"} | json',
            ))

            # Quality warnings
            if action_failures > 0:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Session had {action_failures} action failures",
                    summary=f"Session at {track} completed with {action_failures} action failure(s)",
                    category=self.category,
                    evidence={"track": track, "action_failures": action_failures},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="session_digest"} | json',
                ))

            if p95_latency and p95_latency > 500:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"High action latency: p95={p95_latency:.0f}ms",
                    summary=f"Session at {track} had p95 action latency of {p95_latency:.0f}ms (threshold: 500ms)",
                    category=self.category,
                    evidence={"track": track, "p95_action_latency_ms": p95_latency},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="session_digest"} | json',
                ))

            if plugin_errors > 0:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Session had {plugin_errors} plugin errors",
                    summary=f"Session at {track} completed with {plugin_errors} plugin error(s)",
                    category=self.category,
                    evidence={"track": track, "plugin_errors": plugin_errors},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward", event="session_digest"} | json',
                ))

        return findings


def _to_int(val) -> int:
    try:
        return int(val)
    except (ValueError, TypeError):
        return 0


def _to_float(val) -> float:
    try:
        return float(val)
    except (ValueError, TypeError):
        return 0.0
