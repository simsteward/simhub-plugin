# iRacing SDK — Data Points We Use vs. What We Could Add

Research summary: what the plugin currently captures from the iRacing SDK (IRSDKSharper) and what additional data points we could or should capture for Grafana, the dashboard, or steward use. Constraint: **no per-tick logging** in production (DataUpdate runs at 60 Hz; project rule: &lt; 1 MB per race session).

---

## 0. Data points emitted globally (non–iRacing)

All emissions that go to **every connected client** or to **Loki / log stream** (i.e. global outputs), not just iRacing-derived.

### 0.1 WebSocket message types (pushed to dashboard)

| Type | When | Payload |
|------|------|---------|
| **state** | Throttled (~5/s) from DataUpdate; also on client connect | `pluginMode`, `currentSessionTime`, `currentSessionTimeFormatted`, `replayIsPlaying`, `replayFrameNum`, `replayFrameNumEnd`, `replayPlaySpeed`, `replayPlaySlowMotion`, `replaySessionNum`, `playerCarIdx`, `playerIncidentCount`, `hasLiveIncidentData`, `trackName`, `trackCategory`, `trackLengthM`, `sessionId`, `drivers[]`, `incidents[]`, `metrics`, `diagnostics`, `sessionDiagnostics` |
| **logEvents** | Every `LogWritten` (each structured log); also log tail on connect | `entries[]` (LogEntry: level, message, timestamp, component, event, fields) |
| **incidentEvents** | When new incidents are broadcast (batch) | `events[]` (IncidentEvent: id, sessionTime, carIdx, driverName, carNumber, delta, type, cause, lap, trackPct, replayFrameNum, etc.) |
| **sessionComplete** | After session summary capture (checkered / manual / finalize) | `summary` (SessionSummary: session ids, track, results table, incident feed, laps, cautions, etc.) |
| **actionResult** | After every dashboard action | `action`, `success`, `result`, `error` |
| **pong** | Reply to "ping" action | — |
| **error** | Invalid JSON or missing action in client message | `error` (string) |

### 0.2 State snapshot fields (inside `state`)

- **diagnostics:** `irsdkStarted`, `irsdkConnected`, `wsRunning`, `wsPort`, `wsClients`, `playerCarIdx`
- **sessionDiagnostics:** `simMode`, `irSessionId`, `irSubSessionId`, `sessionState`, `sessionNum`, `sessionInfoUpdate`, `sessionFlags`, `hasSessionInfo`, `selectedResultsSessionNum`, `selectedResultsSessionType`, `resultsPositionsCount`, `resultsLapsComplete`, `resultsOfficial`, `resultsReady`, `activeDriverCount`, `driversWithNonZeroIncidents`, `maxDriverIncidents`, `allNonSpectatorIncidentsZero`, `lastSummaryCapture`
- **metrics:** `l4YamlEvents`, `totalEvents`, `yamlUpdates`, `lastDetectionSessionTime`

### 0.3 Structured log events (to Loki + dashboard log stream)

Same as in **docs/GRAFANA-LOGGING.md** event taxonomy: `logging_ready`, `loki_config_applied`, `settings_saved`, `loki_sink_ready`, `plugin_started`, `actions_registered`, `bridge_starting`, `bridge_start_failed`, `plugin_ready`, `log_streaming_subscribed`, `irsdk_started`, `loki_status`, `plugin_stopped`, `iracing_connected`, `iracing_disconnected`, `ws_client_connected`, `ws_client_disconnected`, `dashboard_opened`, `ws_client_rejected`, `action_received`, `action_dispatched`, `action_result`, `replay_control`, `session_snapshot_recorded`, `checkered_detected`, `checkered_retry`, `session_capture_skipped`, `session_summary_captured`, `finalize_capture_*`, `incident_detected`, `baseline_established`, `session_reset`, `seek_backward_detected`, `yaml_update`, `tracker_status`, `session_digest`. Debug-only: `state_broadcast_summary`, `tick_stats`, `ws_message_raw`.

### 0.4 File outputs (local, not to Grafana)

- **plugin.log** — All PluginLogger file writes (same content as log stream, minus omitted levels/events).
- **broadcast-errors.log** — Send failures and "0 clients" (no logger, no Loki).

---

## 1. What we already use (iRacing SDK)

### Telemetry variables (live, 60 Hz) — read in plugin

| Variable | Where used | Purpose |
|----------|------------|---------|
| `SessionTime` | DataUpdate, IncidentTracker, session summary | Current session time (s); replay position; session_digest |
| `SessionNum` | BuildStateJson, IncidentTracker, capture | Session index; incident/session context |
| `SessionState` | IncidentTracker (post-race check), checkered detection | Checkered = 5+; drive state |
| `SessionInfoUpdate` | IncidentTracker (YAML gate), capture readiness | When to refresh YAML; FinalizeThenCapture wait |
| `SessionFlags` | BuildStateJson (state snapshot) | Exposed in plugin state (not yet logged to Loki) |
| `ReplayFrameNum` / `ReplayFrameNumEnd` | IncidentTracker, FinalizeThenCapture | Replay position; incident replay frame |
| `ReplayPlaySpeed` | DataUpdate, replay actions | Playback speed (e.g. 16x) |
| `ReplayPlaySlowMotion` | ReplaySetPlaySpeed | Slow-mo flag |
| `ReplaySessionNum` | BuildStateJson, capture | Replay session index |
| `CarIdxLapDistPct` | IncidentTracker (float array) | Per-car lap % for cause inference (car-contact vs wall) |
| `FramesDropped` | Debug tick_stats only | SDK health |

### SessionInfo (YAML) — parsed when SessionInfoUpdate changes

| Section / field | Where used | Purpose |
|------------------|------------|---------|
| **WeekendInfo** | Track name, category, SimMode, track length, config | Session summary, incident cause (dirt vs paved), session_digest |
| **DriverInfo** | DriverCarIdx, Drivers[] (UserName, CarNumber, CarIdx, CurDriverIncidentCount, IsSpectator, CarClassShortName) | Player car; incident deltas (Layer 4); results table names/classes |
| **SessionInfo.Sessions[]** | SessionNum, ResultsPositions, ResultsLapsComplete, ResultsNumCautionLaps, ResultsAverageLapTime, ResultsOfficial, etc. | Session summary capture at checkered; session_digest |
| **ResultsPositions[]** | Position, CarIdx, LapsComplete, LapsLed, FastestTime, FastestLap, LastTime, Incidents, ReasonOutStr, ClassPosition | Authoritative results table in session_summary_captured / session_digest |

### Replay / broadcast API (IRSDKSharper)

- `ReplaySetPlaySpeed`, `ReplaySetPlayPosition`, `ReplaySearch`, `ReplaySearchSessionTime` — used for Play/Pause, step, next/prev incident, seek to incident.
- Camera actions: `SetCaptureCamera1` / `SetCaptureCamera2` (wired; implementation may use broadcast or in-sim).

---

## 2. What the iRacing SDK exposes (we don’t yet log or use)

Sources: [sajax irsdkdocs](https://sajax.github.io/irsdkdocs/) (YAML, telemetry), IRSDKSharper (C#), project rules (no 60 Hz logging).

### Telemetry (live) — high level only; many more exist

- **Driver/car**: `PlayerCarIdx` (we set from telemetry CamCarIdx when valid — camera-focused car — else YAML DriverInfo.DriverCarIdx; so in replay the "player" is the driver being watched), `Lap`, `LapDistPct`, `Speed`, `RPM`, `Gear`, throttle/brake/clutch, steering, etc.
- **Session**: `SessionLapsRemaining`, `SessionTimeTotal`, `LapCurrentLapTime`, `LapLastLapTime`, etc.
- **Replay**: We use ReplayFrameNum/End, ReplayPlaySpeed, ReplaySessionNum; SDK also has replay search state.
- **Flags**: `SessionFlags` (we read but don’t log to Loki); bitfield: green, yellow, checkered, white, black, etc.
- **Weather**: `TrackWetness`, `WeatherDeclaredWet`, `AirPressure`, `AirTemp`, etc. (WeekendInfo has more static weather/track env).
- **Car/tires**: various tire temps, wear, pressures; fuel; damage — not used today.

### YAML (SessionInfo) we don’t log as events

- **WeekendInfo**: SeriesID, SeasonID, SessionID, SubSessionID, LeagueID, EventType, RaceWeek, Official, TrackAirTemp, TrackSurfaceTemp, TrackWindVel/Dir, TrackPrecipitation, WeekendOptions (IncidentLimit, FastRepairsLimit, etc.).
- **DriverInfo**: AbbrevName, IRating, Licenses, TeamName, etc. — we use UserName, CarNumber, CarIdx, CurDriverIncidentCount, IsSpectator, CarClassShortName.
- **SessionInfo.Sessions**: ResultsFastestLap, ResultsAverageLapTime, session type names, etc. — we use results and lap counts for session summary.

---

## 3. Data points we could or should add (with caveats)

### 3.1 Good candidates (event-driven, low volume, useful for steward/Grafana)

| Data point | Source | Rationale | How |
|------------|--------|-----------|-----|
| **SessionFlags on change** | Telemetry `SessionFlags` | Green / yellow / checkered / white affects context of incidents and session phase. | Emit a single log event when `SessionFlags` value changes (e.g. `session_flags_changed` with `flags` bitfield or decoded list). Throttle or debounce so at most a few per session. |
| **Session state transitions** | Telemetry `SessionState` | We already use SessionState for checkered; logging state transitions (e.g. → checkered, → cooldown) gives a clear timeline in Grafana. | One log line per transition (e.g. `session_state_changed` with `from`, `to`). |
| **Lap count at checkered** | Already have from ResultsPositions / session summary | Sometimes useful in session_digest or session_summary_captured. | We already have TotalLapsComplete; ensure it’s in session_digest (it is). Optional: add `leader_lap_at_checkered` if available from session. |
| **Weekend/series identifiers in digest** | YAML WeekendInfo | SeriesID, SeasonID, SessionID, SubSessionID improve filtering and linking in Grafana. | Add to `session_digest` (and optionally to `session_summary_captured`) as optional fields; we already send SessionID/SubSessionID in SessionSummary. |
| **IncidentLimit / FastRepairsLimit** | YAML WeekendOptions | Explains steward/session rules. | Include in session_digest or a one-time `session_rules` event at session start (when we first have SessionInfo). |

### 3.2 Optional (useful but need volume control)

| Data point | Source | Rationale | Caveat |
|------------|--------|-----------|--------|
| **Track conditions at session start** | WeekendInfo (TrackSurfaceTemp, TrackAirTemp, TrackWetness if in YAML) or telemetry TrackWetness | Context for incidents and replay. | One-time at session start or first YAML; keep in session_digest or single event. |
| **Replay speed changes** | We already have ReplayPlaySpeed in state | Audit trail of “who ran at 16x” for clips. | We log `replay_control` for seek/play/pause; could add explicit `replay_speed_changed` with speed (throttled if needed). |
| **Camera change** | Broadcast/camera API | Which camera was used for capture. | Log when SetCaptureCamera1/2 is used (event already exists as action; ensure we have a log line with camera id). |

### 3.3 Do not add (or only behind debug)

| Data point | Reason |
|------------|--------|
| **Per-tick telemetry** (Speed, RPM, LapDistPct, throttle/brake every tick) | 60 Hz = 86 MB/hour; violates volume rule. Use only for in-memory logic (e.g. we already use CarIdxLapDistPct for cause inference), not for logging every value. |
| **Full YAML dumps** | Already forbidden (no full state in logs). Log identifiers and counts only. |
| **High-rate SessionTime / Lap** | We already log SessionTime in incidents and session summary; no need for a separate time-series log at 60 Hz. |
| **Every SessionInfoUpdate** | We only log `yaml_update` in debug; in production we use SIU to gate processing, not as a log stream. |

---

## 4. Recommended next steps

1. **Add session_flags_changed (or session_state_changed)**  
   Emit one structured log when `SessionFlags` or `SessionState` changes (with previous/current value). Gives a clear session-phase timeline in Grafana without volume.

2. **Enrich session_digest (and optionally session_summary_captured)**  
   Add WeekendInfo identifiers we don’t yet send (e.g. SeriesID, SeasonID if not already there) and optional WeekendOptions (IncidentLimit, FastRepairsLimit) so Grafana/dashboards can filter and explain rules.

3. **Document SessionFlags in GRAFANA-LOGGING.md**  
   If we add a flags/state event, add it to the event taxonomy and label schema.

4. **Leave telemetry-only (Speed, RPM, Lap, throttle/brake) out of logs**  
   Use them only inside the plugin (e.g. future cause or risk logic), not as logged datapoints to Grafana.

---

## 5. References

- iRacing SDK docs (telemetry, YAML): https://sajax.github.io/irsdkdocs/
- IRSDKSharper (C#): https://github.com/mherbold/IRSDKSharper
- Project: `docs/GRAFANA-LOGGING.md` (event taxonomy, volume budget), `.cursor/rules/SimHub.mdc` (no per-tick logging, volume guard).
