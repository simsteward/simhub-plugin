# Camera Control

**FR-IDs:** FR-010, FR-011  
**Priority:** Must (Part 2)  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Let users configure which camera angles to use for multi-camera clipping, and switch iRacing's replay camera programmatically. This is the foundation for Part 2's automated multi-angle recording -- without camera control, the user has to manually change cameras between recordings.

## Acceptance Criteria

- [ ] Plugin enumerates available iRacing camera groups at session start (from session info YAML)
- [ ] Settings UI shows a checklist/dropdown of available cameras for the user to select
- [ ] User can select 1-4 camera angles to use for multi-camera clipping
- [ ] Plugin switches iRacing replay camera via `irsdk_broadcastMsg` `CamSwitchNum`
- [ ] Camera switch works during replay playback
- [ ] Selected cameras persist in plugin settings across sessions

## Subtasks

- [ ] **Spike: Camera enumeration** -- Parse iRacing session info YAML to extract camera group names and IDs. (PRD Risk #3)
- [ ] Implement camera discovery: read session info, parse camera groups, store in model
- [ ] Add camera selection UI to settings tab (checklist of available cameras with friendly names)
- [ ] Implement `SwitchCamera(cameraGroupId)` method using `sdk.BroadcastMessage(CamSwitchNum, ...)`
- [ ] Persist selected camera preferences in plugin settings
- [ ] Handle edge case: user-selected camera not available in current session (graceful fallback)
- [ ] Test: enumerate cameras from a live iRacing session, switch between them in replay

## Dependencies

- SCAFFOLD-Plugin-Foundation (needs iRacing SDK connection)
- FR-008-Plugin-Settings (settings tab for camera selection UI)

## Notes

- **Includes a technical spike** (PRD constraint #3). Camera enumeration from the session info YAML needs validation before building the UI.
- Camera groups in iRacing have IDs and names (e.g., "Nose", "Chase", "Far Chase", "Helicopter", "TV1"). The session info YAML structure needs investigation during the spike.
- `CamSwitchNum` takes car number + camera group + camera number. For the player's car, we use the player's car index.
- Camera availability varies by track. The UI should refresh the camera list when a new session starts.
