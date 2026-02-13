# iRacing SDK Investigation

> Spike results for incident detection, replay broadcast, camera enumeration, and incident consolidation behavior.
>
> **Last updated:** 2026-02-13

---

## Key Telemetry Variables

Sources: [iRacing SDK Docs](https://sajax.github.io/irsdkdocs/), NickThissen/iRacingSdkWrapper, SimHub `GameRawData.Telemetry`.

| Variable | Type | Usage |
|----------|------|-------|
| `SessionTime` | double (seconds) | Incident timestamp for replay jump |
| `SessionNum` | int | Session identifier (race, qualifying, etc.) |
| `PlayerCarTeamIncidentCount` | int | Incident detection -- monitor delta |
| `IsReplayPlaying` | bool | Detect replay mode vs live racing |

**SimHub access pattern:** `pluginManager.GetPropertyValue("DataCorePlugin.GameRawData.Telemetry.{Var}")` in DataUpdate.

---

## SimHub DataUpdate Rate

SimHub's `DataUpdate` callback fires at `1/fps` intervals:

| SimHub License | DataUpdate Rate |
|----------------|----------------|
| Free (unlicensed) | **10 FPS** (10Hz) |
| Licensed | **Up to 60 FPS** (60Hz) |

**Incident detection runs on every DataUpdate tick** -- we never want to miss an incident count change.

---

## Incident Detection Variable

**`PlayerCarTeamIncidentCount`** -- confirmed in iRacingSdkWrapper `TelemetryInfo.cs`. Matches iRacing results page. On team cars, captures all team incidents.

Also available: `PlayerCarMyIncidentCount` (personal only), `PlayerCarDriverIncidentCount` (current stint). Use `PlayerCarTeamIncidentCount` for Sim Steward.

**SimHub access:** `DataCorePlugin.GameRawData.Telemetry.PlayerCarTeamIncidentCount`

**0x incidents:** iRacing labels some light-contact incidents as "0x" severity, but these still increment `PlayerCarTeamIncidentCount`. Detection via delta > 0 captures all incidents including 0x-labeled ones.

---

## iRacing Incident Consolidation

When multiple incidents occur in quick succession, iRacing only counts the highest-scoring one. Example: a spin (2x) followed immediately by wall contact (4x) produces a single 4x, displayed in-game as "2x -> 4x." The exact time window for "quick succession" is undocumented.

**Impact on detection:** Most rapid-fire chains will appear as a single `PlayerCarTeamIncidentCount` increase. The plugin's debounce only needs to handle the edge case where iRacing reports two separate increases close together (e.g., 5-15 seconds apart) that are part of the same racing event. This informs the merge logic in FR-001-002: when two triggers fire within a short window, they merge rather than creating separate incident records.

**This consolidation also applies across drivers.** If two drivers make light contact (0x) and one subsequently makes heavy contact (4x), the other driver's incident also escalates to 4x.

---

## Replay Broadcast (Confirmed GREEN)

**API:** `irsdk_BroadcastReplaySearchSessionTime` (enum value 12). Takes `sessionNum` (var1) + `sessionTimeMS` as 32-bit int (var2).

**Access:** Via `iRSDKSharp.dll` (ships with SimHub). NickThissen wrapper exposes `SetPosition` and `Jump` but NOT `BroadcastReplaySearchSessionTime` directly. Use lower-level `sdk.BroadcastMessage()`.

**Implementation:** Instantiate separate lightweight `iRSDKSharp.iRacingSDK` instance for broadcast only. Uses Windows `SendMessage`/`PostMessage` -- does not conflict with SimHub's data connection.

**Target:** `(incident.SessionTime - offsetSeconds) * 1000`, clamped to 0. Default offset is 5-10 seconds before the incident (configurable in FR-008 settings, range 0-30s).

**Fallback:** If broadcast fails, display "Go to replay at {time}" for manual navigation.

---

## Camera Enumeration (Spike Needed)

**Status:** Not yet validated. Spike required before FR-010-011 implementation.

**Approach:** Parse iRacing session info YAML to extract camera group names and IDs. Camera groups include: "Nose", "Chase", "Far Chase", "Helicopter", "TV1", etc.

**API:** `irsdk_broadcastMsg` `CamSwitchNum` takes car number + camera group + camera number. For the player's car, use the player's car index.

**Risk:** Camera availability varies by track. The plugin must refresh the camera list when a new session starts.

**Details:** See FR-010-011-Camera-Control story for acceptance criteria.
