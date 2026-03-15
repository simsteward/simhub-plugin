# Scaling Loki Storage and Queries (Hundreds of Drivers / Many Users)

This doc describes how SimSteward logging scales in Grafana Loki for two dimensions: **many drivers per session** (100–200+ cars in session-end results) and **many SimSteward users** (100–200+ plugin instances sending to one central Loki). For collecting logs from many users without a heavy stack per machine, see **docs/SCALING-LOG-COLLECTION.md**.

## Two scaling dimensions

| Dimension | Meaning | How it’s supported |
|-----------|--------|---------------------|
| **Many drivers per session** | One race with 100–200+ cars (e.g. session-end results). iRacing SDK live telemetry is max **64 cars** per session; “hundreds” here applies to **results at session end** (e.g. league exports). | Chunked `session_end_datapoints_results` (35 drivers per log line). Stream count and labels unchanged; only lines per session grow. See **docs/GRAFANA-LOGGING.md** (§ Chunked session results). |
| **Many SimSteward users** | 100–200+ plugin instances (e.g. 120 leagues) sending to **one central Loki**. | Lightweight forwarder per user + central Loki (docs/SCALING-LOG-COLLECTION.md). Stream and volume math below. |

## Labels and streams (must stay bounded)

- Loki’s cost and performance are dominated by **stream cardinality**: each unique label combination = one stream. High-cardinality labels (e.g. `session_id`, `incident_id`, `car_idx`) would create many streams and hurt performance.
- **Rule:** Use labels only for **static, bounded** values. Put `session_id`, `incident_id`, `car_idx`, `correlation_id` in the **log body (JSON)** or in **structured metadata** (Loki 3.3+), not as labels.
- Current design: four labels only (`app`, `env`, `component`, `level`) — see **docs/GRAFANA-LOGGING.md**. Do **not** add `session_id` or `driver_id` as labels when scaling.

**Structured metadata (optional):** Loki 3.3+ supports structured metadata (e.g. `session_id`, `incident_id`) on the push payload. Bloom filters can then speed up queries like “all lines for session X” without indexing those values as labels. Today, JSON body filters (`| json | session_id = "54391827"`) achieve the same; structured metadata is an optimization when volume grows.

## Volume and ingestion

- **Grafana Cloud free tier:** 5,000 active streams, 5 MB/s ingestion, 14-day retention, ~50 GB/month.
- **Many users (e.g. 120):** 120 × &lt; 32 streams ≈ 3,840 streams (under 5,000). Volume: 120 × ~0.23 MB/session × 30 sessions/month ≈ 830 MB/month (well under 50 GB).
- **Session-end with 200 drivers:** 200 ÷ 35 ≈ 6 chunks per session. One `session_end_datapoints_session` + 6 `session_end_datapoints_results` lines. No new streams; only a few extra lines per session.

## What not to store in Loki at this scale

**Per-driver, per-tick (or high-frequency) telemetry:** Logging every driver at 1 Hz would yield 64 × 1 × 7,200 ≈ 460K lines per 2 h session (~230 MB/session). That is **time-series data**, not event logs. For that scale and query pattern (e.g. “speed of car 7 over time”), use **metrics** (Prometheus / OpenTelemetry) or a time-series DB, not Loki. Keep Loki for **events and snapshots** (e.g. `leaderboard_snapshot` every 10 s as one line with a 64-car JSON array, `player_physics_snapshot` throttled or on incident). See **docs/GRAFANA-LOGGING.md** § Phase 2 (OTel metrics).

## Query patterns that scale

- **Narrow by time first** — Loki is optimized for time-bounded reads.
- **Then by labels** — e.g. `{app="sim-steward", component="simhub-plugin"}`.
- **Then by body or structured metadata** — e.g. `| json | session_id = "54391827"` or `| json | incident_id = "a3f9"`.

### Optional: bounded label for multi-tenant (many users)

If 100–200 SimSteward instances send to one Loki, adding a **bounded** label (e.g. `instance_id` with a few hundred values) lets you narrow to one instance before parsing JSON: `{app="sim-steward", instance_id="user_42"} | json | session_id = "54391827"`. Keep cardinality bounded (e.g. &lt; 500); do not use session_id or user_id as the label value.

### Chunked session results (hundreds of drivers per session)

- Query: `{app="sim-steward", component="simhub-plugin"} | json | event = "session_end_datapoints_results" | session_id = "<id>"`.
- In Grafana: sort by `chunk_index`, then merge/flatten the `results` arrays from each chunk into one table. Query cost grows with the number of chunks (e.g. 6 lines for 200 drivers), not with stream count.

### Trace-style queries (session / incident)

- **All events for one session:** `{app="sim-steward"} | json | session_id = "<SubSessionID>"`.
- **All events for one incident:** `{app="sim-steward"} | json | incident_id = "<id>"`.

With a tagging spine (session_id, incident_id in every log line body), these queries stay simple and scale with the number of matching lines, not with stream cardinality.

## References

- **docs/GRAFANA-LOGGING.md** — Label schema, volume budget, LogQL patterns, chunked session results.
- **docs/SCALING-LOG-COLLECTION.md** — Many users (forwarder + central Loki), hundreds of drivers per session (chunked results).
- **docs/END-OF-SESSION-DATAPOINTS.md** — Chunk format (35 drivers per line).
- Grafana: [Label best practices](https://grafana.com/docs/loki/latest/get-started/labels/bp-labels/), [Structured metadata](https://grafana.com/docs/loki/latest/get-started/labels/structured-metadata/), [Query best practices](https://grafana.com/docs/loki/latest/query/bp-query).
