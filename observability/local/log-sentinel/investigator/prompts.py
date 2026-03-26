"""Prompt templates and gather specifications for the investigator."""

from dataclasses import dataclass, field


@dataclass
class GatherQuery:
    label: str
    logql: str
    lookback_sec: int = 300
    limit: int = 100


# ── Investigation prompt ──

INVESTIGATION_PROMPT = """\
FINDING: {title}
DETECTOR: {detector}
SEVERITY: {severity}
SUMMARY: {summary}

EVIDENCE:
{evidence}

GATHERED CONTEXT:
{context}

Analyze the finding and gathered context. Respond with EXACTLY these sections:

ROOT_CAUSE: <one-paragraph root cause analysis>
CORRELATION: <related events or patterns that support the diagnosis>
IMPACT: <user-facing impact and affected workflows>
RECOMMENDATION: <specific actionable fix, config change, or next step>
CONFIDENCE: <low|medium|high>
ISSUE_TYPE: <bug|config|performance|security|unknown>
"""


# ── Pattern analysis prompt (proactive T2) ──

PATTERN_ANALYSIS_PROMPT = """\
RECENT FINDINGS ({count} in last {window_min} minutes):
{finding_summaries}

Analyze these findings for cross-cutting patterns, systemic issues, or escalating trends.
Respond with EXACTLY these sections:

ROOT_CAUSE: <systemic root cause if one exists, or "no common root cause">
CORRELATION: <connections between findings — shared actions, timing clusters, cascading failures>
IMPACT: <aggregate user impact across all findings>
RECOMMENDATION: <highest-priority fix addressing the most findings>
CONFIDENCE: <low|medium|high>
ISSUE_TYPE: <bug|config|performance|security|unknown>
"""


# ── Gather specifications per detector ──
# Each detector maps to a list of GatherQuery objects whose results provide
# context for the LLM investigation.  Queries are aligned to the cache keys
# defined in query_cache.QUERIES so results are already warm when possible.

GATHER_SPECS: dict[str, list[GatherQuery]] = {
    # ── app detectors ──
    "action_failure": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("action_dispatched", '{app="sim-steward", event="action_dispatched"} | json', 300, 200),
        GatherQuery("errors", '{app="sim-steward", level="ERROR"} | json', 300, 50),
    ],
    "error_spike": [
        GatherQuery("errors", '{app="sim-steward", level="ERROR"} | json', 300, 200),
        GatherQuery("warnings", '{app="sim-steward", level="WARN"} | json', 300, 100),
        GatherQuery("lifecycle", '{app="sim-steward"} | json | event=~"plugin_started|plugin_ready|plugin_stopped|deploy_marker"', 300, 50),
    ],
    "silent_session": [
        GatherQuery("all_events", '{app="sim-steward"} | json', 300, 200),
        GatherQuery("ws_events", '{app="sim-steward"} | json | event=~"ws_client_connected|ws_client_disconnected|ws_client_rejected"', 300, 50),
        GatherQuery("lifecycle", '{app="sim-steward"} | json | event=~"iracing_connected|iracing_disconnected|plugin_ready"', 300, 50),
    ],
    "stuck_user": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("ui_events", '{app="sim-steward", event="dashboard_ui_event"} | json', 300, 100),
        GatherQuery("errors", '{app="sim-steward", level="ERROR"} | json', 300, 50),
    ],
    "websocket_health": [
        GatherQuery("ws_events", '{app="sim-steward"} | json | event=~"ws_client_connected|ws_client_disconnected|ws_client_rejected|bridge_start_failed"', 300, 200),
        GatherQuery("lifecycle", '{app="sim-steward"} | json | event=~"plugin_started|plugin_ready|bridge_starting"', 300, 50),
        GatherQuery("errors", '{app="sim-steward", level="ERROR"} | json', 300, 50),
    ],
    # ── ops detectors ──
    "claude_session": [
        GatherQuery("lifecycle", '{app="claude-dev-logging", component="lifecycle"} | json', 300, 200),
        GatherQuery("tools", '{app="claude-dev-logging", component=~"tool|mcp-.*"} | json', 300, 100),
        GatherQuery("errors", '{app="claude-dev-logging", level="ERROR"} | json', 300, 50),
    ],
    "claude_tool_failure": [
        GatherQuery("tools", '{app="claude-dev-logging", component=~"tool|mcp-.*"} | json', 300, 200),
        GatherQuery("errors", '{app="claude-dev-logging", level="ERROR"} | json', 300, 50),
        GatherQuery("lifecycle", '{app="claude-dev-logging", component="lifecycle"} | json', 300, 50),
    ],
    "claude_token_burn": [
        GatherQuery("tokens", '{app="claude-token-metrics"} | json', 300, 200),
        GatherQuery("lifecycle", '{app="claude-dev-logging", component="lifecycle"} | json', 300, 50),
        GatherQuery("agents", '{app="claude-dev-logging", component="agent"} | json', 300, 50),
    ],
    "claude_agent_loop": [
        GatherQuery("agents", '{app="claude-dev-logging", component="agent"} | json', 300, 200),
        GatherQuery("tools", '{app="claude-dev-logging", component=~"tool|mcp-.*"} | json', 300, 100),
        GatherQuery("lifecycle", '{app="claude-dev-logging", component="lifecycle"} | json', 300, 50),
    ],
    "claude_error_spike": [
        GatherQuery("errors", '{app="claude-dev-logging", level="ERROR"} | json', 300, 200),
        GatherQuery("lifecycle", '{app="claude-dev-logging", component="lifecycle"} | json', 300, 50),
        GatherQuery("tools", '{app="claude-dev-logging", component=~"tool|mcp-.*"} | json', 300, 100),
    ],
    # ── flow-based detectors ──
    "flow_session_health": [
        GatherQuery("ws_events", '{app="sim-steward"} | json | event=~"ws_client_connected|ws_client_disconnected|ws_client_rejected"', 300, 100),
        GatherQuery("lifecycle", '{app="sim-steward"} | json | event=~"plugin_started|plugin_ready|dashboard_opened"', 300, 50),
    ],
    "flow_review_incident": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("ui_events", '{app="sim-steward", event="dashboard_ui_event"} | json', 300, 100),
    ],
    "flow_walk_driver": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("action_dispatched", '{app="sim-steward", event="action_dispatched"} | json', 300, 200),
        GatherQuery("incidents", '{app="sim-steward", event="incident_detected"} | json', 300, 100),
    ],
    "flow_walk_session": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("action_dispatched", '{app="sim-steward", event="action_dispatched"} | json', 300, 200),
        GatherQuery("incidents", '{app="sim-steward", event="incident_detected"} | json', 300, 100),
    ],
    "flow_capture_incident": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("action_dispatched", '{app="sim-steward", event="action_dispatched"} | json', 300, 100),
    ],
    "flow_transport_controls": [
        GatherQuery("action_results", '{app="sim-steward", event="action_result"} | json', 300, 200),
        GatherQuery("action_dispatched", '{app="sim-steward", event="action_dispatched"} | json', 300, 100),
    ],
}
