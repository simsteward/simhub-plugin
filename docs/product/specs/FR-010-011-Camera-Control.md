# Spec: Camera Control

**FR-IDs:** FR-010, FR-011  
**Priority:** Must (Part 2)  
**Status:** Ready  
**Part:** 2  
**Source Story:** `docs/product/stories/FR-010-011-Camera-Control.md`

---

## Overview

Users configure which replay camera angles to use for multi-camera clipping (FR-010) and the plugin switches iRacing's replay camera programmatically (FR-011). This is the foundation for the automated multi-camera recording loop (FR-012). Without camera control, users would have to change cameras manually between recordings.

Depends on the camera-enumeration spike (`docs/tech/plans/camera-enumeration-spike.md`) to define how camera groups are discovered and how `CamSwitchNum` is invoked.

---

## Detailed Requirements

### FR-010: Camera Selection

#### R-CAM-01: Enumerate Camera Groups

At session start (or when session info becomes available per spike), the plugin enumerates available iRacing camera groups.

- **Source:** Session info (YAML or equivalent) per camera-enumeration-spike findings.
- **Data model:** Each entry has at least: identifier (for `CamSwitchNum`), display name. Store in a list `AvailableCameras` (or equivalent) for the current session.
- **Refresh:** Re-enumerate when `SessionNum` changes or iRacing reconnects. Camera availability can vary by track.

#### R-CAM-02: Settings UI for Camera Selection

The settings tab (FR-008) includes a **Part 2** section for multi-camera clipping.

- **Control:** Checklist or multi-select list of available cameras, showing display names.
- **Constraint:** User selects 1–4 camera angles. Validation: at least 1, at most 4. Order of selection defines playback order in FR-012 (first selected = angle 1, etc.).
- **Empty state:** If no session is active and enumeration has not run, show a message such as "Join an iRacing session to see available cameras" or use a cached list from the last session if available.

#### R-CAM-03: Persist Selected Cameras

Selected camera IDs (or stable identifiers) are persisted in plugin settings.

- **Storage:** Extend the settings model (FR-008) with a Part 2 field, e.g. `SelectedCameraIds` (list of strings or ints). Persisted via the same SimHub settings API.
- **Survives restart:** Selected cameras are restored when the user reopens settings.

#### R-CAM-04: Selected Camera Not Available

If the user had previously selected a camera that is not in the current session's enumerated list:

- **Behavior:** Do not fail. Use a graceful fallback: e.g., filter out missing IDs and show the remaining selection; or reset to "first available" and notify the user once. Document the chosen behavior in implementation.
- **UI:** Optionally show a warning in the settings tab: "Some previously selected cameras are not available this session."

### FR-011: Camera Switching

#### R-CAM-05: SwitchCamera Method

The plugin implements a method to set the replay camera to a given camera group.

- **Signature:** `SwitchCamera(cameraGroupId)` (or equivalent parameters per spike — e.g., car index + group + camera number).
- **Implementation:** Send `irsdk_broadcastMsg` `CamSwitchNum` with the appropriate parameters. Use the same separate iRacingSDK instance pattern as replay jump (FR-004). Player's car index from telemetry.
- **When:** Callable during replay playback. Used by FR-012 orchestrator before each angle's recording.

#### R-CAM-06: Camera Switch Reliability

- **Idempotent:** Calling SwitchCamera with the current camera is a no-op; no visible flicker required.
- **Error handling:** If the broadcast fails (e.g., iRacing not in replay), log and optionally surface a status message. FR-012 will rely on this for the loop; failure for one camera should not crash the plugin.

---

## Technical Design Notes

- **Spike dependency:** Exact API for enumeration and `CamSwitchNum` parameter order come from `docs/tech/plans/camera-enumeration-spike.md`. Implement after spike verdict is GREEN or YELLOW with documented fallback.
- **Settings model extension:** Part 2 adds fields to the existing settings class or a separate Part 2 settings blob; avoid breaking Part 1 settings schema.
- **Car index:** Use telemetry (e.g., `PlayerCarIdx` or equivalent from session/telemetry) for the player's car when calling `CamSwitchNum`.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **SCAFFOLD** | Plugin lifecycle, telemetry access, settings tab host. |
| **FR-008 Plugin Settings** | Camera selection UI lives in the settings tab; persistence uses same settings API. |
| **Camera enumeration spike** | Must complete before implementation. Defines session info source and CamSwitchNum usage. |

**Constraint:** Camera availability is track/session-dependent. The plugin must not assume a fixed global list.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|------------------|
| Plugin enumerates available camera groups at session start | R-CAM-01 |
| Settings UI shows checklist/dropdown of available cameras | R-CAM-02 |
| User can select 1–4 camera angles | R-CAM-02 |
| Plugin switches replay camera via CamSwitchNum | R-CAM-05 |
| Camera switch works during replay playback | R-CAM-05, R-CAM-06 |
| Selected cameras persist across sessions | R-CAM-03 |
| User-selected camera not available in current session | R-CAM-04 |

---

## Open Questions

- Exact YAML structure and session info API (spike output).
- Whether to show a "default" set of cameras (e.g., Chase, Far Chase) when no session has been run yet (spike may inform).
