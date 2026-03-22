# Technical Requirements
## iRacing Replay Incident Index Capture via SDK Fast-Forward Sampling
**Version 0.2 — Draft**

---

## 1. Overview

This document defines the technical requirements for a test implementation that constructs a complete incident index from an iRacing replay file, operating without any iRacing `/data` REST API calls or iRacing account registration. Telemetry and replay control use the local SDK only.

The approach exploits the iRacing SDK broadcast command system to fast-forward a loaded replay at maximum speed while sampling the memory-mapped telemetry at 60Hz, detecting incident events in real time and recording their session timestamps and associated car indices.

**Grafana / Loki:** When the implementation runs as the SimSteward SimHub plugin (or reuses its logging stack), structured logs SHOULD be emitted to the same **Loki → Grafana** pipeline documented in [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) so index-build phases, detections, and validation outcomes are visible in Grafana Explore and dashboards. Loki push is **optional** and **off** unless `SIMSTEWARD_LOKI_URL` is configured; it does not replace the on-disk JSON index (TR-019).

---

## 2. Background & Constraints

### 2.1 Why Local-Only

The iRacing `/data` REST API provides per-lap incident flags server-side, but requires OAuth client credentials registered with iRacing. This approach targets a fully self-contained workflow where no internet connection, no API registration, and no user credentials are required beyond having iRacing installed and a replay loaded.

### 2.2 Key SDK Limitations Driving This Approach

- The incident index inside `.rpy` is a proprietary undocumented binary — it cannot be read directly
- `BroadcastReplaySearch(NextIncident)` has no per-car filtering — it searches globally across all cars
- `BroadcastReplaySearch` has a minimum ~2.5 second cooldown before reliably accepting another command
- All SDK broadcast commands are fire-and-forget with no return value — feedback is observation-only
- `CarIdxThrottlePct`, `CarIdxBrakePct`, `CarIdxClutchPct` were deliberately removed from the SDK

### 2.3 Signal Sources for Incident Detection

| Variable | Scope | Incident Signal |
|---|---|---|
| `PlayerCarMyIncidentCount` | Player car only | Increments on each incident point award. Delta from previous frame = new incident. |
| `CarIdxSessionFlags[n]` | All cars | `repair` (0x100000) or `furled` (0x80000) bit rising edge = confirmed incident for that car. |
| `CarIdxFastRepairsUsed[n]` | All cars | Value increment = damage confirmed. Less precise timing than flags. |
| `ReplaySessionTime` | Replay state | Current playback position in session seconds. Recorded at moment of detection. |
| `CamCarIdx` | Replay camera | Car the camera switches to after `NextIncident` seek. Used for post-seek car identification. |

### 2.4 Observability (Grafana)

Index-build telemetry for operators and research SHOULD appear in Grafana via **structured logs** ingested by Loki, using the project’s four-label schema and JSON body fields ([GRAFANA-LOGGING.md](GRAFANA-LOGGING.md)). **Do not** log every 60Hz poll; log **event-driven** milestones and each detected incident (similar volume to existing `incident_detected` guidance). Local stack setup: [observability-local.md](observability-local.md).

---

## 3. Test Objectives

1. A replay can be fast-forwarded programmatically via the SDK broadcast system
2. Incident events for all cars can be detected during fast-forward by monitoring `CarIdxSessionFlags`
3. `ReplaySessionTime` accurately captures the session timestamp of each detected incident
4. The resulting incident index matches the known incident record for the session (validated against final `Incidents` count from the YAML session string)
5. Total time to build a complete index for a full race replay is measured and recorded
6. When integrated with SimSteward logging, index-build lifecycle and incident detections are queryable in Grafana (Loki) without exceeding project volume and cardinality rules

---

## 4. Technical Requirements

### 4.1 SDK Connection

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-001 | MUST | Connect to the iRacing memory-mapped file (`Local\IRSDKMemMapFileName`) and verify `IsConnected` before beginning any replay operations. | SDK connection confirmed, `IsConnected = true`, session string readable. |
| TR-002 | MUST | Confirm the session is in replay mode by reading `WeekendInfo.SimMode = 'replay'` from the YAML session string before proceeding. | `SimMode` value logged and asserted as `'replay'`. |
| TR-003 | MUST | Read and store the `SubSessionID` from the YAML session string for use as a reference key for the resulting index. | `SubSessionID` logged and present in output. |

### 4.2 Baseline State Capture

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-004 | MUST | Before initiating fast-forward, seek the replay to the beginning using `BroadcastReplaySearch(ToStart)` and wait for `ReplayFrameNum` to stabilise at 0. | `ReplayFrameNum = 0` confirmed before fast-forward begins. |
| TR-005 | MUST | Capture a baseline snapshot of `CarIdxSessionFlags` for all car indices at frame 0 to correctly detect rising edges rather than false-triggering on flags present at session start. | Baseline flags array logged for all car indices. |
| TR-006 | MUST | Capture baseline value of `PlayerCarMyIncidentCount` at frame 0. | Baseline incident count logged. |
| TR-007 | SHOULD | Record the total frame count from `ReplayFrameNumEnd` for use in progress estimation. | `ReplayFrameNumEnd` logged. |

### 4.3 Fast-Forward Execution

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-008 | MUST | Initiate fast-forward by sending `BroadcastReplaySetPlaySpeed` with the maximum speed multiplier. Start at 16x and increase empirically to find the reliable ceiling. | Replay begins advancing at faster than real-time confirmed by `ReplaySessionTime` increasing. |
| TR-009 | MUST | Poll the SDK at the native 60Hz update rate for the duration of the fast-forward. Do not throttle the polling interval. | Polling confirmed at 60Hz, no frames skipped. |
| TR-010 | MUST | Monitor `IsReplayPlaying`. When it transitions to `false`, treat the replay as complete and stop sampling. | Completion correctly detected. No polling after replay ends. |
| TR-011 | SHOULD | Log elapsed wall-clock time from fast-forward start to completion for performance measurement. | Elapsed time recorded in test output. |

### 4.4 Incident Detection Logic

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-012 | MUST | On each 60Hz sample, compare current `CarIdxSessionFlags[n]` against previous frame for all car indices. Detect any frame where the `repair` bit (0x100000) transitions from 0 to 1. | Rising edge correctly detected. No false positives on pre-existing flags from baseline. |
| TR-013 | MUST | On each 60Hz sample, detect any frame where the `furled`/meatball bit (0x80000) transitions from 0 to 1 for any car index. | Rising edge correctly detected independently of repair bit detection. |
| TR-014 | MUST | On each 60Hz sample, compare current `PlayerCarMyIncidentCount` against previous value. Detect any frame where the count increments. | All increments detected. Delta value (1, 2, or 4 points) recorded. |
| TR-015 | MUST | At the moment of any detection, record the current `ReplaySessionTime` as the incident timestamp. | Timestamp within one frame (1/60s ≈ 16.7ms) of the actual incident moment. |
| TR-016 | MUST | At the moment of any detection, record the `carIdx` of the affected car. | `carIdx` correctly identifies the car involved in each incident. |
| TR-017 | SHOULD | Detect `CarIdxFastRepairsUsed[n]` increments as a secondary confirmation signal. Record separately and cross-reference with flag-based detections. | Fast repair increments logged and correlated with flag events where applicable. |
| TR-018 | MUST | Handle flag bit resets — when a repair or furled bit clears and later re-sets, treat the re-set as a new incident detection. Do not deduplicate within the same car unless within a 1-second window. | Multiple incidents on the same car correctly recorded as separate events. |

### 4.5 Output Format

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-019 | MUST | Produce a structured incident index as a JSON array upon completion. | Valid JSON file written to disk at test completion. |
| TR-020 | MUST | Each entry must contain: `carIdx` (int), `sessionTimeMs` (int), `detectionSource` (string: `repair_flag` \| `furled_flag` \| `player_incident_count`), `incidentPoints` (int or null). | All four fields present and correctly typed in every entry. |
| TR-021 | MUST | Entries must be sorted ascending by `sessionTimeMs`. | Output array is in chronological order. |
| TR-022 | SHOULD | Include a summary block: `subSessionId`, `totalRaceIncidents`, `incidentCountByCarIdx`, `indexBuildTimeMs`. | Summary block present and values correct. |

**Example output:**
```json
{
  "subSessionId": 12345678,
  "indexBuildTimeMs": 34200,
  "totalRaceIncidents": 22,
  "incidentCountByCarIdx": { "3": 2, "7": 1, "12": 4 },
  "incidents": [
    {
      "carIdx": 3,
      "sessionTimeMs": 184320,
      "detectionSource": "repair_flag",
      "incidentPoints": null
    },
    {
      "carIdx": 0,
      "sessionTimeMs": 312800,
      "detectionSource": "player_incident_count",
      "incidentPoints": 2
    }
  ]
}
```

### 4.6 Validation

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-023 | MUST | After index build, read `ResultsPositions` from the YAML session string and extract the final `Incidents` value per driver. | Per-driver final incident totals extracted correctly from YAML. |
| TR-024 | MUST | Cross-reference incident events detected per `carIdx` against final `ResultsPositions.Incidents` count. Log any discrepancies. | Discrepancy report produced. Zero discrepancies is the pass condition, but discrepancies are research data not an automatic failure. |
| TR-025 | SHOULD | Seek to each detected incident timestamp using `BroadcastReplaySearchSessionTime` and confirm via `CamCarIdx` that iRacing's camera switches to the expected car. Allow 2.5 seconds per seek. | Camera car matches expected `carIdx`. Match rate logged as a percentage. |

### 4.7 Grafana / Loki structured logging

Requirements align with [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md): **four labels only** (`app`, `env`, `component`, `level`); **no** high-cardinality values in labels (`subsession_id`, `car_idx`, correlation ids stay in the JSON body).

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-026 | SHOULD | When using the SimSteward plugin logging path, emit structured log lines for replay incident index build: at minimum `replay_incident_index_started` (or equivalent `event` name), `replay_incident_index_baseline_ready`, `replay_incident_index_fast_forward_started`, `replay_incident_index_fast_forward_complete`, `replay_incident_index_validation_summary`. | Corresponding events appear in Loki (if push enabled) and in `plugin-structured.jsonl` per project pipeline. |
| TR-027 | MUST | Do **not** emit structured logs on every 60Hz SDK sample. Sampling loops remain silent in Loki except for explicit detections and phase boundaries. | Zero per-tick log lines; volume consistent with § Volume budget in GRAFANA-LOGGING. |
| TR-028 | SHOULD | For each incident detected during fast-forward, emit one structured line with `event` discriminating replay index build (e.g. `replay_incident_index_detection`) including `car_idx`, `session_time_ms` or `replay_session_time`, `detection_source` (`repair_flag` \| `furled_flag` \| `player_incident_count`), `incident_points` when known, `subsession_id`, `replay_frame` when available, and the same session spine fields used elsewhere (`track_display_name`, `log_env`, `loki_push_target` where applicable). | Each JSON index entry has a traceable log line in Grafana for debugging and cross-check. |
| TR-029 | SHOULD | Log validation outcomes (TR-023–TR-025): discrepancy counts, camera seek match rate, and `index_build_time_ms` / `total_detected` summary fields in the body of `replay_incident_index_validation_summary` (or split events if size limits require). | Grafana Explore can filter by `subsession_id` in JSON and compare to file output. |
| TR-030 | MUST | If Loki URL is unset or push fails, the index build MUST still complete and write TR-019 JSON; logging failures MUST NOT abort the test. | Graceful degradation; behaviour matches existing Loki sink semantics. |

**Taxonomy note:** Register new `event` names in [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) when implemented so LogQL and dashboards stay canonical.

---

## 5. Non-Functional Requirements

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| NFR-001 | MUST | Index construction MUST NOT use the iRacing `/data` API or other iRacing cloud credentials. **Optional:** HTTPS POST to the configured Loki endpoint (`SIMSTEWARD_LOKI_URL`) for Grafana is permitted when enabled; with Loki **disabled**, no outbound observability traffic is required for the test to pass. | No iRacing REST/OAuth traffic. Loki traffic only when explicitly configured. |
| NFR-002 | MUST | The test must fail gracefully with a clear error message if the iRacing SDK is not connected. | Graceful failure message on no connection. |
| NFR-003 | SHOULD | Total index build time for a 45-minute race replay must be measured and documented. Target is under 120 seconds — observation not a pass/fail criterion. | Build time recorded in output regardless of duration. |
| NFR-004 | SHOULD | After completion, restore the replay to its original position using the saved `ReplayFrameNum` from before fast-forward began. | Replay position restored to pre-test position. |
| NFR-005 | MUST | Implement as a SimHub C# plugin OR standalone C# console application using IRSDKSharper or iRacingSdkWrapper. | Executable runs on Windows without additional runtime dependencies beyond .NET and iRacing. |
| NFR-006 | SHOULD | Document or reuse LogQL examples for the new `replay_incident_index_*` events (filter by `component`, `level`, and JSON `event` / `subsession_id` in line filters). | Queries reproducible from Grafana Explore; cross-ref [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) § LogQL. |

---

## 6. SDK Broadcast Command Reference

| Command | Parameters | Purpose |
|---|---|---|
| `BroadcastReplaySearch` | `irsdk_RpySrch_ToStart` | Seek to frame 0 before fast-forward |
| `BroadcastReplaySetPlaySpeed` | `speed` (int), `slowMotion` (bool = false) | Initiate fast-forward at maximum speed |
| `BroadcastReplaySetPlayPosition` | `irsdk_RpyPos_Begin`, `frameNumber` | Restore original playback position after test |
| `BroadcastReplaySearchSessionTime` | `sessionNum` (int), `sessionTimeMS` (int) | Validation: seek to detected incident timestamps |

---

## 7. Open Questions

- What is the maximum reliable play speed multiplier before the SDK starts dropping frames or returning stale `CarIdxSessionFlags` values?
- Does iRacing emit `repair`/`furled` flag bits reliably at high playback speeds, or are flag transitions dropped if the relevant frame is not rendered?
- Is `CarIdxSessionFlags` populated correctly during replay, or are these bits only set during real-time sessions? **Must be confirmed empirically.**
- How does `ReplaySessionTime` relate to `SessionTime` in the `/data` API lap records — same time base and units?
- Do incidents during caution laps, pit lane, or pre-race warm-up appear in `CarIdxSessionFlags` during replay?

---

## 8. Suggested Test Setup

To maximise test value, use a replay that satisfies the following:

- Full race session (not a clip) with a known incident count
- At least 3 different drivers with incidents (validates per-`carIdx` detection)
- At least one driver with multiple incidents on the same lap (validates deduplication logic)
- At least one DNF driver (`ReasonOutStr` populated)
- Ideally a race you participated in, so `PlayerCarMyIncidentCount` provides a second validation channel

---

*iRacing Replay Incident Index — Technical Requirements v0.2 — Draft*
