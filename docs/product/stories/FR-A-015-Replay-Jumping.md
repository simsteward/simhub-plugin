# Replay Jumping

**FR-IDs:** FR-A-015  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver reviewing an incident after a session, I need to click "Review" and have iRacing's replay instantly jump to 30 seconds before the incident -- so I can watch what happened without manually scrubbing through the replay.

## Acceptance Criteria

- [ ] "Review" button exists for each incident in the main tab incident list and/or detail view
- [ ] Clicking Review sends a replay search command to iRacing targeting `IncidentTime - 30s`
- [ ] iRacing replay jumps to the correct position when replay mode is active
- [ ] Works across session numbers (uses `SessionNum` from Incident to target correct session)
- [ ] Graceful degradation: if replay is not active or SDK call fails, show user-facing message ("Replay not available -- enter replay mode in iRacing first")
- [ ] Button is disabled or hidden when no `SessionTick` is available on the incident

## Subtasks

- [ ] **Spike: Verify SDK availability.** Confirm that SimHub or iRacingSdkWrapper exposes `irsdk_broadcastMsg` with `irsdk_BroadcastReplaySearchSessionTime` (or equivalent). Check iRacing SDK docs at https://sajax.github.io/irsdkdocs/ and C# wrappers (iRacingSDK.Net, IRSDKSharper). Document findings before proceeding.
- [ ] Implement `BroadcastHelper.JumpToReplay(sessionNum, sessionTick)` using the verified API
- [ ] Calculate target position: `Incident.SessionTick` minus 30 seconds worth of ticks (or use `SessionTime` equivalent)
- [ ] Add "Review" button to incident list items in main tab
- [ ] Add "Review" button to detail view header
- [ ] Handle replay not active: detect state, show message, disable button
- [ ] Handle SDK call failure: catch exception, show error toast or inline message
- [ ] **Fallback documentation:** If `irsdk_broadcastMsg` is not accessible from SimHub, document the limitation and propose alternative (e.g., display "Go to replay at [time]" for manual navigation)

## Dependencies

- FR-A-012-014-Main-Tab-Incident-List (Review button placement in UI)
- Incident model with `SessionTick` and `SessionNum` (from FR-A-003)
- iRacing SDK broadcast API (verified in spike subtask)

## Notes

- The iRacing SDK uses `irsdk_broadcastMsg` with message type `irsdk_BroadcastReplaySearchSessionTime` to jump to a specific session time. The exact C# wrapper depends on which SDK library SimHub uses internally.
- The spike subtask is the first thing to do in this story. If the API is not accessible, the rest of the story changes significantly (fallback to manual instructions).
- Replay jumping only works when iRacing is in replay mode, not during live racing. The UI should make this clear.
