"""Detect repetitive tool use, retry loops, and agent nesting anomalies."""

from collections import Counter, defaultdict

from detectors.base import BaseDetector
from models import Finding
from query_cache import CycleQueryCache


class AgentLoopDetector(BaseDetector):
    name = "agent_loop"
    category = "ops"

    def detect(self, cache: CycleQueryCache) -> list[Finding]:
        findings: list[Finding] = []
        tools = cache.get("claude_tools")
        agents = cache.get("claude_agents")

        self._check_repetitive_tools(tools, findings)
        self._check_retry_loops(tools, findings)
        self._check_orphan_agents(agents, findings)
        self._check_deep_nesting(agents, findings)

        return findings

    def _check_repetitive_tools(self, tools: list[dict], findings: list[Finding]):
        """Same tool 15+ times in one session -> warn."""
        session_tool_counts: dict[tuple[str, str], int] = Counter()
        for line in tools:
            hook = line.get("hook_type", "")
            if hook not in ("post-tool-use", "post-tool-use-failure"):
                continue
            sid = line.get("session_id", "unknown")
            tool = line.get("tool_name", "unknown")
            session_tool_counts[(sid, tool)] += 1

        for (sid, tool), count in session_tool_counts.items():
            if count >= 15:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title=f"Repetitive tool use: {tool} {count}x",
                    summary=f"Session {sid} called {tool} {count} times — possible loop",
                    category=self.category,
                    evidence={
                        "session_id": sid,
                        "tool_name": tool,
                        "call_count": count,
                    },
                    escalate_to_t2=True,
                    logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
                ))

    def _check_retry_loops(self, tools: list[dict], findings: list[Finding]):
        """More than 3 is_retry=true events -> warn."""
        retry_count = 0
        for line in tools:
            is_retry = line.get("is_retry")
            if is_retry is True or is_retry == "true":
                retry_count += 1

        if retry_count > 3:
            findings.append(Finding(
                detector=self.name,
                severity="warn",
                title="Tool retry loop",
                summary=f"{retry_count} tool retries detected — possible stuck loop",
                category=self.category,
                evidence={"retry_count": retry_count},
                escalate_to_t2=True,
                logql_query='{app="claude-dev-logging", component=~"tool|mcp-.*"} | json',
            ))

    def _check_orphan_agents(self, agents: list[dict], findings: list[Finding]):
        """subagent-start without matching subagent-stop -> warn."""
        started: dict[str, dict] = {}
        stopped: set[str] = set()

        for line in agents:
            hook = line.get("hook_type", "")
            agent_id = line.get("agent_id", "")
            if not agent_id:
                continue
            if hook == "subagent-start":
                started[agent_id] = line
            elif hook == "subagent-stop":
                stopped.add(agent_id)

        for agent_id, line in started.items():
            if agent_id not in stopped:
                findings.append(Finding(
                    detector=self.name,
                    severity="warn",
                    title="Long-running agent",
                    summary=f"Subagent {agent_id} started but has no matching stop event",
                    category=self.category,
                    evidence={
                        "agent_id": agent_id,
                        "session_id": line.get("session_id", ""),
                    },
                    escalate_to_t2=True,
                    logql_query='{app="claude-dev-logging", component="agent"} | json',
                ))

    def _check_deep_nesting(self, agents: list[dict], findings: list[Finding]):
        """agent_depth >= 3 -> info."""
        seen_depths: set[tuple[str, int]] = set()

        for line in agents:
            depth = _safe_int(line.get("agent_depth"))
            agent_id = line.get("agent_id", "unknown")
            if depth >= 3 and (agent_id, depth) not in seen_depths:
                seen_depths.add((agent_id, depth))
                findings.append(Finding(
                    detector=self.name,
                    severity="info",
                    title=f"Deep agent nesting (depth={depth})",
                    summary=f"Agent {agent_id} reached nesting depth {depth}",
                    category=self.category,
                    evidence={
                        "agent_id": agent_id,
                        "agent_depth": depth,
                        "session_id": line.get("session_id", ""),
                    },
                    logql_query='{app="claude-dev-logging", component="agent"} | json',
                ))


def _safe_int(val) -> int:
    try:
        return int(val)
    except (TypeError, ValueError):
        return 0
