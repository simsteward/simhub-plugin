"""Detect tool failure rates, permission friction, and error type spikes."""

from collections import Counter

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class ToolPatternsDetector(BaseDetector):
    name = "tool_patterns"
    category = "ops"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        tools = cache.get("claude_tools")
        all_events = cache.get("claude_all")

        self._check_failure_rates(tools, findings)
        self._check_permission_friction(all_events, findings)
        self._check_error_type_spikes(tools, findings)
        self._check_tool_distribution(tools, findings)

        return findings

    def _check_failure_rates(self, tools: list[dict], findings: list[Finding]):
        """Per-tool failure rate; warn if >15%."""
        success_counts: Counter = Counter()
        failure_counts: Counter = Counter()

        for line in tools:
            hook = line.get("hook_type", "")
            tool = line.get("tool_name", "")
            if not tool:
                continue
            if hook == "post-tool-use":
                success_counts[tool] += 1
            elif hook == "post-tool-use-failure":
                failure_counts[tool] += 1

        all_tools = set(success_counts.keys()) | set(failure_counts.keys())
        for tool in all_tools:
            total = success_counts[tool] + failure_counts[tool]
            if total == 0:
                continue
            fail_rate = (failure_counts[tool] / total) * 100
            if fail_rate > 15:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"{tool}: {fail_rate:.0f}% failure rate",
                    summary=f"{tool} failed {failure_counts[tool]}/{total} calls ({fail_rate:.0f}%)",
                    category=self.category,
                    evidence={
                        "tool_name": tool,
                        "total_calls": total,
                        "failures": failure_counts[tool],
                        "failure_rate_pct": round(fail_rate, 1),
                    },
                    escalate_to_t2=True,
                    logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
                ))

    def _check_permission_friction(self, all_events: list[dict], findings: list[Finding]):
        """More than 5 permission-request events -> info."""
        perm_count = 0
        for line in all_events:
            hook = line.get("hook_type", "")
            if hook == "permission-request":
                perm_count += 1

        if perm_count > 5:
            findings.append(Finding(
                detector=self.name,
                severity="info",
                title=f"Permission friction: {perm_count} requests",
                summary=f"{perm_count} permission requests detected — may slow development flow",
                category=self.category,
                evidence={"permission_request_count": perm_count},
                logql_query='{app="claude-dev-logging"} | json',
            ))

    def _check_error_type_spikes(self, tools: list[dict], findings: list[Finding]):
        """Group failures by error_type; escalate connection_refused."""
        error_types: Counter = Counter()
        for line in tools:
            hook = line.get("hook_type", "")
            if hook == "post-tool-use-failure":
                err = line.get("error_type", "unknown")
                error_types[err] += 1

        for err_type, count in error_types.most_common():
            escalate = err_type == "connection_refused"
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title=f"Error type spike: {err_type} ({count}x)",
                summary=f"Tool error type {err_type!r} occurred {count} times",
                category=self.category,
                evidence={"error_type": err_type, "count": count},
                escalate_to_t2=escalate,
                logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
            ))

    def _check_tool_distribution(self, tools: list[dict], findings: list[Finding]):
        """Info: top-5 tools by call count."""
        call_counts: Counter = Counter()
        for line in tools:
            hook = line.get("hook_type", "")
            tool = line.get("tool_name", "")
            if hook in ("post-tool-use", "post-tool-use-failure") and tool:
                call_counts[tool] += 1

        if not call_counts:
            return

        top5 = call_counts.most_common(5)
        summary_parts = [f"{t} ({c}x)" for t, c in top5]
        findings.append(Finding(
            detector=self.name,
            severity="info",
            title="Tool usage distribution",
            summary=f"Top tools: {', '.join(summary_parts)}",
            category=self.category,
            evidence={
                "top_tools": [{"tool": t, "count": c} for t, c in top5],
                "total_unique_tools": len(call_counts),
            },
            logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
        ))
