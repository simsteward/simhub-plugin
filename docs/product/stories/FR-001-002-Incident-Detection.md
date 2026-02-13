# Incident Detection

**FR-IDs:** FR-001, FR-002  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Detect incidents during an iRacing session. Two triggers: (1) automatic detection when `PlayerCarTeamIncidentCount` increases, and (2) a manual hotkey for the driver to mark "incident happened now." Both produce an incident record with the session timestamp so the user can later jump to replay and clip it.

## Acceptance Criteria

- [ ] Auto-detection fires when `PlayerCarTeamIncidentCount` increases between DataUpdate ticks
- [ ] Each detected incident records: session timestamp (`SessionTime`), session number, incident delta (0x/1x/2x/4x), detection method (auto/manual)
- [ ] Manual mark hotkey triggers an incident record with current `SessionTime`
- [ ] Hotkey is configurable (default: user-friendly key TBD in FR-008)
- [ ] Incidents within a short window (e.g., 5 seconds) are merged, not duplicated
- [ ] Incident events are published internally (event/callback) so other components (overlay, log) can subscribe
- [ ] Detection works at any SimHub DataUpdate rate (10Hz free, 60Hz licensed)

## Subtasks

- [ ] Track `PlayerCarTeamIncidentCount` across DataUpdate ticks; fire event on delta > 0
- [ ] Create `IncidentRecord` model (timestamp, sessionNum, delta, source, id)
- [ ] Implement in-memory incident list (`List<IncidentRecord>`) with add/clear on session change
- [ ] Add debounce/merge logic: if new incident fires within N seconds of previous, merge into one record
- [ ] Register SimHub hotkey action for manual incident mark
- [ ] Publish `OnIncidentDetected` event for other plugin components to subscribe
- [ ] Unit test: simulate incident count changes, verify correct records created
- [ ] Unit test: verify merge logic for rapid-fire incidents

## Dependencies

- SCAFFOLD-Plugin-Foundation (plugin must load and read telemetry)

## Notes

- iRacing consolidates rapid incidents itself (spin -> wall = single 4x), so most rapid chains are one delta. The merge logic handles the edge case where iRacing reports two separate increases close together.
- `PlayerCarTeamIncidentCount` captures all team incidents on team cars. Acceptable for protest clipping.
- **0x incidents clarification:** iRacing labels some light-contact incidents as "0x" severity, but these still increment `PlayerCarTeamIncidentCount` by at least 1. Detection via `delta > 0` captures all incidents including 0x-labeled ones. The "0x" refers to iRacing's severity classification, not a zero-valued delta.
- Clear the incident list on session change (`SessionNum` changes or iRacing disconnects).
