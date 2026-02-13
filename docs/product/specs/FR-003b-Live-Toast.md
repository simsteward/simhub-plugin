# Spec: Live Racing Toast Notification

**FR-IDs:** FR-003
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-003-In-Game-Overlay.md` (§ Live Racing Feedback)
**Note:** Part b of 2 -- see also `FR-003a-Replay-Overlay`

---

## Overview

During live racing, the driver needs passive confirmation that Sim Steward captured an incident -- without any interactive elements that could distract from driving. This spec covers a small, auto-dismissing toast notification that appears briefly when `OnIncidentDetected` fires and the driver is *not* in replay mode.

This is deliberately minimal: one text element, one visibility binding, one timer.

---

## Detailed Requirements

### R-TOAST-01: Trigger Condition

The toast appears when **both** conditions are true:

1. `OnIncidentDetected` fires (from FR-001 auto-detection or FR-002 manual mark -- see R-INC-05).
2. `IsReplayPlaying == false` (the driver is live racing, not watching replay).

If `IsReplayPlaying == true`, the toast does **not** appear. The replay overlay (FR-003a) handles incident display in replay mode.

### R-TOAST-02: Content

The toast displays a single line of text combining:

- **Incident severity:** The `Delta` field from `IncidentRecord`, formatted as `"{Delta}x"` (e.g., "4x"). For manual marks (`Delta == 0`), display "Incident" instead.
- **Formatted timestamp:** The `SessionTime` field, formatted as `mm:ss` (e.g., "12:34").
- **Combined format:** `"{severity} captured at {time}"` -- e.g., `"4x captured at 12:34"` or `"Incident captured at 08:15"`.

The plugin writes this string to `SimSteward.Toast.Text`.

### R-TOAST-03: Auto-Dismiss Timer

The toast auto-dismisses after a configurable duration.

- **Default:** 4 seconds.
- **Configurable:** Exposed in FR-008 Plugin Settings as an integer (seconds). Reasonable range: 2--8 seconds.
- **Mechanism:** The plugin manages the timer, not Dash Studio. On incident detection:
  1. Set `SimSteward.Toast.Text` to the formatted message (R-TOAST-02).
  2. Set `SimSteward.Toast.IsVisible = true`.
  3. Start (or restart) a dismiss timer for the configured duration.
  4. When the timer fires, set `SimSteward.Toast.IsVisible = false`.
- **Timer runs in plugin code.** Dash Studio NCalc/JS timers are unreliable for this (per tech plan).

### R-TOAST-04: No Interactivity

The toast has **zero** interactive elements. No buttons, no links, no click targets.

- The driver is mid-race. Any interactive element risks accidental input, focus theft from iRacing, or distraction.
- The toast is read-only confirmation that the plugin is working. All incident review happens later in replay mode (FR-003a).

### R-TOAST-05: Replacement on Rapid Incidents

If a new incident fires while the toast is already visible, the new incident **replaces** the current toast. Toasts do not stack.

- Update `SimSteward.Toast.Text` with the new message.
- Restart the dismiss timer (full duration from the new incident).
- The driver sees only the most recent incident. Prior incidents are available in the incident log (FR-009) and replay overlay (FR-003a).

### R-TOAST-06: Positioning and Size

The toast must be small and unobtrusive.

- **Size:** Approximately 300x60 pixels. One line of text, readable at 1080p and above.
- **Position:** Top-right corner by default. Must not overlap the iRacing session info bar (top-center) or the relative (top-left on many setups).
- **Repositionable:** Users can adjust position via SimHub's overlay layout editor.
- **Contrast:** Use a semi-transparent dark background with light text for readability against varied racing scenes.

### R-TOAST-07: Sound Cue (Optional / Stretch)

If SimHub supports triggering a sound effect from a plugin action or property change, play a brief audio cue when the toast appears.

- **Not a hard requirement.** If sound triggering is unsupported or complex, skip it. The visual toast is sufficient.
- **If implemented:** Use a short, subtle notification sound. Do not use loud or jarring audio. Make it toggleable in FR-008 settings.

---

## Technical Design Notes

The toast is a Dash Studio overlay element. The plugin drives it entirely through two SimHub properties and a server-side timer.

**Properties (set by plugin):**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.Toast.IsVisible` | `bool` | Controls Dash Studio element visibility |
| `SimSteward.Toast.Text` | `string` | Formatted message (R-TOAST-02) |

**Dash Studio binding:**

- A single text element with visibility bound to `[SimSteward.Toast.IsVisible]`.
- Text content bound to `[SimSteward.Toast.Text]`.
- Semi-transparent background rectangle behind the text, same visibility binding.

**Dismiss flow (pseudocode):**

```
dismissTimer: Timer (or CancellationTokenSource pattern)

on OnIncidentDetected(record):
  if IsReplayPlaying → return         // replay mode, skip toast
  text = FormatToastText(record)      // "4x captured at 12:34"
  SetProperty("SimSteward.Toast.Text", text)
  SetProperty("SimSteward.Toast.IsVisible", true)
  RestartDismissTimer(configuredDuration)

on dismissTimer elapsed:
  SetProperty("SimSteward.Toast.IsVisible", false)
```

**Packaging:** The toast can be a separate Dash Studio overlay file (`.simhubdash`) or a separate screen within the replay overlay file. A separate file is simpler because the toast and replay overlay have independent visibility conditions (tech plan recommendation).

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-001-002 Incident Detection** | Provides `OnIncidentDetected` event and `IncidentRecord` data model. The toast is a subscriber of this event. |
| **SCAFFOLD-Plugin-Foundation** | Provides `IsReplayPlaying` telemetry read and `SetPropertyValue` for SimHub properties. |
| **FR-008 Plugin Settings** | Toast duration setting. Can hardcode 4s initially and make configurable when FR-008 is implemented. |

**Surface area:** This is a small feature -- two properties, one timer, one Dash Studio text element. Low risk.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Brief non-interactive notification appears on incident during live racing | R-TOAST-01, R-TOAST-04 |
| Notification shows incident time and severity | R-TOAST-02 |
| Notification auto-dismisses after a few seconds | R-TOAST-03 |
| No interactive buttons during live racing | R-TOAST-04 |

---

## Open Questions

| # | Question | Impact | Resolution Path |
|---|----------|--------|-----------------|
| 1 | Can SimHub trigger a sound effect from a plugin property change or action? | R-TOAST-07 (optional sound cue) | Test during implementation. If unsupported, drop sound cue -- visual toast is sufficient. |
