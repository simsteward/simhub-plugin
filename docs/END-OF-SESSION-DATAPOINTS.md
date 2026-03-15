# End-of-Session and Checkered-Flag Data — Full Data Point Catalog

This document is the result of deep research on **when** end-of-session data becomes available and **every data point** the iRacing SDK exposes at that moment. The goal is to **identify**, **capture**, and **log** every available data point when the user crosses the line or when data becomes available (checkered/cooldown or replay finalization).

## 1. When Is “End of Session” / Checkered?

### 1.1 SessionState (telemetry)

- **Source**: Live telemetry variable `SessionState` (int), 60 Hz.
- **Official SDK (bitfield, `irsdk_defines.h`)**:  
  `irsdk_StateInvalid` = 0x01, `StateGetInCar` = 0x02, `StateWarmup` = 0x04, `StateParadeLaps` = 0x08, `StateRacing` = 0x10, `StateCheckered` = 0x10, `StateCoolDown` = 0x10.  
  (Note: Racing/Checkered/CoolDown may share 0x10 in some docs; implementation can use a sequential index.)
- **Plugin convention**: Treats **checkered** as `SessionState >= 5` (e.g. 5 = checkered, 6 = cooldown). This matches a sequential state index used by some wrappers.
- **When data is fullest**: As soon as `SessionState` indicates checkered or cooldown, iRacing typically writes a new **session info YAML** with `ResultsPositions` populated. There can be a short delay (1–2 s); the plugin retries once after 2 s if the first capture sees empty results.

### 1.2 SessionInfoUpdate (YAML revision)

- **Source**: Telemetry variable `SessionInfoUpdate` (int). Increments whenever iRacing writes a new session string (YAML).
- **Role**: Gate for “should we re-parse YAML?”. End-of-session results appear in the YAML after an update that includes `ResultsPositions`; the plugin uses `ResultsPositionsCount > 0` as the readiness signal.

### 1.3 Triggers that run capture

| Trigger | When | Notes |
|--------|------|--------|
| **checkered** | First tick with `SessionState >= 5` (transition into checkered/cooldown) | Auto; may retry once at 2 s if results empty |
| **checkered_retry** | 2 s after checkered if first capture failed (ResultsPositions empty) | One retry only |
| **dashboard** | User clicks “Capture summary now” | Immediate attempt |
| **finalizeThenCapture** | Replay: pause → seek to end → capture when ready → restore | For replay when results not yet ready |
| **legacy** | Old code path | Same as dashboard |

---

## 2. Telemetry Variables at Capture Time (snapshot)

At the moment we call `TryCaptureAndEmitSessionSummary`, these live telemetry values are available (we snapshot where used):

| Variable | Type | Captured / Logged | Purpose |
|----------|------|-------------------|---------|
| `SessionState` | int | Yes (diagnostics + checkered_detected) | Checkered/cooldown detection |
| `SessionNum` | int | Yes (session num for selected session) | Which session’s results |
| `SessionInfoUpdate` | int | Yes (diagnostics) | YAML revision |
| `SessionFlags` | int | Yes (diagnostics) | Green/yellow/checkered/etc. (bitfield) |
| `SessionTime` | double | Yes (SessionTimeSec in summary) | Session time at capture |
| `ReplayFrameNum` | int | Available | Replay position (if replay) |
| `ReplayFrameNumEnd` | int | Available | Replay end frame |
| `ReplayPlaySpeed` | int | Available | Playback speed |
| `ReplaySessionNum` | int | Yes (replay session selection) | Replay session index |

All of the above can be included in an end-of-session “snapshot” log (see §5).

---

## 3. Session Info YAML — Every Data Point

When `SessionInfoUpdate` has advanced and `ResultsPositions` is non-empty, the following structures are available from `_irsdk.Data.SessionInfo` (IRSDKSharper).

### 3.1 WeekendInfo (session identity, track, options)

| Field | Type | Currently captured | Notes |
|-------|------|--------------------|------|
| TrackName | string | Yes | |
| TrackID | int | Yes | |
| TrackLength | string | No | e.g. "2.5 km" |
| TrackLengthOfficial | string | No | |
| TrackDisplayName | string | No | |
| TrackDisplayShortName | string | No | |
| TrackConfigName | string | Yes | |
| TrackCity | string | Yes | |
| TrackState | string | No | |
| TrackCountry | string | Yes | |
| TrackAltitude, TrackLatitude, TrackLongitude, TrackNorthOffset | string | No | |
| TrackNumTurns | int | No | |
| TrackPitSpeedLimit | string | No | |
| TrackType, TrackDirection | string | No | |
| TrackWeatherType, TrackSkies | string | No | |
| TrackSurfaceTemp, TrackAirTemp, TrackAirPressure | string | No | |
| TrackWindVel, TrackWindDir | string | No | |
| TrackRelativeHumidity, TrackFogLevel, TrackPrecipitation | string | No | |
| TrackCleanup, TrackDynamicTrack | int | No | |
| SeriesID | int | Yes | |
| SeasonID | int | Yes | |
| SessionID | int | Yes | |
| SubSessionID | int | Yes | |
| LeagueID | int | Yes | |
| Official | int | No | |
| RaceWeek | int | No | |
| EventType | string | Yes | |
| Category | string | Yes | |
| SimMode | string | Yes | |
| TeamRacing, MinDrivers, MaxDrivers | int | No | |
| NumCarClasses, NumCarTypes | int | No | |
| HeatRacing, QualifierMustStartRace | int | No | |
| **WeekendOptions** | object | No (partial below) | |
| WeekendOptions.IncidentLimit | string | No | **Capture** — steward rules |
| WeekendOptions.FastRepairsLimit | string | No | **Capture** — steward rules |
| WeekendOptions.GreenWhiteCheckeredLimit | string | No | Optional |
| WeekendOptions.CourseCautions, Restarts | string | No | Optional |
| WeekendOptions.NumStarters, StartingGrid | mixed | No | Optional |
| WeekendOptions.Date, TimeOfDay | string | No | Optional |

### 3.2 SessionInfo.Sessions[] (per-session results metadata)

| Field | Type | Currently captured | Notes |
|-------|------|--------------------|------|
| SessionNum | int | Yes | |
| SessionLaps | string | No | e.g. "50" or "Fixed" |
| SessionTime | string | No | e.g. "30 min" |
| SessionNumLapsToAvg | int | No | |
| SessionType | string | Yes | Race, Practice, Qualify, etc. |
| SessionName | string | No | **Capture** |
| SessionSubType | string | No | Optional |
| SessionSkipped, SessionRunGroupsUsed | int | No | |
| ResultsAverageLapTime | float | Yes | |
| ResultsNumCautionFlags | int | Yes | |
| ResultsNumCautionLaps | int | Yes | |
| ResultsNumLeadChanges | int | Yes | |
| ResultsLapsComplete | int | Yes | |
| ResultsOfficial | int | Yes | |
| **ResultsPositions** | list | Yes (full) | See §3.3 |
| ResultsFastestLap | list | No | Per-car fastest lap info |
| QualifyPositions | list | No | Qualifying order |

### 3.3 ResultsPositions[] (per-driver result row)

| Field | Type | Currently captured | Notes |
|-------|------|--------------------|------|
| Position | int | Yes | |
| ClassPosition | int | Yes | |
| CarIdx | int | Yes | |
| Lap | int | No | Last lap number |
| Time | float | No | Total time (e.g. race time) |
| FastestLap | int | Yes | Lap number of fastest lap |
| FastestTime | float | Yes | |
| LastTime | float | Yes | Last lap time |
| LapsLed | int | Yes | |
| LapsComplete | int | Yes | |
| JokerLapsComplete | int | No | **Capture** (oval/dirt) |
| LapsDriven | float | No | **Capture** |
| Incidents | int | Yes | Official incident points |
| ReasonOutId | int | No | **Capture** (DNF reason code) |
| ReasonOutStr | string | Yes | Running / Accident / etc. |

### 3.4 DriverInfo.Drivers[] (per-driver identity and stats)

We join to ResultsPositions by `CarIdx`. For each result row we currently take: UserName, CarNumber, CarClassShortName. Additional fields we can capture (for the result row or for logging):

| Field | Type | Currently captured | Notes |
|-------|------|--------------------|------|
| CarIdx | int | Yes | |
| UserName | string | Yes | |
| AbbrevName | string | No | **Capture** — short name |
| Initials | string | No | Optional |
| UserID | int | No | **Capture** — iRacing ID |
| TeamID, TeamName | int/string | No | **Capture** for team events |
| CarNumber | string | Yes | |
| CarNumberRaw | int | No | Optional |
| CarClassShortName | string | Yes | |
| CarClassID, CarID | int | No | Optional |
| IRating | int | No | **Capture** — useful for digest |
| LicLevel, LicSubLevel, LicString, LicColor | mixed | No | Optional |
| IsSpectator | int | No | We filter; can log |
| CurDriverIncidentCount | int | No | Redundant with ResultsPositions.Incidents at session end but useful to log |
| TeamIncidentCount | int | No | **Capture** for team races |
| ClubName, ClubID | string/int | No | Optional |
| DivisionName, DivisionID | string/int | No | Optional |

---

## 4. What We Must Do

1. **Capture**  
   - Add every “**Capture**” / “**Capture** (optional)” field above into `SessionSummary` and `DriverResult` (or a small extension object) so that the full snapshot is in memory and broadcast in `sessionComplete`.
2. **Log**  
   - Emit one structured log event at session end that includes **every captured data point** in a compact form (identifiers + session-level fields + per-driver result rows), without exceeding the 8 KB line limit (trim or summarize if needed).
3. **Do not**  
   - Log full raw YAML or full `PluginSnapshot` (project rule).  
   - Log per-tick; only at the single moment of successful session summary capture.

---

## 5. Events: `session_end_datapoints_session` and `session_end_datapoints_results`

- **Component**: `simhub-plugin`
- **When**: Immediately after a successful `TryCaptureAndEmitSessionSummary`, in addition to `session_summary_captured` and `session_digest`.
- **session_end_datapoints_session** (one per capture): Session metadata and telemetry snapshot only (no results array). Fields: trigger, session_id, session_num, sub_session_id, session_id_ir, series_id, season_id, track, track_length, session_name, session_laps, session_time_str, incident_limit, fast_repairs_limit, green_white_checkered_limit, num_caution_flags, num_caution_laps, num_lead_changes, total_laps_complete, average_lap_time, is_official, sim_mode, captured_at, session_time_sec, telemetry_* (SessionState, SessionNum, SessionInfoUpdate, SessionFlags, SessionTime, ReplayFrameNum, ReplayFrameNumEnd, ReplayPlaySpeed, ReplaySessionNum), results_driver_count.
- **session_end_datapoints_results** (one log line per chunk of 35 drivers): session_id, session_num, chunk_index (0-based), chunk_total, results_driver_count, results (array of driver rows with pos, car_idx, driver, abbrev, car, class, class_pos, laps, laps_led, fastest_time, fastest_lap, last_time, incidents, reason_out, reason_out_id, lap, time, joker_laps, laps_driven, user_id, team, irating, cur_incidents, team_incidents). Merge chunks by session_id and chunk_index for full table. See **docs/GRAFANA-LOGGING.md** for LogQL and dashboard merge patterns.
- **Budget**: Each line under 8 KB; 35 drivers per chunk keeps under budget.

---

## 6. References

- iRacing SDK telemetry: [sajax.github.io/irsdkdocs](https://sajax.github.io/irsdkdocs/) (SessionState, telemetry variables).
- iRacing YAML: same site, WeekendInfo, SessionInfo, DriverInfo.
- IRSDKSharper: [IRacingSdkSessionInfo.cs](https://github.com/mherbold/IRSDKSharper/blob/main/IRacingSdkSessionInfo.cs) — `WeekendInfoModel`, `SessionInfoModel`, `SessionModel`, `PositionModel`, `DriverModel`, `WeekendOptionsModel`.
- Project: `docs/GRAFANA-LOGGING.md` (event taxonomy), `docs/SESSION-DATA-AVAILABILITY.md` (when results are ready), `.cursor/rules/SimHub.mdc` (no per-tick logging, 8 KB line budget).
