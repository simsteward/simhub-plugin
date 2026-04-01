"""Prompt templates and structured output schemas for Log Sentinel v2/v3."""

# ── Stream descriptions injected into every prompt ──────────────────────────

STREAM_DESCRIPTIONS = {
    "sim-steward": (
        "SimHub plugin logs: iRacing session events, user actions (button clicks, "
        "replay controls), WebSocket messages, incident detection, plugin lifecycle. "
        "Key fields: event, domain, component, session_id, subsession_id."
    ),
    "claude-dev-logging": (
        "Claude Code AI agent logs: tool calls (Read, Write, Bash, etc.), "
        "session lifecycle, subagent activity, MCP service calls, token snapshots. "
        "Key fields: event, hook_type, tool_name, service, session_id, duration_ms."
    ),
    "claude-token-metrics": (
        "Claude Code session summaries: one entry per completed AI session. "
        "Fields: total_input_tokens, total_output_tokens, cost_usd, model, effort, "
        "assistant_turns, tool_use_count, session_id."
    ),
}

# ── T1 prompts ───────────────────────────────────────────────────────────────

T1_SYSTEM = """\
You are a log analyst for a SimHub iRacing plugin system that integrates with an AI coding assistant.
You analyze structured JSON logs from three streams to identify what happened and what looks wrong.

Stream guide:
{stream_guide}

Always respond with valid JSON only. No markdown, no explanation outside the JSON object.\
"""

T1_SUMMARY_PROMPT = """\
Analyze the following log activity from the past {window_minutes} minutes.

LOG COUNTS (total lines per stream):
{counts}

RECENT LOGS — sim-steward ({sim_steward_count} lines shown):
{sim_steward_sample}

RECENT LOGS — claude-dev-logging ({claude_dev_count} lines shown):
{claude_dev_sample}

RECENT LOGS — claude-token-metrics ({claude_token_count} lines shown):
{claude_token_sample}

Respond with this JSON schema exactly:
{{
  "summary": "<2-3 sentence narrative of what happened this window>",
  "cycle_notes": "<anything unusual worth flagging for deeper analysis, or empty string>"
}}
"""

T1_ANOMALY_PROMPT = """\
You have already summarized this window:
{summary}

Now analyze the same logs for anomalies. Look for:
- Error spikes or unexpected ERROR/WARN levels
- Gaps in expected activity (e.g. session started but no actions followed)
- Unusual token costs or AI session patterns
- WebSocket disconnects, action failures, plugin crashes
- Anything that deviates from normal healthy operation

LOG COUNTS:
{counts}

RECENT LOGS — sim-steward:
{sim_steward_sample}

RECENT LOGS — claude-dev-logging:
{claude_dev_sample}

RECENT LOGS — claude-token-metrics:
{claude_token_sample}

Respond with this JSON schema exactly:
{{
  "anomalies": [
    {{
      "id": "<short-slug-no-spaces>",
      "stream": "<sim-steward|claude-dev-logging|claude-token-metrics>",
      "description": "<what you noticed and why it is anomalous>",
      "severity": "<info|warn|critical>",
      "needs_t2": <true|false>,
      "suggested_logql": "<a LogQL query to investigate further, or empty string>"
    }}
  ]
}}

Return an empty anomalies array if nothing looks wrong. Do not invent anomalies.
"""

# ── T2 prompts ───────────────────────────────────────────────────────────────

T2_SYSTEM = """\
You are a senior site reliability engineer investigating anomalies in a SimHub iRacing plugin system.
You have been given anomaly flags, a chronological event timeline, and raw log evidence from targeted queries.
Your job: determine root cause, identify cross-stream correlations, and provide concrete actionable recommendations.

Stream guide:
{stream_guide}

Always respond with valid JSON only. No markdown, no explanation outside the JSON object.\
"""

T2_INVESTIGATION_PROMPT = """\
ANOMALIES TO INVESTIGATE:
{anomaly_descriptions}

EVENT TIMELINE (past {window_minutes} minutes, chronological across all streams):
{timeline_text}

TARGETED LOG QUERIES AND RESULTS:
{logql_results}

Based on all of the above, respond with this JSON schema exactly:
{{
  "root_cause": "<specific root cause, referencing evidence from the timeline and queries>",
  "issue_type": "<error_spike|config|regression|user_behavior|infra|unknown>",
  "confidence": "<high|medium|low>",
  "correlation": "<how events across streams connect — what cross-stream evidence links the anomalies>",
  "impact": "<what is affected and how>",
  "recommendation": "<concrete next steps to resolve or monitor — be specific>",
  "logql_queries_used": {logql_queries_list},
  "sentry_worthy": <true if this warrants a Sentry issue — critical bugs, data loss, crashes; false otherwise>
}}
"""

# ── LogQL generation prompt ──────────────────────────────────────────────────

LOGQL_GEN_SYSTEM = """\
You are a Loki LogQL expert. Generate precise LogQL queries to investigate anomalies.
Always respond with a valid JSON array of strings only. No explanation.\
"""

LOGQL_GEN_PROMPT = """\
Generate up to 5 LogQL queries to investigate these anomalies:
{anomaly_descriptions}

Available streams (use exact app label values):
- {{app="sim-steward"}} — plugin actions, iRacing events
- {{app="claude-dev-logging"}} — AI agent tool calls, lifecycle
- {{app="claude-token-metrics"}} — AI session token summaries

Time window: past {window_minutes} minutes.

Rules:
- Every query must start with {{ and contain at least one |
- Use | json to parse JSON log lines
- Use | level = "ERROR" or | event = "..." to filter
- Keep queries focused and specific to the anomalies

Respond with a JSON array of strings:
["<logql query 1>", "<logql query 2>", ...]
"""


# ── Helper: build formatted stream guide ────────────────────────────────────

def build_stream_guide() -> str:
    return "\n".join(
        f"  {app}: {desc}" for app, desc in STREAM_DESCRIPTIONS.items()
    )


# ── Helper: format log sample for prompt ────────────────────────────────────

def format_log_sample(lines: list[dict], max_lines: int = 30) -> str:
    import json
    if not lines:
        return "  (no logs in this window)"
    shown = lines[-max_lines:]  # most recent
    return "\n".join(f"  {json.dumps(line, default=str)}" for line in shown)


# ── Helper: format LogQL results for T2 prompt ──────────────────────────────

def format_logql_results(results: dict[str, list[dict]]) -> str:
    import json
    if not results:
        return "  (no additional queries executed)"
    sections = []
    for query, lines in results.items():
        if not lines:
            sections.append(f"=== {query} ===\n  (0 results)")
        else:
            formatted = "\n".join(
                f"  {json.dumps(line, default=str)}" for line in lines[:50]
            )
            sections.append(f"=== {query} ===\n{formatted}")
    return "\n\n".join(sections)


# ── v3: Feature invocation formatter ────────────────────────────────────────

def format_invocations(invocations, max_invocations: int = 15) -> str:
    """Format FeatureInvocation list for injection into T1 prompt."""
    if not invocations:
        return "  (no feature invocations detected this window)"

    shown = invocations[:max_invocations]
    lines = []
    for inv in shown:
        status = "FAILED" if inv.success is False else ("OK" if inv.success else "?")
        err = f"  error={inv.error[:60]}" if inv.error else ""
        lines.append(
            f"  [{status}] {inv.action_type} via {inv.correlation_method} "
            f"({inv.duration_ms}ms, {len(inv.events)} events){err}"
        )
    if len(invocations) > max_invocations:
        lines.append(f"  [... {len(invocations) - max_invocations} more invocations not shown]")
    return "\n".join(lines)


def format_evidence_packets_for_t2(packet_dicts: list[dict]) -> str:
    """Format Loki-serialized evidence packet metadata for T2 prompt."""
    if not packet_dicts:
        return "  (no evidence packets available)"
    lines = []
    for p in packet_dicts:
        lines.append(
            f"  [{p.get('severity', '?').upper()}] anomaly_id={p.get('anomaly_id', '?')} "
            f"stream={p.get('detector_stream', '?')}"
        )
        lines.append(f"    {p.get('anomaly_description', '')[:120]}")
        if p.get("t1_hypothesis"):
            lines.append(f"    T1 hypothesis: {p['t1_hypothesis'][:120]}")
        lines.append(
            f"    confidence={p.get('t1_confidence', 0):.0%} "
            f"related_lines={p.get('related_lines_count', 0)} "
            f"invocations={p.get('invocation_count', 0)}"
        )
        if p.get("suggested_logql"):
            lines.append(f"    suggested_logql: {p['suggested_logql'][:120]}")
        lines.append("")
    return "\n".join(lines)


# ── v3: T1 anomaly prompt with invocations + baseline context ────────────────

T1_ANOMALY_PROMPT_V3 = """\
You have already summarized this window:
{summary}

FEATURE INVOCATIONS (user actions traced end-to-end this window):
{invocations_text}

BASELINE CONTEXT (historical normal values — use to judge what is anomalous):
{baseline_context}

Now analyze the logs for anomalies. Look for:
- Error spikes or unexpected ERROR/WARN levels
- Failed feature invocations (action_type FAILED)
- Gaps in expected activity (e.g. session started but no actions followed)
- Unusual token costs or AI session patterns
- WebSocket disconnects, action failures, plugin crashes
- Metrics exceeding baselines by 3x or more
- Anything deviating from historical normal operation

LOG COUNTS:
{counts}

RECENT LOGS — sim-steward:
{sim_steward_sample}

RECENT LOGS — claude-dev-logging:
{claude_dev_sample}

RECENT LOGS — claude-token-metrics:
{claude_token_sample}

Respond with this JSON schema exactly:
{{
  "anomalies": [
    {{
      "id": "<short-slug-no-spaces>",
      "stream": "<sim-steward|claude-dev-logging|claude-token-metrics>",
      "event_type": "<specific event type if relevant, or empty string>",
      "description": "<what you noticed and why it is anomalous>",
      "severity": "<info|warn|critical>",
      "needs_t2": <true|false>,
      "hypothesis": "<one-sentence best-guess root cause, or empty string>",
      "confidence": <0.0 to 1.0>,
      "trace_id": "<trace_id if anomaly is linked to a specific invocation, else empty>",
      "suggested_logql": "<a LogQL query to investigate further, or empty string>"
    }}
  ]
}}

Return an empty anomalies array if nothing looks wrong. Do not invent anomalies.
"""


# ── v3: T2 evidence-packet prompts ──────────────────────────────────────────

T2_EVIDENCE_SYSTEM = """\
You are a senior site reliability engineer investigating anomalies in a SimHub iRacing plugin system.
You have been given pre-assembled evidence packets from T1 fast triage, plus relevant Sentry history.
Your job: validate T1 hypotheses, determine root cause, identify cross-stream correlations, and provide
concrete actionable recommendations.

Stream guide:
{stream_guide}

Always respond with valid JSON only. No markdown, no explanation outside the JSON object.\
"""

T2_EVIDENCE_PROMPT = """\
EVIDENCE PACKETS FROM T1 TRIAGE:
{evidence_text}

SENTRY HISTORY (existing issues matching these anomaly signatures):
{sentry_context}

ADDITIONAL LOG EVIDENCE (from targeted LogQL queries):
{logql_results}

Based on all of the above, respond with this JSON schema exactly:
{{
  "root_cause": "<specific root cause referencing evidence from packets and queries>",
  "issue_type": "<error_spike|config|regression|user_behavior|infra|unknown>",
  "confidence": "<high|medium|low>",
  "correlation": "<how events across streams connect — cross-stream evidence>",
  "impact": "<what is affected and how>",
  "recommendation": "<concrete next steps to resolve or monitor — be specific>",
  "sentry_worthy": <true if this warrants a Sentry issue — behavioral bugs, patterns, crashes; false otherwise>,
  "sentry_fingerprint": "<short fingerprint slug for Sentry dedup, or empty if not sentry_worthy>",
  "logql_queries_used": []
}}
"""


# ── v3: T3 synthesis prompts ─────────────────────────────────────────────────

T3_SYSTEM = """\
You are a systems analyst synthesizing log data, anomaly findings, and Sentry history
for a SimHub iRacing plugin with an integrated AI coding assistant.

Your goal: answer "What was the user trying to do, and did it work?"
Produce a human-readable synthesis covering sessions, patterns, costs, regressions, and health.

Stream guide:
{stream_guide}

Always respond with valid JSON only. No markdown, no explanation outside the JSON object.\
"""

T3_SYNTHESIS_PROMPT = """\
SYNTHESIS WINDOW: {window_description}
MODE: {mode}

T1 EVIDENCE PACKETS (anomalies found this period):
{evidence_summary}

T2 INVESTIGATIONS (deep findings this period):
{investigation_summary}

OPEN SENTRY ISSUES:
{sentry_issues}

RECENT RELEASES:
{recent_releases}

SESSION NARRATIVES:
{session_narratives}

Respond with this JSON schema exactly:
{{
  "period_summary": "<2-3 sentence overview of the period>",
  "sessions_analyzed": <count>,
  "features_worked": ["<feature1>", ...],
  "features_failed": ["<feature1>", ...],
  "recurring_patterns": [
    {{
      "pattern": "<description>",
      "occurrences": <count>,
      "first_seen": "<ISO timestamp or relative>",
      "recommendation": "<action to take>"
    }}
  ],
  "cost_summary": {{
    "sessions": <count>,
    "total_usd": <float>,
    "mean_per_session_usd": <float>,
    "trend": "<up|down|stable>"
  }},
  "regression_detected": <true|false>,
  "regression_detail": "<empty string if none, else what regressed and when>",
  "action_items": ["<item1>", "<item2>"],
  "baselines_need_update": <true|false>
}}
"""
