# Incident logged data points

Where incident data is written and which fields are captured. Use this to confirm what we log and to debug missing data.

## iRacing telemetry: PlayerCarMyIncidentCount validation

The iRacing SDK can occasionally return **out-of-range values** for `PlayerCarMyIncidentCount` (e.g. uninitialized shared memory or wrong offset), which would create bogus incidents and corrupt `_prevFocusedCarIncidentCounts`. We **clamp** the value to 0–999 and **do not add from telemetry** when the value was clamped (treat read as invalid). If you see `player_incident_count_clamped` in debug logs, the SDK returned an invalid value and we avoided using it for incident detection.

## Where incidents are logged

| Destination | When | Path / mechanism |
|-------------|------|-------------------|
| **plugin-structured.jsonl** (file) | Every time an incident is emitted from the tracker | `IncidentTracker.AddIncident` → `EmitStructured` → plugin `LogStructured` → `_logger.Emit(entry)` → `PluginLogger.Write` → `WriteJsonLine`. File path: `{PluginsData}/SimSteward/plugin-structured.jsonl` (or `SIMSTEWARD_DEBUG_LOG_PATH` does not affect this). |
| **Grafana / Loki** | Same as above | Alloy (or another tailer) tails `plugin-structured.jsonl` and pushes to Loki. No separate filter; every line we write to the file is sent. |
| **Dashboard log stream** (WebSocket) | When we drain new incidents in DataUpdate | `SimStewardPlugin.DataUpdate` builds `LogEntry` from each `newIncident` and `Broadcast(logEvents)`. Only this subset of fields is sent (see below). |
| **Dashboard incident panel** | Same tick as log stream | `Broadcast(incidentEvents)` with full `IncidentEvent` objects (all properties). |
| **plugin.log** (plain text) | Same as file | `PluginLogger.Write` → `WriteToFile`: one line `{Timestamp} [INFO] {Message}`. No structured fields. |

We do **not** push `incident_detected` from `OnLogWritten` to the WebSocket (we skip that to avoid duplicates); the dashboard log stream gets incident lines only from the DataUpdate path above.

**New clients:** When the dashboard connects, the plugin sends a **log tail** (last 50 entries plus up to 20 `incident_detected` entries that may fall outside that window). So opening the dashboard after incidents still shows recent incidents in the event stream even when high-volume events (e.g. state_broadcast_summary) fill the ring.

---

## Expected data points by destination

### 1. plugin-structured.jsonl / Grafana (full structured entry)

**Source:** `IncidentTracker.AddIncident` builds `incidentFields` and passes them to `EmitStructured`. The plugin callback receives a `LogEntry` with those fields; `PluginLogger.Write` adds `Timestamp` and spine (`session_id`, `session_seq`, `replay_frame` from `_getSpine`) then writes one NDJSON line.

**Top-level keys on the log line (JSON):**

| Key | Type | Set by | Description |
|-----|------|--------|-------------|
| `level` | string | Tracker | Always `"INFO"` for incident_detected. |
| `component` | string | Tracker | `"tracker"`. |
| `event` | string | Tracker | `"incident_detected"`. |
| `message` | string | Tracker | e.g. `"Incident detected: 1x #10 Driver Name"`. |
| `timestamp` | string | PluginLogger | ISO8601 set in `Write()`. |
| `session_id` | string | PluginLogger (spine) | From `_getSpine()` when not set on entry. |
| `session_seq` | string | PluginLogger (spine) | From `_getSpine()`. |
| `replay_frame` | int? | Tracker (in Fields) or spine | From event or spine. |
| `domain` | string | Tracker | `"incident"`. |
| `incident_id` | string | Tracker | On entry; used for spine/tagging. |

**Fields (inside `fields` or as top-level in JSON):**

| Field | Type | Always present | Description |
|-------|------|----------------|-------------|
| `incident_id` | string | Yes | Unique id for this incident. |
| `sub_session_id` | int | Yes | iRacing SubSessionID. |
| `user_id` | int | Yes | iRacing user id. |
| `incident_type` | string | Yes | e.g. `"1x"`, `"2x"`, `"4x"`, `"batched"`. |
| `car_number` | string | Yes | Car number. |
| `driver_name` | string | Yes | Driver display name. |
| `delta` | int | Yes | Incident points (1, 2, 4, etc.). |
| `session_time` | double | Yes | Session time in seconds. |
| `session_num` | int | Yes | Session number. |
| `replay_frame` | int | Yes | Replay frame number (0 if not replay). |
| `lap` | int | Yes | Lap number. |
| `cause` | string | No | Present only if non-empty (e.g. off-track, contact). |
| `other_car_number` | string | No | Present only if other driver involved. |
| `other_driver_name` | string | No | Present only if other driver involved. |

**Debug:** When an incident is emitted, we log to `debug-b0c27e.log` (hypothesisId `ID`) with `incident_captured_fields`: `incident_id`, `fieldCount`, `fieldKeys` (list of keys in `incidentFields`). That confirms exactly what we pass into the file pipeline.

---

### 2. Dashboard log stream (logEvents over WebSocket)

**Source:** `SimStewardPlugin.DataUpdate` builds a `LogEntry` per incident when we have `newIncidents` and call `Broadcast(logEvents)`.

**Top-level keys:** `level`, `event` (`"incident_detected"`), `message`, `timestamp`, `session_id` (from SubSessionId), `replay_frame`, `incident_id`, and `fields` (see below).

**Fields (subset sent to dashboard log):**

| Field | Type | Description |
|-------|------|-------------|
| `incident_id` | string | Same as file. |
| `incident_type` | string | Same as file. |
| `car_number` | string | Same as file. |
| `driver_name` | string | Same as file. |
| `delta` | int | Same as file. |
| `session_time` | double | Same as file. |
| `lap` | int | Same as file. |
| `source` | string | `"telemetry"` or `"yaml"`. |

**Not sent in dashboard log stream:** `sub_session_id`, `user_id`, `session_num`, `replay_frame` (it is top-level), `cause`, `other_car_number`, `other_driver_name`. Those are still in the file and in `incidentEvents` (panel).

**Debug:** We log `incident_dashboard_log_fields` with `fieldKeys` = that list so you can confirm in `debug-b0c27e.log` what we send.

---

### 3. Dashboard incident panel (incidentEvents over WebSocket)

**Source:** Same tick, `Broadcast(incidentEvents)` with `List<IncidentEvent>` (full DTOs). No extra shaping.

**IncidentEvent properties (all sent):**

| Property | Type | Description |
|----------|------|-------------|
| `id` | string | Incident id. |
| `subSessionId` | int | SubSessionID. |
| `userId` | int | iRacing user id. |
| `sessionTime` | double | Session time (s). |
| `sessionTimeFormatted` | string | e.g. "11:23". |
| `carIdx` | int | Car index. |
| `driverName` | string | Driver name. |
| `carNumber` | string | Car number. |
| `delta` | int | Incident points. |
| `totalAfter` | int | Total incidents after this one. |
| `type` | string | e.g. "1x", "4x". |
| `source` | string | "telemetry" or "yaml". |
| `otherCarIdx` | int | -1 if N/A. |
| `otherCarNumber` | string | If contact. |
| `otherDriverName` | string | If contact. |
| `cause` | string | If set. |
| `lap` | int | Lap. |
| `trackPct` | float | Track position. |
| `replayFrameNum` | int | Replay frame. |

---

## Quick checklist (what we capture)

- **File / Grafana:** incident_id, sub_session_id, user_id, incident_type, car_number, driver_name, delta, session_time, session_num, replay_frame, lap, and optionally cause, other_car_number, other_driver_name. Plus level, component, event, message, timestamp, session_id, session_seq, domain.
- **Dashboard log stream:** incident_id, incident_type, car_number, driver_name, delta, session_time, lap, source (and top-level message, timestamp, etc.).
- **Dashboard incident panel:** Full IncidentEvent (all properties above).

If something is missing in one destination, compare against this list and the code paths in section “Where incidents are logged”.
