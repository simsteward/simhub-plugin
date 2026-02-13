# Spec: Incident Detection

**FR-IDs:** FR-001, FR-002
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-001-002-Incident-Detection.md`

---

## Overview

Incident detection is the entry point for the entire Sim Steward workflow. Nothing downstream (overlay, replay jump, OBS recording) can function without knowing *when* an incident happened.

This spec covers two detection sources:

1. **Auto-detection (FR-001):** Monitor `PlayerCarTeamIncidentCount` on every `DataUpdate` tick. When the count increases, create an incident record with the session timestamp.
2. **Manual mark (FR-002):** A SimHub hotkey lets the driver mark "incident happened now" -- capturing the current `SessionTime` regardless of whether iRacing registered an incident count change.

Both sources produce the same `IncidentRecord` data model, publish the same `OnIncidentDetected` event, and feed into the same in-memory incident list that the overlay (FR-003), incident log (FR-009), and replay jump (FR-004) consume.

---

## Detailed Requirements

### R-INC-01: Auto-Detection via Incident Count Delta (FR-001)

On each `DataUpdate` tick, compare the current `PlayerCarTeamIncidentCount` to the previously stored value.

- **Trigger:** `currentCount - previousCount > 0`.
- **Delta:** The difference (`currentCount - previousCount`) is stored on the `IncidentRecord` as the incident severity (1, 2, 4, etc.).
- **Timestamp:** Capture `SessionTime` at the tick where the delta is detected. This is the incident's session timestamp.
- **Session number:** Capture `SessionNum` at the same tick. Stored on the record for replay jump targeting.
- **First tick of session:** On the very first `DataUpdate` tick where `PlayerCarTeamIncidentCount` is non-null, store the value as the baseline. Do **not** fire an incident -- there is no previous value to delta against.
- **Update previous:** After processing, set `previousCount = currentCount`.

**Edge case -- null telemetry:** If `PlayerCarTeamIncidentCount` reads as `null` (iRacing not connected, wrong game), skip detection entirely. Do not reset `previousCount` to zero -- wait for a valid read to resume.

### R-INC-02: Manual Incident Mark via Hotkey (FR-002)

Register a SimHub input action so the driver can mark an incident manually.

- **Registration:** Call `pluginManager.AddAction("SimSteward.MarkIncident", (a, b) => { ... })` in `Init`.
- **On trigger:** Create an `IncidentRecord` with `Source = Manual`, using the current `SessionTime` and `SessionNum` at the moment the action fires.
- **Delta:** Set to `0` for manual marks (no telemetry-derived severity).
- **Availability:** The action only creates a record when `IsIRacingConnected` is `true` and `SessionTime` is non-null. Otherwise, ignore silently.
- **Default binding:** None. The user binds the action to a key/button in SimHub's Controls settings. FR-008 documents the recommended default.

### R-INC-03: IncidentRecord Data Model (FR-001, FR-002)

Both detection sources produce the same record type. Other specs (FR-003 overlay, FR-004 replay jump, FR-009 incident log) reference this model.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Unique identifier. Generate via `Guid.NewGuid().ToString("N")`. |
| `SessionTime` | `double` | iRacing `SessionTime` (seconds from session start) at detection. |
| `SessionNum` | `int` | iRacing `SessionNum` at detection. Used by replay jump (FR-004) to target the correct session. |
| `Delta` | `int` | Incident count increase. `0` for manual marks; `1`, `2`, `4`, etc. for auto-detected. |
| `Source` | `enum { Auto, Manual }` | How the incident was detected. |
| `DetectedAt` | `DateTime` | Wall-clock time (`DateTime.UtcNow`) when the record was created. For display/logging, not for replay targeting. |

**Notes:**
- `Id` is generated at creation time and is stable for the record's lifetime.
- `SessionTime` is the replay-relevant timestamp. `DetectedAt` is supplementary.
- The model is a plain C# class (POCO). No persistence -- records exist only in memory for the current session.

### R-INC-04: Debounce / Merge Logic (FR-001)

When iRacing reports two separate `PlayerCarTeamIncidentCount` increases within a short window, merge them into one incident record rather than creating duplicates.

- **Window:** 5 seconds (configurable constant, not user-facing in Part 1).
- **Behavior:** When auto-detection (R-INC-01) fires and the most recent `IncidentRecord` in the list has `Source = Auto` and its `SessionTime` is within the merge window of the new detection's `SessionTime`:
  - **Update** the existing record: add the new delta to the existing `Delta`. Do not change `SessionTime` (keep the original timestamp -- we want to replay-jump to the *start* of the incident chain).
  - **Re-publish** `OnIncidentDetected` with the updated record so subscribers see the revised severity.
- **Manual marks are never merged.** A manual mark always creates a new record, even if it falls within the window of a recent auto-detected incident. Rationale: the driver pressed the button deliberately.
- **Manual-to-auto:** An auto-detection that fires within the window of a recent *manual* mark also creates a new record (no merge). Merging only applies to consecutive auto-detections.

### R-INC-05: OnIncidentDetected Event (FR-001, FR-002)

Publish an event whenever a new incident record is created (or an existing one is updated via merge).

- **Signature:** `event Action<IncidentRecord> OnIncidentDetected`
- **Raised on:** The same thread as `DataUpdate` (for auto-detection) or the SimHub action callback thread (for manual mark).
- **Subscribers:** Overlay (FR-003), incident log (FR-009), and any future consumer. Subscribers must not block -- they should copy the data and return.
- **Merge case:** When R-INC-04 updates an existing record, fire the event with the updated record. Subscribers can use `Id` to identify updates vs. new incidents.

### R-INC-06: In-Memory Incident List (FR-001, FR-002)

Maintain a `List<IncidentRecord>` (or similar thread-safe collection) holding all incidents for the current session.

- **Add:** Append on new incident. On merge (R-INC-04), update in-place.
- **Read:** The overlay and incident log read from this list. Provide a method or property that returns a read-only snapshot (e.g., `IReadOnlyList<IncidentRecord>`).
- **Clear:** Clear the list when session changes (R-INC-07). After clearing, reset `previousCount` to `null` so the next valid telemetry read establishes a new baseline.
- **No persistence:** The list lives in memory only. It does not survive SimHub restart.

### R-INC-07: Session Lifecycle (FR-001)

Reset detection state when the session changes.

- **SessionNum change:** If `SessionNum` differs from the previously stored value (and is non-null), clear the incident list and reset `previousCount`. This handles transitions between practice, qualifying, and race sessions.
- **iRacing disconnect:** When `IsIRacingConnected` transitions from `true` to `false` (detected via SCAFFOLD's connection tracking, R-SCAF-06), clear the incident list and reset `previousCount`.
- **iRacing reconnect:** When `IsIRacingConnected` transitions back to `true`, the first `DataUpdate` tick with valid telemetry establishes a new baseline per R-INC-01.
- **Do not clear on replay mode.** Entering/exiting replay (`IsReplayPlaying` changes) does not affect the incident list. Incidents are session-scoped, not mode-scoped.

### R-INC-08: DataUpdate Rate Independence (FR-001)

Detection must work correctly at both 10 Hz (free SimHub) and 60 Hz (licensed SimHub).

- The delta check (R-INC-01) is evaluated every tick regardless of rate. At higher rates, the same `PlayerCarTeamIncidentCount` increase is seen on the first tick that reflects the change -- subsequent ticks see zero delta.
- The 5-second merge window (R-INC-04) uses `SessionTime` differences, not tick counts. This makes it rate-independent.
- No timers, no tick-counting, no assumptions about update frequency.

---

## Technical Design Notes

### PlayerCarTeamIncidentCount Tracking

The scaffold (R-SCAF-05) already reads `PlayerCarTeamIncidentCount` on every tick. Incident detection adds delta logic on top:

```
previousCount: int? = null

on DataUpdate:
  count = read PlayerCarTeamIncidentCount  // may be null
  if count is null → skip
  if previousCount is null → previousCount = count; return   // baseline
  delta = count - previousCount
  if delta > 0 → create/merge IncidentRecord, publish event
  previousCount = count
```

### 0x Incident Handling

iRacing labels some light-contact incidents as "0x" severity, but these still increment `PlayerCarTeamIncidentCount` by at least 1. The `delta > 0` check captures all incidents, including 0x-labeled ones. The "0x" is an iRacing UI label, not a zero-valued telemetry delta.

### SimHub Hotkey Registration

SimHub's `AddAction` API registers a named action that users bind to keys/buttons in SimHub's Controls settings.

```csharp
pluginManager.AddAction("SimSteward.MarkIncident", (a, b) =>
{
    // Create manual IncidentRecord using current SessionTime/SessionNum
});
```

The action is registered once in `Init` and remains available for the plugin's lifetime.

### In-Memory Storage

`List<IncidentRecord>` is the simplest structure. Thread safety considerations:

- Auto-detection writes happen on the `DataUpdate` thread.
- Manual marks fire on SimHub's action callback thread (potentially different).
- Reads happen from overlay/UI threads.

Use a `lock` around list mutations and reads, or use `ConcurrentBag<T>` / copy-on-write pattern. A `lock` is simplest given the low frequency of writes (incidents are rare events).

### Recommended File Placement

```
plugin/
├── Detection/
│   ├── IncidentRecord.cs       # Data model (R-INC-03)
│   ├── IncidentDetector.cs     # Delta tracking, merge logic, event (R-INC-01, 04, 05, 06, 07)
│   └── IncidentSource.cs       # Enum: Auto, Manual (R-INC-03)
```

`SimStewardPlugin.cs` calls `IncidentDetector.ProcessTick(...)` from `DataUpdate` and registers the manual mark action in `Init`.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **SCAFFOLD-Plugin-Foundation** | Must be implemented first. Provides `DataUpdate` loop, telemetry reads (R-SCAF-05), connection tracking (R-SCAF-06), and `PluginManager` reference. |
| **iRacing consolidation behavior** | iRacing merges rapid-fire incidents into the highest severity (e.g., spin 2x + wall 4x = single 4x). Most chains appear as one delta. The plugin's merge window handles the edge case of two distinct deltas arriving close together. See `docs/tech/sdk-investigation.md` § "iRacing Incident Consolidation". |
| **PlayerCarTeamIncidentCount scope** | Captures all team incidents on team cars. Acceptable for protest clipping -- the driver needs to know *something* happened, even if caused by a teammate. |
| **SimHub AddAction API** | Used for hotkey registration. No known limitations for this use case. |
| **No external dependencies** | This feature uses only SimHub SDK APIs and iRacing telemetry. No NuGet packages, no external processes. |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Auto-detection fires on `PlayerCarTeamIncidentCount` delta | R-INC-01 |
| Incident records: session timestamp, session number, delta, source | R-INC-03 |
| Manual mark hotkey triggers record with current `SessionTime` | R-INC-02 |
| Hotkey is configurable (SimHub action binding) | R-INC-02 |
| Incidents within 5s merged, not duplicated | R-INC-04 |
| Events published for other components | R-INC-05 |
| Works at any DataUpdate rate (10 Hz / 60 Hz) | R-INC-08 |

---

## Open Questions

None. The detection mechanism (`PlayerCarTeamIncidentCount` delta), hotkey API (`AddAction`), and iRacing consolidation behavior are all confirmed in the SDK investigation.
