# iRacing Data Availability Reference

**Purpose:** Source of truth for which iRacing data exists **where** (SDK telemetry, session YAML, REST `/data`) and **when** (live race, replay, post-checkered / post-results). For variable access patterns and plugin mapping, see [IRACING-TELEMETRY.md](IRACING-TELEMETRY.md).

---

## Group 1 — Live Race Only (not available in replay)

| Variable | Notes |
|---|---|
| `PlayerCarMyIncidentCount` | Running total for your own car only. **Not per-car for others.** |
| `PlayerCarDriverIncidentCount` | Team driver's current incident count. |
| `ChanLatency` / `ChanAvgLatency` / `ChanClockSkew` | Network comms data. Meaningless in replay context. |

**Risks & Gaps:**

- No per-car incident count for other drivers during the live race — this is the most significant gap in the entire data model
- `PlayerCarMyIncidentCount` gives you a running total only — not the per-incident breakdown (no timestamp, no point value per incident)
- Incident point severity (1x/2x/4x) is not exposed at all during live sessions

---

## Group 2 — Live Race + Replay (all cars)

| Variable | Notes |
|---|---|
| `CarIdxLapDistPct` | Track position as % of lap. Core positioning data. |
| `CarIdxLap` | Current lap number. |
| `CarIdxLapCompleted` | Completed laps. |
| `CarIdxLastLapTime` | Last lap time per car. |
| `CarIdxBestLapTime` / `CarIdxBestLapNum` | Best lap per car. |
| `CarIdxPosition` / `CarIdxClassPosition` | Race position. iRacing only updates at S/F line crossing. |
| `CarIdxEstTime` / `CarIdxF2Time` | Gap to leader / fastest lap time. |
| `CarIdxRPM` | Engine RPM per car. |
| `CarIdxGear` | Current gear per car. |
| `CarIdxSteer` | Steering wheel angle (radians) per car. |
| `CarIdxOnPitRoad` | Boolean, on pit road. |
| `CarIdxTrackSurface` / `CarIdxTrackSurfaceMaterial` | Surface type and material under each car. |
| `CarIdxSessionFlags` | Per-car flag bitfield — black, repair, furled, disqualify etc. **Key incident signal.** |
| `CarIdxFastRepairsUsed` | Count of fast repairs used per car. Increments on confirmed damage. |
| `CarIdxTireCompound` | Current tire compound per car. |
| `CarIdxP2P_Count` / `CarIdxP2P_Status` | Push-to-pass state per car. |
| `CarIdxClass` | Car class ID per car. |
| `CarIdxPaceFlags` / `CarIdxPaceLine` / `CarIdxPaceRow` | Pacing state during formation/SC. |
| `SessionFlags` | Global race flags — green, yellow, caution, red, etc. |
| `SessionTime` / `SessionState` / `SessionNum` | Session clock and state. |
| `CamCarIdx` | Which car the camera is currently following. |
| `SessionTrackRubberState` | Track rubber progression. |

**Risks & Gaps:**

- `Throttle`, `Brake`, `Clutch` per car were deliberately removed from the SDK (`CarIdxThrottlePct` etc.) — **permanently unavailable** for other cars
- `CarIdxRPM`, `CarIdxGear`, `CarIdxSteer` are available but represent a thin slice of what's needed to reconstruct driving inputs
- Speed must be **derived** by differentiating `CarIdxLapDistPct × track length` — not directly emitted
- `CarIdxPosition` is only updated at the start/finish line — mid-lap position must be inferred from `CarIdxLapDistPct`
- `CarIdxSessionFlags` repair/furled bits are the best available incident signal for other cars, but it is not confirmed whether these bits are reliably populated at high replay playback speeds — **empirical testing required**

---

## Group 3 — Player Car Only (live + replay, your car only)

| Variable | Notes |
|---|---|
| `Throttle` / `Brake` / `Clutch` | Full pedal inputs at 60Hz. |
| `Speed` | Direct speed output. |
| `RPM` / `Gear` / `SteeringWheelAngle` | Higher fidelity than CarIdx equivalents. |
| All tire data | Temps, pressures, wear per corner. |
| `FuelLevel` / `FuelUsePerHour` | Fuel state. |
| All suspension data | Ride height, shock deflection, shock velocity per corner. |
| `LatAccel` / `LongAccel` / `VertAccel` | G-force channels. |
| All `dc*` in-car adjustments | Brake bias, TC, ABS, diff, MGU-K etc. |
| `PlayerCarTowTime` / `PlayerCarInPitStall` | Pit and tow state. |
| `PlayerCarWeightPenalty` / `PlayerCarPowerAdjust` | Balance of performance modifiers. |
| `PlayerTrackSurface` / `PlayerTrackSurfaceMaterial` | Surface under player car. |
| `WaterTemp` / `OilTemp` / `OilPressure` | Engine health. |
| `SteeringWheelTorque` | FFB torque. Available at 360Hz if enabled. |

**Fidelity note:** For the player's own car, every variable listed here is higher fidelity than the equivalent `CarIdx` array value. `RPM` vs `CarIdxRPM[n]`, `SteeringWheelAngle` vs `CarIdxSteer[n]` — same data, but the player-specific variables are confirmed full-precision while CarIdx values are broadcast-grade approximations.

**Risks & Gaps:**

- Entirely unavailable for any other car — no exceptions
- If you are spectating or have switched camera to another car in replay, these values still reflect **your** car's last known state, not the car being viewed

---

## Group 4 — Replay Only (not available during live race)

| Variable | Notes |
|---|---|
| `IsReplayPlaying` | Boolean playback state. |
| `ReplayFrameNum` / `ReplayFrameNumEnd` | Current frame and total frame count. |
| `ReplayPlaySpeed` / `ReplayPlaySlowMotion` | Current playback speed and slow-mo state. |
| `ReplaySessionTime` / `ReplaySessionNum` | Current playback position in session time. **Key for incident seeking.** |

**Risks & Gaps:**

- `ReplaySessionTime` is the only way to correlate replay position to real-world session timestamps — essential for `BroadcastReplaySearchSessionTime` seeks
- Frame rate of the replay may not be 1:1 with original session at high playback speeds — unconfirmed whether SDK samples remain accurate

---

## Group 5 — Post-Checkered / Results (YAML session string, populated after race ends)

Available via the SDK session string once results are official. Also available via the iRacing `/data` REST API (see Group 6).

| Field | Notes |
|---|---|
| `ResultsPositions[n].Incidents` | **Final incident count per driver.** Not available during the live race per-car. |
| `ResultsPositions[n].ReasonOutStr` / `ReasonOutId` | DNF reason per car — "Contact", "Mechanical", "Disconnected" etc. |
| `ResultsPositions[n].LapsLed` | Laps led per driver. |
| `ResultsPositions[n].LapsDriven` | Float — includes partial laps. |
| `ResultsPositions[n].FastestTime` / `FastestLap` | Best lap per driver. |
| `ResultsPositions[n].LapsComplete` | Completed laps per driver. |
| `ResultsPositions[n].Time` | Total race time per driver. |
| `ResultsPositions[n].Position` / `ClassPosition` | Final official finishing positions. |
| `ResultsFastestLap` | Who set the overall fastest lap and when. |
| `ResultsAverageLapTime` | Session average. |
| `ResultsNumCautionFlags` / `ResultsNumCautionLaps` | Caution summary. |
| `ResultsNumLeadChanges` | Total lead changes. |
| `ResultsOfficial` | Whether the session was officially scored. |

**Fidelity vs REST API:** The YAML session string and the REST API `/data` endpoint draw from the same source. **The REST API has higher fidelity** — it provides per-lap granularity (`Incident: bool` per lap, `SessionTime` per lap) whereas the YAML session string only provides the final cumulative totals.

**Risks & Gaps:**

- `Incidents` is a cumulative integer — no breakdown by incident type, point value, or timestamp
- `ReasonOutStr` strings are iRacing-defined and not guaranteed to be consistent across versions
- The YAML session string only updates when results are posted — in a replay loaded before the race finished, this data may be absent
- No per-incident timestamp anywhere in this data set — only lap-level granularity via the REST API

---

## Group 6 — iRacing REST API `/data` (post-race, requires OAuth)

Entirely separate from the SDK. Accessed via HTTP against iRacing's servers using a registered OAuth client. Requires internet connection and iRacing account credentials.

| Endpoint | Data |
|---|---|
| `GetSingleDriverSubsessionLapsAsync` | Per-driver, per-lap: `LapNumber`, `LapTime`, `SessionTime` (lap end), `SessionStartTime` (lap start), `Incident` (bool), `FlagsRaw`, `LapEvents` |
| `GetSubSessionLapChartAsync` | Same as above plus interval to leader, lap position — all drivers in one call |
| `GetSubsessionEventLogAsync` | Session event log — likely contains incident events with finer granularity than lap boundaries |
| `GetSubSessionResultAsync` | Full session results including per-driver totals |

**Fidelity vs YAML session string:** REST API is **higher fidelity** for incident data. Provides per-lap incident flags with lap start/end timestamps, vs YAML which only provides cumulative totals.

**Fidelity vs SDK fast-forward approach:** REST API provides **lap-level** incident timestamps (start/end of the lap the incident occurred on). The SDK fast-forward approach can provide **frame-level** timestamps (~16.7ms precision) but requires iRacing to be running with the replay loaded.

**Risks & Gaps:**

- Requires OAuth client registration with iRacing — not zero-friction for end users
- Legacy authentication (username/password direct) was removed December 9, 2025 — OAuth is now mandatory
- `Incident: true` on a lap means an incident occurred somewhere in that lap — not the exact moment
- `GetSubsessionEventLogAsync` contents are not fully publicly documented — exact incident event schema is unknown
- Rate limited by iRacing — bulk enumeration of many sessions may be throttled
- Only accessible after the race is complete and results are posted — no live access

---

## Group 7 — Permanently Unavailable

| Variable | Reason |
|---|---|
| `CarIdxThrottlePct` / `CarIdxBrakePct` / `CarIdxClutchPct` | Existed in SDK, deliberately removed by iRacing. Will not return. |
| Per-incident point value for other cars | Never exposed at any layer of the SDK or API |
| Per-incident timestamp for other cars | Not in SDK, not in REST API — only lap-level granularity available |
| `.rpy` file direct parsing | Proprietary undocumented binary. No public reverse-engineering. |
| Other drivers' tire temps / fuel / suspension | Player-only variables, no broadcast equivalent |

---

## Summary — Fidelity Comparison Where Data Overlaps

| Data Point | SDK (live/replay) | REST API | Higher Fidelity |
|---|---|---|---|
| Incident count per driver | Cumulative total only (YAML, post-race) | Per-lap boolean flag | **REST API** |
| Incident timestamp | Frame-level via fast-forward (~17ms) | Lap start/end boundaries only | **SDK fast-forward** |
| Lap times per driver | `CarIdxLastLapTime` (live) | Full lap history with timestamps | **REST API** |
| Final results / positions | YAML session string | `GetSubSessionResultAsync` | Equal |
| DNF reason | `ReasonOutStr` in YAML | `GetSubSessionResultAsync` | Equal |
| Player car inputs | Full 60Hz telemetry | Not available | **SDK** |
| Other car position | `CarIdxLapDistPct` at 60Hz | Lap-level granularity | **SDK** |

---

*Last updated from research conducted March 2026. SDK variable availability subject to change with iRacing updates.*
