"""Detect MCP tool failures and slow calls."""

from collections import Counter

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class McpHealthDetector(BaseDetector):
    name = "mcp_health"
    category = "ops"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        tools = cache.get("claude_tools")

        mcp_failures: list[dict] = []
        mcp_calls_by_service: Counter = Counter()
        slow_calls: list[dict] = []

        for line in tools:
            tool_name = line.get("tool_name", "")
            if not tool_name.startswith("mcp__"):
                continue

            hook = line.get("hook_type", "")
            service = _extract_service(tool_name)
            duration_ms = _safe_int(line.get("duration_ms"))

            # Count all MCP calls (post-tool-use and post-tool-use-failure)
            if hook in ("post-tool-use", "post-tool-use-failure"):
                mcp_calls_by_service[service] += 1

            # Track failures
            if hook == "post-tool-use-failure":
                mcp_failures.append(line)

            # Slow call detection
            if duration_ms > 30_000:
                slow_calls.append(line)

        # MCP failure findings
        failure_count = len(mcp_failures)
        if failure_count > 0:
            if failure_count >= 3:
                findings.append(Finding(
                    detector=self.name,
                    severity="critical",
                    title=f"MCP failure storm: {failure_count} failures",
                    summary=f"{failure_count} MCP tool failures detected — possible service outage",
                    category=self.category,
                    evidence={
                        "failure_count": failure_count,
                        "tools": [f.get("tool_name", "unknown") for f in mcp_failures],
                    },
                    escalate_to_t2=True,
                    logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
                ))
            else:
                for fail in mcp_failures:
                    findings.append(Finding(
                        detector=self.name,
                        severity="warn",
                        title=f"MCP failure: {fail.get('tool_name', 'unknown')}",
                        summary=f"MCP tool call failed: {fail.get('tool_name', 'unknown')}",
                        category=self.category,
                        evidence={
                            "tool_name": fail.get("tool_name"),
                            "error_type": fail.get("error_type", ""),
                            "session_id": fail.get("session_id", ""),
                        },
                        escalate_to_t2=True,
                        logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
                    ))

        # Slow MCP call findings
        for call in slow_calls:
            duration = _safe_int(call.get("duration_ms"))
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"Slow MCP call: {call.get('tool_name', 'unknown')}",
                summary=f"MCP call took {duration}ms (>{30_000}ms threshold)",
                category=self.category,
                evidence={
                    "tool_name": call.get("tool_name"),
                    "duration_ms": duration,
                    "session_id": call.get("session_id", ""),
                },
                logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
            ))

        # Info: MCP call count per service
        if mcp_calls_by_service:
            findings.append(Finding(
                detector=self.name,
                severity="info",
                title="MCP call summary",
                summary=f"MCP calls by service: {dict(mcp_calls_by_service)}",
                category=self.category,
                evidence={"calls_by_service": dict(mcp_calls_by_service)},
                logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
            ))

        return findings


def _extract_service(tool_name: str) -> str:
    """Extract service name from mcp__<service>__<method> pattern."""
    parts = tool_name.split("__")
    return parts[1] if len(parts) >= 2 else "unknown"


def _safe_int(val) -> int:
    try:
        return int(val)
    except (TypeError, ValueError):
        return 0
