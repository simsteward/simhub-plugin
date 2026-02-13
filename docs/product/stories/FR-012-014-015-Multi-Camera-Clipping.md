# Automated Multi-Camera Clipping

**FR-IDs:** FR-012, FR-014, FR-015  
**Priority:** FR-012 Must (Part 2), FR-014 Should (Part 2), FR-015 Should (Part 2)  
**Status:** Ready  
**Created:** 2026-02-13

## Description

One-click multi-camera incident recording. The user selects an incident, presses a button, and the plugin automatically: jumps to replay, starts recording from camera 1, plays for the clip duration, stops recording, rewinds to incident start, switches to camera 2, records again -- repeating for each selected camera angle. A progress indicator shows which angle is currently recording. This is the headline Part 2 feature that turns a multi-minute manual process into a single action.

## Acceptance Criteria

- [ ] "Record All Angles" button (or hotkey) triggers the automated loop for a selected incident
- [ ] Loop sequence per camera: jump to replay start → switch camera → start OBS recording → wait for clip duration → stop recording
- [ ] Loop repeats for each user-selected camera angle (from FR-010)
- [ ] Clip duration is configurable: seconds before and after the incident point (default: 5s before, 10s after)
- [ ] Overlay shows recording progress: "Recording angle N of M..." with current camera name
- [ ] Progress indicator updates in real-time as each angle completes
- [ ] All individual camera recordings are saved (one file per angle)
- [ ] User can cancel the recording loop mid-process (stops current recording, keeps completed ones)
- [ ] Error handling: if one angle fails, continue with remaining angles (don't abort the whole loop)

## Subtasks

- [ ] Implement `MultiCameraRecorder` orchestrator class
- [ ] Build recording loop: for each camera → jump → switch → record → wait → stop
- [ ] Add clip duration settings to FR-008 settings tab (before/after seconds)
- [ ] Calculate replay start/end times from incident timestamp and clip duration settings
- [ ] Implement replay playback duration control (stop/pause replay at clip end time)
- [ ] Expose recording progress as SimHub plugin properties (current angle, total angles, camera name)
- [ ] Build progress overlay in Dash Studio (or extend existing overlay from FR-003)
- [ ] Add cancel mechanism: hotkey or button to abort the loop
- [ ] Handle timing: add small delays between loop steps for OBS/iRacing to process commands
- [ ] Test: run full loop with 2 cameras, verify both clips saved correctly

## Dependencies

- FR-004-Replay-Control (replay jumping)
- FR-005-006-007-OBS-Integration (start/stop recording)
- FR-010-011-Camera-Control (camera switching + user camera selection)

## Notes

- Timing between steps is critical. OBS needs time to start/stop recording, iRacing needs time to switch cameras and seek replay. Build in configurable delays (e.g., 500ms-1s between commands).
- Replay playback duration control may need a timer-based approach: start recording, wait N seconds, stop recording. The plugin may need to monitor `SessionTime` in replay to know when the clip end time is reached.
- The individual camera recordings are separate files. FR-013 handles combining them into one output.
- If only one camera is selected, this still works as a single-angle auto-record (simplified path).
