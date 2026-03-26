"""Detect WebSocket connectivity problems."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class WebSocketHealthDetector(BaseDetector):
    name = "websocket_health"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        ws_events = cache.get("ss_ws")

        if not ws_events:
            return findings

        connects = 0
        disconnects = 0

        for line in ws_events:
            event = line.get("event", "")

            if event == "ws_client_rejected":
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title="WebSocket client rejected",
                    summary=f"A WebSocket client was rejected: {line.get('message', '')}",
                    category=self.category,
                    evidence={"event": event, "message": line.get("message", "")},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward"} | json | event="ws_client_rejected"',
                ))

            elif event == "bridge_start_failed":
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title="WebSocket bridge failed to start",
                    summary=f"Bridge start failed: {line.get('message', '')}",
                    category=self.category,
                    evidence={"event": event, "message": line.get("message", "")},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward"} | json | event="bridge_start_failed"',
                ))

            elif event == "ws_client_connected":
                connects += 1
            elif event == "ws_client_disconnected":
                disconnects += 1

        # Disconnect:connect ratio check
        if disconnects >= 3 and connects > 0 and disconnects / connects > 2:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"High disconnect ratio ({disconnects}:{connects})",
                summary=f"{disconnects} disconnects vs {connects} connects — possible instability",
                category=self.category,
                evidence={
                    "connects": connects,
                    "disconnects": disconnects,
                    "ratio": round(disconnects / connects, 2),
                },
                escalate_to_t2=False,
                logql_query='{app="sim-steward"} | json | event=~"ws_client_connected|ws_client_disconnected"',
            ))

        return findings
