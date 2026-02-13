# Spec: Recording Orchestrator (Multi-Camera Loop)

**FR-IDs:** FR-012  
**Priority:** Must (Part 2)  
**Status:** Ready  
**Part:** 2  
**Source Story:** `docs/product/stories/FR-012-014-015-Multi-Camera-Clipping.md`

---

## Overview

One-click multi-camera incident recording. The user selects an incident and triggers "Record All Angles." The plugin runs an automated loop: for each user-selected camera (FR-010), jump replay to the incident start, switch to that camera, start OBS recording, wait for the configured clip duration, stop recording, then repeat for the next camera. All individual clips are saved (one file per angle). FR-013 handles combining them into a single file; this spec covers only the orchestration and recording loop.

---

## Detailed Requirements

### R-ORC-01: Trigger

- **Trigger:** "Record All Angles" button (overlay or settings) or hotkey. Action name: e.g. `SimSteward.RecordAllAngles`.
- **Preconditions:** (1) At least one incident selected or in context (e.g., selected incident in overlay/log). (2) OBS connected (FR-005). (3) At least one camera selected (FR-010). (4) iRacing in replay mode (optional; orchestrator can jump first).
- **If preconditions fail:** No-op or show a short message (e.g., "Select an incident and ensure OBS is connected").

### R-ORC-02: Loop Sequence per Camera

For each selected camera, in order:

1. **Jump to replay start** — Call FR-004 `JumpToReplay(sessionNum, incidentSessionTime - clipBeforeSeconds, offsetSeconds)` (or equivalent; start time = incident time minus "seconds before" from FR-014).
2. **Delay** — Wait for replay and UI to stabilize (configurable, e.g. 500–1000 ms). See tech plan `recording-orchestrator-timing.md`.
3. **Switch camera** — Call FR-011 `SwitchCamera(cameraGroupId)` for this angle.
4. **Delay** — Short delay (e.g. 300–500 ms).
5. **Start recording** — FR-006 StartRecord.
6. **Delay** — Allow OBS to start (e.g. 500–800 ms).
7. **Wait for clip duration** — Wait for `clipBeforeSeconds + clipAfterSeconds` (or equivalent total duration from FR-014). Implemented via timer per tech plan (no blocking in DataUpdate).
8. **Stop recording** — FR-006 StopRecord.
9. **Delay** — Allow OBS to finalize (e.g. 300–500 ms).
10. **Next camera** — Repeat from step 2 for the next selected camera, or exit if done.

### R-ORC-03: Clip Duration

- **Input:** Clip duration (seconds before incident, seconds after incident) from FR-014 spec (settings). Total recording time per angle = before + after.
- **Replay start:** Jump target = incident time − before. Replay plays; after `(before + after)` seconds, stop recording.

### R-ORC-04: Single Camera

If the user has selected only one camera, the loop runs once (one iteration). Same sequence; no special case beyond "loop of one."

### R-ORC-05: Cancel

- **Cancel trigger:** Hotkey or overlay button (e.g. `SimSteward.CancelMultiCam`).
- **Behavior:** Do not start a new camera. Finish the current angle's recording (complete the wait and StopRecord), then exit the loop. All clips recorded so far are kept. Progress UI (FR-015) reflects "Cancelling..." or similar until idle.

### R-ORC-06: Error Handling

- **StartRecord or StopRecord fails for one angle:** Log, skip that angle, continue with the next. Do not abort the entire loop.
- **OBS disconnects during loop:** Exit loop, transition to Idle, log and notify user. Completed clips are kept.
- **Replay jump or camera switch fails:** Log, skip that angle (or retry once), continue. Per-story: "if one angle fails, continue with remaining angles."

### R-ORC-07: State and Progress

- **Running state:** Orchestrator exposes "running" state so overlay can show progress (FR-015). See tech plan for property names: `SimSteward.MultiCam.IsActive`, `CurrentAngleIndex`, `TotalAngles`, `CurrentCameraName`, `ProgressText`.
- **Idle:** When loop completes or is cancelled, clear progress properties and set IsActive = false.

---

## Technical Design Notes

- **Threading:** Loop must not run on DataUpdate thread. Use a background thread or async task; see `docs/tech/plans/recording-orchestrator-timing.md`.
- **Delays:** All step delays are configurable (constants or Part 2 settings) per tech plan.
- **Clip paths:** Each StopRecord returns an output path; the orchestrator may pass these to FR-013 (stitching) when the loop completes, or store for a separate "Stitch" action. Exact handoff is FR-013's concern.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-004 Replay Control** | JumpToReplay for each angle. |
| **FR-005, FR-006-007** | OBS connection and Start/Stop recording. |
| **FR-010-011 Camera Control** | Selected camera list and SwitchCamera. |
| **FR-014** | Clip duration settings (before/after seconds). |
| **FR-015** | Progress properties for overlay (same spec family). |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|------------------|
| "Record All Angles" triggers automated loop | R-ORC-01 |
| Loop: jump → switch camera → start record → wait → stop | R-ORC-02 |
| Repeats for each user-selected camera | R-ORC-02, R-ORC-04 |
| Clip duration configurable (before/after) | R-ORC-03, FR-014 |
| All individual recordings saved | R-ORC-02 (each StopRecord yields one file) |
| User can cancel; keep completed clips | R-ORC-05 |
| One angle fails → continue with rest | R-ORC-06 |

---

## Open Questions

- Whether to auto-invoke FR-013 (stitching) when loop completes, or require a separate "Stitch" action. Defer to FR-013 spec / product preference.
