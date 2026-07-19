# Test Rig — WS Contract & Locked Decisions

This is the contract between the C# plugin and the Test Rig dashboard view. Both sides build against this doc; deviations require updating it first.

Source of truth for the [Test Rig plan](../../.claude/plans/zany-kindling-marble.md).

## Action bus (dashboard → plugin)

All actions go through `DispatchAction(action, arg, correlationId)` in `src/SimSteward.Plugin/SimStewardPlugin.cs`. Each new branch:

- Wraps its body in `try { ... } catch (Exception ex) { SentrySdk.CaptureException(ex); ... }` matching the existing pattern at `:675`, `:729`, `:771`, etc.
- Logs `action_dispatched` (pre-call) and `action_result` (post-call) via `LogActionResult(...)` per [docs/RULES-ActionCoverage.md](RULES-ActionCoverage.md).
- Returns `(bool success, string error, string supplement)`.

### Replay control actions

| Action | `arg` | SDK call | Errors |
|---|---|---|---|
| `replay_play` | `""` | `_irsdk.ReplaySetPlaySpeed(_lastReplaySpeedMagnitude > 0 ? _lastReplaySpeedMagnitude : 1, false)` (with sign per `_lastReplayDirection`) | `not_connected` |
| `replay_pause` | `""` | `_irsdk.ReplaySetPlaySpeed(0, false)` | `not_connected` |
| `replay_set_speed` | numeric: `"1"`, `"2"`, `"4"`, `"8"`, `"16"`, or `"0.5"` for slow-mo | `_irsdk.ReplaySetPlaySpeed(magnitude, slowMotion)` — magnitude unsigned (1-16); direction comes from `_lastReplayDirection` field; slowMo when arg < 1 | `bad_arg` (non-numeric, ≤0, >16), `not_connected` |
| `replay_set_direction` | `"forward"` \| `"reverse"` | recompute against `_lastReplaySpeedMagnitude` → `ReplaySetPlaySpeed(±magnitude, slowMotion)` | `bad_arg`, `not_connected`, `no_prior_speed` |
| `replay_seek_frame` | int frame, e.g. `"43210"` | `_irsdk.ReplaySetPlayPosition(RpyPosMode.Begin, frame)` — gated by `IsSeekThrottled()` / `MarkSeekIssued()` | `bad_arg`, `seek_throttled`, `not_connected` |
| `replay_jump_next_incident` | `""` | walk `_replayIncidentIndex.Incidents` for first row with `ReplayFrame > current`, then `_irsdk.ReplaySearchSessionTime(sn, row.SessionTimeMs)`. Stamps `_lastJumpExpected*` for misfire detection. | `index_unavailable`, `at_end`, `seek_throttled`, `not_connected` |
| `replay_jump_prev_incident` | `""` | same as above, last row with `ReplayFrame < current` | `index_unavailable`, `at_start`, `seek_throttled`, `not_connected` |

### REMOVED action

- `replay_speed` — **DELETED**. Existing callers in `src/SimSteward.Dashboard/index.html` (main dashboard and the merged Replay Index tab, formerly `replay-incident-index.html`) migrate to `replay_set_speed` (magnitude) + `replay_set_direction` (forward/reverse) as part of Phase 2.

### Speed semantics (sim-expert verified)

Per [iRacing SDK research](../.claude/plans/zany-kindling-marble.md):

- `ReplaySetPlaySpeed(speed, slowMotion)`:
  - `slowMotion = false`: `speed` is the multiplier (1, 2, 4, 8, 16). Negative = reverse direction at that magnitude.
  - `slowMotion = true`: `speed` is the divisor (1 = ½×, 2 = ¼×, etc.).
- iRacing's documented max is 16×; values >16 are undefined behavior. **Plugin clamps to ±16 with `bad_arg` error if requested out of range.**
- Plugin tracks two state fields:
  - `int _lastReplaySpeedMagnitude` — unsigned, last applied magnitude (default 1)
  - `string _lastReplayDirection` — `"forward"` (default) or `"reverse"`
- `replay_set_speed` updates magnitude only; direction unchanged.
- `replay_set_direction` updates direction only; magnitude unchanged.
- `replay_play` resumes at last (magnitude, direction).
- `replay_pause` does NOT clear magnitude/direction — pause only sets `playSpeed=0`.

## WS pushes (plugin → dashboard)

All broadcasts go through `DashboardBridge.Broadcast(json, channelKey)`. Channel keys feed the dedup throttle map; new keys: `"replayStateTick"` and `"replaySweepProgressTick"`.

### `session_hello`

Sent to each client right after it connects, and re-broadcast to all clients whenever the loaded subsession changes (so a client that connected before the replay finished loading still learns the id). One broadcast per change — never per tick. Built by `SessionHello.BuildJson`.

```json
{
  "type": "session_hello",
  "sub_session_id": 12345678,
  "sim_mode": "replay",
  "plugin_mode": "Replay"
}
```

- `sub_session_id` is `null` when no session is loaded (`SubSessionID == 0` / IRSDK absent).
- `sim_mode` is `null` when unknown; `plugin_mode` is `"Replay"` or `"Unknown"`.
- The test rig (`scripts/test-rig/run.js`) reads this to auto-detect `--subsession`: flag omitted → use this id; flag supplied and mismatched → abort; no non-null id within 30 s → fail (`no_replay_loaded`). `sub_session_id` is intentionally **not** on `replay_state_tick`.

### `replay_state_tick`

Emitted every 250 ms when `_pluginMode == "Replay"` AND `!_replayIndexBuildActive`.

```json
{
  "type": "replay_state_tick",
  "ts": "2026-05-07T12:34:56.789Z",
  "frame": 48231,
  "frame_end": 124000,
  "session_time": 754.5,
  "paused": false,
  "speed": 4,
  "direction": "forward",
  "slow_motion": false,
  "aggregates": {
    "ours":  { "incidents": 47, "points": 91, "off_tracks": 31, "car_contacts": 16 },
    "yaml":  { "incidents": 49, "points": 95, "off_tracks": 32, "car_contacts": 17 }
  },
  "drivers": [
    {
      "pos": 1,
      "car_idx": 12,
      "name": "J. Smith",
      "cust_id": "123456",
      "our_inc": 4,
      "yaml_inc": 4,
      "our_pts": 8,
      "off_tracks": 3,
      "car_contacts": 1
    }
  ],
  "misfire": {
    "active": false,
    "direction": null,
    "expected_frame": 0,
    "landed_frame": 0,
    "delta_frames": 0,
    "delta_ms": 0,
    "expected_fingerprint": null,
    "nearest_fingerprint": null
  }
}
```

**YAML in-progress rule:** when YAML `ResultsPositions[].Incidents` are all 0 (session not yet final), `aggregates.yaml.*` and per-driver `yaml_inc` fields emit as `null` (NOT 0). Dashboard renders `"—"` instead of `"0"` for null. This prevents Δ values from looking like 100% over-detection during in-progress replays.

**`misfire.active`** is `true` for ~2 seconds after a `replay_jump_*_incident` evaluation flagged a mismatch. Auto-clears after 2 s; the field then reverts to the all-null skeleton above.

### `replay_sweep_progress_tick`

Emitted every ~1 Hz from `ProcessFastForwardingLocked` while a FF sweep is running. The live aggregator pauses during sweep, so this tick is the dashboard's only signal.

```json
{
  "type": "replay_sweep_progress_tick",
  "ts": "2026-05-07T12:34:56.789Z",
  "frame": 48231,
  "frame_end": 124000,
  "samples_so_far": 1287,
  "est_completion_pct": 38.9,
  "est_remaining_ms": 142000,
  "telemetry_play_speed": 16,
  "play_speed_requested": 16
}
```

## Misfire detection

### Trigger

Every successful `replay_jump_next_incident` / `replay_jump_prev_incident` stamps:

- `_lastJumpRequestedAt = DateTime.UtcNow`
- `_lastJumpDirection = "next" | "prev"`
- `_lastJumpExpectedFrame = row.ReplayFrame`
- `_lastJumpExpectedSessionTimeMs = row.SessionTimeMs`
- `_lastJumpExpectedFingerprint = row.Fingerprint`
- `_lastJumpEvaluated = false`

### Evaluation

In `DataUpdate()`, once `now - _lastJumpRequestedAt > 750ms` AND `!IsSeekThrottled()`:

1. `landedFrame = SafeGetInt("ReplayFrameNum")`
2. `landedSessionTimeMs = ReplayIncidentIndexDetection.ToSessionTimeMs(SessionTime)`
3. Find the row in `_replayIncidentIndex.Incidents` with smallest `|SessionTimeMs - landedSessionTimeMs|`.
4. **Misfire** if no row exists within ±500 ms tolerance OR matched row's `Fingerprint != _lastJumpExpectedFingerprint`.
5. Set `_lastJumpMisfire = misfire`, `_lastJumpLandedFrame = landedFrame`, `_lastJumpEvaluated = true`.
6. Emit log + Sentry breadcrumb.
7. Auto-clear `misfire.active` after 2 s so the WS tick stops including the field.

### Log event

Name: `replay_jump_misfire`. Level: WARN if misfire, DEBUG otherwise. Domain: `action`.

Required fields (in addition to `MergeSessionAndRoutingFields()`):

- `direction`
- `expected_frame`
- `expected_session_time_ms`
- `expected_fingerprint`
- `landed_frame`
- `landed_session_time_ms`
- `delta_ms`
- `nearest_fingerprint`
- `nearest_session_time_ms`
- `misfire` (bool)
- `index_path`

### Sentry breadcrumb

```csharp
SentrySdk.AddBreadcrumb(
    "replay_jump_misfire",
    "action",
    level: misfire ? BreadcrumbLevel.Warning : BreadcrumbLevel.Info,
    data: new Dictionary<string, string> { ["direction"] = direction, ["delta_ms"] = deltaMs.ToString() });
```

## Replay loader API (PowerShell — `scripts/test-rig/load-replay.ps1`)

### Functions

```
LoadReplay -SubSessionId <int>
LoadReplayByPath -Path <string>
```

### `LoadReplay -SubSessionId N`

1. Glob `C:\Users\winth\OneDrive\Documents\iRacing\replay\*.rpy`.
2. Regex match each filename: `subses(\d{7,9})(?:\D|$)` (case-insensitive). The trailing `(?:\D|$)` rejects 10+ digit runs and matches cleanly before `.`/`_`/`-`/end-of-string. **Do not use `\b`** — `\b` does not fire between a digit and `_` because `_` is a word-char.
3. Prefer exact `subses{id}.rpy`; fall back to embedded match.
4. If 0 matches → `replay_load_failed` with `error: "not_found_locally"`.
5. `Start-Process <path>` — Windows file association handles Steam handoff + iRacing UI launch.
6. Poll IRSDK shared-memory handle (`Local\IRSDKMemMapFileName`) until ready (timeout 90 s → `error: "irsdk_timeout"`).
7. Read `SessionInfoYaml.WeekendInfo.SubSessionID` from IRSDK.
8. Compare with requested `SubSessionId`:
   - Match → `replay_load_success` (INFO).
   - Mismatch → `replay_load_mismatch` (WARN). Caller decides whether to abort.

### `LoadReplayByPath -Path P`

Same flow but skips steps 1-4. Logs `actual_sub_session_id` discovered post-load with no equality check.

### Log events (via `node scripts/hooks/loki-log.js`)

| Event | Level | Fields |
|---|---|---|
| `replay_load_started` | INFO | `requested_sub_session_id`, `file_path`, `mode` (`by_id`\|`by_path`) |
| `replay_load_success` | INFO | `requested_sub_session_id`, `actual_sub_session_id`, `file_path`, `ms_to_irsdk_ready` |
| `replay_load_mismatch` | WARN | `requested_sub_session_id`, `actual_sub_session_id`, `file_path`, `ms_to_irsdk_ready` |
| `replay_load_failed` | ERROR | `requested_sub_session_id`, `file_path`, `error`, `phase` |

All carry `app="sim-steward"`, `env=$env:SIMSTEWARD_LOG_ENV`, domain `system`.

## Plugin DTO records (`src/SimSteward.Plugin/PluginState.cs`)

All sealed, JSON-serialized via the existing serializer. Field names are `snake_case` per the existing dashboard contract.

- `ReplayStateTickPayload` — root of `replay_state_tick`
- `ReplayStateAggregates` — wraps `Ours` + `Yaml` (each is `ReplayStateAggregateBucket`)
- `ReplayStateAggregateBucket` — `Incidents`, `Points`, `OffTracks`, `CarContacts` (all nullable for in-progress YAML rule)
- `ReplayStateDriverRow` — per-driver line
- `ReplayStateLastJump` — misfire payload (all fields nullable so the all-null skeleton serializes cleanly when inactive)
- `ReplaySweepProgressPayload` — root of `replay_sweep_progress_tick`

## Dashboard element IDs (`src/SimSteward.Dashboard/test-rig.html`)

Every button click emits `dashboard_ui_event` with:

```json
{ "action": "log", "event": "dashboard_ui_event", "domain": "ui",
  "element_id": "tr-<id>", "event_type": "click", "message": "<human label>" }
```

Sort-column clicks and banner-dismiss use `event_type: "ui_interaction"` (UI-only, no plugin action triggered).

| Element ID | Label | Plugin action triggered |
|---|---|---|
| `tr-play-pause` | ⏸ / ▶ | `replay_play` or `replay_pause` |
| `tr-speed-1` | 1× | `replay_set_speed` (`"1"`) |
| `tr-speed-2` | 2× | `replay_set_speed` (`"2"`) |
| `tr-speed-4` | 4× | `replay_set_speed` (`"4"`) |
| `tr-speed-8` | 8× | `replay_set_speed` (`"8"`) |
| `tr-speed-16` | 16× | `replay_set_speed` (`"16"`) |
| `tr-direction-toggle` | ⇄ Reverse | `replay_set_direction` |
| `tr-seek-frame-go` | Go (after frame input) | `replay_seek_frame` |
| `tr-jump-start` | ⏮ Start | `replay_jump` (existing action — `start`) |
| `tr-jump-end` | ⏭ End | `replay_jump` (existing action — `end`) |
| `tr-prev-incident` | ⟵ Prev incident | `replay_jump_prev_incident` |
| `tr-next-incident` | Next incident ⟶ | `replay_jump_next_incident` |
| `tr-table-sort-<col>` | column header | none — UI only |
| `tr-misfire-dismiss` | banner ✕ | none — UI only |

## Constants & file references

- FF sweep speed: `ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed = 16` at `src/SimSteward.Plugin/ReplayIncidentIndexBuild.cs:15` (changes from 32 in this rig).
- Misfire tolerance: 500 ms session-time delta.
- Misfire settle: 750 ms wall-clock after broadcast.
- Misfire visibility: 2 s on the WS tick after evaluation.
- Live tick cadence: 250 ms.
- Sweep progress cadence: ~1 Hz.
- WS port: 19847 (existing).
- Replay folder: `C:\Users\winth\OneDrive\Documents\iRacing\replay\`.
- iRacing Steam app id: 266410. Launch URL: `steam://rungameid/266410`.
- IRSDK shared-memory handle name: `Local\IRSDKMemMapFileName`.
