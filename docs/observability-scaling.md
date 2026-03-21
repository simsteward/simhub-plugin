# Observability scaling (Loki / many users / large sessions)

How SimSteward logging scales in Grafana Loki: **many drivers per session**, **many plugin instances**, and **collecting logs from many users** without a heavy stack on each PC. Label rules and volume: **docs/GRAFANA-LOGGING.md**.

---

## Part A — Storage, queries, many drivers per session

### Two scaling dimensions

| Dimension | Meaning | How it’s supported |
|-----------|--------|---------------------|
| **Many drivers per session** | 100–200+ cars in session-end results (not 64-car live telemetry cap). | Chunked `session_end_datapoints_results` (35 drivers per line). See **docs/GRAFANA-LOGGING.md** (chunked session results). |
| **Many SimSteward users** | 100–200+ instances → one central Loki. | Lightweight forwarder per user + central Loki (**Part B** below). |

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

Local Docker + Alloy + Loki per developer does **not** scale to ~120 users each running the full stack.

### Current pipeline (local, single-user)

Plugin → `plugin-structured.jsonl` → Alloy (Docker) → Loki. No in-plugin network I/O on the hot path.


### Option A — Optional plugin push to central URL (recommended)

Background thread batches to central Loki when `SIMSTEWARD_LOKI_URL` points at central; trade-off vs file-only simplicity.

### Option D — Hybrid / Fallback

Always write file; optionally also push when central URL configured.

### Recommendation

- **Default:** file → local tailer → Loki (current).
- **Many users:** central Loki + per-user lightweight forwarder with auth token; no full stack on user PCs.

### Central Loki / Grafana Cloud

One tenant or self-hosted Loki; many senders. Scale ingestion/retention to `users × volume per session`.

### Hundreds of drivers per session

Same as Part A: chunked `session_end_datapoints_results` lines; **docs/GRAFANA-LOGGING.md** for LogQL merge patterns.

---

## References

- **docs/GRAFANA-LOGGING.md** — Schema, volume budget, LogQL, housekeeping.
- **docs/observability-local.md** — Local stack quick start.
- Grafana: [Label best practices](https://grafana.com/docs/loki/latest/get-started/labels/bp-labels/), [Query best practices](https://grafana.com/docs/loki/latest/query/bp-query/).
