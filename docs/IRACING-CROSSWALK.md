# iRacing SDK Crosswalk — Three-Source Reference

> Comprehensive mapping of iRacing SDK telemetry fields across iRacing SDK (ground truth), CrewChief V4 (reference implementation), and SimSteward (this project).

## How to Read This Document

| Column | Meaning |
|--------|---------|
| **iRacing SDK Field** | Canonical field name from the iRacing SDK header (`irsdk_defines.h`) |
| **Type** | C# type. Arrays noted as `type[64]` (max 64 cars). |
| **Avail** | **L** = Live only, **R** = Replay only, **B** = Both live + replay, **Y** = YAML session string, **P** = Player car only |
| **CrewChief Usage** | How CrewChief V4 reads/uses the field (file reference in parentheses) |
| **SimSteward Usage** | How this project uses the field, or "not yet used" |
| **Notes** | Gotchas, limitations, cross-references |

**Verification rules:**
- Fields marked "B" have been confirmed readable in both live and replay contexts.
- Fields marked "P" return data only for the player's own car (other indices are 0 or -1).
- YAML properties are refreshed once per session transition, not per tick.

---

## 1. Position & Race Tracking

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| CarIdxLap | int[64] | B | Current lap per car (iRacingGameStateMapper — gap/position calc) | Incident index — lap context for detections | Resets to 0 when car not on track |
| CarIdxLapCompleted | int[64] | B | Laps completed per car (Position.cs — overtake detection) | not yet used | More reliable than CarIdxLap for results |
| CarIdxLapDistPct | float[64] | B | Track position 0.0-1.0 (iRacingSpotter — proximity, Position.cs — gaps) | Incident index — track position at detection | High frequency, 60 Hz |
| CarIdxPosition | int[64] | B | Overall position per car (Position.cs — standings) | Incident index — position context | 0 = not classified yet |
| CarIdxClassPosition | int[64] | B | Class position per car (Position.cs — multiclass gaps) | not yet used | 0 = not classified |
| CarIdxF2Time | float[64] | B | Time behind leader (Position.cs — gap calculations) | not yet used | Negative = laps down |
| CarIdxEstTime | float[64] | B | Estimated time around track (Position.cs — estimated gaps) | not yet used | Used for gap-to-car-ahead |
| PlayerCarPosition | int | B | Player overall position (iRacingGameStateMapper) | not yet used | Redundant with CarIdxPosition[PlayerCarIdx] |
| PlayerCarClassPosition | int | B | Player class position (iRacingGameStateMapper) | not yet used | Multiclass only |
| Lap | int | P | Player current lap (iRacingGameStateMapper) | not yet used | Player car only |
| LapCompleted | int | P | Player laps completed (iRacingGameStateMapper) | not yet used | |
| LapDist | float | P | Player distance around track in meters (iRacingGameStateMapper) | not yet used | Raw meters, not pct |
| LapDistPct | float | P | Player track position 0.0-1.0 (iRacingGameStateMapper) | Dashboard — player position display | Player-only version of CarIdxLapDistPct |
| RaceLaps | int | B | Laps completed by race leader (iRacingGameStateMapper) | not yet used | |
| CarIdxClass | int[64] | B | Car class ID per car (iRacingGameStateMapper — multiclass) | not yet used | Maps to YAML CarClassID |

---

## 2. Incidents & Contact Detection

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| PlayerCarMyIncidentCount | int | B | Player incident total (Penalties.cs — incident announcements) | ReplayIncidentIndexDetector — primary incident signal, delta triggers detection | Cumulative; diff between ticks = new incident |
| PlayerCarTeamIncidentCount | int | B | Team incident total (Penalties.cs — team penalty tracking) | not yet used | Team races only |
| PlayerCarDriverIncidentCount | int | B | Driver incident total (Penalties.cs) | not yet used | Multi-driver team context |
| CarIdxSessionFlags | bitField[64] | B | Not directly read by CrewChief (uses global SessionFlags) | ReplayIncidentIndexDetector — per-car flag state for incident classification | CRITICAL: per-car black/meatball flags |
| CarIdxTrackSurface | int[64] | B | Surface state per car (iRacingGameStateMapper — pit/off-track detection) | ReplayIncidentIndexDetector — off-track vs on-track decomposition | See Appendix A: TrackSurfaces enum |
| CarIdxTrackSurfaceMaterial | int[64] | B | Surface material per car (iRacingGameStateMapper — surface detail) | not yet used | Grass/gravel/sand distinction |
| Speed | float | P | Player speed m/s (DamageReporting.cs — impact detection) | Incident index — speed at time of incident | Meters per second |
| LatAccel | float | P | Lateral acceleration (DamageReporting.cs — impact G classification) | Incident index — lateral G at detection | m/s^2; divide by 9.81 for G |
| LongAccel | float | P | Longitudinal acceleration (DamageReporting.cs — braking/impact) | Incident index — longitudinal G at detection | Named LonAccel in some SDK versions |
| VertAccel | float | P | Vertical acceleration (DamageReporting.cs — airborne detection) | not yet used | Useful for flip/airborne |
| YawRate | float | P | Yaw rotation rate (DamageReporting.cs — spin detection) | Incident index — spin detection signal | rad/s; high values = spinning |
| CarIdxGear | int[64] | B | Gear per car (iRacingGameStateMapper) | Incident index — gear context (0=neutral, -1=reverse) | 0 during incidents suggests loss of control |

---

## 3. Flags & Session State

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| SessionFlags | bitField | B | Global flag state (FlagsMonitor.cs — flag announcements; checks Black, Furled, YellowWaving, Blue, White, Green, OneLapToGreen, StartSet, StartGo) | ReplayIncidentIndexDetector — session flag context | See Appendix A for all bits |
| SessionState | int | B | Session state enum (iRacingGameStateMapper — race phase detection) | Incident index — guards against non-racing states | See Appendix A: SessionStates |
| SessionNum | int | B | Active session index (iRacingGameStateMapper — session identification) | Incident index — session context for log correlation | 0-based index into Sessions[] YAML |
| SessionTime | double | B | Current session elapsed time in seconds (iRacingGameStateMapper) | Incident index — timestamp for detections | High precision double |
| SessionTick | int | B | Session tick counter (iRacingData.cs — freshness check) | not yet used | Monotonic per session |
| SessionTimeRemain | double | B | Time remaining in session (iRacingGameStateMapper — race end detection) | not yet used | -1 if unlimited |
| SessionLapsRemain | int | B | Laps remaining (iRacingGameStateMapper) | not yet used | -1 if unlimited |
| SessionLapsRemainEx | int | B | Laps remaining including final lap (iRacingGameStateMapper) | not yet used | More precise for finish detection |
| SessionTrackRubberState | int | B | Track rubber buildup state | not yet used | Affects grip |
| CarLeftRight | int | P | Spotter proximity (iRacingSpotter.cs — car-left/car-right announcements) | not yet used | See Appendix A: CarLeftRight enum |
| CarIdxPaceFlags | int[64] | B | Pace car flags per car (iRacingGameStateMapper — SC procedures) | not yet used | Safety car / pace procedures |
| CarIdxPaceLine | int[64] | B | Pace line assignment per car (iRacingGameStateMapper) | not yet used | Inside/outside on restart |
| CarIdxPaceRow | int[64] | B | Pace row assignment per car (iRacingGameStateMapper) | not yet used | Row number for restart grid |

---

## 4. Damage & Mechanical

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| PitRepairLeft | float | P | Mandatory repair time remaining (PitStops.cs — repair countdown) | not yet used | Seconds; 0 = no damage |
| PitOptRepairLeft | float | P | Optional repair time remaining (PitStops.cs — optional repair estimate) | not yet used | Seconds |
| CarIdxFastRepairsUsed | int[64] | B | Fast repairs consumed per car | not yet used | Limited per race |
| EngineWarnings | bitField | P | Engine warning flags (EngineMonitor.cs — checks WaterTemp, OilPressure, FuelPressure, EngineStalled, PitSpeedLimiter) | not yet used | See Appendix A: EngineWarnings |

---

## 5. Penalties & Disqualification

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| CarIdxSessionFlags | bitField[64] | B | (see also Section 2) Black/Disqualify/Furled/Repair flags per car (FlagsMonitor.cs uses global SessionFlags) | ReplayIncidentIndexDetector — black flag detection per car | Bit 0x00010000=Black, 0x00020000=DQ |
| PlayerCarMyIncidentCount | int | B | (see also Section 2) Threshold for penalty (Penalties.cs) | ReplayIncidentIndexDetector | Penalty threshold varies by series |
| PlayerCarTeamIncidentCount | int | B | Team total for DQ threshold (Penalties.cs) | not yet used | |

---

## 6. Pit Stops & Service

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| OnPitRoad | bool | P | Player on pit road (PitStops.cs — pit entry/exit detection) | not yet used | True from pit entry to exit |
| CarIdxOnPitRoad | bool[64] | B | Pit road status per car (PitStops.cs — opponent pit tracking) | not yet used | |
| PlayerCarInPitStall | bool | P | Player in pit stall (PitStops.cs — service active) | not yet used | True only when stationary in stall |
| PlayerCarTowTime | float | P | Tow countdown remaining (PitStops.cs — tow announcements) | not yet used | Seconds; 0 = no tow |
| IsOnTrack | bool | P | Player on track (iRacingGameStateMapper — track/pit/garage state) | not yet used | False in pits/garage |
| IsOnTrackCar | bool | P | Car is on track surface (iRacingGameStateMapper) | not yet used | Subtly different from IsOnTrack |
| IsInGarage | bool | P | Player in garage (iRacingGameStateMapper) | not yet used | True only in garage |

---

## 7. Tires & Brakes

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| RFcoldPressure | float | P | Right-front cold pressure (TyreMonitor.cs — pressure tracking) | not yet used | kPa |
| RFtempCL | float | P | RF temp center-left (TyreMonitor.cs — temp spread) | not yet used | Celsius |
| RFtempCM | float | P | RF temp center-mid (TyreMonitor.cs) | not yet used | |
| RFtempCR | float | P | RF temp center-right (TyreMonitor.cs) | not yet used | |
| RFwearL | float | P | RF wear left (TyreMonitor.cs — wear estimation) | not yet used | 0.0-1.0 (1.0 = new) |
| RFwearM | float | P | RF wear mid (TyreMonitor.cs) | not yet used | |
| RFwearR | float | P | RF wear right (TyreMonitor.cs) | not yet used | |
| LFcoldPressure | float | P | Left-front cold pressure (TyreMonitor.cs) | not yet used | kPa |
| LFtempCL | float | P | LF temp center-left (TyreMonitor.cs) | not yet used | |
| LFtempCM | float | P | LF temp center-mid (TyreMonitor.cs) | not yet used | |
| LFtempCR | float | P | LF temp center-right (TyreMonitor.cs) | not yet used | |
| LFwearL | float | P | LF wear left (TyreMonitor.cs) | not yet used | |
| LFwearM | float | P | LF wear mid (TyreMonitor.cs) | not yet used | |
| LFwearR | float | P | LF wear right (TyreMonitor.cs) | not yet used | |
| RRcoldPressure | float | P | Right-rear cold pressure (TyreMonitor.cs) | not yet used | kPa |
| RRtempCL | float | P | RR temp center-left (TyreMonitor.cs) | not yet used | |
| RRtempCM | float | P | RR temp center-mid (TyreMonitor.cs) | not yet used | |
| RRtempCR | float | P | RR temp center-right (TyreMonitor.cs) | not yet used | |
| RRwearL | float | P | RR wear left (TyreMonitor.cs) | not yet used | |
| RRwearM | float | P | RR wear mid (TyreMonitor.cs) | not yet used | |
| RRwearR | float | P | RR wear right (TyreMonitor.cs) | not yet used | |
| LRcoldPressure | float | P | Left-rear cold pressure (TyreMonitor.cs) | not yet used | kPa |
| LRtempCL | float | P | LR temp center-left (TyreMonitor.cs) | not yet used | |
| LRtempCM | float | P | LR temp center-mid (TyreMonitor.cs) | not yet used | |
| LRtempCR | float | P | LR temp center-right (TyreMonitor.cs) | not yet used | |
| LRwearL | float | P | LR wear left (TyreMonitor.cs) | not yet used | |
| LRwearM | float | P | LR wear mid (TyreMonitor.cs) | not yet used | |
| LRwearR | float | P | LR wear right (TyreMonitor.cs) | not yet used | |
| CarIdxTireCompound | int[64] | B | Tire compound per car | not yet used | Compound index; meaning varies by car |
| Brake | float | P | Brake pedal 0.0-1.0 (iRacingGameStateMapper) | not yet used | Player input |

---

## 8. Fuel & Consumption

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| FuelLevel | float | P | Fuel remaining in liters (Fuel.cs — fuel estimation) | not yet used | Liters |
| FuelLevelPct | float | P | Fuel remaining as fraction (Fuel.cs — low fuel warning) | not yet used | 0.0-1.0 |
| FuelUsePerHour | float | P | Fuel burn rate (Fuel.cs — laps remaining calc) | not yet used | Liters/hour |
| FuelPress | float | P | Fuel pressure (EngineMonitor.cs — fuel system health) | not yet used | kPa |

---

## 9. Engine & Powertrain

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| RPM | float | P | Engine RPM (iRacingGameStateMapper — rev limiter, stall detection) | Dashboard — RPM display | Player car only |
| CarIdxRPM | float[64] | B | RPM per car (iRacingGameStateMapper — opponent engine state) | Incident index — engine state context | 0 = engine off/stalled |
| Gear | int | P | Current gear (iRacingGameStateMapper) | Dashboard — gear display | -1=R, 0=N, 1-8=forward |
| Throttle | float | P | Throttle pedal 0.0-1.0 (iRacingGameStateMapper) | not yet used | Player input |
| Clutch | float | P | Clutch pedal 0.0-1.0 (iRacingGameStateMapper) | not yet used | 0=engaged, 1=disengaged |
| WaterTemp | float | P | Coolant temperature (EngineMonitor.cs — overheat warning) | not yet used | Celsius |
| WaterLevel | float | P | Coolant level (EngineMonitor.cs) | not yet used | Liters |
| OilTemp | float | P | Oil temperature (EngineMonitor.cs — oil overheat) | not yet used | Celsius |
| OilPress | float | P | Oil pressure (EngineMonitor.cs — low pressure warning) | not yet used | kPa |
| OilLevel | float | P | Oil level (EngineMonitor.cs) | not yet used | Liters |
| Voltage | float | P | Battery voltage (EngineMonitor.cs) | not yet used | Volts |
| EngineWarnings | bitField | P | Warning flags (EngineMonitor.cs — stall, overheat, limiter) | not yet used | See Appendix A |

---

## 10. Weather & Environment

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| TrackTemp | float | B | Track surface temperature (iRacingGameStateMapper) | not yet used | Celsius |
| TrackTempCrew | float | B | Track temp at pit crew location (iRacingGameStateMapper) | not yet used | May differ from TrackTemp |
| AirTemp | float | B | Ambient air temperature (iRacingGameStateMapper) | not yet used | Celsius |
| WeatherType | int | B | Weather mode (iRacingGameStateMapper) | not yet used | See Appendix A |
| Skies | int | B | Sky condition (iRacingGameStateMapper) | not yet used | See Appendix A |
| AirDensity | float | B | Air density (iRacingGameStateMapper) | not yet used | kg/m^3 |
| AirPressure | float | B | Barometric pressure (iRacingGameStateMapper) | not yet used | mmHg |
| WindVel | float | B | Wind speed (iRacingGameStateMapper) | not yet used | m/s |
| WindDir | float | B | Wind direction (iRacingGameStateMapper) | not yet used | Radians from north |
| RelativeHumidity | float | B | Humidity (iRacingGameStateMapper) | not yet used | 0.0-1.0 |

---

## 11. Camera & Replay Control

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| IsReplayPlaying | bool | B | Replay state detection (iRacingGameStateMapper — disables some logic in replay) | SimStewardPlugin — replay mode detection, guards live vs replay behavior | |
| ReplayFrameNum | int | R | Not read by CrewChief | ReplayIncidentIndexBuild — current replay frame position | **NOTE:** Plugin field name is inverted vs SDK; see docs/feedback_inverted_frame_names |
| ReplayFrameNumEnd | int | R | Not read by CrewChief | ReplayIncidentIndexBuild — total frame count (snapshot at frame 0) | **NOTE:** Plugin field name is inverted vs SDK; snapshot once, never re-read during sweep |
| ReplayPlaySpeed | int | R | Not read by CrewChief | ReplayIncidentIndexBuild — playback speed control/verification | Negative = reverse |
| ReplayPlaySlowMotion | int | R | Not read by CrewChief | not yet used | 1 = slow-mo active |
| ReplaySessionTime | double | R | Not read by CrewChief | ReplayIncidentIndexBuild — session time within replay | Matches SessionTime context in replay |
| ReplaySessionNum | int | R | Not read by CrewChief | not yet used | Which session is being replayed |
| CamCarIdx | int | B | Not read by CrewChief | SimStewardPlugin — camera target car, used to validate player car focus | Must verify == PlayerCarIdx before reads |
| CamGroupNumber | int | B | Not read by CrewChief | SimStewardPlugin — camera group selection for replay navigation | Alias: `CameraGroupNumber`. Corresponds to YAML CameraInfo.Groups[].GroupNum |
| CamCameraNumber | int | B | Not read by CrewChief | SimStewardPlugin — specific camera within group | |

---

## 12. Per-Car Array Summary

All `CarIdx*` arrays are indexed 0-63 (max 64 cars). Index corresponds to `CarIdx` in YAML DriverInfo.

| Array Field | Type | Section | Primary Use |
|---|---|---|---|
| CarIdxLap | int[64] | 1 | Current lap |
| CarIdxLapCompleted | int[64] | 1 | Completed laps |
| CarIdxLapDistPct | float[64] | 1 | Track position |
| CarIdxPosition | int[64] | 1 | Overall position |
| CarIdxClassPosition | int[64] | 1 | Class position |
| CarIdxF2Time | float[64] | 1 | Time behind leader |
| CarIdxEstTime | float[64] | 1 | Estimated lap time |
| CarIdxTrackSurface | int[64] | 2 | Surface state (on/off track) |
| CarIdxTrackSurfaceMaterial | int[64] | 2 | Surface material |
| CarIdxSessionFlags | bitField[64] | 2, 5 | Per-car flags |
| CarIdxOnPitRoad | bool[64] | 6 | Pit road status |
| CarIdxRPM | float[64] | 9 | Engine RPM |
| CarIdxGear | int[64] | 2 | Gear |
| CarIdxTireCompound | int[64] | 7 | Tire compound |
| CarIdxLastLapTime | float[64] | 13 | Last lap time |
| CarIdxBestLapTime | float[64] | 13 | Best lap time |
| CarIdxBestLapNum | int[64] | 13 | Best lap number |
| CarIdxP2P_Count | int[64] | 13 | Push-to-pass uses remaining |
| CarIdxP2P_Status | bool[64] | 13 | Push-to-pass active |
| CarIdxPaceFlags | int[64] | 3 | Pace/SC flags |
| CarIdxPaceLine | int[64] | 3 | Pace line assignment |
| CarIdxPaceRow | int[64] | 3 | Pace row |
| CarIdxFastRepairsUsed | int[64] | 4 | Fast repairs consumed |
| CarIdxClass | int[64] | 1 | Car class ID |
| CarIdxSteer | float[64] | 13 | Steering angle per car |

---

## 13. Player-Only Telemetry

| iRacing SDK Field | Type | Avail | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|---|
| Speed | float | P | Impact detection (DamageReporting.cs) | Incident index — speed at detection | m/s |
| LatAccel | float | P | Lateral G (DamageReporting.cs) | Incident index — lateral G | m/s^2 |
| LongAccel | float | P | Longitudinal G (DamageReporting.cs) | Incident index — longitudinal G | m/s^2 |
| VertAccel | float | P | Vertical G (DamageReporting.cs) | not yet used | m/s^2 |
| YawRate | float | P | Spin rate (DamageReporting.cs) | Incident index — spin detection | rad/s |
| Pitch | float | P | Car pitch angle (iRacingData.cs) | not yet used | Radians |
| Yaw | float | P | Car yaw angle (iRacingData.cs) | not yet used | Radians |
| Roll | float | P | Car roll angle (iRacingData.cs) | not yet used | Radians |
| SteeringWheelAngle | float | P | Steering input (iRacingGameStateMapper) | not yet used | Radians |
| SteeringWheelTorque | float | P | Force feedback torque (iRacingData.cs) | not yet used | Nm |
| CarIdxSteer | float[64] | B | Not read by CrewChief | not yet used | Per-car steering angle |
| Throttle | float | P | Throttle input (iRacingGameStateMapper) | not yet used | 0.0-1.0 |
| Brake | float | P | Brake input (iRacingGameStateMapper) | not yet used | 0.0-1.0 |
| Clutch | float | P | Clutch input (iRacingGameStateMapper) | not yet used | 0.0-1.0 |
| PlayerCarIdx | int | B | Identifies player car (iRacingGameStateMapper — all player-specific lookups) | SimStewardPlugin — identifies which CarIdx is the player | Constant per session |
| PlayerCarWeightPenalty | float | P | Ballast (iRacingGameStateMapper) | not yet used | kg |
| PlayerCarPowerAdjust | float | P | Power adjustment (iRacingGameStateMapper) | not yet used | Percentage |
| PlayerCarTowTime | float | P | Tow countdown (PitStops.cs) | not yet used | Seconds |
| PlayerCarInPitStall | bool | P | In pit stall (PitStops.cs) | not yet used | |
| PlayerTrackSurface | int | P | Player surface state (iRacingGameStateMapper) | not yet used | Same enum as CarIdxTrackSurface |
| PlayerTrackSurfaceMaterial | int | P | Player surface material (iRacingGameStateMapper) | not yet used | Same enum as CarIdxTrackSurfaceMaterial |
| DisplayUnits | int | B | Unit preference (iRacingGameStateMapper) | not yet used | 0=English, 1=Metric |
| DriverMarker | bool | P | Driver marker flag (iRacingData.cs) | not yet used | |
| PushToPass | bool | P | P2P active (iRacingData.cs) | not yet used | |
| CarIdxP2P_Count | int[64] | B | P2P uses remaining per car | not yet used | |
| CarIdxP2P_Status | bool[64] | B | P2P active per car | not yet used | |
| CarIdxLastLapTime | float[64] | B | Last lap time per car (Position.cs — pace comparison) | not yet used | Seconds; -1 = no lap |
| CarIdxBestLapTime | float[64] | B | Best lap time per car (Position.cs) | not yet used | Seconds |
| CarIdxBestLapNum | int[64] | B | Best lap number per car (Position.cs) | not yet used | |
| LapBestLap | int | P | Player best lap number (iRacingGameStateMapper) | not yet used | |
| LapBestLapTime | float | P | Player best lap time (iRacingGameStateMapper) | not yet used | Seconds |
| LapLastLapTime | float | P | Player last lap time (iRacingGameStateMapper) | not yet used | Seconds |
| LapCurrentLapTime | float | P | Player current lap elapsed (iRacingGameStateMapper) | not yet used | Seconds |

---

## 14. Session YAML Properties

| YAML Path | Type | CrewChief Usage | SimSteward Usage | Notes |
|---|---|---|---|---|
| WeekendInfo.SubSessionID | int | Session identification (iRacingGameStateMapper) | Incident index — subsession_id for log correlation | Unique per race instance |
| WeekendInfo.SessionID | int | Parent session ID (iRacingGameStateMapper) | Incident index — parent_session_id for log correlation | Groups subsessions |
| WeekendInfo.TrackDisplayName | string | Track name (iRacingGameStateMapper — track-specific logic) | Incident index — track_display_name in logs | Human-readable |
| WeekendInfo.TrackDisplayShortName | string | Short track name (iRacingGameStateMapper) | not yet used | Abbreviation |
| WeekendInfo.TrackLength | string | Track length (iRacingGameStateMapper — fuel/gap calc) | not yet used | Format: "3.70 km" |
| WeekendInfo.SimMode | string | Sim mode (iRacingGameStateMapper) | not yet used | "full" for normal racing |
| WeekendInfo.EventType | string | Event type (iRacingGameStateMapper) | not yet used | "Race", "Practice", etc. |
| WeekendInfo.HeatRacing | int | Heat racing flag (iRacingGameStateMapper) | not yet used | 0 or 1 |
| DriverInfo.DriverCarIdx | int | Player car index (iRacingGameStateMapper) | not yet used | Same as PlayerCarIdx telemetry |
| DriverInfo.Drivers[].CarIdx | int | Car index mapping (iRacingGameStateMapper — all driver lookups) | not yet used | Index into CarIdx* arrays |
| DriverInfo.Drivers[].UserName | string | Driver name (Position.cs — announcements) | not yet used | |
| DriverInfo.Drivers[].TeamName | string | Team name (iRacingGameStateMapper) | not yet used | |
| DriverInfo.Drivers[].CarNumber | string | Car number (iRacingGameStateMapper) | not yet used | String, not int (can be "07") |
| DriverInfo.Drivers[].IRating | int | iRating (iRacingGameStateMapper — skill context) | not yet used | |
| DriverInfo.Drivers[].LicLevel | int | License level (iRacingGameStateMapper) | not yet used | |
| DriverInfo.Drivers[].LicString | string | License string (iRacingGameStateMapper) | not yet used | e.g., "A 4.99" |
| DriverInfo.Drivers[].CarClassID | int | Class ID (iRacingGameStateMapper — multiclass) | not yet used | Maps to CarIdxClass |
| DriverInfo.Drivers[].CarScreenName | string | Car model name (iRacingGameStateMapper) | not yet used | |
| Sessions[].SessionNum | int | Session index (iRacingGameStateMapper) | Incident index — session_num correlation | Matches telemetry SessionNum |
| Sessions[].SessionType | string | Session type (iRacingGameStateMapper) | not yet used | "Race", "Qualify", "Practice" |
| Sessions[].SessionLaps | string | Session lap count (iRacingGameStateMapper) | not yet used | "unlimited" or number |
| Sessions[].SessionTime | string | Session time limit (iRacingGameStateMapper) | not yet used | Format: "3600.0000 sec" |
| Sessions[].ResultsPositions[].Position | int | Final position (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].ClassPosition | int | Final class position (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].CarIdx | int | Car index (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].Lap | int | Laps at finish (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].LapsLed | int | Laps led (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].LapsDriven | int | Laps driven (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].LapsComplete | int | Laps completed (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].Time | float | Finish time (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].FastestTime | float | Fastest lap (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].FastestLap | int | Fastest lap number (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsPositions[].Incidents | int | Total incidents (Penalties.cs — race results) | not yet used | Cumulative for session |
| Sessions[].ResultsPositions[].ReasonOutId | int | Reason out (iRacingGameStateMapper) | not yet used | See Appendix A: ReasonOutId |
| Sessions[].ResultsPositions[].ReasonOutStr | string | Reason out text (iRacingGameStateMapper) | not yet used | Human-readable |
| Sessions[].ResultsFastestLap.CarIdx | int | Fastest lap car (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsFastestLap.FastestLap | int | Fastest lap number (iRacingGameStateMapper) | not yet used | |
| Sessions[].ResultsFastestLap.FastestTime | float | Fastest time (iRacingGameStateMapper) | not yet used | |
| CameraInfo.Groups[].GroupNum | int | Camera group ID | SimStewardPlugin — camera group lookup for replay nav | Maps to CamGroupNumber |
| CameraInfo.Groups[].GroupName | string | Camera group name | SimStewardPlugin — human-readable camera selection | e.g., "Cockpit", "TV1" |

---

## Appendix A: Enum Definitions

### TrackSurfaces (CarIdxTrackSurface / PlayerTrackSurface)

| Value | Name | Meaning |
|---|---|---|
| -1 | NotInWorld | Car not spawned / disconnected |
| 0 | OffTrack | Off racing surface |
| 1 | InPitStall | In assigned pit stall |
| 2 | AproachingPits | On pit road approaching stall |
| 3 | OnTrack | On racing surface |

### TrackSurfaceMaterial (CarIdxTrackSurfaceMaterial / PlayerTrackSurfaceMaterial)

| Value | Name |
|---|---|
| -1 | SurfaceNotInWorld |
| 0 | UndefinedMaterial |
| 1 | Asphalt1 |
| 2 | Asphalt2 |
| 3 | Asphalt3 |
| 4 | Asphalt4 |
| 5 | Concrete1 |
| 6 | Concrete2 |
| 7 | RacingDirt1 |
| 8 | RacingDirt2 |
| 9 | Paint1 |
| 10 | Paint2 |
| 11 | Rumble1 |
| 12 | Rumble2 |
| 13 | Rumble3 |
| 14 | Rumble4 |
| 15 | Grass1 |
| 16 | Grass2 |
| 17 | Grass3 |
| 18 | Grass4 |
| 19 | Dirt1 |
| 20 | Dirt2 |
| 21 | Dirt3 |
| 22 | Dirt4 |
| 23 | Sand |
| 24 | Gravel1 |
| 25 | Gravel2 |
| 26 | Grasscrete |
| 27 | Astroturf |

### SessionStates

| Value | Name | Meaning |
|---|---|---|
| 0 | Invalid | Session not active |
| 1 | GetInCar | Waiting for drivers |
| 2 | Warmup | Warmup period |
| 3 | ParadeLaps | Formation/pace laps |
| 4 | Racing | Green flag racing |
| 5 | Checkered | Checkered flag |
| 6 | CoolDown | Cool-down lap |

### SessionFlags (bitField)

| Bit | Hex | Name | Meaning |
|---|---|---|---|
| 0 | 0x00000001 | Checkered | Checkered flag |
| 1 | 0x00000002 | White | White flag (final lap) |
| 2 | 0x00000004 | Green | Green flag |
| 3 | 0x00000008 | Yellow | Yellow flag |
| 4 | 0x00000010 | Red | Red flag |
| 5 | 0x00000020 | Blue | Blue flag (faster car approaching) |
| 6 | 0x00000040 | Debris | Debris on track |
| 7 | 0x00000080 | Crossed | Crossed flags |
| 8 | 0x00000100 | YellowWaving | Yellow waving (local yellow) |
| 9 | 0x00000200 | OneLapToGreen | One lap to green restart |
| 10 | 0x00000400 | GreenHeld | Green held (pace car still out) |
| 11 | 0x00000800 | TenToGo | 10 laps to go |
| 12 | 0x00001000 | FiveToGo | 5 laps to go |
| 13 | 0x00002000 | RandomWaving | Random waving flag |
| 14 | 0x00004000 | Caution | Full course caution |
| 15 | 0x00008000 | CautionWaving | Caution waving |
| 16 | 0x00010000 | Black | Black flag (per-car via CarIdxSessionFlags) |
| 17 | 0x00020000 | Disqualify | Disqualification |
| 18 | 0x00040000 | Servicible | Can be serviced (note: typo in SDK) |
| 19 | 0x00080000 | Furled | Furled black flag (warning) |
| 20 | 0x00100000 | Repair | Mandatory repair required |
| 28 | 0x10000000 | StartHidden | Start lights hidden |
| 29 | 0x20000000 | StartReady | Start lights ready |
| 30 | 0x40000000 | StartSet | Start lights set |
| 31 | 0x80000000 | StartGo | Start lights go |

### EngineWarnings (bitField)

| Bit | Hex | Name | CrewChief Check |
|---|---|---|---|
| 0 | 0x01 | WaterTempWarning | EngineMonitor.cs — overheat announcement |
| 1 | 0x02 | FuelPressureWarning | EngineMonitor.cs — fuel system alert |
| 2 | 0x04 | OilPressureWarning | EngineMonitor.cs — oil pressure alert |
| 3 | 0x08 | EngineStalled | EngineMonitor.cs — stall detection |
| 4 | 0x10 | PitSpeedLimiter | iRacingGameStateMapper — pit limiter active |
| 5 | 0x20 | RevLimiterActive | iRacingGameStateMapper — rev limiter |

### CarLeftRight

| Value | Name | Meaning |
|---|---|---|
| 0 | Off | Spotter disabled |
| 1 | Clear | No cars nearby |
| 2 | CarLeft | Car on left |
| 3 | CarRight | Car on right |
| 4 | CarLeftRight | Cars on both sides |
| 5 | 2CarsLeft | Two cars on left |
| 6 | 2CarsRight | Two cars on right |

### WeatherType

| Value | Name |
|---|---|
| 0 | Constant |
| 1 | Dynamic |

### Skies

| Value | Name |
|---|---|
| 0 | Clear |
| 1 | PartlyCloudy |
| 2 | MostlyCloudy |
| 3 | Overcast |

### ReasonOutId

| Value | Name |
|---|---|
| 0 | NotOut |
| 1 | DidNotStart |
| 2 | BrakeFailure |
| 3 | CoolantLeak |
| 4 | RadiatorProblem |
| 5 | EngineFailure |
| 6 | EngineHeader |
| 7 | EngineValve |
| 8 | EnginePiston |
| 9 | EngineGearbox |
| 10 | EngineClutch |
| 11 | EngineCamshaft |
| 12 | EngineIgnition |
| 13 | EngineFire |
| 14 | EngineElectrical |
| 15 | FuelLeak |
| 16 | FuelInjector |
| 17 | FuelPump |
| 18 | FuelLine |
| 19 | OilLeak |
| 20 | OilLine |
| 21 | OilPump |
| 22 | OilPressure |
| 23 | SuspensionFailure |
| 24 | TirePuncture |
| 25 | TireProblem |
| 26 | WheelProblem |
| 27 | Accident |
| 28 | Retired |
| 29 | Disqualified |
| 30 | NoFuel |
| 31 | BrakeLine |
| 32 | LostConnection |
| 33 | Ejected |

### PitSvFlags (bitField)

| Bit | Hex | Name | Meaning |
|---|---|---|---|
| 0 | 0x0001 | LFTireChange | Left-front tire change |
| 1 | 0x0002 | RFTireChange | Right-front tire change |
| 2 | 0x0004 | LRTireChange | Left-rear tire change |
| 3 | 0x0008 | RRTireChange | Right-rear tire change |
| 4 | 0x0010 | FuelFill | Fuel fill |
| 5 | 0x0020 | WindshieldTearoff | Windshield tearoff |
| 6 | 0x0040 | FastRepair | Fast repair |

---

## Appendix B: Permanently Removed Fields

| Field | Reason | Alternative |
|---|---|---|
| CarIdxThrottlePct | Deliberately removed by iRacing (competitive fairness) | None — will not return |
| CarIdxBrakePct | Deliberately removed by iRacing (competitive fairness) | None — will not return |
| CarIdxClutchPct | Deliberately removed by iRacing (competitive fairness) | None — will not return |
| Per-incident point values (other cars) | Never exposed in SDK | Only PlayerCarMyIncidentCount is available |
| Per-incident timestamps (other cars) | Not in SDK or REST API | Must detect via telemetry delta observation |
| .rpy file direct parsing | Proprietary undocumented binary format | Use replay playback via SDK commands |

---

## Appendix C: CrewChief File Index

| Domain | CrewChief File (relative to CrewChiefV4/CrewChiefV4/) | Key Responsibilities |
|---|---|---|
| Telemetry definitions | iRacing/iRacingData.cs | Reads all telemetry fields from shared memory |
| Telemetry to game state | iRacing/iRacingGameStateMapper.cs | Maps raw telemetry to normalized game state; checks SessionFlags bits, EngineWarnings bits |
| Enum definitions | iRacing/Enums.cs | TrackSurface, SessionState, flag enums |
| Spotter/proximity | iRacing/iRacingSpotter.cs | Proximity detection using world coordinates |
| Flag state machine | Events/FlagsMonitor.cs | Yellow/blue/black flag announcements, flag transitions |
| Damage classification | Events/DamageReporting.cs | Impact detection from speed/accel changes, damage severity |
| Pit stops | Events/PitStops.cs | Pit window, mandatory stops, pit countdown, tow time |
| Fuel estimation | Events/Fuel.cs | Fuel consumption per lap, laps remaining, low fuel warning |
| Penalties/incidents | Events/Penalties.cs | Incident count tracking, penalty type handling |
| Tire monitoring | Events/TyreMonitor.cs | Tire wear/temp/condition, compound tracking |
| Engine health | Events/EngineMonitor.cs | Engine temp warnings, oil/water/fuel pressure |
| Position/overtakes | Events/Position.cs | Gap tracking, overtake detection, position change |
| Game state model | GameState/GameStateData.cs | Normalized game state shared across all events |
| Opponent data | GameState/OpponentData.cs | Per-opponent tracking data structure |
| Session data | GameState/SessionData.cs | Session type, phase, timing data structure |
