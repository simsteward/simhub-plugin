# Telemetry Circular Buffer

**FR-IDs:** FR-A-001, FR-A-002  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a driver, I need the plugin to continuously record telemetry in the background so that when an incident happens, the last 60+ seconds of driving context are already captured and ready to analyze -- without me having to press anything beforehand.

## Acceptance Criteria

- [ ] A circular (ring) buffer runs continuously while iRacing is connected
- [ ] Buffer holds at least 90 seconds of telemetry at the configured sample rate (headroom beyond the 60s window)
- [ ] Oldest samples are silently overwritten when the buffer is full
- [ ] Each sample contains the fields defined in the API contract (`docs/tech/api-design.md`): Time, Speed, BrakePct, SteerPct, Gap, Overlap
- [ ] Each sample also stores `SessionTick` and `SessionNum` (FR-A-005 prerequisite)
- [ ] Buffer exposes `GetWindow(centerTick, preSeconds, postSeconds)` to extract a time-bounded slice
- [ ] Buffer does not block the `DataUpdate` loop (append is O(1))

## Subtasks

- [ ] Define `TelemetrySample` struct: Time, Speed, BrakePct, SteerPct, Gap, Overlap, SessionTick, SessionNum
- [ ] Define iRacing SDK variable -> CSV column mapping (see Notes for open questions)
- [ ] Implement ring buffer with fixed capacity based on sample rate and desired duration
- [ ] Implement `Append(sample)` called from `DataUpdate` on every tick
- [ ] Implement `GetWindow(centerTick, preSeconds, postSeconds)` returning `List<TelemetrySample>`
- [ ] Integrate with `DataUpdate` loop from Scaffold story
- [ ] Validate buffer wrap-around and window extraction with test data

## Dependencies

- SCAFFOLD-SimHub-Plugin

## Notes

- **Sample rate:** ~20 Hz (50ms) recommended per API contract. At 20 Hz, 90s = 1800 samples. Memory is trivial.
- **The buffer does not know about incidents.** It is always-on. Incident detection (FR-A-003) decides when to snapshot a window.
- **SDK column mapping -- open questions:**
  - `Speed`: likely `irsdk_Speed` or SimHub `GameRawData.Telemetry.Speed`
  - `BrakePct`: likely `irsdk_Brake` (0.0-1.0, scale to 0-100)
  - `SteerPct`: likely `irsdk_SteeringWheelAngle` (radians, normalize to -100..100)
  - `Gap`: gap to car ahead -- may require computation from `irsdk_CarIdxLapDistPct` array. Flag as spike if complex.
  - `Overlap`: overlap with car alongside -- may require computation from car position arrays. Flag as spike if complex.
  - Finalize mapping during implementation; update API contract if columns change.
