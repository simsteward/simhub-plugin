# In-Game Overlay

**FR-IDs:** FR-A-013  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need a small transparent overlay on screen while I'm racing that shows me the plugin status, my last few incidents with their fault color, and lets me mark an incident without leaving the sim -- so I stay informed without alt-tabbing.

## Acceptance Criteria

- [ ] Overlay renders as a transparent HUD over the game
- [ ] Overlay shows plugin status: "Connected", "Buffering", "Ready", "Error"
- [ ] Overlay shows the last 3 incidents as a compact list (time, severity, grade icon)
- [ ] Grade icons use the same verdict-to-color mapping as the main tab (Red/Yellow/Skull/Grey)
- [ ] Overlay includes a "Mark" button or visual indicator that the manual trigger action is available
- [ ] **Show/hide toggle:** User can toggle overlay visibility via a SimHub action ("SimSteward.ToggleOverlay")
- [ ] Overlay follows SimHub overlay/Dash Studio positioning and sizing conventions
- [ ] Overlay is non-intrusive: minimal screen real estate, high contrast for readability at a glance

## Subtasks

- [ ] Create overlay layout: status bar, last 3 incidents list, Mark indicator
- [ ] Bind status to plugin connection state (iRacing connected, buffer active, etc.)
- [ ] Bind last 3 incidents from `IncidentStore` with grade icons
- [ ] Wire Mark button/indicator to "SimSteward.MarkIncident" action (same as FR-A-003)
- [ ] Register "SimSteward.ToggleOverlay" action for show/hide
- [ ] Integrate with SimHub overlay system or Dash Studio per `simhub-dashboard.mdc`
- [ ] Design for readability: large fonts, high contrast, minimal elements
- [ ] Test overlay does not obstruct critical driving view areas

## Dependencies

- FR-A-003-Incident-Detection (Incident model, IncidentStore, MarkIncident action)
- FR-A-012-014-Main-Tab-Incident-List (verdict-to-icon mapping is defined there; overlay reuses it)

## Notes

- The "Mark" button on an overlay while driving raises a UX question: the user is holding a steering wheel. In practice, the SimHub action "SimSteward.MarkIncident" is mapped to a physical button/key, so the overlay Mark indicator is informational ("press X to mark") rather than a clickable button. The actual trigger is the same hotkey from FR-A-003.
- Overlay format depends on SimHub's overlay system. See `simhub-dashboard.mdc` and Dash Studio documentation.
- The overlay consumes the same `IncidentStore` and verdict mapping as the main tab -- no separate data path.
