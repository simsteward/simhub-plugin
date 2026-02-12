# Main Plugin Tab: Incident List, Report View, and Visual Grading

**FR-IDs:** FR-A-012, FR-A-014  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need a desktop dashboard inside SimHub where I can see every incident from my session at a glance -- color-coded by fault -- and drill into any one to read the full AI ruling, timeline, and a ready-to-paste protest statement.

## Acceptance Criteria

### Incident List
- [ ] Main plugin tab displays "Master Incident List" showing all incidents in the current session
- [ ] Each list item shows: incident time/lap, severity (0x/1x/2x/4x or Manual), and visual grade icon
- [ ] List updates in real-time as new incidents are detected and ruled

### Visual Grading (FR-A-014)
- [ ] Each incident displays a color-coded icon based on the ruling verdict
- [ ] Red circle: Opponent At Fault (`OpponentAtFault`)
- [ ] Yellow circle: Racing Incident (`RacingIncident`)
- [ ] Skull icon: Player At Fault (`PlayerAtFault`)
- [ ] Grey/neutral icon: Pending (no ruling yet) or Error state
- [ ] Verdict-to-icon mapping uses `Incident.Ruling.verdict` from API contract response schema

### Detailed Report View
- [ ] Selecting an incident shows the full report in an HTML-rendered detail pane
- [ ] Report displays: Short Summary, Detailed Report (timeline), Ruling, Protest Statement
- [ ] Report fields map to API contract response schema fields
- [ ] Pending incidents show a loading/waiting state with "Analyzing..." message
- [ ] Error incidents show the error message with option to retry

### Protest Statement
- [ ] "Copy to Clipboard" button copies the protest statement text for pasting into iRacing
- [ ] Visual confirmation when copied (e.g., button text changes to "Copied!")

### States
- [ ] Empty state: "No incidents this session" when list is empty
- [ ] Session reset: List clears when a new iRacing session starts

## Subtasks

- [ ] Create main tab UI layout: incident list (left/top) + detail pane (right/bottom)
- [ ] Implement incident list binding to `IncidentStore` data
- [ ] Implement verdict-to-icon mapping (Red/Yellow/Skull/Grey)
- [ ] Add visual grade icons to list items
- [ ] Implement Detailed Report View with HTML rendering
- [ ] Map `StewardResponse` fields to report display sections
- [ ] Implement pending/loading state for incidents awaiting ruling
- [ ] Implement error state with error message display
- [ ] Add "Copy to Clipboard" button for protest statement
- [ ] Handle empty session state
- [ ] Handle session reset (clear list on new session)
- [ ] Follow `simhub-dashboard.mdc` conventions for plugin tab HTML/CSS

## Dependencies

- FR-A-003-Incident-Detection (Incident model and IncidentStore -- incidents exist to display)
- API contract response schema (`docs/tech/api-design.md`) for report field mapping
- `simhub-dashboard.mdc` for UI conventions

## Notes

- The main tab does NOT depend on FR-A-006 (POST) as a build dependency. It can display incidents in Pending state before any POST completes. POST is a runtime dependency only.
- FR-A-014 is merged here because grading IS how incidents are displayed. Building the list without grading would mean rebuilding it later.
- SimHub plugin tabs use HTML rendered inside the desktop shell. Keep HTML/CSS self-contained per `simhub-dashboard.mdc`.
- Color values per `simhub-dashboard.mdc`: Red (#E53935), Yellow (#FDD835), Skull (#424242).
