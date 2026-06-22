# Logging rules — verbose, intentional, 100% coverage

**Principle: debugging ability is king.** Every meaningful event in the plugin must be observable in Grafana without attaching a debugger, without a repro session, and without asking the user to describe what happened. If it happened, it must be in Loki.

---

## 1. Log levels — when to use each

| Level | Use when |
|-------|----------|
| `INFO` | Something significant happened that completed normally. Lifecycle transitions, actions accepted, operations started/finished, detections. |
| `WARN` | Something unexpected happened but execution continued. Speed dropped, a command was re-issued, a prerequisite was soft-failed, a file write partially failed. |
| `ERROR` | An operation could not complete and the user or system is in a degraded state. |
| `DEBUG` | High-frequency checks, verbose periodic state dumps. Off by default; gated on `_logger.IsDebugMode`. Never emit DEBUG unconditionally in hot paths. |

---

## 2. What always requires a log entry

### 2.1 State machine transitions

Any time the plugin enters or exits a named phase/state:

```
event:  <domain>_<noun>_<transition>   e.g. replay_index_build_phase_changed
fields: previous_state, new_state, reason, [elapsed_ms in previous state]
level:  INFO
```

Example transitions that require logs:
- `Idle → SeekingStart` — log that seek was issued, saved frame, target speed
- `SeekingStart → FastForwarding` — log that frame 0 was confirmed, baseline values captured
- `FastForwarding → Idle` — log completion reason, duration, detection count, final frame

### 2.2 Long-running operations (> ~2 seconds)

Any operation that runs longer than a few seconds must emit:

1. **Start log** — what is starting, what parameters, what was the precondition state
2. **Periodic heartbeat** — at a regular interval (e.g., every 1000 ticks ~16.7s), log frame position, % complete, samples so far, actual speed, elapsed wall ms
3. **Completion log** — duration, counts, outcome reason, key metrics

The heartbeat interval must be defined as a named constant. The heartbeat must go to Loki (not WS-only).

### 2.3 External / SDK calls

Every call out to iRacing SDK, file system, or network that has observable side effects:

| Call type | Before | After |
|-----------|--------|-------|
| SDK playback command (`ReplaySetPlaySpeed`, `ReplaySearch`, `ReplaySetPlayPosition`) | Log intent: target value, current state | Log confirmation or failure; if fire-and-forget, log the telemetry read-back separately |
| File write (index JSON, etc.) | Not required | Log path, bytes written (or error) |
| Loki push (from scripts) | Not required | Log status code or error |

### 2.4 Commands and actions

Every `DispatchAction` branch — **without exception**:

```
Before:  action_dispatched  — action, arg, correlation_id, session context
After:   action_result      — action, arg, correlation_id, success, result/error, session context
```

Both entries must be present even if the action fails immediately. The `correlation_id` links them.

### 2.5 Dashboard button clicks

Every button click that sends a WS message must emit BEFORE sending:

```js
{ action:"log", event:"dashboard_ui_event", element_id:"<id>", event_type:"click", message:"<human label>" }
```

UI-only interactions (no WS message): same shape but `event_type:"ui_interaction"`, `domain:"ui"`.

### 2.6 iRacing SDK events

| Event | Log name | Required fields |
|-------|----------|-----------------|
| iRacing connect | `iracing_connected` | `subsession_id`, `sim_mode`, `track_display_name` |
| iRacing disconnect | `iracing_disconnected` | reason if determinable |
| Session start | `iracing_session_start` | `subsession_id`, `parent_session_id`, `session_num`, `track_display_name`, `sim_mode` |
| Session end | `iracing_session_end` | same as start + `session_duration_sec` |
| Mode change | `iracing_mode_change` | `mode`, `previous_mode` |
| Replay seek issued | `iracing_replay_seek` | `frame`, `mode` (ToStart / ToPrevIncident / etc.) |
| Incident detected | `iracing_incident` | `unique_user_id`, `display_name`, `camera_view`, `start_frame`, `end_frame`, `session_time`, `lap`, full session context |

### 2.7 Prerequisite / guard checks

When code evaluates a prerequisite before allowing an operation to proceed, log the outcome at the point of failure (not only the eventual error):

```
event:  <operation>_prerequisite_failed
fields: check, reason, [current_value vs expected_value]
level:  WARN
```

Example: build can't start because `SimMode != "full"` → log `replay_index_build_prerequisite_failed` with `check:"sim_mode"`, `actual:"offline"`, `required:"full"`.

### 2.8 Speed / timing verification for playback commands

iRacing playback commands are fire-and-forget. Any time the plugin issues a speed command:

1. Log the command issued (intent)
2. On first telemetry read-back confirming success: log `*_speed_confirmed` with `actual_speed`, `ticks_to_confirm`
3. On any read-back showing wrong speed: log `*_speed_lost` (WARN) with `actual_speed`, `expected_speed`, `reissued:true/false`
4. Periodic speed checks: log at DEBUG level; escalate to WARN only when speed is wrong

### 2.9 Detection events (incident / flag / anomaly)

Every detection of a meaningful game state (incident, checkered flag, session boundary, etc.) must log:

```
event:  <domain>_<detection_name>_detected
fields: fingerprint (if applicable), car_idx or driver id, session_time, replay_frame, value/delta, lap
level:  INFO
```

Detections must never be silent. If a detection fires, a log line must exist.

### 2.10 Completion with metrics

Any operation that completes (success or failure) must log a summary with:
- `duration_ms` or `elapsed_wall_ms`
- Key output counts (`detected_incident_rows`, `samples_processed`, etc.)
- `completion_reason` (natural end, user cancelled, error, cap hit, etc.)
- Key input parameters (what was it processing, at what speed, over what frame range)

---

## 3. Required context fields

### 3.1 Session context (all `action` + `iracing` + `lifecycle` logs)

Injected via `MergeSessionAndRoutingFields()`. Always present, fallback to `"not in session"`.

| Field | Source |
|-------|--------|
| `subsession_id` | `WeekendInfo.SubSessionID` — globally unique split |
| `parent_session_id` | `WeekendInfo.SessionID` — broader event |
| `session_num` | Current phase: practice / qualify / race |
| `track_display_name` | Track name |
| `lap` | Focus car `CarIdxLap`; `-1` if unknown |
| `session_yaml_fingerprint_sha256_16` | 16-char SHA-256 prefix of SessionInfoYaml |

### 3.2 Replay index build context (all logs during an active FF sweep)

When `_logCtxReplayTotalFrames > 0`, automatically injected via `MergeSessionAndRoutingFields()`:

| Field | Meaning |
|-------|---------|
| `replay_total_frames` | Total frames in the replay file (snapshot at frame 0) |
| `replay_session_count` | Number of sessions in the replay (from SessionInfo.Sessions[]) |

These must never be live-read per-tick during the sweep; they are set once at FF start and cleared at completion/cancel.

### 3.3 Incident uniqueness fields

| Field | Meaning |
|-------|---------|
| `unique_user_id` | iRacing CustID |
| `display_name` | Driver display name |
| `camera_view` | Camera/view context |
| `start_frame` | Replay frame at incident start |
| `end_frame` | Replay frame at incident end |
| `session_time` | `SessionTime` at detection |
| `fingerprint` | Hex fingerprint (subsession + car + time + source + points) |

---

## 4. Logging anti-patterns — never do these

| Anti-pattern | Why |
|---|---|
| Logging in `DataUpdate()` unconditionally | Runs at 60 Hz — generates millions of lines; use event-driven triggers |
| Swallowing exceptions silently (`catch { }`) with no log | The error becomes invisible in Grafana |
| Logging only on success, not on failure | Failures are the most important thing to see |
| Using `_logger.Warn(string)` instead of `Structured()` for anything meaningful | Unstructured lines can't be queried by field in Grafana |
| Logging a state change without the previous state | Makes it impossible to trace transitions |
| Periodic heartbeats to WebSocket only (not Loki) | WS is ephemeral; only Loki survives after the session |
| Large array data in log fields at INFO level | Keep per-car arrays to baseline-only; use counts/deltas elsewhere |
| Same `correlation_id` reused across different requests | Breaks the ability to isolate a single action's before/after pair |

---

## 5. Domain taxonomy

| `domain` | When to use |
|----------|-------------|
| `lifecycle` | Plugin init/shutdown, SDK connect/disconnect, index build phases, FF sweep events |
| `action` | `DispatchAction` entries — dashboard → plugin commands |
| `ui` | Dashboard-only interactions that never cross the WS bridge |
| `iracing` | iRacing SDK events: session, mode, incident, replay seek, flag |
| `system` | Dependency checks, ping, WS client connect/disconnect, deploy markers |

---

## 6. PR checklist

Before merging any change that touches plugin logic or dashboard buttons:

- [ ] New `DispatchAction` branch → `action_dispatched` + `action_result` with session context
- [ ] New dashboard button → `dashboard_ui_event` log before WS send
- [ ] New state machine phase or transition → log previous + new state + reason
- [ ] New long-running operation → start + periodic heartbeat (Loki, not WS-only) + completion with metrics
- [ ] New iRacing SDK call with side effects → log intent, log telemetry read-back confirmation
- [ ] New detection (incident / flag / anomaly) → structured log with uniqueness fields
- [ ] New prerequisite guard → log on failure with `check`, `actual`, `required`
- [ ] Any new `catch` block → log the exception with context; never silent swallow
- [ ] `replay_total_frames` + `replay_session_count` present in all FF-phase logs (automatic via `MergeSessionAndRoutingFields` when build active)

---

## 7. Code touchpoints

| Concern | Location |
|---------|----------|
| Session context injection | `SimStewardPlugin.cs` — `MergeSessionAndRoutingFields()` |
| Replay build context injection | Same method — `_logCtxReplayTotalFrames` / `_logCtxReplaySessionCount` |
| Structured log API | `PluginLogger.cs` — `Structured(level, component, event, message, fields, domain, null)` |
| Dashboard WS bridge log | `DashboardBridge.cs` — `OnDashboardStructuredLog()` |
| Session fallback constant | `SessionLogging.cs` — `NotInSession`, `LapUnknown` |
| Index build lifecycle | `SimStewardPlugin.ReplayIncidentIndexBuild.cs` |
| Constants + event names | `ReplayIncidentIndexBuild.cs` — all `Event*` constants |
