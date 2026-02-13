# First Development Step: SCAFFOLD-Plugin-Foundation

Planning is done. The next step is **SCAFFOLD-Plugin-Foundation**, which is already marked "Now (In Progress)" in [docs/product/priorities.md](../product/priorities.md). Everything else (incident detection, OBS, replay, overlay) depends on this.

**Note:** Implement and test on a Windows machine (SimHub + iRacing required).

---

## What You Have

- **Spec:** [docs/product/specs/SCAFFOLD-Plugin-Foundation.md](../product/specs/SCAFFOLD-Plugin-Foundation.md) — requirements R-SCAF-01 through R-SCAF-07
- **Story:** [docs/product/stories/SCAFFOLD-Plugin-Foundation.md](../product/stories/SCAFFOLD-Plugin-Foundation.md) — acceptance criteria and subtasks
- **Tech plan:** [scaffold-plugin-setup.md](scaffold-plugin-setup.md) — project structure, SimHub contract, telemetry pattern, logging
- **Plugin folder:** Only [plugin/README.md](../../plugin/README.md) exists; no .csproj or C# source yet

---

## Implementation Order

1. **Project and references**
   - Add `plugin/SimSteward.csproj`: class library, .NET Framework 4.8, output `SimSteward.dll`.
   - Reference SimHub assemblies via `$(SimHubDir)` (e.g. `SimHub.Plugins.dll`, `GameReaderCommon.dll`) with `<Private>False</Private>`; WPF refs (`PresentationFramework`, etc.) from framework.
   - Optional: post-build copy of `SimSteward.dll` to `$(SimHubDir)\PluginsData\` for quick testing.

2. **Plugin class**
   - Add `plugin/SimStewardPlugin.cs`: one class implementing `IPlugin` and `IDataPlugin`, with `[PluginDescription(Name = "Sim Steward", ...)]`.
   - Implement: `Init` (store manager, log version), `End` (log shutdown), `LeftMenuTitle` => `"Sim Steward"`, `GetWPFSettingsControl` (return placeholder control), `DataUpdate` (game check, then telemetry read + throttled log).

3. **Placeholder settings tab**
   - Add `plugin/Settings/SettingsControl.xaml` + `.xaml.cs`: minimal WPF `UserControl` showing "Sim Steward" and version; no interaction (per R-SCAF-04).

4. **Telemetry and connection state**
   - In `DataUpdate`: treat `pluginManager.GameName == "IRacing"` as connected; maintain `IsIRacingConnected` and log connect/disconnect transitions.
   - When connected, read via `GetPropertyValue("DataCorePlugin.GameRawData.Telemetry.SessionTime")` (and same path for `SessionNum`, `PlayerCarTeamIncidentCount`); null-check and safe cast; no per-tick log (throttle e.g. once per 5 seconds or by debug flag).

5. **Properties**
   - Add `plugin/Properties/AssemblyInfo.cs` with version/assembly metadata.

6. **Verification**
   - Build; copy DLL to SimHub PluginsData; start SimHub and confirm "Sim Steward" in plugin list and in left menu; open settings and see placeholder tab; run iRacing and confirm logs show connection + throttled telemetry.

---

## Spec vs Tech Plan

- **Structure:** Spec recommends `plugin/Settings/SettingsControl.xaml`; tech plan shows control at plugin root. Follow the **spec** and use a `Settings/` subfolder.
- **iRSDKSharp:** Omit for scaffold; add when FR-004 (replay) is implemented.
- **SimHub.Logging:** Tech plan uses `SimHub.Logging.Current`; use whatever the SimHub SDK exposes for `SimHub.Logging` (spec R-SCAF-07).

---

## After SCAFFOLD

Once the scaffold is done and accepted:

- Mark SCAFFOLD done in [docs/product/priorities.md](../product/priorities.md) and [memory-bank/progress.md](../../memory-bank/progress.md).
- Promote **FR-001-002 Incident Detection** to "Now" (or run OBS spike first per priorities — priorities say FR-001-002 is next, with OBS spike before full FR-005-006-007).
