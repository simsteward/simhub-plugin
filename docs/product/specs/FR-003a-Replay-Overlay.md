# Spec: Replay Mode Overlay

**FR-IDs:** FR-003
**Priority:** Must
**Status:** Ready
**Part:** 1 of 2 — see also FR-003b-Live-Toast
**Source Story:** `docs/product/stories/FR-003-In-Game-Overlay.md` (Replay Mode Overlay section)

---

## Overview

The replay overlay is the primary interaction surface for the entire Sim Steward clipping workflow. It assembles pieces from five other specs — incident list (FR-001-002), replay jump (FR-004), OBS connection (FR-005), and recording controls (FR-006-007) — into a single in-game panel the user interacts with during replay review.

It appears **only** when the user is viewing iRacing replay (`IsReplayPlaying == true`). During live racing, the overlay is hidden — the driver should not be distracted. (Live-race feedback is covered by FR-003b.)

This is the spec where the product comes together: the user sees their incidents, jumps to one, and records a clip — all without leaving the iRacing window.

---

## Detailed Requirements

### R-OVR-01: Visibility — Replay Mode Only (FR-003)

The overlay is visible **only** when iRacing replay mode is active.

- **Show condition:** `SimSteward.IsReplayMode == true` (set by the plugin when `IsReplayPlaying` is truthy — see tech plan for access path).
- **Hide condition:** `SimSteward.IsReplayMode == false` — includes live racing, iRacing disconnected, and `IsReplayPlaying == null`.
- **Binding:** The entire overlay's Dash Studio visibility formula is bound to `[SimSteward.IsReplayMode]`. No partial visibility — the full panel shows or hides as a unit.
- **Transition:** Appearance/disappearance is immediate on state change. No animation required.

### R-OVR-02: Incident List — Fixed-Slot Pattern (FR-003, FR-001-002)

The overlay displays up to **8 incident slots**, each bound to an individual set of SimHub properties. This follows the Dash Studio fixed-slot pattern documented in the tech plan (`docs/tech/plans/dash-studio-overlay.md`), which works around Dash Studio's lack of a native repeater widget.

Each slot shows three fields from the `IncidentRecord` model (R-INC-03):

| Column | Property Binding | Example | Source Field |
|--------|-----------------|---------|--------------|
| Timestamp | `[SimSteward.Incident.{N}.TimeFormatted]` | `"12:34"` | `SessionTime` formatted as `MM:SS` |
| Severity | `[SimSteward.Incident.{N}.DeltaText]` | `"4x"` | `Delta` (e.g., `4` → `"4x"`; `0` → `"Manual"`) |
| Source | `[SimSteward.Incident.{N}.Source]` | `"Auto"` | `Source` enum (`Auto` / `Manual`) |

Where `{N}` is the slot index `0` through `7`.

**Slot visibility:** Each slot row's visibility is bound to `[SimSteward.Incident.{N}.IsPopulated]`. Empty slots are hidden — the overlay only shows rows for actual incidents, not blank space.

**Overflow handling:** If more than 8 incidents exist in the session, the overlay shows the 8 most recent. The count summary (R-OVR-04) tells the user if incidents are truncated.

### R-OVR-03: Jump-to-Incident Action (FR-003, FR-004)

Each populated incident slot has a **Jump** button that sends iRacing's replay to that incident's timestamp.

- **Action per slot:** `SimSteward.JumpToIncident.{N}` (where `{N}` is `0` through `7`).
- **Binding:** Each button's `TriggerSimHubInputName` is set to its slot-specific action name.
- **Plugin behavior:** When action `SimSteward.JumpToIncident.{N}` fires, the plugin resolves slot `{N}` to the corresponding `IncidentRecord` and calls `ReplayController.JumpToReplay(sessionNum, sessionTime, offsetSeconds)` per FR-004 (R-RPL-01).
- **Offset:** Uses the current `ReplayOffsetSeconds` setting from FR-008 (R-RPL-07). Default: 5 seconds before the incident.
- **Empty slot guard:** If the user somehow triggers an action for an unpopulated slot (e.g., via SimHub controls rather than the overlay), the plugin ignores it silently.

### R-OVR-04: Incident Count Summary (FR-003, FR-001-002)

Below the incident list, display a count summary.

- **Property:** `[SimSteward.Incident.CountText]`
- **Format:** `"Showing {visible} of {total} incidents"` when total > visible (overflow); `"{total} incidents"` when all fit.
- **Examples:** `"Showing 8 of 12 incidents"`, `"3 incidents"`, `"No incidents"`.

### R-OVR-05: OBS Connection Status (FR-003, FR-005)

The overlay displays the current OBS WebSocket connection state so the user knows whether recording is available.

| Element | Property Binding | Values |
|---------|-----------------|--------|
| Status text | `[SimSteward.OBS.StatusText]` | `"Connected"`, `"Disconnected"`, `"Connecting"`, `"Reconnecting"`, `"Recording..."` |
| Status indicator color | `[SimSteward.OBS.IsConnected]` | `true` → green, `false` → red (NCalc formula on color attribute) |

The status bar is always visible within the replay overlay — it does not depend on whether incidents exist.

**Recording active:** When `SimSteward.OBS.IsRecording == true`, the status text switches to `"Recording..."` to give clear feedback that OBS is capturing.

### R-OVR-06: Start / Stop Recording Button (FR-003, FR-006)

The overlay provides a recording toggle button.

- **Action:** `SimSteward.ToggleRecording` — the same action registered in FR-006 (R-REC-01).
- **Binding:** Button `TriggerSimHubInputName` is `SimSteward.ToggleRecording`.
- **Label:** Bound to `[SimSteward.OBS.RecordButtonText]` — displays `"Start Recording"` when idle, `"Stop Recording"` when recording.
- **State awareness:** The plugin's recording state machine (R-REC-03) handles toggle logic. The overlay simply fires the action — it does not need to know the current state beyond the button label.
- **Disabled appearance (optional):** When OBS is disconnected (`[SimSteward.OBS.IsConnected] == false`), the button should appear visually muted or show `"OBS Disconnected"` as its label. Pressing it when disconnected is a no-op (FR-006 R-REC-01 ignores the command).

### R-OVR-07: Positioning and Layout (FR-003)

The overlay must not obstruct critical replay HUD elements.

- **Default position:** Right edge of screen, vertically centered. This avoids iRacing's replay control bar (bottom) and session info bar (top).
- **Repositionable:** Users can move the overlay via SimHub's standard overlay layout editor. No custom positioning code needed.
- **Size:** Approximately 350-450px wide, height varies with populated slots. Well within Dash Studio's 480,000 pixel surface limit.
- **Stacking order:** Managed by SimHub's overlay renderer. The plugin does not control z-order.

**Layout structure (top to bottom):**

1. Header — "SIM STEWARD"
2. OBS status bar (R-OVR-05)
3. Incident list rows (R-OVR-02), 0-8 visible
4. Count summary (R-OVR-04)
5. Recording button (R-OVR-06)
6. Clip prompt bar (R-OVR-09) — visible only when a clip is pending

### R-OVR-09: Clip Save/Discard Prompt (FR-003, FR-007)

After recording stops, the overlay shows the clip path and save/discard controls.

- **Visibility:** Shown only when `SimSteward.LastClipStatus == "Pending"`. Hidden otherwise.
- **Clip path display:** Bound to `[SimSteward.LastClipPath]`. Shows the filename (not full path) to save space; full path as tooltip or in the settings tab incident log.
- **Save button:** Action `SimSteward.ClipSave`. Sets `ClipRecord.Status = Saved` per FR-007 R-CLIP-03.
- **Discard button:** Action `SimSteward.ClipDiscard`. Triggers confirmation and file deletion per FR-007 R-CLIP-04.
- **Auto-dismiss:** When `LastClipStatus` transitions from `"Pending"` to `"Saved"` or `"Discarded"`, the prompt hides automatically (visibility binding handles this).

### R-OVR-08: Replay Position Indicator (FR-003) — Should

If feasible, display the current replay position relative to the selected incident.

- **Property:** `[SimSteward.Replay.PositionText]` — e.g., `"-3s"` (3 seconds before incident), `"+1s"` (1 second after).
- **Computation:** Plugin compares current `SessionTime` (from telemetry during replay) to the last-jumped-to `IncidentRecord.SessionTime`.
- **Visibility:** Only shown after a jump action has been triggered (otherwise there's no "selected incident" to measure against).
- **Priority:** Should. This is useful but not critical for the MVP clipping workflow. If computing replay position adds complexity, defer it.

---

## SimHub Property Binding Reference

All properties the overlay binds to. The overlay **reads** these; the plugin (and other specs) **write** them.

### Replay State

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.IsReplayMode` | `bool` | Plugin DataUpdate | `true` when `IsReplayPlaying` is truthy |

### Incident Slots (N = 0..7)

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.Incident.{N}.IsPopulated` | `bool` | FR-001-002 | Slot has an incident |
| `SimSteward.Incident.{N}.TimeFormatted` | `string` | FR-001-002 | `"MM:SS"` from `SessionTime` |
| `SimSteward.Incident.{N}.DeltaText` | `string` | FR-001-002 | `"4x"`, `"2x"`, `"Manual"` |
| `SimSteward.Incident.{N}.Source` | `string` | FR-001-002 | `"Auto"` / `"Manual"` |

### Incident Summary

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.Incident.Count` | `int` | FR-001-002 | Total incidents in session |
| `SimSteward.Incident.CountText` | `string` | FR-001-002 | `"Showing 3 of 5 incidents"` |

### OBS Status

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.OBS.IsConnected` | `bool` | FR-005 | WebSocket connection alive |
| `SimSteward.OBS.IsRecording` | `bool` | FR-006 | OBS is currently recording |
| `SimSteward.OBS.StatusText` | `string` | FR-005 / FR-006 | Human-readable status |
| `SimSteward.OBS.RecordButtonText` | `string` | FR-006 | `"Start Recording"` / `"Stop Recording"` |

### Clip Prompt (R-OVR-09)

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.LastClipPath` | `string` | FR-007 | File path of most recent clip |
| `SimSteward.LastClipStatus` | `string` | FR-007 | `"Pending"`, `"Saved"`, `"Discarded"`, or `""` |

### Replay Position (Should — R-OVR-08)

| Property | Type | Written By | Description |
|----------|------|-----------|-------------|
| `SimSteward.Replay.PositionText` | `string` | Plugin DataUpdate | `"-3s"`, `"+1s"` relative to last jumped incident |

### Actions (registered via `AddAction`)

| Action Name | Trigger | Handled By |
|-------------|---------|-----------|
| `SimSteward.JumpToIncident.0` through `.7` | Overlay jump buttons | FR-004 `ReplayController.JumpToReplay` |
| `SimSteward.ToggleRecording` | Overlay record button | FR-006 `RecordingController.ToggleRecording` |
| `SimSteward.ClipSave` | Overlay save button | FR-007 Clip management |
| `SimSteward.ClipDiscard` | Overlay discard button | FR-007 Clip management |

---

## Technical Design Notes

### Dash Studio Implementation

The overlay is built as a `.simhubdash` file using the Dash Studio fixed-slot pattern. Reference `docs/tech/plans/dash-studio-overlay.md` for the full technical approach. Key points:

- **No repeater widget.** Each incident slot is a manually duplicated group of elements (timestamp text, severity text, source text, jump button) with index-specific property bindings.
- **Visibility via NCalc.** The overlay root binds to `[SimSteward.IsReplayMode]`. Each slot row binds to `[SimSteward.Incident.{N}.IsPopulated]`.
- **Button actions via `TriggerSimHubInputName`.** Each jump button fires its slot-specific action. The record button fires `SimSteward.ToggleRecording`. Per-slot actions are the recommended approach (tech plan confirmed this is safer than a shared action + index property).
- **OBS status indicator color** uses an NCalc formula on the element's color attribute: `iif([SimSteward.OBS.IsConnected], '#00CC00', '#CC0000')`.

### Property Update Strategy

All incident slot properties are refreshed from the in-memory `List<IncidentRecord>` (R-INC-06) on each `DataUpdate` tick. The plugin iterates through the most recent 8 incidents and writes all slot properties in a batch. This is simple, avoids partial-update flicker, and costs ~35 property writes per tick — well within SimHub's capacity (see tech plan Open Question #3).

### Distribution

The `.simhubdash` file ships alongside the plugin DLL. SimHub auto-discovers overlay files from plugin packages. Users enable the overlay via SimHub's overlay management UI.

---

## Dependencies & Constraints

| Dependency | Role | Detail |
|------------|------|--------|
| **FR-001-002 Incident Detection** | Data source | Provides `IncidentRecord` model, in-memory incident list, `OnIncidentDetected` event. The overlay reads incident data via slot properties. |
| **FR-004 Replay Control** | Jump action handler | `JumpToReplay` is called when the user presses a jump button. The overlay fires the action; FR-004 handles the iRacing broadcast. |
| **FR-005 OBS Connection** | Status source | Provides `SimSteward.OBS.IsConnected`, `SimSteward.OBS.StatusText`. The overlay reads these for the status indicator. |
| **FR-006-007 Recording & Clips** | Recording action handler | `ToggleRecording` is called when the user presses the record button. FR-006 handles the OBS request. Provides `SimSteward.OBS.IsRecording`, `SimSteward.OBS.RecordButtonText`. |
| **FR-008 Plugin Settings** | Configuration | `ReplayOffsetSeconds` affects jump behavior (passed through to FR-004). OBS settings affect connection status. |
| **SCAFFOLD-Plugin-Foundation** | Runtime | Plugin lifecycle, `DataUpdate` loop, property registration, action registration. |
| **Dash Studio** | Rendering | SimHub's overlay engine. Renders the `.simhubdash` file, manages positioning, handles focus. |

**This is the integration surface.** The overlay itself contains minimal logic — it binds to properties and fires actions. The intelligence lives in the specs it depends on.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Overlay appears when iRacing is in replay mode | R-OVR-01 |
| Overlay hides when returning to live driving | R-OVR-01 |
| Shows list of captured incidents (time, severity, source) | R-OVR-02 |
| User can select an incident to jump to replay (triggers FR-004) | R-OVR-03 |
| Shows OBS connection status (connected/disconnected) | R-OVR-05 |
| Provides Start/Stop Recording controls (ties into FR-006) | R-OVR-06 |
| Shows current replay position relative to selected incident | R-OVR-08 (Should) |
| Does not block critical replay HUD elements | R-OVR-07 |

---

## Open Questions

| # | Question | Impact | Status |
|---|----------|--------|--------|
| 1 | **Does `TriggerSimHubInputName` fire reliably while iRacing has focus?** SimHub overlays render on top of the game, but button interactions may require SimHub's overlay to capture the click before the game window consumes it. If this fails, the overlay buttons are non-functional and a WPF fallback is needed. | Blocks all overlay button actions (jump + record) | Flagged in tech plan (Open Question #2). Must test early during implementation. |
| 2 | **Can a single `.simhubdash` file contain both the replay overlay (this spec) and the live toast (FR-003b) as separate screens with independent visibility?** Or do they need separate overlay files? | Packaging only — not functional | Flagged in tech plan (Open Question #1). Test during implementation. |
| 3 | **Replay position accuracy during replay.** Does `SessionTime` from telemetry update accurately when iRacing is in replay mode, or does it reflect the live session time? If the latter, R-OVR-08 needs a different data source. | Affects R-OVR-08 (Should priority — deferrable) | Test during implementation. |
