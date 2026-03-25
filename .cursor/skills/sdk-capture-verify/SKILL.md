---
name: sdk-capture-verify
description: Verify SDK Data Capture Suite test results against local disk logs and Grafana Loki. Identifies three gap categories — on-disk-not-Loki, Loki-not-disk, both-exist-but-inconsistent.
---

# SDK Capture Verify

Manually verifies that a data capture test run made it correctly to all sinks.

## What to do

### 1. Read credentials from `.env`

Read `c:\Users\winth\dev\sim-steward\simhub-plugin\.env` and extract:
- `SIMSTEWARD_LOKI_URL` — e.g. `https://logs-prod-us-east-0.grafana.net`
- `SIMSTEWARD_LOKI_USER` — numeric user ID
- `CURSOR_ELEVATED_GRAFANA_TOKEN` — elevated read key (use as HTTP Basic password)
- `GRAFANA_API_TOKEN` — fallback

### 2. Find `test_run_id`

If the user provided a `test_run_id`, use it.
Otherwise read the last 500 lines of `%LocalAppData%\SimSteward\plugin-structured.jsonl` (expand `%LocalAppData%` to the Windows path `C:\Users\<username>\AppData\Local`) and find the most recent line containing `"event":"sdk_capture_suite_started"`. Extract its `test_run_id` field.

### 3. Read local disk logs

Read **all** lines from `plugin-structured.jsonl` where `test_run_id` matches.
Also scan `C:\Users\winth\AppData\Local\SimSteward\replay-incident-index\record-samples\` for `.ndjson` files that contain the matching `test_run_id` (these are 60 Hz raw samples — note their presence but do NOT expect them in Loki).

Group disk events by `event` field and note:
- Total matching line count
- Unique `event` values and their counts

### 4. Query local Loki (if reachable)

```
GET http://localhost:3100/loki/api/v1/query_range
  ?query={app="sim-steward"}|json|test_run_id="<id>"
  &start=<unix_ns_1h_ago>&end=<unix_ns_now>
  &limit=5000
```

Use `WebFetch` for all HTTP calls. Group results by `event` and `test_tag`.

### 5. Query Grafana Cloud Loki

```
GET https://logs-prod-us-east-0.grafana.net/loki/api/v1/query_range
  ?query={app="sim-steward"}|json|test_run_id="<id>"
  &start=<unix_ns_1h_ago>&end=<unix_ns_now>
  &limit=5000
Authorization: Basic base64(SIMSTEWARD_LOKI_USER:CURSOR_ELEVATED_GRAFANA_TOKEN)
```

Use `WebFetch`. Group results the same way.

### 6. Gap analysis — three categories

**Gap 1 — On disk, missing from both Loki instances:**
List each `event`+`test_tag` that appears in the JSONL file but in neither local Loki nor Cloud Loki.
Include disk timestamp and key fields: `car_idx`, `replay_frame`, `test_tag`.
Likely causes: Alloy file-tail lag, network drop, not yet ingested.

**Gap 2 — In Loki, missing from disk:**
List events found in Loki but absent from the JSONL file.
Likely cause: in-memory push succeeded but disk flush failed.

**Gap 3 — Both exist but field values disagree:**
For each event that exists in both disk and Loki, compare:
- `car_idx`, `replay_frame`, `replay_session_time`, `cam_car_idx`, `test_run_id`, `detection_source`

Flag any field where the disk value ≠ Loki value.
**Note:** Frequency/rate discrepancies (e.g. different counts of the same event) are informational only — focus on value accuracy for matched events.

### 7. Output a structured report

```
## SDK Capture Verify Report
Test Run ID: <guid>
Disk events: N | Local Loki: N | Cloud Loki: N

### Gap 1: On disk, missing from Loki
<table: event | test_tag | disk_timestamp | car_idx | replay_frame>

### Gap 2: In Loki, missing from disk
<table: event | test_tag | loki_timestamp>

### Gap 3: Inconsistencies
<table per event: field | disk_value | loki_value>

### Summary
- Total gaps: N
- Accuracy score: N/N events fully consistent
- Grafana Explore link: https://...grafana.net/explore?...
```

## Notes

- Use `WebFetch` for all Loki HTTP calls (GET with Authorization header where needed).
- If Loki returns 401, remind the user to check `CURSOR_ELEVATED_GRAFANA_TOKEN` in `.env`.
- If no `test_run_id` found on disk, prompt the user to run the suite first via `data-capture-suite.html`.
- The 60 Hz record samples (`.ndjson` files in `record-samples/`) are cross-referenced for completeness but are NOT expected in Loki — their absence from Loki is normal behaviour, not a gap.
- Unix nanosecond timestamps: `Date.now()` in JS gives ms; multiply by 1,000,000 for ns. For Claude: use current UTC time minus 1 hour as start, current UTC as end.
