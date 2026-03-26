"""Investigation prompt template and gather query specifications."""

from dataclasses import dataclass


@dataclass
class GatherQuery:
    label: str
    logql: str
    lookback_sec: int = 600
    limit: int = 30


INVESTIGATION_PROMPT = """\
FINDING from detector "{detector}" (severity: {severity}):
{title}

SUMMARY: {summary}

EVIDENCE:
{formatted_evidence}

ADDITIONAL CONTEXT (gathered from Loki):
{formatted_context}

Analyze the above finding and context. Respond with EXACTLY these sections:

ROOT_CAUSE: <one paragraph explaining the most likely root cause>
CORRELATION: <one paragraph on how log events correlate to explain this issue>
IMPACT: <one paragraph on user-facing impact and affected workflows>
RECOMMENDATION: <one paragraph with specific remediation steps>
CONFIDENCE: <low|medium|high>\
"""


# ── Gather specifications per detector ──
# Each detector maps to a list of GatherQuery objects that the gather phase
# executes against Loki to build context for the LLM.

GATHER_SPECS: dict[str, list[GatherQuery]] = {
    "error_spike": [
        GatherQuery(
            label="recent_errors",
            logql='{app="sim-steward", level="ERROR"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="lifecycle_context",
            logql='{app="sim-steward", domain="lifecycle"} | json',
            lookback_sec=600,
            limit=20,
        ),
        GatherQuery(
            label="action_activity",
            logql='{app="sim-steward", event="action_result"} | json',
            lookback_sec=600,
            limit=20,
        ),
        GatherQuery(
            label="host_resources",
            logql='{app="sim-steward", event="host_resource_sample"} | json',
            lookback_sec=600,
            limit=10,
        ),
    ],
    "action_failure": [
        GatherQuery(
            label="failed_actions",
            logql='{app="sim-steward", event="action_result"} | json | fields_success != "true"',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="all_actions_context",
            logql='{app="sim-steward", event=~"action_dispatched|action_result"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="ws_health",
            logql='{app="sim-steward", event=~"ws_client_connected|ws_client_disconnected|bridge_start_failed|bridge_stopped"} | json',
            lookback_sec=600,
            limit=20,
        ),
    ],
    "websocket_health": [
        GatherQuery(
            label="ws_events",
            logql='{app="sim-steward", event=~"ws_client_connected|ws_client_disconnected|bridge_start_failed|bridge_stopped"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="errors",
            logql='{app="sim-steward", level="ERROR"} | json',
            lookback_sec=600,
            limit=20,
        ),
    ],
    "silent_session": [
        GatherQuery(
            label="all_events",
            logql='{app="sim-steward"} | json',
            lookback_sec=900,
            limit=30,
        ),
        GatherQuery(
            label="lifecycle_events",
            logql='{app="sim-steward", domain="lifecycle"} | json',
            lookback_sec=900,
            limit=20,
        ),
    ],
    "stuck_user": [
        GatherQuery(
            label="repeated_actions",
            logql='{app="sim-steward", event=~"action_dispatched|action_result"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="dashboard_ui_events",
            logql='{app="sim-steward", event="dashboard_ui_event"} | json',
            lookback_sec=600,
            limit=20,
        ),
    ],
    "incident_anomaly": [
        GatherQuery(
            label="incidents",
            logql='{app="sim-steward", event="incident_detected"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="session_digest",
            logql='{app="sim-steward", event="session_digest"} | json',
            lookback_sec=600,
            limit=10,
        ),
    ],
    "flow_gap": [
        GatherQuery(
            label="all_non_heartbeat",
            logql='{app="sim-steward", event!="host_resource_sample"} | json',
            lookback_sec=600,
            limit=30,
        ),
        GatherQuery(
            label="action_dispatched",
            logql='{app="sim-steward", event="action_dispatched"} | json',
            lookback_sec=600,
            limit=20,
        ),
    ],
}
