# Live Toast (Part 2 of FR-003)

**FR-IDs:** FR-003  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-14  
**Part:** 2 of 2 — see also [FR-003a-Replay-Overlay.md](FR-003a-Replay-Overlay.md)

**Parent story:** [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md) (Live Racing Feedback section)  
**Spec:** [../specs/FR-003b-Live-Toast.md](../specs/FR-003b-Live-Toast.md)

## Description

Minimal live-racing feedback: when an incident is detected during a race, show a brief non-interactive toast (e.g. "4x captured at 12:34") that auto-dismisses. No overlay or buttons while driving.

## Acceptance Criteria

See [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md) (Live Racing Feedback) and [../specs/FR-003b-Live-Toast.md](../specs/FR-003b-Live-Toast.md).

- [ ] On incident during live racing, brief toast appears (time + severity)
- [ ] Toast auto-dismisses after configured seconds (FR-008: ToastDurationSeconds)
- [ ] No toast when in replay mode (FR-003a handles replay)
- [ ] No interactive buttons during live racing

## Subtasks

- [ ] Implement toast UI (Dash Studio or SimHub notification)
- [ ] Bind to OnIncidentDetected; show only when not in replay
- [ ] Wire duration to settings (ToastDurationSeconds)

## Dependencies

- FR-001-002-Incident-Detection (incident events)
- FR-008-Plugin-Settings (toast duration setting)

## Notes

Scheduling uses FR-003a (replay overlay) and this story (FR-003b) as separate queue items. Parent: [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md).
