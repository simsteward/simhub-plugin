# Grafana Alerts Design — Log Sentinel Layer 0
**Date:** 2026-03-30
**Status:** Approved

---

## Context

The log-sentinel V2 LLM investigation pipeline (T1 triage + T2 agentic tool loop) is expensive to run continuously — qwen3:8b T1 scan on a 6700 XT takes 60-90 seconds, T2 takes 3-4 minutes. Running this on a fixed hourly poll means real incidents can sit undetected for up to 60 minutes, and the models waste cycles on quiet periods.

Grafana Alerts solves this as **Layer 0**: always-on, no GPU cost, fires webhooks only when something is actually wrong. The sentinel switches from polling to event-driven. When Grafana fires, it delivers structured alert context (labels, values, timeframe) directly in the webhook payload — T1 skips cold-start gathering for the relevant domain and goes straight to targeted investigation.

**Layer 0 (Grafana Alerts) → Layer 1 (T1 fast triage) → Layer 2 (T2 agentic tool loop)**

---

## Alert Architecture

### Transport: Webhook-Only
Grafana alert notifications route exclusively to log-sentinel's `/trigger` HTTP endpoint. No email, Slack, or PagerDuty at this stage. The sentinel logs every trigger, runs the appropriate tier, and emits findings to Loki (queryable by Grafana dashboards).

### Provisioning Structure
All alerts are provisioned as code — no manual UI configuration:
```
observability/local/grafana/provisioning/alerting/
  contact-points.yml           # webhook endpoint definition
  notification-policies.yml    # routing: all alerts → webhook
  rules-infrastructure.yml     # Domains 1+2
  rules-iracing.yml            # Domain 3+7
  rules-claude-sessions.yml    # Domain 4
  rules-token-cost.yml         # Domain 5
  rules-sentinel-health.yml    # Domain 6
  rules-cross-stream.yml       # Domain 8
```

### Trigger Tier Labeling
Every alert rule carries a `trigger_tier` label (`t1` or `t2`). The sentinel's `/trigger` endpoint reads this label and routes accordingly — T1 for most alerts, T2 for critical multi-signal correlations.

---

## Alert Catalog

### Domain 1+2: Infrastructure & Deploy Quality (10 alerts)

| Alert ID | LogQL / Condition | Severity | Tier |
|---|---|---|---|
| `bridge-start-failed` | `count_over_time({app="sim-steward"} \| json \| event="plugin_lifecycle" \| level="ERROR" [5m]) > 0` | critical | T1 |
| `plugin-never-ready` | plugin_lifecycle start, no ready within 60s | warn | T1 |
| `sentinel-cycle-stalled` | No `sentinel_cycle` event in 90 min | critical | T1 |
| `ollama-unreachable` | `sentinel_health` event with `ollama_reachable=false` | critical | T1 |
| `loki-circuit-open` | `sentinel_health` with `loki_circuit_open=true` | critical | T1 |
| `post-deploy-warn-rate` | WARN rate > 5/min in 10 min after lifecycle event | warn | T1 |
| `bridge-failure-post-deploy` | ERROR in sim-steward within 15 min of plugin_start | critical | T1 |
| `plugin-slow-start` | Time from plugin_lifecycle start → ready > 30s | warn | T1 |
| `error-spike-post-deploy` | Error count doubles vs prior 15 min window after deploy | warn | T1 |
| `error-spike-general` | `count_over_time({app="sim-steward"} \| json \| level="ERROR" [10m]) > 10` | warn | T1 |

### Domain 3: iRacing Session Behavior (5 alerts)

| Alert ID | Condition | Severity | Tier |
|---|---|---|---|
| `session-no-actions` | Session active 15+ min, zero `action_dispatched` events | warn | T1 |
| `session-no-end` | `iracing_session_start` with no `iracing_session_end` within 4h | warn | T1 |
| `action-failure-streak` | 3+ consecutive `action_result` errors in same session | critical | T1 |
| `websocket-disconnect-spike` | 3+ `websocket_disconnect` events in 5 min | warn | T1 |
| `incident-detection-zero` | iRacing session > 30 min, zero `iracing_incident` events | warn | T1 |

### Domain 4: Claude Code Session Health (7 alerts)

| Alert ID | Condition | Severity | Tier |
|---|---|---|---|
| `session-abandoned` | Session start, no completion token entry, no activity for 30 min | warn | T1 |
| `claude-error-spike` | 5+ ERROR entries in claude-dev-logging in 5 min | warn | T1 |
| `permission-flood` | 10+ permission-related log entries in 5 min | warn | T1 |
| `subagent-explosion` | Subagent spawn count > 20 in single session | warn | T2 |
| `mcp-service-errors` | MCP call failures > 5 in 10 min | warn | T1 |
| `tool-loop-detected` | Same tool called 5+ times in same session without progress | warn | T2 |
| `session-zero-output` | Session completes (token entry exists), zero assistant messages logged | warn | T1 |

### Domain 5: Token/Cost Budget (7 alerts)

| Alert ID | Condition | Severity | Tier |
|---|---|---|---|
| `session-cost-spike` | Single session cost > $1.00 | warn | T1 |
| `session-cost-critical` | Single session cost > $3.00 | critical | T2 |
| `daily-spend-warning` | Rolling 24h spend > $10.00 | warn | T1 |
| `daily-spend-critical` | Rolling 24h spend > $25.00 | critical | T2 |
| `tool-use-flood` | Tool calls per session > 100 | warn | T1 |
| `unexpected-model` | Model field not in approved set (claude-opus-4, claude-sonnet-4-6, etc.) | warn | T1 |
| `cache-hit-rate-low` | Cache hit rate < 20% over 1h (when sessions active) | info | T1 |

### Domain 6: Sentinel Self-Health (7 alerts)

| Alert ID | Condition | Severity | Tier |
|---|---|---|---|
| `sentinel-cycle-stalled` | No `sentinel_cycle` event in 90 min | critical | T1 |
| `detector-error-rate` | Detector errors > 3 in single cycle | warn | T1 |
| `t1-slow` | T1 inference duration > 120s | warn | T1 |
| `t2-slow` | T2 tool loop duration > 300s | warn | T1 |
| `sentry-flood` | Sentry-worthy findings > 5 in 1h | warn | T1 |
| `findings-flood` | Total findings > 20 in single cycle | warn | T1 |
| `zero-findings-48h` | No findings at all in 48h (system may be suppressing) | info | T1 |

### Domain 7: Replay & Incident Investigation (5 alerts)

| Alert ID | Condition | Severity | Tier |
|---|---|---|---|
| `replay-no-seeks` | Replay started, zero `iracing_replay_seek` in 5 min | warn | T1 |
| `incident-detection-stall` | iRacing session active > 30 min, zero `iracing_incident` events in replay mode | warn | T1 |
| `incident-camera-stuck` | Same `camera_view` on 3+ consecutive incidents | info | T1 |
| `replay-session-no-close` | Replay session start, no session_end within 2h | warn | T1 |
| `action-incident-gap` | Incident detected, no `action_dispatched` within 10 min | info | T1 |

### Domain 8: Cross-Stream Correlation (5 alerts)
*Implemented as multi-query rules using Grafana `math` expressions — fires only when both conditions true simultaneously.*

| Alert ID | Streams | Condition | Severity | Tier |
|---|---|---|---|---|
| `ws-claude-coinflict` | sim-steward + claude-dev-logging | WebSocket disconnect + Claude ERROR in same 5-min window | warn | T2 |
| `session-token-abandon` | claude-dev-logging + claude-token-metrics | Session ERROR + no token entry for that session_id | warn | T2 |
| `action-fail-session-fail` | sim-steward + claude-dev-logging | `action_result` errors + Claude session ERROR within 10 min | critical | T2 |
| `deploy-triple-signal` | all 3 streams | 2+ streams show elevated error rate within 15 min of plugin lifecycle event | critical | T2 |
| `cost-spike-tool-flood` | claude-dev-logging + claude-token-metrics | Tool call count spike + session cost spike in same cycle | warn | T1 |

**Total: 46 alerts across 8 domains.**

---

## `/trigger` Endpoint Design

The log-sentinel app gains a new HTTP endpoint:

```
POST /trigger
Content-Type: application/json

{
  "alerts": [{
    "labels": {
      "alertname": "ws-claude-coinflict",
      "trigger_tier": "t2",
      "severity": "warn"
    },
    "annotations": {
      "summary": "WebSocket disconnects co-occurring with Claude errors",
      "description": "3 ws_disconnect events and 2 Claude ERROR entries in 5-min window ending 14:32:00"
    },
    "startsAt": "2026-03-30T14:32:00Z",
    "endsAt": "0001-01-01T00:00:00Z"
  }]
}
```

Sentinel behavior on receipt:
1. Parse alert labels — extract `alertname`, `trigger_tier`, `severity`
2. Derive lookback window from `startsAt` (default: 30 min before alert fired)
3. If `trigger_tier=t1`: run T1 with alert context injected into summary prompt
4. If `trigger_tier=t2`: run T1 (for context) then immediately run T2 — skip the `needs_t2` gate
5. Deduplicate: if the same `alertname` triggered within `SENTINEL_DEDUP_WINDOW_SEC`, skip
6. Log `sentinel_trigger` event to Loki with alert metadata

Alert context injection into T1 prompt:
```
ALERT CONTEXT (from Grafana):
  Alert: ws-claude-coinflict (warn)
  Fired: 2026-03-30 14:32:00 UTC
  Description: 3 ws_disconnect events and 2 Claude ERROR entries in 5-min window
  → Focus investigation on this signal. Do not suppress even if recent history is quiet.
```

---

## Alert Covenant (Living Document)

**Every behavioral change to the plugin, dashboard, or LLM integration must include a corresponding Grafana alert review.**

When adding or changing:
- A new action handler → check Domain 3 (action-failure-streak thresholds)
- A new Claude integration → check Domain 4 + 5
- A new log event or field → check if it should trigger an alert in the relevant domain
- Removing a log event → check if any alert depends on it (alert will go silent, not fire)

Alert silence ≠ alert passing. Test new alerts by writing a test event to Loki via the gateway and verifying the alert fires within its evaluation window.

**Canonical reference: `docs/RULES-GrafanaAlerts.md`** (to be added to CLAUDE.md)

---

## Implementation Files

### New files
- `observability/local/grafana/provisioning/alerting/contact-points.yml`
- `observability/local/grafana/provisioning/alerting/notification-policies.yml`
- `observability/local/grafana/provisioning/alerting/rules-infrastructure.yml`
- `observability/local/grafana/provisioning/alerting/rules-iracing.yml`
- `observability/local/grafana/provisioning/alerting/rules-claude-sessions.yml`
- `observability/local/grafana/provisioning/alerting/rules-token-cost.yml`
- `observability/local/grafana/provisioning/alerting/rules-sentinel-health.yml`
- `observability/local/grafana/provisioning/alerting/rules-cross-stream.yml`
- `docs/RULES-GrafanaAlerts.md`

### Modified files
- `observability/local/log-sentinel/app.py` — add `POST /trigger` endpoint
- `observability/local/log-sentinel/sentinel.py` — add `trigger_cycle()` method (alert-context-aware T1/T2 dispatch)
- `observability/local/log-sentinel/config.py` — no new fields needed (uses existing dedup window)
- `observability/local/docker-compose.yml` — no changes needed (grafana already provisioned, port 3000)
- `.claude/CLAUDE.md` — add alert covenant reference

---

## Verification

1. **Provisioning loads**: `docker compose up grafana` — check Grafana UI → Alerting → Alert Rules shows all 46 rules
2. **Webhook fires**: Manually set an alert rule to always-firing in Grafana UI, verify `/trigger` receives POST and logs `sentinel_trigger` event to Loki
3. **T1 trigger path**: Confirm T1 runs after a non-critical alert fires, `sentinel_analyst_run` appears in logs with `trigger_source=grafana_alert`
4. **T2 direct trigger**: Confirm T2 runs immediately (skipping `needs_t2` gate) when `trigger_tier=t2` alert fires
5. **Dedup**: Fire same alert twice within dedup window, verify second is silently skipped
6. **Cross-stream rule**: Write test events to both sim-steward and claude-dev-logging streams via Loki push API, verify `ws-claude-coinflict` fires
