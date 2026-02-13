# Incident Log

**FR-IDs:** FR-009  
**Priority:** Should  
**Status:** Ready  
**Created:** 2026-02-13

## Description

A persistent in-session list of all detected incidents, displayed in the plugin's settings tab (WPF). Users can browse incidents, see timestamps and severity, and select any incident to jump to replay. This complements the in-game replay overlay (FR-003) by providing a desktop-accessible view of the full incident history -- useful between sessions or when not in replay mode.

## Acceptance Criteria

- [ ] All detected incidents (auto and manual) appear in a scrollable list
- [ ] Each entry shows: timestamp (formatted as mm:ss or hh:mm:ss), severity (Nx), source (auto/manual)
- [ ] Selecting an incident and clicking "Jump to Replay" triggers replay jump (FR-004)
- [ ] List clears on session change (new race/qualifying)
- [ ] List updates in real-time as new incidents are detected
- [ ] Most recent incident is highlighted or at the top of the list

## Subtasks

- [ ] Add incident log UI to the settings tab (WPF ListView or DataGrid)
- [ ] Bind list to the in-memory incident list from FR-001-002
- [ ] Format timestamps for display (raw seconds → mm:ss)
- [ ] Add "Jump to Replay" button per incident row (or single button for selected row)
- [ ] Wire jump action to FR-004 replay control
- [ ] Handle session change: clear list, reset state
- [ ] Test: generate multiple incidents, verify all appear, jump to each

## Dependencies

- FR-001-002-Incident-Detection (provides incident data)
- FR-004-Replay-Control (provides jump-to-replay action)

## Notes

- This is a "Should" priority per the PRD. It's valuable but not critical for the core clip workflow.
- The incident log lives in the WPF settings tab, not the in-game overlay. Drivers use this between sessions or after switching to replay mode.
- Consider also exposing incident data as SimHub properties so users could build custom Dash Studio dashboards showing the log. Low priority enhancement.
