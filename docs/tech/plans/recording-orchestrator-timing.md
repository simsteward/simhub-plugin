# Tech Plan: Recording Orchestrator Timing

**Related FR-IDs:** FR-012, FR-014, FR-015  
**Related Stories:** `docs/product/stories/FR-012-014-015-Multi-Camera-Clipping.md`  
**Last updated:** 2026-02-13

---

## Purpose

The multi-camera recording loop (FR-012) must coordinate: replay jump, camera switch, OBS start/stop, and clip duration. This plan covers how to implement the orchestrator in .NET 4.8 within SimHub's execution model — without blocking `DataUpdate` — and how to control replay playback duration so each clip is exactly the configured length.

---

## Orchestrator State Machine

The `MultiCameraRecorder` (or equivalent) runs a sequential loop over the user-selected cameras. High-level states:

1. **Idle** — Not running.
2. **Running** — For each camera: JumpToReplay → SwitchCamera → StartRecord → Wait(duration) → StopRecord → (next camera or Done).
3. **Cancelling** — User requested cancel; finish current recording and stop, keep completed clips.

**Transition triggers:** Start (user action), Cancel (user action), Step complete (internal), Error (internal).

---

## Async and SimHub DataUpdate

- **Constraint:** `DataUpdate` must not block. The orchestrator cannot `Thread.Sleep(duration)` or block on OBS/iRacing calls inside `DataUpdate`.
- **Approach:** Run the loop on a background thread or use a state machine driven by timers/callbacks:
  - **Option A:** Dedicated `Task` or `Thread` that runs the sequence with `Thread.Sleep` or delay between steps. Progress and state exposed via shared fields/properties; `DataUpdate` only reads and publishes to SimHub properties.
  - **Option B:** Timer-driven state machine. A single timer (e.g., 500 ms) fires; each tick advances the orchestrator state (e.g., "waiting for clip end" decrements remaining time, then transitions to StopRecord when zero). OBS and replay commands are still fire-and-forget or callback-based.
- **Recommendation:** Option A is simpler to reason about. Ensure OBS StartRecord/StopRecord and replay Jump/CameraSwitch are invoked from the same thread that runs the loop (or marshal to the connection manager's thread) to avoid re-entrancy.

---

## Replay Playback Duration Control

**Goal:** Record for exactly `clipDurationSeconds` (e.g., 5s before + 10s after = 15s total, or configurable).

- **Option 1 — Timer only:** After StartRecord, start a timer for `clipDurationSeconds`. When the timer fires, call StopRecord. Does not require reading `SessionTime` during replay. Assumes iRacing replay plays at 1x and is already at the correct start time (from JumpToReplay). Risk: if replay is paused or slowed, clip will be too long.
- **Option 2 — SessionTime monitoring:** In `DataUpdate`, compare current `SessionTime` (during replay) to the computed clip end time. When `SessionTime >= clipEndTime`, call StopRecord. Requires reliable `SessionTime` during replay (open question in FR-004/FR-003a). More accurate if replay speed varies.
- **Recommendation:** Start with Option 1 (timer). Document Option 2 as a future improvement if users report clip length issues. Clip duration settings (FR-014) define the timer length.

---

## Command Timing and Delays

OBS and iRacing need time to process commands:

| Step | Suggested delay after | Rationale |
|------|------------------------|-----------|
| After JumpToReplay | 500–1000 ms | Replay seek and stabilize |
| After SwitchCamera | 300–500 ms | Camera switch render |
| After StartRecord | 500–800 ms | OBS starts writing; avoid missing frames |
| After StopRecord | 300–500 ms | OBS finalizes file; next StartRecord should not overlap |

Make delays configurable (e.g., in a Part 2 settings section or constants) so they can be tuned without code changes.

---

## Cancellation

- **User cancel:** Hotkey or overlay button sets a "cancel requested" flag. The orchestrator checks this between cameras (and optionally after StopRecord within a camera). When set: stop starting new cameras, allow current recording to finish (call StopRecord at next opportunity), then transition to Idle. Completed clips are kept; current clip is saved.
- **Thread safety:** The cancel flag must be visible to the orchestrator thread (e.g., `volatile` or lock-protected). No need to interrupt mid-recording; finishing the current clip is acceptable.

---

## Error Handling

- **One camera fails (e.g., StartRecord fails):** Log, skip that camera, continue with the next. Per story AC: "if one angle fails, continue with remaining angles."
- **OBS disconnects mid-loop:** Treat as fatal for the loop: transition to Idle, log, surface message. Already-recorded clips remain.
- **iRacing replay exits:** Same as above; cannot continue without replay.

---

## SimHub Properties for Progress (FR-015)

Expose for the overlay:

- `SimSteward.MultiCam.IsActive` (bool) — orchestrator is running.
- `SimSteward.MultiCam.CurrentAngleIndex` (int) — 1-based index of the camera being recorded (e.g., 1 of 2).
- `SimSteward.MultiCam.TotalAngles` (int) — total cameras in this run.
- `SimSteward.MultiCam.CurrentCameraName` (string) — display name of current camera.
- `SimSteward.MultiCam.ProgressText` (string) — e.g., "Recording angle 1 of 2... Far Chase".

Updated at the start of each camera's recording step. Cleared when the orchestrator returns to Idle.

---

## Key Decisions Summary

| Decision | Choice |
|----------|--------|
| Loop execution | Background thread/task, not in DataUpdate |
| Clip duration | Timer-based wait after StartRecord (Option 1) |
| Delays between commands | 300–1000 ms, configurable |
| Cancel | Between cameras + after current clip; keep completed clips |
| Progress | SimHub properties updated at each camera step |
