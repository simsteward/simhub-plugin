---
name: grafana-loki-observability
description: Expert in this project's Grafana/Loki observability stack — Loki, LogQL, dashboards, alerting, OTEL, Sift, Grafana Assistant, and testing. Use when working on Loki, LogQL, Grafana dashboards, alerting, observability stack, OTEL, Sift, Grafana Assistant, session_digest, GRAFANA-LOGGING, provisioned dashboards, local Docker stack, or logging docs/tests.
---

# Grafana / Loki Observability

## Authority

The **Grafana Loki logging plan** (`.cursor/plans/grafana_loki_logging_4a33de1c.plan.md`) and **docs/GRAFANA-LOGGING.md** (when present) are the source of truth. This skill summarizes and extends that for agent use. For full event taxonomy, label schema, and LogQL reference, see `docs/GRAFANA-LOGGING.md`.

## Free-tier limits (Grafana Cloud)

Do not recommend or design anything that violates these:

| Limit | Value | Implication |
|-------|--------|-------------|
| Ingestion rate | 5 MB/s per user | Event-driven logs only; never tick-rate logging. |
| Active streams | 5,000 max | Our 4-label schema yields at most ~24–32 streams — safe. |
| Retention | 14 days (free tier) | Queries and dashboards target this window. |
| Line size | 256 KB hard (Grafana); we self-impose 8 KB | Typical lines &lt; 800 bytes; trim fields if near 8 KB. |
| Volume budget | &lt; 1 MB per race session | Target ~525 entries/session; &lt; 7 MB/month at 30 sessions. |

## Label and event schema

**Labels (exactly four):** `app`, `env`, `component`, `level`.

- `app` = `sim-steward`
- `env` = `production` or `local` (from `SIMSTEWARD_LOG_ENV`)
- `component` = `simhub-plugin` | `bridge` | `tracker` | `dashboard`
- `level` = `INFO` | `WARN` | `ERROR` | `DEBUG` (DEBUG only when `SIMSTEWARD_LOG_DEBUG=1`)

**Never use as labels:** `session_id`, `car_number`, `driver_name`, `correlation_id`, `action` — high-cardinality; keep in log line body only.

**Event taxonomy:** Every log line has an `event` field. Core events include `plugin_started`, `plugin_ready`, `plugin_stopped`, `iracing_connected`, `iracing_disconnected`, `ws_client_connected`, `ws_client_disconnected`, `action_received`, `action_dispatched`, `action_result`, `incident_detected`, `session_digest`, `baseline_established`, `session_reset`, `seek_backward_detected`, and others. See `docs/GRAFANA-LOGGING.md` for the full list and required fields per event.

## LogQL patterns

Reference queries for dashboards and investigations:

- **Command audit (all actions):** `{app="sim-steward", component="simhub-plugin"} | json | event = "action_result"`
- **Failed commands only:** `{app="sim-steward", component="simhub-plugin"} | json | event = "action_result" | success = "false"`
- **Incident timeline:** `{app="sim-steward", component="tracker"} | json | event = "incident_detected"`
- **All errors:** `{app="sim-steward", level="ERROR"}`
- **Lifecycle breadcrumb:** `{app="sim-steward", component="simhub-plugin"} | json | event =~ "plugin_started|plugin_ready|iracing_connected|iracing_disconnected|plugin_stopped"`
- **Trace by correlation_id:** `{app="sim-steward"} | json | correlation_id = "<id>"` (start from `session_digest` or `action_result`)
- **AI / production filter:** Always add `level != "DEBUG"` when using Sift, Grafana Assistant, or natural-language LogQL to avoid debug noise.

## Dashboards

Four provisioned dashboards live under **observability/local/grafana/provisioning/dashboards/** (or equivalent provisioning path):

| Dashboard | Role | Key data |
|-----------|------|----------|
| **command-audit** | Action outcomes and latency | Table: `action_result` with action, success, duration_ms, error, correlation_id; stat for errors; timeseries for volume. |
| **incident-timeline** | Incident events | Logs + table: `incident_detected` (type, driver, car, lap, session_time); timeseries incident rate. |
| **plugin-health** | Errors and lifecycle | ERROR/WARN rate timeseries; all ERROR logs panel; lifecycle breadcrumb. |
| **session-overview** | Session digests and clients | Table: `session_digest` (one row per session); lifecycle logs; ws client gauge. |

To add or change panels: use LogQL with `| json` and the event/field names from the taxonomy. Dashboard JSON is provisioned via `dashboards.yml`.

## Alerting

- **Phase 1:** Optional LogQL-based alerts (e.g. error rate over threshold, count of `event="action_result" | success="false"` in a window). Configure in Grafana Alerting with Loki datasource.
- **Phase 2:** Metric-based alerting via OTEL (simsteward_actions_total, simsteward_plugin_errors_total) will be more reliable; prefer that once implemented.

## Local stack

- **Path:** `observability/local/` — Docker Compose with Loki (port 3100), Grafana (3000), optional Alloy (file-tail profile).
- **Switch target:** Set in `.env`: `SIMSTEWARD_LOKI_URL=http://localhost:3100`, leave `SIMSTEWARD_LOKI_USER`/`SIMSTEWARD_LOKI_TOKEN` blank for local; use Grafana Cloud URL + Basic auth for production. `SIMSTEWARD_LOG_ENV=local`, `SIMSTEWARD_LOG_DEBUG=1` for debug.
- **Validation:** Follow **docs/LOCAL-LOKI-LOGGING.md** — stack bring-up, datasource health, label/log discovery, MCP query checks (e.g. `query_loki_logs` with `{app="sim-steward",env="local"}`).

## OTEL (Phase 2 — documented only)

Phase 2 adds OpenTelemetry metrics via `Grafana.OpenTelemetry` NuGet: `simsteward_actions_total`, `simsteward_action_duration_seconds`, `simsteward_incidents_total`, `simsteward_ws_clients`, `simsteward_plugin_errors_total`. OTLP endpoint switch via env vars (local Alloy vs Grafana Cloud). Application Observability in Grafana Cloud will show RED-style panels automatically. Until then, use log-derived panels; when Phase 2 is implemented, prefer metrics for latency and rate and logs for drill-down.

## AI-assisted investigations

- **Sift:** Run error pattern analysis on selector `{app="sim-steward", level="ERROR"}` (and always exclude DEBUG in production).
- **Grafana Assistant:** Use `session_digest` as entry point (“summarize session abc123”); drill down with `session_id` and `correlation_id` on other events.
- **Natural-language LogQL (Explore):** Prompts like “show failed actions in last 24 hours” work with our `event`, `action`, `success` fields.
- **Level filter:** All AI/assistant usage must filter `level != "DEBUG"` to avoid debug-mode noise.
- **Optional later:** Grafana MCP server for Cursor (mcp-grafana) for querying production logs from the IDE — not in current scope.

## Testing

- **Deploy gate:** Unchanged; `deploy.ps1` still requires build + unit tests + post-deploy scripts. No change to test phases.
- **Observability tests:** Local stack up, plugin or Alloy pushing logs, then validate via Grafana Explore or MCP (e.g. `query_loki_logs`, `list_loki_label_names`) per **docs/LOCAL-LOKI-LOGGING.md**. Success = datasource green, expected labels, new log lines visible within expected delay.
