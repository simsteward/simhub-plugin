# Replay Overlay (Part 1 of FR-003)

**FR-IDs:** FR-003  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-14  
**Part:** 1 of 2 — see also [FR-003b-Live-Toast.md](FR-003b-Live-Toast.md)

**Parent story:** [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md) (Replay Mode Overlay section)  
**Spec:** [../specs/FR-003a-Replay-Overlay.md](../specs/FR-003a-Replay-Overlay.md)

## Description

Replay-mode overlay: incident list, jump-to-incident, OBS status, Start/Stop Recording. Visible only when iRacing is in replay. This is the primary interaction surface for the clipping workflow.

## Acceptance Criteria

See [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md) (Replay Mode Overlay) and [../specs/FR-003a-Replay-Overlay.md](../specs/FR-003a-Replay-Overlay.md).

- [ ] Overlay appears when iRacing is in replay mode; hides when returning to live
- [ ] Shows incident list (time, severity, source); user can select and jump (FR-004)
- [ ] Shows OBS connection status; Start/Stop Recording (FR-005/006)
- [ ] Does not block critical replay HUD elements

## Subtasks

- [ ] Detect replay mode; bind overlay visibility to replay state
- [ ] Design overlay layout in Dash Studio (incident list, clip controls, OBS status)
- [ ] Display incident list via SimHub properties; wire jump to FR-004, record to FR-005/006
- [ ] Test at common resolutions

## Dependencies

- FR-001-002-Incident-Detection (incident data)
- FR-004-Replay-Control (replay jump)
- FR-005-006-007-OBS-Integration (recording controls)

## Notes

Scheduling uses this story (FR-003a) and FR-003b (live toast) as separate queue items. The parent [FR-003-In-Game-Overlay.md](FR-003-In-Game-Overlay.md) remains the single story file for both.
