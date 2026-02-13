# Spec: Clip Duration Control & Recording Progress (Multi-Camera)

**FR-IDs:** FR-014, FR-015  
**Priority:** FR-014 Should (Part 2), FR-015 Should (Part 2)  
**Status:** Ready  
**Part:** 2  
**Source Story:** `docs/product/stories/FR-012-014-015-Multi-Camera-Clipping.md`

---

## Overview

**FR-014:** User-configurable clip duration (seconds before and after the incident point) for the multi-camera recording loop. The orchestrator (FR-012) uses these values to compute replay start time and how long to record each angle.

**FR-015:** Overlay (or settings) shows which camera angle is currently being recorded during the "Record All Angles" loop — e.g., "Recording angle 1 of 2... Far Chase" — so the user has clear feedback.

---

## Detailed Requirements

### FR-014: Clip Duration Control

#### R-DUR-01: Duration Settings

- **Seconds before incident:** How many seconds before the incident timestamp the clip starts. Default: 5. Range: 0–60 (or similar; document max in implementation). Stored in plugin settings (Part 2 extension).
- **Seconds after incident:** How many seconds after the incident timestamp the clip ends. Default: 10. Range: 0–120 (or similar).
- **Total duration per angle:** Computed as `before + after`. Used by FR-012 orchestrator as the wait time after StartRecord before calling StopRecord.

#### R-DUR-02: Settings UI

- **Location:** Settings tab (FR-008), in the same Part 2 section as camera selection or a dedicated "Clip duration" group.
- **Controls:** Two numeric inputs (integer seconds): "Seconds before incident" and "Seconds after incident," with labels and optional tooltips. Validation: non-negative, within range; clamp or reject invalid values.
- **Persistence:** Same mechanism as other settings (SimHub ReadCommonSettings/SaveCommonSettings). Changes take effect immediately for the next "Record All Angles" run.

#### R-DUR-03: Consumption by Orchestrator

FR-012 Recording Orchestrator reads these values at the start of each run. Replay jump target = incident time − beforeSeconds. Record duration = beforeSeconds + afterSeconds.

### FR-015: Recording Progress Indicator

#### R-PROG-01: Progress Properties

The plugin exposes SimHub properties so the overlay (or a dedicated progress panel) can display current multi-camera progress. See tech plan `recording-orchestrator-timing.md` for the property list; this spec defines the product contract:

- **IsActive** (bool): Multi-camera loop is running.
- **CurrentAngleIndex** (int): 1-based index of the angle currently being recorded (e.g., 1 or 2).
- **TotalAngles** (int): Total number of angles in this run.
- **CurrentCameraName** (string): Display name of the current camera (from FR-010 camera list).
- **ProgressText** (string): Human-readable line, e.g. "Recording angle 1 of 2... Far Chase".

Property namespace: e.g. `SimSteward.MultiCam.*` (align with tech plan).

#### R-PROG-02: When Updated

- **Start of loop:** Set IsActive = true; set TotalAngles; set CurrentAngleIndex = 1 and CurrentCameraName / ProgressText for first camera.
- **Start of each new angle:** Update CurrentAngleIndex, CurrentCameraName, ProgressText.
- **End of loop (complete or cancel):** Set IsActive = false; clear or reset progress fields so the overlay can hide the progress block.

#### R-PROG-03: Overlay Display

- **Visibility:** Progress block visible when `SimSteward.MultiCam.IsActive == true`.
- **Content:** At minimum, show ProgressText. Optionally show a simple progress bar (CurrentAngleIndex / TotalAngles). Layout can extend the existing replay overlay (FR-003a) or live in a separate overlay panel; implementation choice.

---

## Technical Design Notes

- **Settings model:** Part 2 adds `ClipBeforeSeconds` and `ClipAfterSeconds` (or equivalent) to the persisted settings. Defaults 5 and 10.
- **Progress:** Orchestrator (FR-012) is the writer of progress properties; this spec and the tech plan define the contract. Overlay only reads.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-008** | Settings tab hosts duration inputs and persistence. |
| **FR-012** | Orchestrator consumes duration and writes progress. |
| **FR-010** | Camera names for ProgressText come from camera list. |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|------------------|
| Clip duration configurable (before/after), default 5s before 10s after | R-DUR-01, R-DUR-02 |
| Overlay shows "Recording angle N of M..." with camera name | R-PROG-01, R-PROG-02, R-PROG-03 |
| Progress updates in real time as each angle completes | R-PROG-02 |

---

## Open Questions

- Exact property names and namespace (finalize with FR-012 implementation and tech plan).
- Whether to show elapsed time within the current clip (e.g., "0:05 / 0:15") — optional enhancement.
