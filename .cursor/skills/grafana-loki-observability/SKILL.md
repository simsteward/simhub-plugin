---
name: grafana-loki-observability
description: Grafana/Loki observability stack expert.
---
# Grafana / Loki Observability

## Limits (Grafana Cloud Free)
- **Rate:** 5 MB/s/user (Event-driven only, no tick-rate logging).
- **Streams:** Max 5,000. Our 4-label schema is safe.
- **Retention:** 14 days.
- **Line size:** < 800 bytes typical, max 8 KB.
- **Volume:** < 1 MB per session.

## Schema
- **Labels (4):** `app=sim-steward`, `env=production|local`, `component=simhub-plugin|bridge|tracker|dashboard`, `level=INFO|WARN|ERROR|DEBUG`.
- **Never label:** `session_id`, `car_number`, `correlation_id` (high cardinality).
- **Events:** `plugin_started`, `action_result`, `incident_detected`, `session_digest`, etc.

## LogQL
- **Audit:** `{app="sim-steward"} | json | event="action_result"`
- **Errors:** `{app="sim-steward", level="ERROR"}`
- **Trace:** `{app="sim-steward"} | json | correlation_id="<id>"`
- **AI Filter:** ALWAYS append `| level != "DEBUG"`.

## Stack
- **Dashboards:** `command-audit`, `incident-timeline`, `plugin-health`, `session-overview`.
- **Local:** `observability/local/`. Loki on 3100. Use `SIMSTEWARD_LOKI_URL=http://localhost:3100`, `SIMSTEWARD_LOG_ENV=local`.