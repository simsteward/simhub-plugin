# Incident Detection and Capture

**FR-IDs:** FR-A-003  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need the plugin to automatically detect when I receive an incident penalty (0x, 1x, 2x, 4x) and also let me manually mark an incident with a button press, so that every on-track incident is captured for review -- even ones the game's penalty system misses.

## Acceptance Criteria

- [ ] **Auto detection:** Incident fires when `PlayerCarTeamIncidentCount` increases between ticks
- [ ] **Severity captured:** The delta (0x, 1x, 2x, 4x) is recorded on the Incident object
- [ ] **Manual detection:** User can map a SimHub action/hotkey ("SimSteward.MarkIncident") to trigger manually
- [ ] **Post-incident delay:** After trigger, the system waits 30 seconds before snapshotting the buffer so the 60s window (30s pre + 30s post) is complete
- [ ] **Buffer snapshot:** After the 30s delay, `TelemetryBuffer.GetWindow(triggerTick, 30, 30)` is called and the resulting window is passed to serialization
- [ ] **Incident model created:** Each incident is stored as an `Incident` object in the session list (see Data Model below)
- [ ] **Multiple incidents:** Overlapping incidents (new trigger during 30s wait) are tracked independently
- [ ] **Non-blocking:** Detection and the 30s timer do not block the `DataUpdate` loop
- [ ] **Debouncing:** Rapid same-tick triggers (e.g., 0x+4x in one frame) produce one incident, not two

## Data Model: Incident

Every story downstream depends on this model. Defined here because Detection is where incidents are born.

| Field | Type | Description |
|-------|------|-------------|
| Id | string/GUID | Unique identifier |
| Timestamp | DateTime | Wall-clock time of trigger |
| SessionTick | int | iRacing session tick at trigger |
| SessionNum | int | iRacing session number |
| IncidentType | enum | `Auto_0x`, `Auto_1x`, `Auto_2x`, `Auto_4x`, `Manual` |
| Severity | int | Incident point value (0, 1, 2, 4) or -1 for manual |
| TelemetryWindow | List of TelemetrySample | Populated after 30s delay; null until then |
| SerializedPayload | string | CSV payload; null until serialized |
| Ruling | StewardResponse | Parsed JSON response; null until ruled |
| Status | enum | `Detected`, `WaitingForPost`, `Sending`, `Ruled`, `Error` |
| ErrorMessage | string | Error detail if Status is Error; null otherwise |

## Subtasks

- [ ] Monitor `PlayerCarTeamIncidentCount` in `DataUpdate`; detect delta and compute severity
- [ ] Register SimHub action "SimSteward.MarkIncident" for manual trigger
- [ ] On trigger: create `Incident` with Status=Detected, start 30s async timer
- [ ] After 30s: call `TelemetryBuffer.GetWindow()`, set TelemetryWindow, advance Status to WaitingForPost
- [ ] Pass completed Incident to serialization pipeline (FR-A-004-005)
- [ ] Store Incident in session-scoped `IncidentStore` list
- [ ] Expose `SimSteward.IncidentCount` and `SimSteward.LastIncidentSummary` as SimHub properties
- [ ] Handle overlapping incidents (second trigger during first's 30s wait)
- [ ] Debounce rapid triggers within a configurable window (e.g., 500ms)

## Dependencies

- FR-A-001-002-Telemetry-Buffer (provides `GetWindow`)

## Notes

- The 30s post-incident delay is critical. Without it, the serialized window would contain 30s of pre-incident data and 0s of post-incident data.
- Manual trigger is useful for contact without penalty (e.g., unsafe rejoin, off-track that wasn't penalized).
- The `IncidentStore` is an in-memory list scoped to the current session. It resets when a new session starts.
- Downstream stories (Serialization, POST, UI) all consume the `Incident` model defined here.
