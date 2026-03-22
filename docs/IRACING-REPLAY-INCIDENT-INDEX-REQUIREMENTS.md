# Technical Requirements
## iRacing Replay Incident Index Capture via SDK Fast-Forward Sampling
**Version 0.1 — Draft**

---

## 1. Overview

This document defines the technical requirements for a test implementation that constructs a complete incident index from an iRacing replay file, operating entirely on the local machine without any external API calls or iRacing account registration.

The approach exploits the iRacing SDK broadcast command system to fast-forward a loaded replay at maximum speed while sampling the memory-mapped telemetry at 60Hz, detecting incident events in real time and recording their session timestamps and associated car indices.

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

---

## 3. Test Objectives

1. A replay can be fast-forwarded programmatically via the SDK broadcast system
2. Incident events for all cars can be detected during fast-forward by monitoring `CarIdxSessionFlags`
3. `ReplaySessionTime` accurately captures the session timestamp of each detected incident
4. The resulting incident index matches the known incident record for the session (validated against final `Incidents` count from the YAML session string)
5. Total time to build a complete index for a full race replay is measured and recorded

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

---

## 5. Non-Functional Requirements

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| NFR-001 | MUST | The entire index-build process must operate without any network calls. | Network monitoring confirms zero outbound connections during test execution. |
| NFR-002 | MUST | The test must fail gracefully with a clear error message if the iRacing SDK is not connected. | Graceful failure message on no connection. |
| NFR-003 | SHOULD | Total index build time for a 45-minute race replay must be measured and documented. Target is under 120 seconds — observation not a pass/fail criterion. | Build time recorded in output regardless of duration. |
| NFR-004 | SHOULD | After completion, restore the replay to its original position using the saved `ReplayFrameNum` from before fast-forward began. | Replay position restored to pre-test position. |
| NFR-005 | MUST | Implement as a SimHub C# plugin OR standalone C# console application using IRSDKSharper or iRacingSdkWrapper. | Executable runs on Windows without additional runtime dependencies beyond .NET and iRacing. |

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

*iRacing Replay Incident Index — Technical Requirements v0.1 — Draft*
