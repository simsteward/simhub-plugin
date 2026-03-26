"""Detect plugin lifecycle events and restart loops."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class PluginLifecycleDetector(BaseDetector):
    name = "plugin_lifecycle"
    category = "app"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        lifecycle = cache.get("ss_lifecycle")

        if not lifecycle:
            return findings

        plugin_started_count = 0
        has_deploy_marker = False
        has_plugin_ready = False
        deploy_ts: float | None = None
        ready_ts: float | None = None

        for line in lifecycle:
            event = line.get("event", "")

            if event == "plugin_started":
                plugin_started_count += 1
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title="Plugin started",
                    summary="sim-steward plugin started (100% logging)",
                    category=self.category,
                    evidence={"event": event, "message": line.get("message", "")},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward"} | json | event="plugin_started"',
                ))

            elif event == "iracing_connected":
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title="iRacing connected",
                    summary="iRacing SDK connection established",
                    category=self.category,
                    evidence={"event": event},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward"} | json | event="iracing_connected"',
                ))

            elif event == "iracing_disconnected":
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title="iRacing disconnected",
                    summary="iRacing SDK connection lost",
                    category=self.category,
                    evidence={"event": event},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward"} | json | event="iracing_disconnected"',
                ))

            elif event == "bridge_start_failed":
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title="Bridge start failed",
                    summary=f"WebSocket bridge failed to start: {line.get('message', '')}",
                    category=self.category,
                    evidence={"event": event, "message": line.get("message", "")},
                    escalate_to_t2=True,
                    logql_query='{app="sim-steward"} | json | event="bridge_start_failed"',
                ))

            elif event == "deploy_marker":
                has_deploy_marker = True
                deploy_ts = _parse_ts(line)

            elif event == "plugin_ready":
                has_plugin_ready = True
                ready_ts = _parse_ts(line)

        # Restart loop: 2+ starts in one window
        if plugin_started_count >= 2:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"Plugin restart loop ({plugin_started_count} starts)",
                summary=f"Plugin started {plugin_started_count} times in window — possible crash loop",
                category=self.category,
                evidence={"plugin_started_count": plugin_started_count},
                escalate_to_t2=True,
                logql_query='{app="sim-steward"} | json | event="plugin_started"',
            ))

        # Deploy without ready within 60s
        if has_deploy_marker and not has_plugin_ready:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title="Deploy without plugin_ready",
                summary="deploy_marker seen but plugin_ready not received within window",
                category=self.category,
                evidence={"deploy_marker": True, "plugin_ready": False},
                escalate_to_t2=False,
                logql_query='{app="sim-steward"} | json | event=~"deploy_marker|plugin_ready"',
            ))
        elif has_deploy_marker and has_plugin_ready and deploy_ts and ready_ts:
            gap_sec = ready_ts - deploy_ts
            if gap_sec > 60:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Slow deploy: plugin_ready {gap_sec:.0f}s after deploy",
                    summary=f"plugin_ready arrived {gap_sec:.0f}s after deploy_marker (threshold: 60s)",
                    category=self.category,
                    evidence={"gap_sec": round(gap_sec, 1)},
                    escalate_to_t2=False,
                    logql_query='{app="sim-steward"} | json | event=~"deploy_marker|plugin_ready"',
                ))

        return findings


def _parse_ts(line: dict) -> float | None:
    raw = line.get("timestamp")
    if raw is None:
        return None
    try:
        return float(raw)
    except (ValueError, TypeError):
        pass
    try:
        from datetime import datetime
        dt = datetime.fromisoformat(str(raw).replace("Z", "+00:00"))
        return dt.timestamp()
    except Exception:
        return None
