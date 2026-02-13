# Plugin Settings

**FR-IDs:** FR-008  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Build the SimHub settings tab where users configure Sim Steward. This is the control panel for the plugin -- OBS connection details, hotkey bindings, detection preferences, and replay offset. Without settings, every other feature uses hardcoded defaults.

## Acceptance Criteria

- [ ] Settings tab renders in SimHub's plugin settings area (WPF UserControl)
- [ ] OBS WebSocket settings: URL/port (default `ws://localhost:4455`), password field (masked)
- [ ] Hotkey settings: manual incident mark key, start/stop recording key
- [ ] Auto-detect toggle: enable/disable automatic incident detection (default: on)
- [ ] Replay offset: seconds before incident for replay jump (default: 5, range 0-30)
- [ ] Settings persist across SimHub restarts (saved to SimHub settings store or local file)
- [ ] Settings changes take effect immediately (no restart required)
- [ ] OBS connection test button: attempt connection with current settings, show result

## Subtasks

- [ ] Design WPF settings layout (grouped sections: OBS, Hotkeys, Detection, Replay)
- [ ] Implement settings model class with default values
- [ ] Wire settings persistence via SimHub's `ReadCommonSettings` / `SaveCommonSettings` or plugin-local JSON
- [ ] Build OBS connection section: URL, port, password inputs + test button
- [ ] Build hotkey section: key binding pickers for manual mark and recording
- [ ] Build detection section: auto-detect toggle
- [ ] Build replay section: offset slider/input with validation
- [ ] Bind all settings to the settings model; save on change
- [ ] Test: change settings, restart SimHub, verify settings persist

## Dependencies

- SCAFFOLD-Plugin-Foundation (settings tab extends the placeholder from scaffold)

## Notes

- SimHub provides settings persistence APIs. Prefer those over custom file I/O.
- The settings tab is WPF, not Dash Studio. Dash Studio is for overlays; settings use SimHub's built-in WPF hosting.
- Hotkey registration uses SimHub's `AddAction` API. The actual hotkey binding is done through SimHub's controls mapping, not custom key capture.
- This story defines the settings UI and persistence. Other stories (FR-005, FR-001, FR-004) consume these settings values.
