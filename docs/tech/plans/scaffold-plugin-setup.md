# Tech Plan: Plugin Scaffold + iRacing Connection

**Story:** `docs/product/stories/SCAFFOLD-Plugin-Foundation.md`  
**FR-IDs:** N/A (foundation)  
**Status:** Draft  
**Date:** 2026-02-13

---

## Project Structure

```
plugin/
├── SimSteward.csproj          # Class library, .NET Framework 4.8
├── SimStewardPlugin.cs        # Main plugin class (IPlugin + IDataPlugin)
├── SettingsControl.xaml        # Placeholder WPF UserControl
├── SettingsControl.xaml.cs     # Code-behind
└── Properties/
    └── AssemblyInfo.cs         # Version, assembly metadata
```

**Key .csproj settings:**
- `<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>`
- `<OutputType>Library</OutputType>`
- Output assembly: `SimSteward.dll`
- No NuGet packages for scaffold -- all dependencies are SimHub-provided assemblies.

---

## SimHub Plugin Contract

The plugin must implement both `IPlugin` and `IDataPlugin`. SimHub discovers plugins via the `PluginDescription` attribute.

```csharp
[PluginDescription(
    Name = "Sim Steward",
    Description = "Incident detection and protest automation for iRacing",
    Author = "Sim Steward")]
public class SimStewardPlugin : IPlugin, IDataPlugin
{
    public PluginManager PluginManager { get; set; }

    public void Init(PluginManager pluginManager) { ... }
    public void DataUpdate(PluginManager pluginManager, ref GameData data) { ... }
    public void End(PluginManager pluginManager) { ... }
    public string LeftMenuTitle => "Sim Steward";
    public Control GetWPFSettingsControl(PluginManager pluginManager) { ... }
}
```

**Method responsibilities for scaffold:**

| Method | Scaffold Behavior |
|--------|-------------------|
| `Init` | Store `pluginManager` reference. Log "Sim Steward loaded". |
| `DataUpdate` | Check game name, read telemetry vars, log values. |
| `End` | Log "Sim Steward unloaded". Cleanup if needed. |
| `LeftMenuTitle` | Return `"Sim Steward"` (appears in SimHub left nav). |
| `GetWPFSettingsControl` | Return `new SettingsControl()` (placeholder). |

---

## Reference Assemblies

All assemblies live in the SimHub installation directory (typically `C:\Program Files (x86)\SimHub\`).

| Assembly | Provides | Reference Type |
|----------|----------|----------------|
| `SimHub.Plugins.dll` | `IPlugin`, `IDataPlugin`, `PluginManager`, `PluginDescription` | Local file reference |
| `GameReaderCommon.dll` | `GameData`, `GameRawData` | Local file reference |
| `iRSDKSharp.dll` | iRacing SDK wrapper (broadcast messages, used in later stories) | Local file reference -- **not needed for scaffold**, add when FR-004 work begins |

**How to reference:** Add as `<Reference>` with `<HintPath>` pointing to the SimHub install directory. Use a relative path or environment variable so the project is portable:

```xml
<Reference Include="SimHub.Plugins">
  <HintPath>$(SimHubDir)\SimHub.Plugins.dll</HintPath>
  <Private>False</Private>
</Reference>
```

**Decision:** Use `$(SimHubDir)` MSBuild property, defaulting to `C:\Program Files (x86)\SimHub`. Developers set this as an environment variable if SimHub is installed elsewhere. `<Private>False</Private>` avoids copying SimHub DLLs to output (they already exist at runtime).

---

## Telemetry Access Pattern

SimHub exposes iRacing telemetry through `pluginManager.GetPropertyValue()`. See `docs/tech/sdk-investigation.md` for the full variable list.

**Scaffold reads these variables in `DataUpdate`:**

| Variable | Access Path | Type | Purpose |
|----------|-------------|------|---------|
| `SessionTime` | `DataCorePlugin.GameRawData.Telemetry.SessionTime` | double | Timestamp reference |
| `PlayerCarTeamIncidentCount` | `DataCorePlugin.GameRawData.Telemetry.PlayerCarTeamIncidentCount` | int | Incident counter (baseline for FR-001) |
| `SessionNum` | `DataCorePlugin.GameRawData.Telemetry.SessionNum` | int | Session identifier |

**Pattern in code:**

```csharp
var sessionTime = pluginManager.GetPropertyValue(
    "DataCorePlugin.GameRawData.Telemetry.SessionTime");
```

Returns `null` when iRacing is not connected or the variable is unavailable. Always null-check before casting.

**Decision:** Scaffold reads and logs these values only. No state tracking or incident detection logic -- that's FR-001-002.

---

## iRacing Connection State

Detect whether iRacing is the active game:

```csharp
private bool IsIRacingRunning(PluginManager pluginManager)
{
    return pluginManager.GameName == "IRacing";
}
```

**Behavior in `DataUpdate`:**
1. Check `pluginManager.GameName`. If not `"IRacing"`, return early.
2. When transitioning from not-connected to connected, log once.
3. When transitioning from connected to not-connected, log once.

**Decision:** Track connection state with a `bool _isConnected` field. Log state changes, not every tick. This avoids spamming SimHub logs at 10-60 Hz (see SDK investigation for DataUpdate rates).

---

## Settings Tab Placeholder

Minimal WPF `UserControl`. No functional settings for scaffold -- that's FR-008.

```xml
<!-- SettingsControl.xaml -->
<UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
    <StackPanel Margin="20">
        <TextBlock Text="Sim Steward" FontSize="20" FontWeight="Bold" />
        <TextBlock Text="v0.1.0 — Plugin loaded." Margin="0,8,0,0" />
    </StackPanel>
</UserControl>
```

Empty code-behind (no bindings, no events).

---

## Build & Deploy

1. **Build:** `msbuild SimSteward.csproj /p:Configuration=Release` (or build in Visual Studio / Rider).
2. **Output:** `bin/Release/SimSteward.dll`
3. **Deploy:** Copy `SimSteward.dll` to `{SimHubDir}\PluginsData\` — SimHub scans this directory on startup.
4. **Verify:** Launch SimHub → Settings → Plugins → "Sim Steward" should appear in the list. Enable it, restart SimHub.

**Decision:** No automated deploy script for scaffold. Manual copy is fine for development. A post-build copy step can be added later:

```xml
<Target Name="PostBuild" AfterTargets="PostBuildEvent">
  <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(SimHubDir)\PluginsData\" />
</Target>
```

---

## Debug Logging

Use `SimHub.Logging.Current` -- SimHub's built-in logging backed by NLog. Logs go to `{SimHubDir}\Logs\`.

```csharp
SimHub.Logging.Current.Info("Sim Steward: Init complete");
SimHub.Logging.Current.Info($"Sim Steward: SessionTime={sessionTime}, Incidents={incidentCount}");
SimHub.Logging.Current.Error("Sim Steward: Unexpected error in DataUpdate", ex);
```

**Guidelines:**
- Prefix messages with `"Sim Steward:"` for easy log filtering.
- Log telemetry values at `Info` level during scaffold (temporary; will be `Debug` once stable).
- **Do not log every DataUpdate tick.** Log on state changes (connection, new session) and at a throttled interval for telemetry (e.g., once per 5 seconds) to avoid flooding.

---

## Key Decisions

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | Single class for scaffold (`SimStewardPlugin.cs`) | No need to split into services yet. FR-001-002 will introduce `IncidentDetector` as a separate class. |
| 2 | `$(SimHubDir)` MSBuild property for assembly references | Keeps project portable across dev machines without hardcoded paths. |
| 3 | `<Private>False</Private>` on SimHub references | Avoids copying SimHub DLLs to output -- they exist in the SimHub runtime directory. |
| 4 | No `iRSDKSharp.dll` reference in scaffold | Not needed until FR-004 (replay jump). Adding unused references creates noise. |
| 5 | Throttled logging, not per-tick | DataUpdate fires at 10-60 Hz. Logging every tick would flood logs and hurt performance. |
| 6 | `pluginManager.GameName` for connection detection | Simplest reliable way to know if iRacing is active. No need for a separate iRacing SDK connection for this. |
| 7 | No settings persistence in scaffold | SimHub provides `pluginManager.GetSettingsValue<T>()` and `SaveSettingsValue()` for later use in FR-008. Scaffold has no settings to persist. |
