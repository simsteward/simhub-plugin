# Replay Control

**FR-IDs:** FR-004  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Jump iRacing's replay to the moment of an incident. When the user clicks "Jump to Replay" (from the overlay or incident log), the plugin sends iRacing to the correct replay timestamp with a configurable offset before the incident. This eliminates the worst part of protesting -- manually scrubbing through replay to find the incident.

## Acceptance Criteria

- [ ] Plugin sends `irsdk_BroadcastReplaySearchSessionTime` with correct session number and timestamp
- [ ] Replay jumps to `incidentTime - offsetSeconds` (configurable, default 5-10 seconds before)
- [ ] Offset is clamped to 0 if incident time minus offset would go negative
- [ ] Works for any incident in the incident log (not just the most recent)
- [ ] Graceful fallback if broadcast fails: display "Go to replay at {time}" in UI
- [ ] Replay jump works while in the iRacing replay screen

## Subtasks

- [ ] Instantiate a lightweight `iRacingSDK` instance for broadcast commands (separate from SimHub's data connection)
- [ ] Implement `JumpToReplay(sessionNum, sessionTimeSeconds, offsetSeconds)` method
- [ ] Calculate target: `(sessionTime - offset) * 1000` (convert to milliseconds, clamp ≥ 0)
- [ ] Send broadcast via `sdk.BroadcastMessage(irsdk_BroadcastReplaySearchSessionTime, sessionNum, targetMs)`
- [ ] Wire "Jump to Replay" action from overlay (FR-003) and incident log (FR-009)
- [ ] Add error handling: catch broadcast failures, log, show fallback message
- [ ] Test: trigger incident, jump to replay, verify replay lands at expected time

## Dependencies

- FR-001-002-Incident-Detection (needs incident records with timestamps)

## Notes

- Per SDK investigation: use lower-level `sdk.BroadcastMessage()` since NickThissen wrapper doesn't expose `BroadcastReplaySearchSessionTime` directly.
- The separate iRacingSDK instance uses Windows `SendMessage`/`PostMessage` and doesn't conflict with SimHub's data connection.
- Replay timing precision is a known risk (PRD constraint #4). The configurable offset is the mitigation -- users can adjust if jumps are too early/late.
- This story does NOT handle camera switching (that's Part 2, FR-010/011).
