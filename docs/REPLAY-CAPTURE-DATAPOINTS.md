# Replay capture — capturable data points

Reference for what the plugin and iRacing SDK can capture during replay (and live). Use when designing snapshots, replay metadata, or telemetry blocks. See **docs/IRACING-SDK-DATAPOINTS-RESEARCH.md** for full SDK notes.

---

## 1. Per-car data: player vs other drivers

iRacing exposes **full telemetry** (throttle, brake, clutch, steering, speed, G-force, etc.) only for the **player (focused) car**. For other cars, only **CarIdx*** array variables are available.

| Source | Player car | Other cars (CarIdx arrays) | Notes |
|--------|------------|----------------------------|--------|
| **SessionTime** | Yes | — | Single value; replay position. |
| **ReplayFrameNum / ReplayFrameNumEnd** | Yes | — | Replay position. |
| **CarIdxLap** | — | Yes (array) | Lap number per car. |
| **CarIdxLapDistPct** | — | Yes (array) | Track position 0–1. |
| **CarIdxPosition** | — | Yes (array) | Race position. |
| **CarIdxClassPosition** | — | Yes (array) | Class position. |
| **CarIdxTrackSurface** | — | Yes (array) | On track / off / pit. |
| **CarIdxGear** | — | Yes (array) | Gear. |
| **CarIdxRPM** | — | Yes (array) | RPM. |
| **CarIdxSteer** | — | Yes (array) | Steering angle. |
| **CarIdxBestLapTime / CarIdxLastLapTime** | — | Yes (array) | Lap times. |
| **Throttle / Brake / Clutch** | Yes | **No** | Player-only in iRacing SDK. |
| **Steering (player)** | Yes | — | Player-only; other cars have CarIdxSteer. |
| **Speed, RPM, G-force (player)** | Yes | — | Player-only. |

**Implication:** We **cannot** capture throttle, brake, or clutch for other drivers (e.g. contact driver). We **can** capture for any car: position, lap, lap distance, gear, RPM, steering, track surface, and lap times via the CarIdx* arrays.

---

## 2. Session and roster data (YAML)

Available from SessionInfo when YAML is loaded (replay or live); does not require checkered or ResultsPositions:

- **WeekendInfo:** SessionID, SubSessionID, TrackDisplayName, TrackConfigName, Category, SimMode.
- **DriverInfo.Drivers[]:** CarIdx, UserName, CarNumber, AbbrevName, CarClassShortName, CurDriverIncidentCount, IsSpectator, TeamName (roster for the session).
- **SessionInfo.Sessions[]:** SessionNum, SessionType, SessionName, SessionLaps, SessionTime (practice, qualify, race list).

**ResultsPositions** (official results table) is populated only when iRacing has finalized the session (e.g. checkered or replay at session/replay end). Short clips without checkered will not have ResultsPositions until we seek to end (e.g. ReplaySeekToSessionEnd or FinalizeThenCaptureSessionSummary).

---

## 3. What we already capture (snapshot and state)

- **RecordSessionSnapshot:** replayFrameNum, replayFrameNumEnd, replayPlaySpeed, replaySessionNum, sessionDiagnostics; optionally playerCarIdx and replay metadata (WeekendInfo, driver roster, sessions list, incident feed) when implemented.
- **State (broadcast):** playerCarIdx, drivers[], incidents[], currentSessionTime, trackName, sessionId, metrics, diagnostics, sessionDiagnostics.
- **Session summary (when results ready):** results table, incident feed, session metadata via session_summary_captured, session_digest, session_end_datapoints_*.

---

## 4. Optional telemetry block for two drivers

When adding a telemetry block to a snapshot (e.g. for primary + contact driver), include:

- **Player car:** Throttle, Brake, Clutch, Speed, RPM, steering (and any other player-only vars needed).
- **Other car (by CarIdx):** From CarIdx* arrays: Lap, LapDistPct, Position, Gear, RPM, Steer, TrackSurface, BestLapTime, LastLapTime. No throttle/brake/clutch.

Keep each snapshot line under the 8 KB self-imposed limit (see **docs/GRAFANA-LOGGING.md**).
