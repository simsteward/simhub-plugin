# Telemetry Serialization

**FR-IDs:** FR-A-004, FR-A-005  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need the captured telemetry window to be compressed into a compact format so it can be sent to the AI backend quickly and cheaply -- without me noticing any delay or data loss.

## Acceptance Criteria

- [ ] Receives a pre-built `TelemetryWindow` (list of samples) from the Detection pipeline -- does NOT listen for triggers directly
- [ ] Serializes the window to CSV format matching `docs/tech/api-design.md` exactly
- [ ] Output includes metadata rows: `SessionNum`, `SessionTick`, `IncidentTime`
- [ ] Output includes header row: `Time,Speed,BrakePct,SteerPct,Gap,Overlap`
- [ ] Output includes one data row per sample in the window
- [ ] `SessionTick` and `SessionNum` values come from the `Incident` object (FR-A-005)
- [ ] Serialized string is stored on `Incident.SerializedPayload`
- [ ] Incident Status advances to `WaitingForPost` after serialization
- [ ] Output size is reasonable (~1200 rows for 60s at 20 Hz; validate against token budget)

## Subtasks

- [ ] Implement `CsvSerializer.Serialize(Incident incident)` returning CSV string
- [ ] Write metadata block (SessionNum, SessionTick, IncidentTime as key-value rows)
- [ ] Write header row per API contract column order
- [ ] Write data rows from `Incident.TelemetryWindow` samples
- [ ] Format numeric values to reasonable precision (e.g., 1 decimal for speed, 0 for percentages)
- [ ] Store result on `Incident.SerializedPayload`; advance Status
- [ ] Validate output against API contract example in `docs/tech/api-design.md`
- [ ] Document iRacing SDK var -> CSV column mapping decisions (cross-reference Buffer story notes)

## Dependencies

- FR-A-003-Incident-Detection (provides `Incident` with populated `TelemetryWindow`)
- API contract (`docs/tech/api-design.md`) for CSV schema

## Notes

- This component is a pure function: Incident in, CSV string out. No timers, no triggers, no network.
- The iRacing SDK var -> CSV column mapping is initially defined in the Buffer story (FR-A-001-002). This story consumes whatever the buffer recorded. If columns change, update both stories and the API contract.
- Token Diet (FR-A-009, Worker-side) constrains the format. The plugin must produce what the Worker expects to consume. The API contract is the single source of truth for the schema.
- Gap and Overlap columns may require special handling if the buffer stores raw SDK values that need normalization. Document any transformations.
