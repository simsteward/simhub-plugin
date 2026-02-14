# Sim Steward Status NOC Overlay Seed

Use these properties in Dash Studio to build a live status panel:

- `SimSteward.Status.Plugin.State`
- `SimSteward.Status.Plugin.LastStatusChangeUtc`
- `SimSteward.Status.Plugin.LastHeartbeatUtc`
- `SimSteward.Status.iRacing.IsConnected`
- `SimSteward.Status.iRacing.State`
- `SimSteward.Status.iRacing.GameName`
- `SimSteward.Status.Telemetry.UpdateCount`
- `SimSteward.Status.Telemetry.SessionTime`
- `SimSteward.Status.Telemetry.SessionNum`
- `SimSteward.Status.Telemetry.IncidentCount`
- `SimSteward.Status.OBS.State`
- `SimSteward.Status.Incident.State`
- `SimSteward.Status.Recording.State`
- `SimSteward.Status.Replay.State`
- `SimSteward.Status.Error.HasError`
- `SimSteward.Status.Error.LastMessage`

Recommended indicator color mapping:

- Green: `Running`, `Connected`, `Active`
- Yellow: `Starting`, `Waiting`
- Orange: `Warning`
- Red: `Error`
- Gray: `NotConfigured`, `Disabled`, `Disconnected`, `Shutdown`

Suggested first dashboard rows:

1. Plugin Runtime
2. iRacing Connection
3. Telemetry counters (UpdateCount, SessionTime, IncidentCount)
4. Feature readiness (OBS, Incident, Recording, Replay)
5. Error lane (`HasError` + `LastMessage`)

## Wiring / Data Contract (What’s real vs placeholder)

This overlay is designed to be *fully driven* by SimHub plugin properties under `SimSteward.Status.*`.
The plugin publishes these values every `DataUpdate()` tick.

### Runtime lane (real)

- `SimSteward.Status.Plugin.State`
	- Source: `StatusManager.PluginState` (`Starting` → `Running` → `Shutdown` or `Error`).
	- Feed point: lifecycle in `SimStewardPlugin.Init()` / `End()` and `StatusManager.SetError()`.
- `SimSteward.Status.Plugin.LastStatusChangeUtc`
	- Source: `StatusManager` internal timestamp updated whenever a *stateful* status changes.
- `SimSteward.Status.Plugin.LastHeartbeatUtc`
	- Source: `StatusManager.RecordHeartbeat()` called during publish.
	- Meaning: “the plugin is alive and publishing” (not “iRacing is connected”).

### iRacing lane (real)

- `SimSteward.Status.iRacing.IsConnected`
	- Source: `StatusManager.IsIRacingConnected`.
	- Feed point: `SimStewardPlugin.DataUpdate()` calls `UpdateConnectionState(IsIRacingGame(GameName))`.
- `SimSteward.Status.iRacing.State`
	- Source: `StatusManager.IRacingConnection` (`Connected` / `Disconnected` / `Error`).
- `SimSteward.Status.iRacing.GameName`
	- Source: `PluginManager.GameName` (always published, even when not iRacing).
	- Intended use: gives immediate operator context for “why disconnected” (e.g. wrong sim running).

### Telemetry lane (real when iRacing connected)

- `SimSteward.Status.Telemetry.UpdateCount`
	- Source: increments each successful telemetry sample.
- `SimSteward.Status.Telemetry.SessionTime`
	- Source: SimHub property `DataCorePlugin.GameRawData.Telemetry.SessionTime`.
- `SimSteward.Status.Telemetry.SessionNum`
	- Source: SimHub property `DataCorePlugin.GameRawData.Telemetry.SessionNum`.
- `SimSteward.Status.Telemetry.IncidentCount`
	- Source: SimHub property `DataCorePlugin.GameRawData.Telemetry.PlayerCarTeamIncidentCount`.

If iRacing is not connected, telemetry values remain at last known/zeroed values and `UpdateCount` stops increasing.

### Feature readiness lane (placeholder by design)

These are intentionally exposed *now* so the overlay/UI can be finalized and later feature modules can “plug in” by updating a single state enum.
Today, the plugin sets these to `NotConfigured` during init.

- `SimSteward.Status.OBS.State` → `StatusManager.ObsState`
- `SimSteward.Status.Incident.State` → `StatusManager.IncidentDetectionState`
- `SimSteward.Status.Recording.State` → `StatusManager.RecordingState`
- `SimSteward.Status.Replay.State` → `StatusManager.ReplayState`

**How to feed these when the feature exists:**

- Any future OBS/Incident/Recording/Replay module should set the corresponding `StatusManager.*State` to one of:
	- `NotConfigured` (missing required settings/credentials)
	- `Waiting` (configured but waiting on prerequisites: iRacing/OBS connection, session start)
	- `Active` (operational)
	- `Warning` (degraded but still running)
	- `Error` (feature failed)
	- `Disabled` (intentionally off)

The values are published on every plugin tick via `PublishStatusProperties()`; updating the `StatusManager` property is enough for the overlay to reflect it.

### Error lane (real)

- `SimSteward.Status.Error.HasError`
	- Source: computed (`LastMessage` non-empty).
- `SimSteward.Status.Error.LastMessage`
	- Source: set by `StatusManager.SetError(...)` when an exception is caught.