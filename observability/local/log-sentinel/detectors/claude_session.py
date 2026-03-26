"""Detect Claude Code session lifecycle events and anomalies."""

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class ClaudeSessionDetector(BaseDetector):
    name = "claude_session"
    category = "ops"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        lifecycle = cache.get("claude_lifecycle")
        tools = cache.get("claude_tools")

        # Build set of session_ids that have post-tool-use events
        sessions_with_tools: set[str] = set()
        for line in tools:
            hook = line.get("hook_type", "")
            sid = line.get("session_id", "")
            if hook == "post-tool-use" and sid:
                sessions_with_tools.add(sid)

        # Track sessions that emitted stop hooks (session-end)
        sessions_with_stop: set[str] = set()

        for line in lifecycle:
            hook = line.get("hook_type", "")
            session_id = line.get("session_id", "unknown")

            if hook == "session-start":
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"New Claude session: {session_id}",
                    summary=f"Claude Code session started: {session_id}",
                    category=self.category,
                    evidence={"session_id": session_id, "hook_type": hook},
                    logql_query='{app="claude-dev-logging", component="lifecycle"} | json',
                ))

            elif hook == "pre-compact":
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"Context compaction in {session_id}",
                    summary=f"Claude Code context compaction triggered in session {session_id}",
                    category=self.category,
                    evidence={"session_id": session_id, "hook_type": hook},
                    logql_query='{app="claude-dev-logging", component="lifecycle"} | json',
                ))

            elif hook == "session-end":
                sessions_with_stop.add(session_id)
                duration_ms = _safe_int(line.get("session_duration_ms"))
                if duration_ms > 7_200_000:
                    hours = round(duration_ms / 3_600_000, 1)
                    findings.append(Finding(
                        detector=self.name,
                        severity="warn",
                        title="Long session >2h",
                        summary=f"Session {session_id} lasted {hours}h ({duration_ms}ms)",
                        category=self.category,
                        evidence={
                            "session_id": session_id,
                            "duration_ms": duration_ms,
                        },
                        escalate_to_t2=True,
                        logql_query='{app="claude-dev-logging", component="lifecycle"} | json',
                    ))

            elif hook == "stop":
                sessions_with_stop.add(session_id)

        # Empty session: stop hook emitted but zero tool-use events
        for sid in sessions_with_stop:
            if sid and sid not in sessions_with_tools:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title="Empty session",
                    summary=f"Session {sid} ended with stop hooks but 0 tool-use events",
                    category=self.category,
                    evidence={"session_id": sid},
                    escalate_to_t2=True,
                    logql_query='{app="claude-dev-logging", component="lifecycle"} | json',
                ))

        return findings


def _safe_int(val) -> int:
    """Convert a value to int, returning 0 on failure."""
    try:
        return int(val)
    except (TypeError, ValueError):
        return 0
