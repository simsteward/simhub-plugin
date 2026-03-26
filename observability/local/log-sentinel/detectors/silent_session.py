"""Tier 1 detector: active iRacing session with no meaningful events."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward"} | json'

# Events that indicate actual activity (not just heartbeat/resource sampling)
ACTIVITY_DOMAINS = {"action", "iracing", "ui"}
HEARTBEAT_EVENTS = {"host_resource_sample"}

DEFAULT_SILENT_THRESHOLD_SEC = 300


class SilentSessionDetector(BaseDetector):
    name = "silent_session"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)
        if not lines:
            return []

        # Check for active iRacing session (connected without subsequent disconnect)
        session_active = False
        has_activity = False
        total_events = 0
        heartbeat_only_count = 0

        for line in lines:
            event = line.get("event", "")
            domain = line.get("domain", "")
            total_events += 1

            if event == "iracing_connected":
                session_active = True
            elif event == "iracing_disconnected":
                session_active = False

            if event in HEARTBEAT_EVENTS:
                heartbeat_only_count += 1
            elif domain in ACTIVITY_DOMAINS:
                has_activity = True

        if not session_active:
            return []

        # Session is active but only heartbeat events seen
        if not has_activity and total_events > 0:
            # Escalate if silent for the entire lookback window
            escalate = window.duration_sec >= DEFAULT_SILENT_THRESHOLD_SEC

            return [
                Finding(
                    detector=self.name,
                    severity="warn",
                    title="Silent iRacing session detected",
                    summary=(
                        f"iRacing session is active but no action/iracing/ui events "
                        f"detected in the last {window.duration_sec}s. "
                        f"Only {heartbeat_only_count} heartbeat events seen."
                    ),
                    evidence={
                        "window_sec": window.duration_sec,
                        "total_events": total_events,
                        "heartbeat_events": heartbeat_only_count,
                        "activity_events": 0,
                    },
                    escalate_to_t2=escalate,
                    logql_query=QUERY,
                )
            ]

        return []
