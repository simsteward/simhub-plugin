"""Detect sessions that go silent — iRacing connected but no meaningful events."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class SilentSessionDetector(BaseDetector):
    name = "silent_session"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        all_events = cache.get("ss_all")

        if not all_events:
            return findings

        has_iracing_connected = False
        has_iracing_disconnected = False
        resource_only = True

        for line in all_events:
            event = line.get("event", "")

            if event == "iracing_connected":
                has_iracing_connected = True
            elif event == "iracing_disconnected":
                has_iracing_disconnected = True

            if event and event != "host_resource_sample":
                resource_only = False

        # Session active (connected without disconnect) but only resource samples
        session_active = has_iracing_connected and not has_iracing_disconnected

        if session_active and resource_only:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title="Silent session detected",
                summary="iRacing connected but only host_resource_sample events seen — no actions, incidents, or lifecycle events",
                category=self.category,
                evidence={
                    "total_events": len(all_events),
                    "session_active": True,
                    "resource_only": True,
                },
                escalate_to_t2=True,
                logql_query='{app="sim-steward"} | json',
            ))

        return findings
