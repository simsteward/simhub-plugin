# Spec: Replay Control

**FR-IDs:** FR-004
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-004-Replay-Control.md`

---

## Overview

Replay jump is the bridge between detecting an incident and clipping it. Without this, the user is back to manually scrubbing iRacing's replay to find the moment -- the exact problem Sim Steward exists to solve.

When the user selects an incident (from the overlay FR-003 or incident log FR-009) and triggers "Jump to Replay," the plugin sends a broadcast command to iRacing that positions the replay at a configurable number of seconds *before* the incident. The user lands in replay ready to review and record.

Aligns with PRD Section 4 (FR-004) and Section 6 (Technical Architecture -- "Replay Control (iRacing SDK)" box).

---

## Detailed Requirements

### R-RPL-01: JumpToReplay Method (FR-004)

Expose a method that any caller (overlay button, incident log action, future automation) can invoke to jump iRacing's replay to a specific incident.

**Signature:**

```csharp
void JumpToReplay(int sessionNum, double sessionTimeSeconds, int offsetSeconds)
```

| Parameter | Source | Description |
|-----------|--------|-------------|
| `sessionNum` | `IncidentRecord.SessionNum` (R-INC-03) | iRacing session number (race, qualifying, etc.) |
| `sessionTimeSeconds` | `IncidentRecord.SessionTime` (R-INC-03) | Incident timestamp in seconds from session start |
| `offsetSeconds` | `SimStewardSettings.ReplayOffsetSeconds` (R-SET-01) | How many seconds before the incident to land |

The caller reads `offsetSeconds` from settings (FR-008) and passes it in. The method does not read settings directly -- this keeps it testable and decoupled.

### R-RPL-02: Target Timestamp Calculation (FR-004)

Compute the replay target in milliseconds, clamped to zero:

```
targetMs = Max(0, (int)((sessionTimeSeconds - offsetSeconds) * 1000))
```

**Edge cases:**

| Scenario | sessionTimeSeconds | offsetSeconds | targetMs | Behavior |
|----------|-------------------|---------------|----------|----------|
| Normal | 120.5 | 5 | 115500 | Lands 5s before incident |
| Early incident | 3.0 | 5 | 0 | Clamped to session start |
| Zero offset | 120.5 | 0 | 120500 | Lands exactly at incident |
| Max offset | 120.5 | 30 | 90500 | Lands 30s before incident |
| Very early + max offset | 10.0 | 30 | 0 | Clamped to session start |

The clamp to `>= 0` prevents sending a negative millisecond value to iRacing, which has undefined behavior.

### R-RPL-03: Broadcast to iRacing (FR-004)

Send the replay jump command via iRacing SDK broadcast:

```csharp
sdk.BroadcastMessage(
    irsdk_BroadcastReplaySearchSessionTime,  // enum value 12
    sessionNum,                               // var1: session number
    targetMs                                  // var2: target time in milliseconds
);
```

**Key details:**

- `irsdk_BroadcastReplaySearchSessionTime` is enum value 12 in the iRacing SDK broadcast message enum.
- `var1` is `sessionNum` (int). `var2` is `targetMs` (32-bit int, milliseconds).
- The NickThissen wrapper does not expose this broadcast directly. Use the lower-level `sdk.BroadcastMessage()` on the `iRacingSDK` instance. See `docs/tech/sdk-investigation.md` § "Replay Broadcast" for confirmed API details.

### R-RPL-04: Separate iRacingSDK Instance (FR-004)

Instantiate a dedicated lightweight `iRSDKSharp.iRacingSDK` instance for broadcast commands, separate from SimHub's data connection.

- **Why separate:** SimHub maintains its own iRacing SDK connection for telemetry data. Broadcast commands use Windows `SendMessage`/`PostMessage` under the hood and do not conflict with SimHub's data connection -- but the plugin should not touch SimHub's internal SDK instance.
- **Lifecycle:** Create in `Init`, dispose in `End`. The instance needs no `Startup()` or data-read loop -- it's used only for `BroadcastMessage` calls.
- **Thread safety:** `BroadcastMessage` calls may come from UI threads (overlay button click) or SimHub action threads (hotkey). Windows `SendMessage` is thread-safe, but the SDK instance creation/disposal should be guarded.
- See `docs/tech/sdk-investigation.md` § "Replay Broadcast" for implementation details.

### R-RPL-05: Works for Any Incident (FR-004)

The `JumpToReplay` method accepts `sessionNum` and `sessionTimeSeconds` as parameters -- it is not hardcoded to the most recent incident. Any `IncidentRecord` from the in-memory incident list (R-INC-06) can be used as input:

- Latest incident (from overlay quick-action)
- Any historical incident (from incident log selection, FR-009)
- Manual marks (FR-002) with `Source = Manual`

The caller is responsible for selecting which `IncidentRecord` to jump to and extracting the fields.

### R-RPL-06: Works in Replay Mode (FR-004)

The broadcast command works while the user is viewing iRacing's replay screen. iRacing accepts `BroadcastReplaySearchSessionTime` regardless of whether the user is in live racing or replay mode -- the replay position simply updates.

No precondition check for `IsReplayPlaying` is required before sending the broadcast. The command is valid in both states.

### R-RPL-07: Offset from Settings (FR-004, FR-008)

The replay offset is configured via FR-008's `ReplayOffsetSeconds` setting (R-SET-01, R-SET-04):

| Property | Value |
|----------|-------|
| Default | 5 seconds |
| Range | 0–30 seconds (inclusive) |
| Persistence | SimHub settings API (R-SET-06) |
| Effect | Immediate -- no restart needed (R-SET-07) |

Callers read `plugin.Settings.ReplayOffsetSeconds` at invocation time to get the current value.

### R-RPL-08: Graceful Fallback (FR-004)

If the broadcast fails (exception thrown by `BroadcastMessage`), the plugin does not crash. Instead:

1. **Catch** the exception in `JumpToReplay`.
2. **Log** the error via SimHub's logging (`SimHub.Logging.Current.Info` or similar).
3. **Surface a fallback message** to the UI: `"Could not jump to replay. Go to replay at {formattedTime}"` where `formattedTime` is the incident's `SessionTime` formatted as `MM:SS` (e.g., "2:00" for 120.5 seconds).
4. The fallback message is delivered to callers via a return value or callback (implementation detail -- not prescribed here) so the overlay or incident log can display it.

### R-RPL-09: Error Handling -- iRacing Not Running (FR-004)

If iRacing is not running when `JumpToReplay` is called:

- The `BroadcastMessage` call will fail silently (Windows `SendMessage` to a non-existent window handle returns without error) or throw depending on the SDK instance state.
- The plugin treats this the same as R-RPL-08: catch any exception, log it, surface the fallback message.
- No precondition check for iRacing connectivity is required. The broadcast-and-catch approach is simpler and handles all failure modes uniformly.

---

## Technical Design Notes

### Broadcast SDK Instance

```
plugin/Replay/
├── ReplayController.cs    # JumpToReplay method, SDK instance lifecycle
```

The `ReplayController` owns the lightweight `iRacingSDK` instance. Pseudo-structure:

```
class ReplayController
  sdk: iRacingSDK              // created in Init, disposed in End
  
  JumpToReplay(sessionNum, sessionTimeSeconds, offsetSeconds):
    targetMs = Max(0, (int)((sessionTimeSeconds - offsetSeconds) * 1000))
    try:
      sdk.BroadcastMessage(ReplaySearchSessionTime, sessionNum, targetMs)
    catch:
      log error
      return fallback message
```

### Why Not SimHub's SDK Connection?

SimHub uses its own internal iRacing SDK instance for telemetry data reads. The plugin accesses telemetry through SimHub's `GameRawData` abstraction, not directly through an SDK instance. For *broadcast commands*, we need direct access to `BroadcastMessage()`, which SimHub does not expose. A separate lightweight instance solves this cleanly. See `docs/tech/sdk-investigation.md` for confirmation that the two instances coexist without conflict.

### Time Formatting for Fallback

The fallback message formats `SessionTime` (a double in seconds) as `M:SS` for readability:

- `120.5` → `"2:00"` (truncate sub-second)
- `3.7` → `"0:03"`
- `3661.0` → `"61:01"`

No hours component -- iRacing sessions rarely exceed 60 minutes for the target persona, and `M:SS` stays simple.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-001-002 Incident Detection** | Provides `IncidentRecord` with `SessionNum` and `SessionTime` fields (R-INC-03). Without incident records, there is nothing to jump to. |
| **FR-008 Plugin Settings** | Provides `ReplayOffsetSeconds` (R-SET-01, R-SET-04). Without this, the offset must be hardcoded. |
| **SCAFFOLD-Plugin-Foundation** | Plugin lifecycle (`Init`, `End`) for SDK instance management. |
| **iRSDKSharp.dll** | Ships with SimHub. Provides `iRacingSDK` class and `BroadcastMessage` method. No additional NuGet package needed. |
| **iRacing must be running** | Broadcast targets iRacing's window via Windows messaging. If iRacing isn't running, the command fails gracefully (R-RPL-08, R-RPL-09). |
| **No dependency on replay mode** | The broadcast works whether the user is in live or replay mode (R-RPL-06). |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Plugin sends `irsdk_BroadcastReplaySearchSessionTime` with correct session number and timestamp | R-RPL-03 |
| Replay jumps to `incidentTime - offsetSeconds` | R-RPL-01, R-RPL-02 |
| Offset clamped to 0 if result would go negative | R-RPL-02 (edge cases table) |
| Works for any incident in the incident log | R-RPL-05 |
| Graceful fallback if broadcast fails | R-RPL-08 |
| Replay jump works while in iRacing replay screen | R-RPL-06 |

---

## Open Questions

| # | Question | Status | Mitigation |
|---|----------|--------|------------|
| 1 | **Replay timing precision** (PRD Constraint #4) -- does `BroadcastReplaySearchSessionTime` land exactly at the requested millisecond, or is there drift? | Unknown until tested in-sim | Configurable offset (R-RPL-07, 0–30s range) lets users compensate. If drift is consistent, a future calibration setting could adjust automatically. |
