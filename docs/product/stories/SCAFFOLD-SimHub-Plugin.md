# SimHub Plugin Scaffold

**FR-IDs:** (new -- prerequisite for FR-A-001)  
**Status:** Draft  
**Created:** 2026-02-11

## Description

As a developer, I need a working SimHub plugin shell so that all subsequent telemetry, detection, and UI work has a stable foundation to build on. The plugin must load in SimHub, connect to iRacing, and prove it can read live telemetry on every tick.

## Acceptance Criteria

- [ ] SimHub plugin solution builds without errors targeting .NET Framework 4.8
- [ ] Plugin loads in SimHub and appears in the plugin list
- [ ] `DataUpdate` loop fires on every game tick when iRacing is running
- [ ] At least one iRacing SDK variable (e.g., `Speed`) is read and logged per tick to prove data flow
- [ ] A "Sim Steward" tab exists in the SimHub plugin UI with a title and empty content area
- [ ] Plugin gracefully handles iRacing not running (no crash, logs "waiting for connection")

## Subtasks

- [ ] Create SimHub plugin project (C#, .NET Framework 4.8)
- [ ] Add NuGet references: SimHub.Plugins, iRacingSdkWrapper, Newtonsoft.Json
- [ ] Implement `IPlugin` interface with `Init`, `DataUpdate`, `End` lifecycle methods
- [ ] Wire iRacing SDK connection; log connection state changes
- [ ] Read one telemetry var (`Speed`) in `DataUpdate` and expose as SimHub property `SimSteward.Debug.Speed`
- [ ] Add placeholder "Sim Steward" tab with title text
- [ ] Manual verification: load plugin in SimHub, connect to iRacing, confirm property updates

## Dependencies

- None

## Notes

- This story is purely structural. No buffering, detection, or serialization.
- The placeholder tab will be replaced by the full UI in FR-A-012.
- SimHub plugin project layout may vary; reference SimHub SDK docs or existing community plugins.
