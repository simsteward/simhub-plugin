# Spec: OBS Recording & Clip Management

**FR-IDs:** FR-006, FR-007
**Priority:** FR-006 Must, FR-007 Should
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-005-006-007-OBS-Integration.md`

---

## Overview

This spec covers the "record and manage" layer that sits on top of the OBS connection (FR-005). Once the plugin has a live WebSocket connection to OBS, two things need to happen:

1. **Recording control (FR-006):** The user starts and stops OBS recording via plugin UI or hotkey. The plugin tracks recording state so the overlay and other features know what's happening.
2. **Clip management (FR-007):** After recording stops, OBS returns the saved file path. The plugin surfaces this path and lets the user confirm save or discard (delete) the clip.

Together, these turn "connected to OBS" into "I have a clip file on disk." Downstream features (incident log FR-009, future multi-camera FR-012) build on this output.

**Out of scope:** OBS connection lifecycle (FR-005), OBS output settings (format, path, filename pattern — controlled by the user in OBS), automated recording triggers (future: detect → record is assembled later).

---

## Detailed Requirements

### FR-006: Start/Stop Recording

#### R-REC-01: Start Recording Command

The user triggers a "Start Recording" action. The plugin sends a `StartRecord` request to OBS via the WebSocket connection (FR-005).

- **Trigger sources:** SimHub hotkey action `SimSteward.ToggleRecording` (registered in FR-008, R-SET-05) or a UI button in the replay overlay (FR-003, future wiring).
- **Toggle behavior:** `ToggleRecording` is a single action that starts *or* stops depending on current state. If `RecordingState == Idle`, it starts. If `RecordingState == Recording`, it stops. If `RecordingState == Stopping`, it is ignored (stop already in flight).
- **Precondition:** OBS must be connected (`ObsConnectionState == Connected` per FR-005). If not connected, the command is ignored and a warning is logged. No error dialog — the overlay's connection status indicator (FR-005) already tells the user OBS is disconnected.
- **Request:** Send obs-websocket `StartRecord` (OpCode 6). No request parameters needed.
- **Success:** OBS responds with a successful RequestResponse (OpCode 7, `requestStatus.result == true`). Transition `RecordingState` to `Recording` (R-REC-03).
- **Failure:** OBS responds with an error. Log the error. Remain in `Idle` state. Publish `OnRecordingStateChanged` with `Idle` so the UI reflects the failure.

#### R-REC-02: Stop Recording Command

The user triggers a "Stop Recording" action. The plugin sends a `StopRecord` request to OBS.

- **Trigger sources:** Same as R-REC-01 (toggle action or UI button).
- **Precondition:** `RecordingState == Recording`. If not, the command is ignored.
- **Request:** Send obs-websocket `StopRecord` (OpCode 6). No request parameters needed.
- **Transition:** Immediately move `RecordingState` to `Stopping` (R-REC-03). This prevents duplicate stop requests if the user presses the hotkey again while OBS is finalizing the file.
- **Success:** OBS responds with `requestStatus.result == true`. The response payload includes `outputPath` (string) — the absolute path to the saved recording file. Transition to `Idle`. Pass `outputPath` to clip management (R-CLIP-01).
- **Failure:** OBS responds with an error. Log the error. Transition back to `Idle`. No clip path available — skip clip management for this attempt.

#### R-REC-03: Recording State Machine

The plugin maintains a `RecordingState` enum that governs all recording behavior.

```
┌───────┐  StartRecord sent   ┌───────────┐  StopRecord sent   ┌──────────┐
│  Idle │ ─────────────────► │ Recording │ ─────────────────► │ Stopping │
└───────┘                     └───────────┘                     └──────────┘
    ▲                              │                                │
    │         StartRecord fails    │    StopRecord succeeds/fails   │
    │◄─────────────────────────────┘◄───────────────────────────────┘
```

| State | Meaning | Allowed Transitions |
|-------|---------|---------------------|
| `Idle` | Not recording. Ready to start. | → `Recording` (on StartRecord success) |
| `Recording` | OBS is actively recording. | → `Stopping` (on StopRecord sent) |
| `Stopping` | StopRecord sent, waiting for OBS response. | → `Idle` (on StopRecord response, success or failure) |

**Invalid transitions are silently ignored.** For example, calling StartRecord while in `Recording` or `Stopping` does nothing. This is the toggle action's natural behavior — pressing the hotkey in `Stopping` is a no-op, not an error.

#### R-REC-04: RecordingState Changed Event

Publish an event whenever `RecordingState` transitions.

- **Signature:** `event Action<RecordingState> OnRecordingStateChanged`
- **Raised on:** The thread that processes the OBS WebSocket response (FR-005's message handler).
- **Subscribers:** Overlay (FR-003 — to show recording indicator), clip management (R-CLIP-01 — to handle the stopped-with-path case).

#### R-REC-05: RecordingState as Plugin Property

Expose `RecordingState` as a SimHub plugin property so Dash Studio overlays can bind to it.

- **Property name:** `SimSteward.RecordingState`
- **Type:** `string` — one of `"Idle"`, `"Recording"`, `"Stopping"`
- **Updated:** On every state transition (same time as R-REC-04 event).

**Derived overlay properties** (updated alongside `RecordingState` for convenient Dash Studio binding):
- `SimSteward.OBS.IsRecording` (bool) — `true` when `RecordingState == Recording`, `false` otherwise. Used by the overlay (FR-003a R-OVR-05) for status indicator color.
- `SimSteward.OBS.RecordButtonText` (string) — `"Start Recording"` when `Idle`, `"Stop Recording"` when `Recording`, `"Stopping..."` when `Stopping`. Used by the overlay (FR-003a R-OVR-06) for the record button label.

This allows overlay designers to show/hide recording indicators, change colors, etc., without custom code.

#### R-REC-06: OBS Already Recording on Connect

When the plugin connects to OBS (FR-005), OBS may already be recording (user started recording manually in OBS before the plugin connected).

- **On successful connection:** Send a `GetRecordStatus` request.
- **If `outputActive == true`:** Set `RecordingState` to `Recording`. The user can then stop via the plugin.
- **If `outputActive == false`:** Set `RecordingState` to `Idle`.

This sync ensures the plugin's state machine matches OBS's actual state from the moment of connection.

#### R-REC-07: OBS Disconnects Mid-Recording

If OBS disconnects (FR-005 raises its disconnection event) while `RecordingState` is `Recording` or `Stopping`:

- Transition `RecordingState` to `Idle`.
- Log a warning: "OBS disconnected while recording. Recording may still be in progress in OBS."
- **Do not assume the recording was lost.** OBS continues recording independently of the WebSocket connection. The file will exist on disk, but the plugin won't have the `outputPath`. The user can find it in OBS's configured output directory.
- No clip management prompt — there's no `outputPath` to work with.

#### R-REC-08: RecordStateChanged Event Subscription

As an alternative to request-response polling, subscribe to OBS `RecordStateChanged` events (Outputs event category, subscription bitmask `64` in the Identify message per FR-005).

- Use `RecordStateChanged` events to confirm state transitions. If the event arrives before the request response (race condition), the event takes precedence for state updates.
- This also catches external state changes (e.g., user stops recording from within OBS's own UI). If a `RecordStateChanged` event reports `OBS_WEBSOCKET_OUTPUT_STOPPED` and the plugin is in `Recording` state, transition to `Idle` and attempt to extract `outputPath` from the event data.

---

### FR-007: Clip Save / Discard

#### R-CLIP-01: Clip Record Data Model

When `StopRecord` succeeds and returns `outputPath`, create a `ClipRecord`:

| Field | Type | Description |
|-------|------|-------------|
| `OutputPath` | `string` | Absolute file path returned by OBS `StopRecord` response. |
| `CreatedAt` | `DateTime` | `DateTime.UtcNow` when the StopRecord response was processed. |
| `Status` | `enum { Pending, Saved, Discarded }` | Lifecycle state of the clip. Starts as `Pending`. |

The `ClipRecord` is a transient in-memory object. It does not survive SimHub restart. It exists only to track the clip from "OBS stopped" to "user decided to save or discard."

#### R-CLIP-02: Surface Clip Path in UI

After a `ClipRecord` is created (R-CLIP-01), expose it for the overlay and settings UI:

- **Plugin property:** `SimSteward.LastClipPath` — string, the `OutputPath` of the most recent clip. Empty string when no clip is pending.
- **Plugin property:** `SimSteward.LastClipStatus` — string, one of `"Pending"`, `"Saved"`, `"Discarded"`, or `""` (empty when no clip).
- **Event:** `event Action<ClipRecord> OnClipCreated` — raised when a new `ClipRecord` is created. Subscribers (overlay, incident log) use this to show the save/discard prompt.

The overlay (FR-003) will bind to these properties to display the clip path and prompt. The exact UI layout is FR-003's concern.

#### R-CLIP-03: Save Action

The user confirms "Save" (via overlay button or future UI element).

- Set `ClipRecord.Status = Saved`.
- Update `SimSteward.LastClipStatus` property to `"Saved"`.
- No file system action — the file is already on disk where OBS saved it. "Save" simply confirms the user wants to keep it.
- Log: "Clip saved: {outputPath}".

#### R-CLIP-04: Discard Action

The user selects "Discard" to delete the clip file.

- **Confirmation required.** Before deleting, the user must confirm. The overlay or UI shows a confirmation prompt (e.g., "Delete this clip? This cannot be undone."). The confirmation mechanism is the overlay's responsibility (FR-003 spec); this spec defines the behavior after confirmation.
- **On confirm:** Attempt to delete the file at `ClipRecord.OutputPath`.
  - **Success:** Set `ClipRecord.Status = Discarded`. Update `SimSteward.LastClipStatus` to `"Discarded"`. Log: "Clip discarded: {outputPath}".
  - **File not found:** The file was already moved or deleted externally. Treat as successful discard — set status to `Discarded`. Log warning: "Clip file not found (already deleted?): {outputPath}".
  - **File locked / access denied:** The file may be locked by OBS (still finalizing) or another process. Log error: "Could not delete clip: {reason}". Leave status as `Pending` so the user can retry. Do not silently swallow the failure — the user should see that discard didn't work.
- **On cancel:** No action. Status remains `Pending`.

#### R-CLIP-05: Clip Lifecycle Timeout

If the user neither saves nor discards within a reasonable period, the clip stays on disk (safe default). No auto-delete, no auto-archive. The `Pending` status simply remains until the next clip is created, at which point:

- The previous `ClipRecord` is replaced by the new one in the `LastClipPath` / `LastClipStatus` properties.
- The previous clip file is **not deleted** — it stays on disk. Only explicit discard deletes files.

#### R-CLIP-06: Clip History (Minimal)

For Part 1, the plugin tracks only the **most recent** clip (`LastClipPath`, `LastClipStatus`). A full clip history list is not required. The incident log (FR-009) may associate clips with incidents in a future iteration, but that's out of scope here.

---

## Technical Design Notes

### obs-websocket Request-Response Pattern

Recording requests use the standard OpCode 6 (Request) / OpCode 7 (RequestResponse) flow:

```json
// StartRecord request
{
  "op": 6,
  "d": {
    "requestType": "StartRecord",
    "requestId": "unique-id-1"
  }
}

// StartRecord response (success)
{
  "op": 7,
  "d": {
    "requestType": "StartRecord",
    "requestId": "unique-id-1",
    "requestStatus": { "result": true, "code": 100 }
  }
}

// StopRecord response (success, with outputPath)
{
  "op": 7,
  "d": {
    "requestType": "StopRecord",
    "requestId": "unique-id-2",
    "requestStatus": { "result": true, "code": 100 },
    "responseData": {
      "outputPath": "C:\\Users\\Driver\\Videos\\2026-02-13 19-45-22.mkv"
    }
  }
}
```

The `requestId` is a unique string (GUID) generated per request. The plugin matches responses to requests by `requestId`. The OBS connection manager (FR-005) should provide a general-purpose `SendRequest` / await-response mechanism that recording control uses.

### Recording State Machine Implementation

```csharp
public enum RecordingState { Idle, Recording, Stopping }

// State transitions are guarded:
public void StartRecording()
{
    if (_state != RecordingState.Idle) return;
    if (!_obsConnection.IsConnected) return;
    _obsConnection.SendRequest("StartRecord", onResponse: HandleStartResponse);
}

public void StopRecording()
{
    if (_state != RecordingState.Recording) return;
    SetState(RecordingState.Stopping);
    _obsConnection.SendRequest("StopRecord", onResponse: HandleStopResponse);
}
```

State transitions go through a single `SetState` method that updates the enum, publishes the SimHub property, and fires the event. This prevents scattered state mutation.

### File Deletion for Discard

```csharp
try
{
    File.Delete(clipRecord.OutputPath);
    clipRecord.Status = ClipStatus.Discarded;
}
catch (FileNotFoundException)
{
    // Already gone -- treat as discarded
    clipRecord.Status = ClipStatus.Discarded;
}
catch (IOException ex)
{
    // Locked or in use -- log and leave as Pending
    Log.Warn($"Could not delete clip: {ex.Message}");
}
catch (UnauthorizedAccessException ex)
{
    Log.Warn($"Could not delete clip: {ex.Message}");
}
```

### Thread Safety

- Recording state transitions are triggered by OBS WebSocket response callbacks, which fire on a background thread (FR-005's WebSocket receive loop).
- SimHub property updates (`SetPropertyValue`) must be called safely. The SCAFFOLD spec (R-SCAF-05) establishes the pattern for cross-thread property writes.
- The `ClipRecord` is written on the WebSocket callback thread and read from the UI thread. Use a `lock` or make `ClipRecord` immutable (create a new instance per state change rather than mutating).

### Recommended File Placement

```
plugin/
├── Recording/
│   ├── RecordingState.cs         # Enum (R-REC-03)
│   ├── RecordingController.cs    # Start/stop logic, state machine (R-REC-01 through R-REC-08)
│   ├── ClipRecord.cs             # Data model (R-CLIP-01)
│   └── ClipStatus.cs             # Enum: Pending, Saved, Discarded (R-CLIP-01)
```

`SimStewardPlugin.cs` instantiates `RecordingController`, wires the `ToggleRecording` action to it, and exposes its properties as SimHub plugin properties.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-005 OBS Connection** | Recording control requires an established, authenticated WebSocket connection. The connection manager provides `SendRequest`, `IsConnected`, connection/disconnection events, and event subscriptions. |
| **SCAFFOLD-Plugin-Foundation** | Plugin lifecycle, `DataUpdate` loop, SimHub property registration (`AddProperty`, `SetPropertyValue`), `AddAction` for hotkey. |
| **FR-008 Plugin Settings** | `SimSteward.ToggleRecording` action is registered in R-SET-05. OBS URL/password are configured there. |
| **OBS output settings** | The plugin does **not** control recording format, quality, file path, or filename pattern. These are configured by the user in OBS's Settings → Output. The plugin reads `outputPath` from the `StopRecord` response — it doesn't choose the path. |
| **OBS must not be paused** | obs-websocket 5.x has separate `PauseRecord` / `ResumeRecord` requests. The plugin does not support pause/resume in Part 1. If OBS is paused, `StopRecord` still works (OBS finalizes the file). |
| **No external NuGet packages** | File operations use `System.IO.File`. JSON uses `Newtonsoft.Json` (bundled with SimHub). |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| "Start Recording" command starts OBS recording (via button or hotkey) | R-REC-01, R-REC-03 |
| "Stop Recording" command stops OBS recording | R-REC-02, R-REC-03 |
| After recording stops, the saved clip file path is displayed in the overlay/UI | R-CLIP-01, R-CLIP-02 |
| Clip save prompt allows user to confirm save or discard (discard = delete file) | R-CLIP-03, R-CLIP-04 |
| Handle: OBS already recording | R-REC-06 |
| Handle: OBS disconnects mid-recording | R-REC-07 |
| Handle: recording fails to start | R-REC-01 (failure case) |
| Handle: file not found on discard | R-CLIP-04 (FileNotFound case) |
| Handle: file locked on discard | R-CLIP-04 (IOException case) |
| Handle: discard confirmation | R-CLIP-04 (confirmation required) |

---

## Open Questions

None. The obs-websocket `StartRecord` / `StopRecord` request-response pattern and `outputPath` in the stop response are confirmed in the spike plan (`docs/tech/plans/obs-websocket-spike.md`). File deletion semantics are standard .NET `System.IO.File.Delete`. The recording state machine is straightforward and fully defined above.
