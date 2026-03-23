# Observability scaling (Loki / many users / large sessions)

How SimSteward logging scales in Grafana Loki: **many drivers per session**, **many plugin instances**, and **collecting logs from many users** without a heavy stack on each PC. Label rules and volume: **docs/GRAFANA-LOGGING.md**. **What goes to Loki vs OTel/metrics** and rough **~1k-user** math: **docs/DATA-ROUTING-OBSERVABILITY.md**.

---

## Part A — Storage, queries, many drivers per session

### Two scaling dimensions

| Dimension | Meaning | How it’s supported |
|-----------|--------|---------------------|
| **Many drivers per session** | 100–200+ cars in session-end results (not 64-car live telemetry cap). | Chunked `session_end_datapoints_results` (35 drivers per line). See **docs/GRAFANA-LOGGING.md** (chunked session results). |
| **Many SimSteward users** | 100–200+ instances → one central Loki. | Plugin pushes directly to central Loki (see **Part B** below). |

### Labels and streams (must stay bounded)

- High-cardinality values (`session_id`, `incident_id`, `car_idx`) belong in the **JSON body** or structured metadata, **not** labels.
- Current design: four labels only (`app`, `env`, `component`, `level`) — **docs/GRAFANA-LOGGING.md**.

### Volume and ingestion

- Grafana Cloud free tier: 5,000 streams, 5 MB/s, 14-day retention, ~50 GB/month.
- ~120 users × &lt;32 streams ≈ under 5,000. Session-end with 200 drivers ≈ 6 chunk lines; no new streams.

### What not to store in Loki at scale

Per-driver per-tick telemetry is time-series data; use metrics (OTel), not Loki. Loki = events and throttled snapshots — **docs/GRAFANA-LOGGING.md** § Phase 2.

### Query patterns that scale

- Time range → labels → `| json` → filter body fields.
- Optional bounded `instance_id` label for multi-tenant (&lt;500 values).
- Chunked results: `{app="sim-steward", component="simhub-plugin"} | json | event = "session_end_datapoints_results" | session_id = "<id>"`; merge by `chunk_index`.
- Trace-style: `| json | session_id = "..."` or `incident_id = "..."`.

---

## Part B — Scaling log collection to many users

Local Docker + Loki per developer does **not** scale to ~120 users each running the full stack.

### Current pipeline (this repo, local single-user)

Plugin → **`plugin-structured.jsonl`** (durability) + WebSocket to dashboard. **No** in-process Loki POST in `SimSteward.Plugin` today. Optional: **`deploy.ps1`** → **`send-deploy-loki-marker.ps1`** POSTs one **`deploy_marker`** line when **`SIMSTEWARD_LOKI_URL`** is set.

### Target / production shape

Always write **`plugin-structured.jsonl`**. **Intended:** batch **HTTPS POST** of NDJSON to **`SIMSTEWARD_LOKI_URL`** from inside the plugin (or an approved sidecar), **or** tail the same file with **Grafana Alloy** / Promtail — **one** Loki HTTP endpoint (central or Grafana Cloud).

### Recommendation

- **Today:** Run a **file tail → Loki** agent for **`plugin-structured.jsonl`**, or wait for in-process batch POST.
- **Many users:** Same pattern: many instances → one central Loki; scale ingestion/retention to `users × volume per session`.

### Central Loki / Grafana Cloud

One tenant or self-hosted Loki; many senders. Scale ingestion/retention to `users × volume per session`.

### Hundreds of drivers per session

Same as Part A: chunked `session_end_datapoints_results` lines; **docs/GRAFANA-LOGGING.md** for LogQL merge patterns.

---

## References

- **docs/DATA-ROUTING-OBSERVABILITY.md** — Routing decisions (events → Loki; high-rate telemetry → OTel → Prometheus/Mimir), sizing, car telemetry taxonomy.
- **docs/GRAFANA-LOGGING.md** — Schema, volume budget, LogQL, housekeeping.
- **docs/observability-local.md** — Local stack quick start.
- Grafana: [Label best practices](https://grafana.com/docs/loki/latest/get-started/labels/bp-labels/), [Query best practices](https://grafana.com/docs/loki/latest/query/bp-query/).

---

## ContextStream KB links

| Spec | Doc ID |
|------|--------|
| Sim Steward — Data Routing (OTel / Loki / Prometheus) | `cbae1c33-c778-4e9a-9a8d-6b3e3c8c368b` |
| Grafana Loki (summary) | `58a20aaf-bdde-4318-88f7-1ec8ec44377b` |
| Observability — Local Stack | `25ed8579-c142-4040-b9a2-87b14523475f` |
| Troubleshooting | `88274879-cd2d-4d86-9766-c86b88f95cfe` |
