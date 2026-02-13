# Plugin Scaffold + iRacing Connection

**FR-IDs:** N/A (foundation)  
**Priority:** Must  
**Status:** Ready  
**Created:** 2026-02-13

## Description

Get the SimHub plugin project compiling, loading in SimHub, and reading iRacing telemetry. This is the foundation everything else builds on -- no features, just a working plugin shell that proves we can talk to iRacing through SimHub's data pipeline.

## Acceptance Criteria

- [ ] Plugin project targets .NET Framework 4.8 and compiles without errors
- [ ] Plugin implements `IPlugin` and `IDataPlugin` interfaces
- [ ] SimHub loads the plugin without errors (visible in SimHub plugin list)
- [ ] Plugin has a placeholder settings tab (WPF `UserControl`) that displays in SimHub
- [ ] `DataUpdate` callback fires when iRacing is running and receives telemetry data
- [ ] Plugin reads `SessionTime` and `PlayerCarTeamIncidentCount` from `GameRawData.Telemetry`
- [ ] Plugin detects iRacing connection/disconnection (game running vs not)
- [ ] Debug logging outputs telemetry values to SimHub log

## Subtasks

- [ ] Create C# class library project with SimHub plugin references (`SimHub.Plugins`, `GameReaderCommon`)
- [ ] Implement `IPlugin` + `IDataPlugin` (Init, DataUpdate, End, LeftMenuTitle, GetWPFSettingsControl)
- [ ] Add `PluginDescription` attribute for SimHub discovery
- [ ] Create minimal WPF `UserControl` for settings tab placeholder
- [ ] Read `SessionTime`, `PlayerCarTeamIncidentCount`, `SessionNum` in `DataUpdate`
- [ ] Add iRacing connection state tracking (game running check via `pluginManager.GameName`)
- [ ] Add `SimHub.Logging` calls for debugging
- [ ] Test: load plugin in SimHub, verify it appears and logs telemetry when iRacing runs

## Dependencies

- None (first story)

## Notes

- iRSDKSharp.dll ships with SimHub -- no need to bundle separately.
- SimHub access pattern: `pluginManager.GetPropertyValue("DataCorePlugin.GameRawData.Telemetry.{Var}")`.
- This story deliberately has no user-facing features. It's purely infrastructure.
- Keep the settings tab minimal -- just a label saying "Sim Steward" and version. FR-008 builds the real settings UI.
