# iRacing Observability Strategy
## Grafana · Loki · Prometheus

---

## 1. Design Principles

**Prometheus** stores what you want to query, graph, and alert on numerically over time — gauges, counters, rates.

**Loki** stores discrete events with structured context — things that happened, not things that are happening.

The split is determined by one question: *is this a measurement or an event?*

| If it's... | Goes to |
|---|---|
| A numeric value sampled repeatedly over time | **Prometheus** |
| A discrete occurrence with contextual metadata | **Loki** |
| A state transition | **Loki** (event) + **Prometheus** (resulting gauge) |

**Correlation key** across both systems:

```
subsession_id  +  car_idx  +  session_time_ms  +  camera_angle_id
```

This four-field tuple is sufficient to join any Prometheus metric to any Loki log line for the same car at the same moment. `subsession_id` is the race identity, `car_idx` is the driver identity within that race, `session_time_ms` is the timestamp in the session's own clock (not wall clock, avoiding replay/live discrepancy), and `camera_angle_id` (or equivalent view identifier like `camera_group`) captures the perspective.

---

## 2. Architecture

```
iRacing SDK (60Hz)
        │
        ▼
  SDK Exporter (local C# process)
        │
        ├──── Prometheus metrics (/metrics endpoint, scraped every 1s)
        │
        └──── Loki log shipping (via Promtail or Grafana Alloy, structured JSON)

iRacing REST API (post-race)
        │
        └──── Loki enrichment logs (one-shot on race completion)
```

All components run locally. No external services required unless you want remote Grafana Cloud.

**Sampling strategy:** The SDK emits at 60Hz. The exporter maintains a rolling in-memory state and:
- Exposes the **latest value** to Prometheus (scraped at 1s interval — sufficient for dashboards)
- Emits a **log line to Loki only when something changes** — not on every 60Hz tick

This keeps Prometheus cardinality manageable and Loki volume reasonable.

---

## 3. Prometheus — Metrics

### 3.1 Labels

Apply consistently to all metrics. Keep cardinality controlled.

```
job="iracing"
source="sdk_live" | "sdk_replay" | "rest_api"
subsession_id="<int>"        # changes per race — acceptable single-session cardinality
car_idx="<0-63>"             # only on per-car metrics
```

> `driver_name` and `cust_id` are **never Prometheus labels** — use Loki for driver-identity events. High-cardinality string labels in Prometheus are expensive.

### 3.2 Metric Definitions

#### Session State

| Metric | Type | Labels | Notes |
|---|---|---|---|
| `iracing_session_state` | Gauge | `job`, `source`, `subsession_id` | Enum: 0=invalid, 1=get_in_car, 2=warmup, 3=parade, 4=racing, 5=checkered, 6=cooldown |
| `iracing_session_flags` | Gauge | `job`, `source`, `subsession_id` | Bitfield. Use recording rules to extract individual flag bits. |
| `iracing_session_time_seconds` | Gauge | `job`, `source`, `subsession_id` | Current session clock. |
| `iracing_session_caution_count_total` | Counter | `job`, `source`, `subsession_id` | Increments on each caution. |

#### Per-Car State (all cars, live + replay)

| Metric | Type | Labels | Notes |
|---|---|---|---|
| `iracing_car_lap_dist_pct` | Gauge | `…`, `car_idx` | 0.0–1.0 track position. |
| `iracing_car_lap` | Gauge | `…`, `car_idx` | Current lap number. |
| `iracing_car_position` | Gauge | `…`, `car_idx` | Race position. Lags until S/F line — expected. |
| `iracing_car_rpm` | Gauge | `…`, `car_idx` | Broadcast-grade RPM. |
| `iracing_car_gear` | Gauge | `…`, `car_idx` | -1=reverse, 0=neutral, 1–n. |
| `iracing_car_steer_angle_rad` | Gauge | `…`, `car_idx` | Steering angle in radians. |
| `iracing_car_on_pit_road` | Gauge | `…`, `car_idx` | 0/1 boolean. |
| `iracing_car_speed_kph` | Gauge | `…`, `car_idx` | **Derived.** Computed from `CarIdxLapDistPct` × track length, differentiated. Not a native SDK field. |
| `iracing_car_last_lap_seconds` | Gauge | `…`, `car_idx` | Last lap time. |
| `iracing_car_best_lap_seconds` | Gauge | `…`, `car_idx` | Best lap time this session. |
| `iracing_car_fast_repairs_used` | Counter | `…`, `car_idx` | Increments on confirmed damage repair. |
| `iracing_car_incident_flag_active` | Gauge | `…`, `car_idx` | 1 if repair or furled bit currently set in `CarIdxSessionFlags`. |

#### Player Car Only (higher fidelity)

| Metric | Type | Labels | Notes |
|---|---|---|---|
| `iracing_player_throttle` | Gauge | `job`, `source`, `subsession_id` | 0.0–1.0. Native SDK, not derived. |
| `iracing_player_brake` | Gauge | `…` | 0.0–1.0. |
| `iracing_player_clutch` | Gauge | `…` | 0.0–1.0. |
| `iracing_player_speed_kph` | Gauge | `…` | Native SDK — higher fidelity than derived per-car speed. |
| `iracing_player_rpm` | Gauge | `…` | Native, higher precision than `iracing_car_rpm`. |
| `iracing_player_fuel_level_liters` | Gauge | `…` | |
| `iracing_player_incident_count_total` | Counter | `…` | `PlayerCarMyIncidentCount` — running total for player only. |
| `iracing_player_lat_accel_g` | Gauge | `…` | |
| `iracing_player_long_accel_g` | Gauge | `…` | |

#### Replay / Index Build

| Metric | Type | Labels | Notes |
|---|---|---|---|
| `iracing_replay_frame_current` | Gauge | `job`, `subsession_id` | `ReplayFrameNum` |
| `iracing_replay_frame_total` | Gauge | `job`, `subsession_id` | `ReplayFrameNumEnd` |
| `iracing_replay_play_speed` | Gauge | `job`, `subsession_id` | Current playback multiplier. |
| `iracing_incident_index_build_duration_seconds` | Histogram | `job`, `subsession_id` | Duration of full fast-forward index build. |
| `iracing_incident_index_total` | Gauge | `job`, `subsession_id` | Total incidents found in completed index. |

### 3.3 Recording Rules

Useful pre-computed rules to avoid query-time complexity:

```yaml
groups:
  - name: iracing_derived
    rules:
      - record: iracing_car_gap_to_leader_seconds
        expr: max(iracing_car_last_lap_seconds) by (subsession_id) - iracing_car_last_lap_seconds

      - record: iracing_session_flag_caution
        expr: (iracing_session_flags % 16384) >= 8192  # bit 0x2000

      - record: iracing_session_flag_yellow
        expr: (iracing_session_flags % 16) >= 8        # bit 0x0008
```

---

## 4. Loki — Events

### 4.1 Labels

Loki labels must be **low cardinality**. Keep them coarse.

```
job="iracing"
source="sdk_live" | "sdk_replay" | "rest_api"
level="info" | "warn" | "error"
event_type="incident" | "lap" | "flag" | "session" | "retirement" | "pit" | "index"
```

> `subsession_id`, `car_idx`, `driver_name` are **JSON body fields, not labels.** Putting high-cardinality values in Loki labels destroys query performance and increases index size.

### 4.2 Log Line Schema

All log lines are structured JSON. The correlation key fields are present in every line:

```json
{
  "ts": "<ISO-8601 wall clock>",
  "session_time_ms": 184320,
  "subsession_id": 12345678,
  "session_num": 0,
  "event_type": "<see below>",
  "car_idx": 3,
  "cust_id": 987654,
  "driver_name": "J. Verstappen",
  "camera_angle_id": "TV2",
  "source": "sdk_replay"
}
```

`session_time_ms` is the iRacing session clock in milliseconds — use this to join with Prometheus metrics queried at the equivalent timestamp.

### 4.3 Event Types

#### `incident`

Emitted when `CarIdxSessionFlags` repair or furled bit rises, or `PlayerCarMyIncidentCount` increments.

```json
{
  "event_type": "incident",
  "car_idx": 3,
  "cust_id": 987654,
  "driver_name": "J. Verstappen",
  "camera_angle_id": "TV2",
  "session_time_ms": 184320,
  "subsession_id": 12345678,
  "session_num": 0,
  "detection_source": "repair_flag",
  "incident_points": null,
  "lap_number": 14,
  "lap_dist_pct": 0.623,
  "source": "sdk_replay"
}
```

> `incident_points` is non-null only when `detection_source = player_incident_count` — this is the only case where point value is known. For other cars it is always null.

#### `lap_complete`

Emitted on each `CarIdxLapCompleted` increment.

```json
{
  "event_type": "lap_complete",
  "car_idx": 3,
  "cust_id": 987654,
  "driver_name": "J. Verstappen",
  "session_time_ms": 312400,
  "subsession_id": 12345678,
  "lap_number": 14,
  "lap_time_ms": 87320,
  "is_personal_best": false,
  "is_session_best": false,
  "position": 2,
  "source": "sdk_live"
}
```

#### `flag_change`

Emitted on `SessionFlags` global change or `CarIdxSessionFlags[n]` state change.

```json
{
  "event_type": "flag_change",
  "flag": "yellow" | "caution" | "green" | "red" | "repair" | "furled" | "black" | "disqualify",
  "scope": "global" | "car",
  "car_idx": null,
  "session_time_ms": 220100,
  "subsession_id": 12345678,
  "previous_state": 0,
  "new_state": 1,
  "source": "sdk_live"
}
```

#### `session_state_change`

```json
{
  "event_type": "session_state_change",
  "previous_state": "racing",
  "new_state": "checkered",
  "session_time_ms": 3840000,
  "subsession_id": 12345678,
  "session_num": 0,
  "source": "sdk_live"
}
```

#### `retirement`

Emitted when `ReasonOutStr` becomes non-empty for a car.

```json
{
  "event_type": "retirement",
  "car_idx": 7,
  "cust_id": 112233,
  "driver_name": "S. Hamilton",
  "reason_str": "Contact",
  "reason_id": 3,
  "session_time_ms": 1920450,
  "lap_number": 22,
  "subsession_id": 12345678,
  "source": "sdk_live"
}
```

#### `pit_entry` / `pit_exit`

On `CarIdxOnPitRoad` transition.

```json
{
  "event_type": "pit_entry",
  "car_idx": 5,
  "cust_id": 445566,
  "driver_name": "C. Leclerc",
  "session_time_ms": 980000,
  "lap_number": 11,
  "subsession_id": 12345678,
  "fast_repairs_used": 0,
  "source": "sdk_live"
}
```

#### `incident_index_complete`

Emitted once when the fast-forward index build finishes.

```json
{
  "event_type": "incident_index_complete",
  "subsession_id": 12345678,
  "session_num": 0,
  "total_incidents_detected": 18,
  "incident_count_by_car_idx": { "3": 2, "7": 1, "12": 4 },
  "build_duration_ms": 34200,
  "replay_speed_multiplier": 64,
  "source": "sdk_replay"
}
```

---

## 5. Correlation in Grafana

### 5.1 Joining Prometheus + Loki

The standard pattern in Grafana is a dashboard variable on `subsession_id`, used in both a Prometheus metric query and a Loki log panel.

**Prometheus query** — player throttle trace during an incident lap:
```promql
iracing_player_throttle{subsession_id="$subsession_id"}
```

**Loki query** — incidents for the same race:
```logql
{job="iracing", event_type="incident"} | json | subsession_id="$subsession_id"
```

To correlate to a specific moment: take `session_time_ms` from a Loki incident log line, convert to a Prometheus timestamp using the session start wall-clock time, and use Grafana's annotations to mark the moment on the metric graph.

### 5.2 Session Start Wall-Clock Offset

The session clock (`session_time_ms`) and wall clock diverge. To align them:

- Record wall-clock time when `SessionState` transitions to `racing` (4) and `session_time_ms` at that moment
- Store this as a `session_start_offset` in the `session_state_change` Loki event
- All subsequent joins: `wall_time = session_start_wall + (session_time_ms - session_start_session_ms)`

### 5.3 Annotations

Configure Prometheus alerting or Grafana annotations to mark incident events on time series panels:

```logql
{job="iracing", event_type="incident"} | json | car_idx="$car_idx" | subsession_id="$subsession_id"
```

Use `session_time_ms` from the log as the annotation timestamp (after offset conversion).

---

## 6. Risks & Gaps

### Prometheus

| Risk | Impact | Mitigation |
|---|---|---|
| 60Hz SDK data → Prometheus scrape mismatch | Prometheus sees only the value at scrape time; transient spikes within a second are invisible | Accept — 1Hz is sufficient for dashboards. Use Loki events for precise incident moments. |
| `iracing_car_speed_kph` is derived from `LapDistPct` | Inaccurate at low speeds, pits, and when cars are stationary. dP/dt is noisy. | Flag as derived in metric name. Do not use for safety-critical analysis. |
| `iracing_car_position` lags real position | iRacing only updates at S/F line — mid-lap overtakes not reflected | Known SDK behaviour. Use `CarIdxLapDistPct` for real-time relative position calculations. |
| SDK may not emit `CarIdxSessionFlags` repair bits during fast-forward | Entire incident detection approach is at risk | Primary open question — empirical test required (see tech requirements doc). |
| Prometheus cardinality: `car_idx` × `subsession_id` | ~63 cars × N races = manageable for local setup, but grows with retention | Set short retention (7–14 days) for per-car metrics. Use recording rules to aggregate. |

### Loki

| Risk | Impact | Mitigation |
|---|---|---|
| `incident_points` is null for all non-player cars | Cannot determine 1x/2x/4x for other drivers | Unavoidable SDK limitation. Document clearly. `player_incident_count` delta is the only source. |
| `driver_name` and `cust_id` not available during fast-forward replay | Logs may lack driver identity for non-player cars if YAML hasn't loaded | Cross-reference `car_idx` against `DriverInfo` from session YAML after index build. Enrich log lines in post-processing. |
| REST API incident data (per-lap) and SDK fast-forward data (per-frame) may show different timestamps | Confusion when joining the two sources | Always prefer SDK fast-forward timestamp for precision. REST API lap-level data is only for validation and gap-fill when replay is unavailable. |
| Event deduplication during replay seek | If replay jumps backward, incident events may fire again | Include `source: sdk_replay` label. Filter or deduplicate by `subsession_id + car_idx + session_time_ms + camera_angle_id` at ingest. |

### General

| Risk | Impact | Mitigation |
|---|---|---|
| `CarIdxThrottlePct` / `CarIdxBrakePct` permanently removed | No pedal telemetry for other cars anywhere in the stack | Nothing to mitigate. Document as a known permanent gap. |
| iRacing REST API requires OAuth (no local fallback) | Per-lap incident flags unavailable without credentials | The SDK fast-forward approach is the local fallback. REST API is optional enrichment only. |
| YAML session string `ResultsPositions` may not be populated if replay loaded before race ended | Final incident totals missing for validation | Check `ResultsOfficial = 1` before reading. If absent, rely solely on SDK detection. |

---

## 7. What Is Not Logged (and Why)

| Data | Decision |
|---|---|
| Raw 60Hz CarIdx arrays | Too voluminous for Loki. Only state changes and events are logged. Prometheus holds the sampled time series. |
| `CamCarIdx` changes | Not meaningful as a logged event — camera switches are too frequent in replay. Only relevant as context within incident events. |
| `ReplayFrameNum` per frame | Prometheus gauge is sufficient. No log line needed. |
| `CarIdxTireCompound` | Static within a stint. Log a single `tire_change` event on compound change rather than sampling. |
| Network/comms variables (`ChanLatency` etc.) | Irrelevant to race analysis. Not logged. |

---

*iRacing Observability Strategy — March 2026*
