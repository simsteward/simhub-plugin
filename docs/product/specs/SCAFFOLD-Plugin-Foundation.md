# Spec: Plugin Scaffold + iRacing Connection

**FR-IDs:** N/A (foundation infrastructure)
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/SCAFFOLD-Plugin-Foundation.md`

---

## Overview

The plugin scaffold is the structural foundation for Sim Steward. It creates a C# class library that satisfies SimHub's plugin contract (`IPlugin` + `IDataPlugin`), loads without errors in SimHub, and proves iRacing telemetry flows through SimHub's data pipeline into plugin code.

This is pure infrastructure -- no user-facing features. Every subsequent story (FR-001 through FR-015) depends on this scaffold existing and working. The goal: a plugin DLL that SimHub discovers, loads, and calls on every data tick when iRacing is running.

Aligns with PRD Section 6 (Technical Architecture): the "Sim Steward Plugin (C#)" box inside the SimHub Host, targeting .NET Framework 4.8 with iRSDKSharp.dll bundled by SimHub.

---

## Detailed Requirements

### R-SCAF-01: Project Configuration

- C# class library targeting **.NET Framework 4.8** (SimHub's runtime).
- Assembly references: `SimHub.Plugins`, `GameReaderCommon`, `WPF` assemblies (`PresentationFramework`, `PresentationCore`, `WindowsBase`).
- iRSDKSharp.dll is **not** bundled -- SimHub ships it. The project references SimHub's copy.
- Output DLL name: `SimSteward.dll` (or `SimStewardPlugin.dll` -- consistent with SimHub naming conventions).
- Build output copies to SimHub's plugin directory for local development.

### R-SCAF-02: Plugin Contract Implementation

The plugin class must implement both `IPlugin` and `IDataPlugin`:

| Member | Contract | Behavior |
|--------|----------|----------|
| `LeftMenuTitle` | `string` property | Returns `"Sim Steward"`. Displayed in SimHub's left navigation. |
| `Init(PluginManager)` | Called once on SimHub startup | Store `PluginManager` reference. Initialize state. Log plugin version. |
| `DataUpdate(PluginManager, ref GameData)` | Called every tick (10-60 Hz) | Read telemetry variables. Track connection state. Log values. See R-SCAF-04, R-SCAF-05. |
| `End(PluginManager)` | Called on SimHub shutdown | Clean up resources. Log shutdown. |
| `GetWPFSettingsControl(PluginManager)` | Returns WPF `Control` | Return placeholder `UserControl`. See R-SCAF-03. |

**Edge cases:**
- `DataUpdate` may fire before iRacing is connected (other games, or no game). Must not throw.
- `Init` must not block. Long-running initialization (if any in future stories) must be async.
- `End` must be idempotent -- safe to call even if `Init` partially failed.

### R-SCAF-03: Plugin Discovery Attribute

- Class decorated with `[PluginDescription(...)]` attribute containing:
  - **Name:** "Sim Steward"
  - **Description:** Brief one-liner (e.g., "iRacing incident clipping tool")
  - **Author:** Project author name
- This attribute is how SimHub discovers and lists the plugin. Without it, the DLL is ignored.

### R-SCAF-04: Placeholder Settings Tab

- Minimal WPF `UserControl` returned by `GetWPFSettingsControl`.
- Displays: plugin name ("Sim Steward") and version string.
- No interactive controls. FR-008 builds the real settings UI on top of this.
- Must not throw if the control is instantiated before `Init` completes.

### R-SCAF-05: Telemetry Reading

On each `DataUpdate` tick, read three variables from iRacing via SimHub's property bridge:

| Variable | SimHub Property Path | Type | Purpose |
|----------|---------------------|------|---------|
| `SessionTime` | `DataCorePlugin.GameRawData.Telemetry.SessionTime` | `double` (seconds) | Timestamp for incidents (FR-001) |
| `SessionNum` | `DataCorePlugin.GameRawData.Telemetry.SessionNum` | `int` | Session identifier for replay jump (FR-004) |
| `PlayerCarTeamIncidentCount` | `DataCorePlugin.GameRawData.Telemetry.PlayerCarTeamIncidentCount` | `int` | Incident detection delta (FR-001) |

**Access pattern:** `pluginManager.GetPropertyValue("DataCorePlugin.GameRawData.Telemetry.{Var}")`.

**Edge cases:**
- Property values are `null` when iRacing is not running or the property doesn't exist. All reads must null-check and fall back gracefully (no exceptions).
- Type safety: `GetPropertyValue` returns `object`. Cast carefully; log and skip on type mismatch rather than crashing.
- `SessionTime` resets to 0 on session transitions. The scaffold does not need to handle this (FR-001 will), but it must not assume `SessionTime` is monotonically increasing.

### R-SCAF-06: iRacing Connection State Tracking

- Track whether iRacing is the active game by checking `pluginManager.GameName` in `DataUpdate`.
- Maintain a boolean state: `IsIRacingConnected`.
- Detect transitions:
  - **Connected:** `GameName` changes to `"IRacing"` (or SimHub's equivalent identifier). Log the transition.
  - **Disconnected:** `GameName` changes away from `"IRacing"` or becomes null/empty. Log the transition.
- Only attempt telemetry reads (R-SCAF-05) when `IsIRacingConnected` is `true`.
- Connection state change should be logged but does not fire events in this story. FR-001 will build on this.

**Edge cases:**
- SimHub may report other games (ACC, AMS2, etc.). The plugin must be inert for non-iRacing games -- no errors, no telemetry reads, no log spam.
- `GameName` may be null on startup before any game connects. Handle as disconnected.

### R-SCAF-07: Debug Logging

- Use `SimHub.Logging` for all log output.
- Log the following:
  - Plugin init (version, startup).
  - Plugin shutdown.
  - iRacing connection/disconnection transitions.
  - Telemetry values on each tick (gated behind a debug flag or throttled to avoid log flooding -- e.g., log every N seconds, not every tick).
- Log levels: `Info` for lifecycle events, `Debug` for telemetry values, `Error` for unexpected exceptions.
- Never log at a rate that degrades SimHub performance. At 60 Hz, logging every tick produces ~3600 lines/minute. Throttle telemetry logging to once per second or use a debug toggle.

---

## Technical Design Notes

### SimHub Plugin Lifecycle

```
SimHub starts → discovers DLL with [PluginDescription] → instantiates class
  → calls Init(PluginManager) → plugin is live
  → calls DataUpdate(...) on every tick (10-60 Hz depending on license)
  → user opens settings → calls GetWPFSettingsControl(PluginManager)
  → SimHub shuts down → calls End(PluginManager)
```

### Key Interfaces

- **`IPlugin`** -- base plugin contract. Provides `Init`, `End`, `GetWPFSettingsControl`, `LeftMenuTitle`.
- **`IDataPlugin`** -- extends with `DataUpdate(PluginManager, ref GameData)`. Required to receive telemetry ticks.
- Both must be implemented on the same class. SimHub uses reflection to discover plugins implementing these interfaces with the `[PluginDescription]` attribute.

### SimHub Property Bridge vs Direct SDK

This scaffold reads telemetry through SimHub's property bridge (`GetPropertyValue`), not through a direct iRSDKSharp connection. SimHub already maintains the iRacing SDK connection and exposes all telemetry as named properties.

A **separate** iRSDKSharp instance will be needed later for replay broadcast commands (FR-004) -- SimHub's property bridge is read-only. That is out of scope for this story.

### Project Structure (Recommended)

```
plugin/
├── SimSteward.csproj          # Class library, .NET 4.8
├── SimStewardPlugin.cs        # Main plugin class (IPlugin + IDataPlugin)
├── Settings/
│   └── SettingsControl.xaml    # Placeholder WPF UserControl
│       SettingsControl.xaml.cs
└── Properties/
    └── AssemblyInfo.cs
```

Future stories will add folders: `Detection/`, `Replay/`, `OBS/`, `Overlay/`.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **.NET Framework 4.8** | SimHub's runtime. Cannot use .NET 5+/Core. |
| **SimHub SDK assemblies** | `SimHub.Plugins.dll`, `GameReaderCommon.dll`. Referenced from local SimHub install. |
| **iRSDKSharp.dll** | Ships with SimHub. Not bundled in plugin output. |
| **WPF** | Settings tab requires `PresentationFramework`. Available in .NET 4.8. |
| **SimHub installed locally** | Required for both development (references) and testing (loading the plugin). |
| **iRacing installed locally** | Required for telemetry testing. Plugin must still load cleanly without iRacing running. |

**No external NuGet packages required for this story.**

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Plugin targets .NET 4.8, compiles without errors | R-SCAF-01 |
| Implements `IPlugin` and `IDataPlugin` | R-SCAF-02 |
| SimHub loads plugin without errors | R-SCAF-02, R-SCAF-03 |
| Placeholder settings tab displays in SimHub | R-SCAF-04 |
| `DataUpdate` fires and receives telemetry | R-SCAF-02, R-SCAF-05 |
| Reads `SessionTime` and `PlayerCarTeamIncidentCount` | R-SCAF-05 |
| Detects iRacing connection/disconnection | R-SCAF-06 |
| Debug logging outputs telemetry values | R-SCAF-07 |

---

## Open Questions

None. This is well-understood infrastructure with clear contracts.
