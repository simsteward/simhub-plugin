# Replay Mode Overlay

**FR-IDs:** FR-003  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-13  
**Updated:** 2026-02-14

**Scheduling:** This story is split into two queue items for delivery: [FR-003a-Replay-Overlay.md](FR-003a-Replay-Overlay.md) (replay overlay) and [FR-003b-Live-Toast.md](FR-003b-Live-Toast.md) (live toast). This file remains the parent story for both.

## Description

When the driver enters iRacing replay mode, the overlay becomes the primary interaction surface. It shows captured incidents, lets the driver jump between them, and provides clip recording controls. This is where the core clipping workflow lives.

During live racing, the plugin works silently -- there is no interactive overlay while driving. The only live feedback is a subtle indication that an incident was captured (e.g., a brief toast or sound cue via SimHub). The real UI appears in replay mode.

## Acceptance Criteria

### Replay Mode Overlay
- [ ] Overlay appears when iRacing is in replay mode (detected via `IsReplayPlaying` or similar SDK state)
- [ ] Overlay hides when returning to live driving
- [ ] Shows list of captured incidents for the current session (time, severity, source)
- [ ] User can select an incident to jump to it in replay (triggers FR-004 replay jump)
- [ ] Shows OBS connection status (connected/disconnected)
- [ ] Provides "Start Recording" / "Stop Recording" controls (ties into FR-005/006)
- [ ] Shows current replay position relative to selected incident
- [ ] Does not block critical replay HUD elements (positioned unobtrusively)

### Live Racing Feedback (minimal)
- [ ] When an incident is detected during live racing, a brief non-interactive notification appears (toast or sound)
- [ ] Notification shows incident time and severity (e.g., "4x captured at 12:34")
- [ ] Notification auto-dismisses after a few seconds
- [ ] No interactive buttons during live racing -- the driver is driving

## Subtasks

- [ ] Detect iRacing replay mode vs live racing state in DataUpdate
- [ ] Design replay overlay layout in SimHub Dash Studio (incident list, clip controls, OBS status)
- [ ] Bind overlay visibility to replay mode state
- [ ] Display incident list from `IncidentRecord` data via SimHub properties
- [ ] Wire incident selection to FR-004 replay jump
- [ ] Wire record buttons to FR-005/006 OBS start/stop
- [ ] Implement minimal live-racing toast (brief text notification on incident detection)
- [ ] Test overlay at common resolutions (1080p, 1440p, ultrawide)

## Dependencies

- FR-001-002-Incident-Detection (needs incident data)
- FR-004-Replay-Control (replay jump on incident selection)
- FR-005-006-007-OBS-Integration (recording controls)

## Notes

- **The overlay is a replay-mode tool, not a live-racing HUD.** The driver interacts with Sim Steward after the race, in replay. During the race, the plugin is passive.
- The live toast is deliberately minimal -- drivers are mid-race and should not be distracted. It's just confirmation that the plugin is working.
- Dash Studio is the preferred approach for the replay overlay. If it can't handle dynamic lists or button actions, fall back to WPF overlay window.
- The dependency on FR-004 and FR-005/006/007 means this story assembles the pieces those stories build. It's the integration point.
- Consider: should the overlay also be accessible from the SimHub main tab (FR-009) as a desktop-only view? That may be simpler to build first as a fallback.
- **Open question for product owner:** This story covers two distinct UI surfaces (replay overlay + live toast). Consider whether to split into two stories or keep as one. The replay overlay is the larger effort; the live toast is minimal.
