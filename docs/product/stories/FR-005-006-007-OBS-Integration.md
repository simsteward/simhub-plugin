# OBS Integration

**FR-IDs:** FR-005, FR-006, FR-007  
**Priority:** FR-005 Must, FR-006 Must, FR-007 Should  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Connect to OBS Studio via its WebSocket API to start/stop recording and manage clip files. This is the recording backbone of Sim Steward -- without OBS integration, there are no video clips. The plugin acts as a remote control for OBS: connect, start recording when the user wants to capture an incident, stop recording, and surface the saved clip path.

## Acceptance Criteria

- [ ] Plugin connects to OBS via obs-websocket 5.x protocol
- [ ] Connection uses configurable URL/port/password (from FR-008 settings)
- [ ] Connection status is surfaced in the plugin UI (connected/disconnected/error)
- [ ] Plugin handles OBS not running, connection loss, and reconnection attempts
- [ ] "Start Recording" command starts OBS recording (via button or hotkey)
- [ ] "Stop Recording" command stops OBS recording
- [ ] After recording stops, the saved clip file path is displayed in the overlay/UI
- [ ] Clip save prompt allows user to confirm save or discard (discard = delete file)

## Subtasks

- [ ] **Spike: OBS WebSocket from .NET 4.8** -- Can a SimHub plugin maintain a stable WebSocket connection to OBS? Test with minimal client. (PRD Risk #1)
- [ ] Add or reference a WebSocket client library compatible with .NET 4.8 (e.g., `websocket-sharp` or raw `System.Net.WebSockets`)
- [ ] Implement OBS connection manager: connect, disconnect, reconnect with backoff
- [ ] Implement obs-websocket 5.x authentication handshake (challenge-response)
- [ ] Send `StartRecord` request, handle response
- [ ] Send `StopRecord` request, handle response, extract output file path
- [ ] Surface connection status as a SimHub plugin property (for UI binding)
- [ ] Implement clip save/discard prompt in overlay or settings UI
- [ ] Handle edge cases: OBS already recording, OBS closed mid-recording, recording fails
- [ ] Test: connect to OBS, start/stop recording, verify file saved

## Dependencies

- SCAFFOLD-Plugin-Foundation (plugin must load)

## Notes

- **This story includes a technical spike** (PRD constraint #1). The spike should be done first -- if .NET 4.8 WebSocket to OBS doesn't work, the architecture needs rethinking.
- OBS saves files per its own output settings (format, path, filename pattern). The plugin doesn't control where OBS saves -- it just reads the path from the `StopRecord` response.
- obs-websocket 5.x is built into OBS since version 28. Users don't need to install a separate plugin.
- The "discard" action in the clip save prompt deletes the file from disk. Confirm before deleting.
- This story is independent of incident detection -- the user can start/stop recording at any time. The automated workflow (detect → jump → record) is assembled later.
