# Grafana Loki Structured Logging

Structured logging from the SimSteward plugin to Grafana Loki (Grafana Cloud or local Docker). All logs are event-driven; no per-tick logging in production. The pipeline: **Plugin** → `PluginLogger.Structured()` → **plugin-structured.jsonl** (NDJSON on disk); **Grafana Alloy** (or another tailer) tails that file and pushes to Loki. The plugin does no Loki HTTP I/O. The in-dashboard log stream is pushed via WebSocket; if sends fail, the plugin writes to **broadcast-errors.log** (see **docs/TROUBLESHOOTING.md** §4b) — that file is not sent to Loki. Dashboards and AI tooling use the 4-label schema and fixed `event` taxonomy below. For scaling (many users, large grids, label rules, LogQL), see **docs/observability-scaling.md**. Local Docker / quick start: **docs/observability-local.md**.

**Loki: unencumbered stream.** No filtering is applied before Loki. The plugin writes every log entry to **plugin-structured.jsonl**; Alloy (or any forwarder) tails that file and pushes to Loki with no filter. Loki retains the full stream.

**Filtering is dashboard-only.** The web dashboard receives the full stream via WebSocket and applies level/event visibility filters for display only (checkboxes and `hiddenLevels` / `hiddenEvents`). Toggling "hide DEBUG" or "hide state_broadcast_summary" etc. in the dashboard shows or hides entries that are already in the stream; nothing is dropped at the plugin.

### Local vs prod (same pipeline; env label only)

One pipeline for both: plugin writes all logs to **plugin-structured.jsonl**; Alloy (or any forwarder) tails it and pushes to Loki. You choose the Loki endpoint (local URL or Grafana Cloud) in the **forwarder** config, not in the plugin. Set `SIMSTEWARD_LOG_ENV=local` for local dev (e.g. Docker stack) or `SIMSTEWARD_LOG_ENV=production` (default); this sets the `env` label only. No source-level omission: Loki and the dashboard stream are full. Volume is controlled by event-driven logging (no per-tick logs) and by the dashboard display filter.

## Grafana Cloud free tier limits

| Limit | Value | Impact |
|-------|--------|--------|
| Ingestion rate | 5 MB/s per user | Batches are typically &lt; 20 KB; stay well below. |
| Active streams | 5,000 | Our 4-label schema yields &lt; 32 streams. |
| Retention | 14 days | Two weeks of sessions queryable. |
| Max line size | 256 KB (hard) | Target &lt; 800 bytes per line; self-impose 8 KB max. |
| Label names per series | 15 | We use 4 labels. |
| Label value length | 2,048 chars | No concern with static label values. |

Volume allowance: free tier ~50 GB/month; our budget is &lt; 1 GB/month.

### Scale: hundreds of drivers / many users

Stream count and labels stay bounded (four labels only; no `session_id` or `driver_id` as labels). Session-end results with 100–200+ drivers use chunked `session_end_datapoints_results` (35 drivers per line); merge chunks in Grafana. For many SimSteward users (e.g. 120) sending to one Loki, use a lightweight forwarder per user and optional bounded `instance_id` label. Do not log per-driver per-tick in Loki; use metrics (OTel) for high-frequency telemetry. Full stream/volume math, label rules, and query patterns: **docs/observability-scaling.md**.

### Volume budget (per session, ~2 h)

| Source | Logs / session | Bytes / entry | MB / session |
|--------|----------------|---------------|--------------|
| Action commands (2 lines per action) | ~440 | ~400 B | ~0.18 |
| Incidents | ~50 | ~600 B | ~0.03 |
| Lifecycle / iRacing | ~15 | ~300 B | &lt; 0.01 |
| WS client connect/disconnect | ~10 | ~300 B | &lt; 0.01 |
| Errors / warnings | ~10 | ~350 B | &lt; 0.01 |
| **Total** | **~525** | — | **~0.23 MB** |

At 30 sessions/month: ~7 MB. Never log on a tick; `DataUpdate()` runs at 60 Hz.

## Label schema

Four labels only. Do **not** put high-cardinality values (`session_id`, `car_number`, `action`, `correlation_id`) in labels—they stay in the JSON body.

| Label | Values | Rationale |
|-------|--------|-----------|
| `app` | `sim-steward` | Namespace. |
| `env` | `production` or `local` | From `SIMSTEWARD_LOG_ENV`. |
| `component` | `simhub-plugin`, `bridge`, `tracker`, `dashboard` | Subsystem. |
| `level` | `INFO`, `WARN`, `ERROR`, `DEBUG` | Severity. `DEBUG` only when `SIMSTEWARD_LOG_DEBUG=1`. |

## Event taxonomy

Every log line has an `event` field. Key events:

| Event | Component | Key fields | Notes |
|-------|-----------|------------|-------|
| `logging_ready` | simhub-plugin | — | First log after logger creation; init continues. |
| `settings_saved` | simhub-plugin | — | UI settings persisted. |
| `file_tail_ready` | simhub-plugin | `path` | Structured log file path ready for Alloy/Loki file-tail. |
| `plugin_started` | simhub-plugin | — | SimSteward plugin starting; tracker callback set. |
| `actions_registered` | simhub-plugin | — | SimHub properties and actions registered. |
| `bridge_starting` | simhub-plugin | `bind`, `port` | WebSocket bridge starting. |
| `bridge_start_failed` | simhub-plugin | `bind`, `port`, `error` | WebSocket server failed to start (WARN). |
| `plugin_ready` | simhub-plugin | `ws_port`, `env` | Lifecycle readiness. |
| `log_streaming_subscribed` | simhub-plugin | — | Dashboard log streaming attached. |
| `irsdk_started` | simhub-plugin | — | iRacing SDK started. |
| `plugin_stopped` | simhub-plugin | — | Emitted from `End()`. |
| `iracing_connected` / `iracing_disconnected` | simhub-plugin | — | IRSDK connection state. |
| `ws_client_connected` / `ws_client_disconnected` | bridge | `client_ip`, `client_count` | Each connect/disconnect. |
| `dashboard_opened` | bridge | `client_ip`, `client_count` | When a dashboard client connects (page load or refresh). |
| `ws_client_rejected` | bridge | `client_ip`, `reason` | Token missing or invalid. |
| `action_received` | bridge | `action`, `arg`, `client_ip`, `correlation_id` | Logged before `DispatchAction`. In production, omitted by default; enable "Log all action traffic" in settings or `SIMSTEWARD_LOG_ALL_ACTIONS=1` to keep. |
| `action_dispatched` | simhub-plugin | `action`, `arg`, `correlation_id`, `subsession_id`, `parent_session_id`, `session_num`, `track_display_name`, `log_env`, `loki_push_target`, plus spine `session_id` / `replay_frame` when set | Start of every command. **subsession_id** = iRacing `WeekendInfo.SubSessionID` when &gt; 0, else `"not in session"`. **parent_session_id** = `WeekendInfo.SessionID` when &gt; 0, else `"not in session"`. **session_num** = telemetry `SessionNum` when connected, else `"not in session"`. **log_env** = `SIMSTEWARD_LOG_ENV` or `unset`. **loki_push_target** = `disabled` \| `grafana_cloud` \| `local_or_custom` from `SIMSTEWARD_LOKI_URL` (same env the Loki sink uses — set before SimHub starts, e.g. launcher loading `.env`). In production, omitted by default; enable "Log all action traffic" to keep. |
| `action_result` | simhub-plugin | Same session/routing fields as `action_dispatched` where applicable, plus `success`, `result`, `error`, `duration_ms` | End of command. |
| `plugin_ui_changed` | simhub-plugin | `element`, `value` | Settings panel interaction (omit level/event, data API endpoint, log all action traffic). |
| `dashboard_ui_event` | bridge | `client_ip`, `element_id`, `event_type`, `value`, plus same `subsession_id`, `parent_session_id`, `session_num`, `track_display_name`, `log_env`, `loki_push_target` as actions | Dashboard UI-only interaction (panel toggles, log filter checkboxes, filter chips, view buttons, results drawer, etc.). |
| `replay_control` | simhub-plugin | `mode`, `speed`, `search_mode` | Replay buttons. |
| `session_snapshot_recorded` | simhub-plugin | `path` | Writable snapshot log. |
| `session_end_fingerprint` | simhub-plugin | `session_num`, `results_ready`, `results_positions_count`, `replay_frame_num`, `session_time` | Emitted when RecordSessionSnapshot is called with a trigger containing "session_end" (e.g. session_end:2). Fingerprint of what data is available at session end. |
| `checkered_detected` | simhub-plugin | `session_state` | Emitted when replay/live crosses the line (SessionState ≥ 5); before attempting capture. |
| `checkered_retry` | simhub-plugin | `session_state` | Emitted when running the 2s-delayed retry after checkered. |
| `session_capture_skipped` | simhub-plugin | `trigger`, `error`, `details`, `will_retry` | When capture is attempted but ResultsPositions is empty (e.g. at checkered). |
| `session_capture_incident_mismatch` | simhub-plugin | `results_incidents`, `tracker_incidents`, `player_car_idx` | WARN when player's ResultsPositions incident count ≠ IncidentTracker count (wrong session or SDK mapping). |
| `session_summary_captured` | simhub-plugin | `trigger`, `session_num`, `driver_count`, `wanted_session_num`, `selected_session_num`, `session_match_exact`, `results_incident_sample` | When `TryCaptureAndEmitSessionSummary` succeeds. Use `session_match_exact` to see when fallback session was used; `results_incident_sample` = first 3 drivers' car_idx, position, incidents for SDK verification. |
| `session_end_datapoints_session` | simhub-plugin | `trigger`, `session_id`, `session_num`, session-level fields (track, series_id, session_name, incident_limit, …), `telemetry_*` at capture, `results_driver_count` | Emitted once per successful session summary capture. Session metadata and telemetry snapshot only; no results array. Use with `session_end_datapoints_results` chunks to get full data. Scales to hundreds of drivers. |
| `session_end_datapoints_results` | simhub-plugin | `session_id`, `session_num`, `chunk_index`, `chunk_total`, `results_driver_count`, `results` (array of up to 35 driver rows: pos, car_idx, driver, abbrev, car, class, laps, incidents, reason_out, user_id, team, irating, etc.) | One log line per chunk (35 drivers per chunk). Merge chunks by `session_id` and sort by `chunk_index` for full results table. See **docs/observability-scaling.md** and § LogQL reference below. |
| `finalize_capture_started` / `complete` / `timeout` | simhub-plugin | `target_frame`, `duration_ms` | Debug / automation. |
| `incident_detected` | tracker | `incident_type`, `car_number`, `driver_name`, `unique_user_id` (iRacing **CustID**), `delta`, `session_time`, `session_num`, `replay_frame`, `replay_frame_end` (optional window), `cause`, `other_car_number`, `subsession_id`, `parent_session_id`, `track_display_name`, `cam_car_idx` / `camera_group` (when available), `log_env`, `loki_push_target` | Each YAML delta. **Incident fingerprint (for correlation / future storage):** combine `parent_session_id`, `subsession_id`, `session_num`, focused driver (`unique_user_id` + `driver_name`), camera/view fields, `track_display_name`, `session_time`, and `replay_frame` (± `replay_frame_end` if the event spans a window). Use `"not in session"` for `subsession_id` / `parent_session_id` when iRacing has no loaded session. *Tracker implementation may not emit all optional fields until wired in code.* |
| `baseline_established` | tracker | `driver_count` | When tracker baseline is ready. |
| `session_reset` | tracker | `old_session`, `new_session` | When `SessionNum` changes. |
| `seek_backward_detected` | tracker | `from_frame`, `to_frame`, `session_time` | Replay seek. |
| `yaml_update` | tracker | `session_info_update`, `session_num`, `session_time` | Debug-only. |
| `session_digest` | simhub-plugin | `session_id`, `session_num`, `track`, `duration_minutes`, `total_incidents`, `results_incident_sum`, `incident_summary`, `incident_summary_truncated`, `results_table`, `results_driver_count`, `actions_dispatched`, … | Single-row session summary. **total_incidents** = count of incident_detected events (plugin); **results_incident_sum** = sum of iRacing per-driver incident points; **results_table** = authoritative ResultsPositions (pos, car, driver, incidents, laps, class, reason_out per driver). |

`incident_detected` feeds the **Incident Timeline** dashboard; `session_digest` feeds the **Session Overview** dashboard.

## Local vs. cloud configuration

| Setting | Local Docker | Grafana Cloud |
|---------|--------------|---------------|
| `SIMSTEWARD_LOKI_URL` | `http://localhost:3100` | `https://logs-prod-us-east-0.grafana.net` |
| `SIMSTEWARD_LOKI_USER` | *(blank)* | Your instance user ID |
| `SIMSTEWARD_LOKI_TOKEN` | *(blank)* | Your log-write token |
| `SIMSTEWARD_LOG_ENV` | `local` | `production` |
| `SIMSTEWARD_LOG_DEBUG` | `1` (optional) | `0` or unset |
| `SIMSTEWARD_LOG_ALL_ACTIONS` | `1` to keep `action_received` and `action_dispatched` in logs | unset (production omits them by default) |

**Log all action traffic:** In production, `action_received` and `action_dispatched` are omitted at source to reduce volume. To capture every command (e.g. for debugging or full click/event visibility), enable **Log all action traffic** in the plugin settings (Observability / Log filters), or set `SIMSTEWARD_LOG_ALL_ACTIONS=1` before starting SimHub.

The plugin reads these once at `Init()`. To switch environment, edit `.env` and restart SimHub.

**Important:** SimHub does **not** load a `.env` file. The plugin only sees environment variables that are set in the process that starts SimHub. To get logs into Grafana you must either:

- **Local Loki:** Start SimHub with the script so env vars are set before launch:
  - From the plugin repo root: `.\scripts\run-simhub-local-observability.ps1`
  - This sets `SIMSTEWARD_LOKI_URL=http://localhost:3100` and `SIMSTEWARD_LOG_ENV=local`, then starts SimHub.
- **Grafana Cloud:** Set `SIMSTEWARD_LOKI_URL`, `SIMSTEWARD_LOKI_USER`, and `SIMSTEWARD_LOKI_TOKEN` in your user or system environment, then start SimHub (or use a launcher that sets them).

## No data in Grafana

If Explore or dashboards show no logs for `{app="sim-steward"}`:

1. **Loki URL not set** — The plugin pushes to Loki only when `SIMSTEWARD_LOKI_URL` is set. If you start SimHub by double‑clicking (or from the Start menu), that variable is usually unset.
   - **Fix:** Start SimHub via `.\scripts\run-simhub-local-observability.ps1` for local Loki, or set the Loki env vars before starting SimHub.
   - **Check:** Open `%LOCALAPPDATA%\SimHubWpf\PluginsData\SimSteward\plugin.log` and look for a line with `event` = `loki_status`. If it says "Loki logging disabled", the URL was not set when the plugin started.
2. **Local stack not running** — For local Docker Loki, ensure the stack is up: `cd observability/local && docker compose up -d`. Grafana should be at http://localhost:3000 and Loki at http://localhost:3100.
3. **Wrong query** — In Grafana Explore, select the Loki datasource and use LogQL: `{app="sim-steward"}` or `{app="sim-steward", env="local"}`. Use a time range that includes when the plugin was running.

After fixing, restart SimHub (using the script for local) and trigger some activity (e.g. open the dashboard, connect iRacing, or run a replay); logs should appear within a few seconds to a minute depending on flush interval.

### Dashboards show no data

If provisioned dashboards load but every panel shows "No data", check both:

1. **Dashboard JSON and datasource** — Each file in `observability/local/grafana/provisioning/dashboards/` must be a **single** provisioner object: `{ "dashboard": { ... }, "overwrite": true }`. Every panel must use the explicit datasource `{ "type": "loki", "uid": "loki_local" }`. If a panel uses `"datasource": "${DS_LOKI}"`, that variable is not set in provisioning and the panel will have no datasource. Remove any duplicate or legacy JSON objects from the same file.
2. **Data flow** — If the pipeline is not sending logs to Loki, dashboards will be empty even with correct JSON. Follow the **Logs not in Grafana? Checklist** below; confirm in **Explore** with query `{app="sim-steward"}` and time range "Last 1 hour" that at least some log lines are returned before relying on dashboards.

### Logs not in Grafana? Checklist

Use this checklist so button presses (Play, etc.) show up in Grafana:

1. **Start SimHub with env set** — Use `.\scripts\run-simhub-local-observability.ps1` for local Loki, or set `SIMSTEWARD_LOKI_URL` (and optional user/token) in your environment before starting SimHub.
2. **Or enable Loki in the plugin** — In SimSteward plugin settings, enable **Enable Loki logging** so the plugin sets `SIMSTEWARD_LOKI_URL=http://localhost:3100` for this run (persists for next start).
3. **Local stack running** — For local Loki: `cd observability/local && docker compose up -d`; confirm Loki at http://localhost:3100.
4. **Query and time range** — In Grafana Explore, select the Loki datasource, query `{app="sim-steward"}` (optionally `env="local"`), and set time range to **Last 5 minutes** or **Last 15 minutes**.
5. **Wait for flush** — After pressing Play or other buttons (or opening the dashboard, connecting iRacing, or an incident firing), wait 1–2 seconds; these events trigger a debounced flush so logs appear quickly. The periodic timer can still take up to 5 s for other events.
6. **Check plugin.log** — Look for `loki_status` ("Loki logging enabled" vs "disabled") and `loki_first_push_ok` (confirms at least one batch reached Loki). If you see push failure warnings, Loki is unreachable (stack down or wrong URL).

**Events that trigger prompt flush (1–2 s):** Button actions (`action_result`, `action_dispatched`), incidents (`incident_detected`), session lifecycle (`checkered_detected`, `checkered_retry`, `session_summary_captured`, `session_digest`, `session_end_datapoints_session`, `session_end_datapoints_results`, `session_capture_skipped`), dashboard and iRacing (`dashboard_opened`, `iracing_connected`, `iracing_disconnected`), tracker (`baseline_established`, `session_reset`), and bridge/plugin readiness (`plugin_ready`, `bridge_starting`, `ws_client_connected`, `ws_client_disconnected`). All other events are sent on the next batch-size or 5 s timer.

## Debug mode

Set `SIMSTEWARD_LOG_DEBUG=1` for local debugging only. When enabled:

- **PluginLogger.Debug()** emits `DEBUG`-level entries (still sent to Loki; filter in dashboards/AI).
- **LokiSink** uses relaxed flush rules: 2 s timer, 500-entry batch, 5,000-entry queue, no line-size enforcement.
- Additional log events are emitted:
  - `state_broadcast_summary` (throttled ~5/s): plugin mode, incident count, client count, replay frame.
  - `tick_stats` every 60 ticks (≈1 s): running average `data_update_ms`, `frames_dropped`.
  - `yaml_update`: each `SessionInfoUpdate` refresh.
  - `ws_message_raw`: every WebSocket message (raw JSON) for debugging dashboard commands.
  - `incident_detected` includes a `snapshot` field with the current `PluginSnapshot`.

Never enable debug in production. For AI or assistant queries, filter with `| level != "DEBUG"`.

## SessionStats and session_digest

**SessionStats** accumulates per session (reset on `iracing_connected`):

| Metric | What it tracks |
|--------|----------------|
| `ActionsDispatched` | Total actions processed this session. |
| `ActionFailures` | Count of actions that returned `success = false`. |
| `PluginErrors` / `PluginWarns` | From `_sessionStats.IncrementErrors()` / `IncrementWarns()`. |
| `WsPeakClients` | Peak WebSocket client count per session. |
| `ActionLatenciesMs` | Rolling sample for P50/P95. |
| `Incidents` | Incident summaries (e.g. for digest). |

**session_digest** is emitted at most once per session (guarded by `_sessionDigestEmitted`). It caps `incident_summary` to 20 entries (highest severity first) and sets `incident_summary_truncated: true` when truncated. Trigger the digest manually (`CaptureSessionSummaryNow`, `FinalizeThenCaptureSessionSummary`, or checkered flag) so downstream AI and dashboards see the session as complete.

**Incident semantics:** `total_incidents` is the **count of incident_detected events** (from IncidentTracker CurDriverIncidentCount deltas). The **results_table** `incidents` column and **results_incident_sum** are iRacing’s per-driver incident **points** at session end (from ResultsPositions). So total_incidents (e.g. 12 events) and results_incident_sum (e.g. 24 points) can both be correct but differ.

## Provisioned dashboards

Grafana can load dashboards from `observability/local/grafana/provisioning/dashboards/`. Each JSON file uses the provisioner format `{ "dashboard": { ... }, "overwrite": true }`. Most panels reference datasource UID `loki_local`; **Event Coverage** uses a datasource variable `DS_LOKI` so Grafana Cloud users can select their Loki instance.

| File | Dashboard | Panels |
|------|-----------|--------|
| `event-coverage.json` | Event Coverage | Log volume (timeseries), event distribution (table), component breakdown (table), recent logs (logs). Default time range 7 days. |
| `command-audit.json` | Command Audit | Action results (logs), failed actions stat, action volume (timeseries). |
| `incident-timeline.json` | Incident Timeline | Incidents (logs), incidents table (type/driver/car/lap/session_time), incident rate (timeseries). |
| `plugin-health.json` | Plugin Health | ERROR/WARN rate (timeseries), all ERROR logs (logs), lifecycle (logs). |
| `session-overview.json` | Session Overview | Session digests (logs), lifecycle events (logs), WebSocket client events (logs). |
| `session-capture.json` | Session Capture | session_summary_captured, session_end_datapoints_session, checkered/retry, capture skipped/mismatch/fingerprint, capture vs skip rate. |
| `replay-tracker.json` | Replay & Tracker | replay_control (logs), tracker state (baseline_established, session_reset, seek_backward_detected), replay control count. |
| `bridge-actions.json` | Bridge & Actions | action_received, dashboard_opened/ws_client_rejected/bridge_start_failed, rejections stat. |
| `errors-warnings.json` | Errors & Warnings | ERROR/WARN rate (timeseries), ERROR logs, WARN logs. |
| `session-end-results.json` | Session End Results | session_end_datapoints_results (all chunks), chunks over time; merge by session_id and chunk_index for full table. |

To validate which events appear in your data (e.g. last 7 days) and confirm the datasource, see **docs/observability-testing.md** (dashboard validation).

See **Local quickstart** below to run Grafana and Loki so these dashboards load.

### Derived fields (Loki datasource)

The provisioned Loki datasource (`observability/local/grafana/provisioning/datasources/loki.yml`) defines **derived fields** so Grafana extracts and shows key JSON fields when viewing log lines:

| Derived field   | Extracted from | Notes |
|-----------------|----------------|-------|
| Event           | `event`        | Event type (e.g. `action_result`, `incident_detected`). |
| Correlation ID  | `correlation_id` | Clickable: runs LogQL filter by this correlation (trace). |
| Session ID      | `session_id`   | Session identifier. |
| Action          | `action`       | Command/action name. |
| Success         | `success`      | Action outcome (`true` / `false`). |
| Trigger         | `trigger`      | Session summary trigger (e.g. `checkered`, `finalize`). |
| Incident type   | `incident_type` | Incident classification. |
| Driver name     | `driver_name`  | Driver name in incident/session context. |
| Car number     | `car_number`   | Car number (when serialized as string). |

In Explore or any Loki log panel, these appear as parsed columns/links alongside the raw line.

## Local quickstart

```powershell
# Persistent storage (host path must exist before Docker)
New-Item -ItemType Directory -Force "S:\sim-steward-grafana-storage"

# In project .env, for local:
# SIMSTEWARD_LOKI_URL=http://localhost:3100
# SIMSTEWARD_LOKI_USER=
# SIMSTEWARD_LOKI_TOKEN=
# SIMSTEWARD_LOG_ENV=local
# SIMSTEWARD_LOG_DEBUG=1

cd observability/local
docker compose up -d
# Grafana: http://localhost:3000  |  Loki: http://localhost:3100 (no auth for direct push)

# Optional: start Alloy (file-tail) and Loki gateway for file-based ingestion:
# docker compose --profile file-tail up -d
```

See **docs/observability-local.md** for the file-tail/gateway/alloy setup and token-protected push.

## LogQL reference

| Purpose | LogQL |
|---------|-------|
| Command audit | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "action_result"` |
| Failed commands | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "action_result" \| success = "false"` |
| Action volume (timeseries) | `count_over_time({app="sim-steward", component="simhub-plugin"} \| json \| event = "action_result" [$__interval])` |
| Incident timeline | `{app="sim-steward", component="tracker"} \| json \| event = "incident_detected"` |
| Plugin lifecycle | `{app="sim-steward", component="simhub-plugin"} \| json \| event =~ "plugin_started\|plugin_ready\|iracing_connected\|iracing_disconnected\|plugin_stopped"` |
| Session digests | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "session_digest"` |
| Session end metadata | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "session_end_datapoints_session"` |
| Session end results (all chunks for one session) | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "session_end_datapoints_results" \| session_id = "<id>"` — merge in dashboard by sorting on `chunk_index` and flattening `results`. |
| WS peak (stat) | `max_over_time({app="sim-steward"} \| json \| event = "session_digest" \| unwrap ws_peak_clients [24h])` |
| Trace by correlation | `{app="sim-steward"} \| json \| correlation_id = "<id>"` |
| All errors | `{app="sim-steward", level="ERROR"}` |

### Replay control and incident counts

| Purpose | LogQL |
|---------|-------|
| Replay control by speed | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "replay_control" \| speed != ""` — filter by `speed` (e.g. 16, 8, 1) to see which replay speed was used. |
| Replay control (all) | `{app="sim-steward", component="simhub-plugin"} \| json \| event = "replay_control"` — seek/play/pause and speed. |
| Incident count (timeseries) | `count_over_time({app="sim-steward", component="tracker"} \| json \| event = "incident_detected" [$__interval])` |
| Incident count (total in range) | `count_over_time({app="sim-steward", component="tracker"} \| json \| event = "incident_detected" [$__range])` |

**session_summary_captured** is emitted only when the plugin successfully captures the session results table (ResultsPositions). It does **not** fire when results are not yet available (e.g. before checkered, or in a short replay clip that never reaches session end). Use **session_capture_skipped** to see when capture was attempted but results were empty; use **session_end_fingerprint** (when implemented) to see what data was available at session end.

### Chunked session results (hundreds of drivers)

End-of-session driver results are emitted as **session_end_datapoints_session** (metadata once) plus **session_end_datapoints_results** (one log line per chunk of 35 drivers). To show a full results table for a session in Grafana:

1. Query: `{app="sim-steward", component="simhub-plugin"} | json | event = "session_end_datapoints_results" | session_id = "<session_id>"`.
2. In the panel transform: sort by `chunk_index`, then use a transform that flattens the `results` array from each chunk into a single table (e.g. "Merge" / "Flatten" or a custom transformation that concatenates `results` in order).

Exact driver count is in **session_end_datapoints_session** (`results_driver_count`); use that event when you need session metadata without parsing result chunks.

## AI integrations (Grafana Cloud)

- **Grafana Sift** — Pattern grouping for errors: use `{app="sim-steward", level="ERROR"}`.
- **Grafana Assistant** — Start with `session_digest`; then drill down by `session_id` or `correlation_id`. Filter `| level != "DEBUG"` for production.
- **Natural-language LogQL** — In Explore, use field names like `action`, `success`, `duration_ms`, `incident_type`, `session_id`, `correlation_id`.
- **MCP** — Optional: Grafana MCP connector (e.g. github.com/grafana/mcp-grafana) so Cursor or other tools query logs; point at Grafana Cloud with a token.

## Phase 2 (future): OTel metrics

After Phase 1 is in use and logs are observed in production, Phase 2 can add OpenTelemetry metrics (e.g. via `Grafana.OpenTelemetry` NuGet) for faster dashboards, longer retention, and metric-based alerting. Metric names and env vars are in the Grafana Loki logging plan.
