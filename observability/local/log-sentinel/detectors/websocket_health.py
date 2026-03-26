"""Tier 1 detector: WebSocket connection health."""

from detectors.base import BaseDetector
from loki_client import LokiClient
from models import Finding, TimeWindow

QUERY = '{app="sim-steward"} | json'

WS_EVENTS = {
    "ws_client_rejected",
    "ws_client_disconnected",
    "ws_client_connected",
    "bridge_start_failed",
}


class WebSocketHealthDetector(BaseDetector):
    name = "websocket_health"

    def detect(self, loki: LokiClient, window: TimeWindow) -> list[Finding]:
        lines = loki.query_lines(QUERY, window.start_ns, window.end_ns)

        rejected = []
        disconnected = []
        connected = []
        bridge_failed = []

        for line in lines:
            event = line.get("event", "")
            if event not in WS_EVENTS:
                continue
            if event == "ws_client_rejected":
                rejected.append(line)
            elif event == "ws_client_disconnected":
                disconnected.append(line)
            elif event == "ws_client_connected":
                connected.append(line)
            elif event == "bridge_start_failed":
                bridge_failed.append(line)

        findings: list[Finding] = []

        # Bridge start failure — critical
        if bridge_failed:
            findings.append(
                Finding(
                    detector=self.name,
                    severity="critical",
                    title=f"WebSocket bridge failed to start ({len(bridge_failed)}x)",
                    summary=(
                        "The WebSocket bridge failed to start. "
                        "Dashboard communication is unavailable."
                    ),
                    evidence={
                        "failure_count": len(bridge_failed),
                        "messages": [
                            l.get("message", "")[:200] for l in bridge_failed[:5]
                        ],
                    },
                    escalate_to_t2=True,
                    logql_query=QUERY,
                )
            )

        # Client rejected — security concern
        if rejected:
            findings.append(
                Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"WebSocket client rejected ({len(rejected)}x)",
                    summary=(
                        f"{len(rejected)} WebSocket connection(s) were rejected. "
                        "Possible unauthorized access attempt."
                    ),
                    evidence={
                        "rejected_count": len(rejected),
                        "messages": [
                            l.get("message", "")[:200] for l in rejected[:5]
                        ],
                    },
                    escalate_to_t2=True,
                    logql_query=QUERY,
                )
            )

        # Disconnect:connect ratio — flapping detection
        disconnect_count = len(disconnected)
        connect_count = len(connected)
        if disconnect_count >= 3 and connect_count > 0:
            ratio = disconnect_count / connect_count
            if ratio > 2.0:
                findings.append(
                    Finding(
                        detector=self.name,
                        severity="warn",
                        title=(
                            f"WebSocket flapping: {disconnect_count} disconnects "
                            f"vs {connect_count} connects"
                        ),
                        summary=(
                            f"Disconnect:connect ratio is {ratio:.1f}:1 "
                            f"({disconnect_count} disconnects, {connect_count} connects). "
                            "Clients may be rapidly reconnecting."
                        ),
                        evidence={
                            "disconnects": disconnect_count,
                            "connects": connect_count,
                            "ratio": round(ratio, 2),
                        },
                        logql_query=QUERY,
                    )
                )

        return findings
