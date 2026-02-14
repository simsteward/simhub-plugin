# Sim Steward SimHub Plugin

The official SimHub plugin for simsteward.com – incident detection and protest automation for iRacing.

## Structure

- **SimSteward.csproj** – .NET Framework 4.8 plugin project
- **src/** – C# source code and WPF settings UI
  - **SimStewardPlugin.cs** – plugin entry point (`IPlugin`, `IDataPlugin`)
	- **StatusManager.cs** – centralized runtime status model and transitions
	- **Settings/** – WPF status dashboard (NOC-style indicators)
- **Properties/** – assembly metadata (`AssemblyInfo.cs`)
- **overlays/** – overlay seed docs for Dash Studio status surfaces

## Building

1. Create local SDK links (hard-link first, symbolic-link fallback):

	`pwsh -ExecutionPolicy Bypass -File .\scripts\create-simhub-sdk-links.ps1`

	If SimHub is not installed in the default location, pass `-SimHubDir`.

	This usually avoids requiring admin in typical Windows setups.
	On locked-down machines, symbolic-link fallback may still require Developer Mode or Administrator privileges.

2. Set SimHub install path through MSBuild property `SimHubDir` (or environment variable `SIMHUB_DIR`) only if you are not using local links.
3. Build from this folder:

	`msbuild SimSteward.csproj /p:Configuration=Release`

4. Deploy to SimHub plugin directory (fixed path):

	`pwsh -ExecutionPolicy Bypass -File .\scripts\deploy-plugin.ps1`

	The script enforces this sequence: close SimHub, verify it is stopped, build, copy DLL to both SimHub root and PluginsData, verify deployed DLL hash, then launch SimHub.
	It also ensures Sim Steward is enabled and visible in the SimHub left menu.

	This always copies `SimStewardPlugin.dll` to `C:\Program Files (x86)\SimHub\PluginsData\`.

	The deploy script resolves build output from either `bin\<Configuration>\net48\` or `bin\<Configuration>\`.

5. Restart SimHub and verify `Sim Steward` appears in the plugin list.

The project resolves references in this order:
- `plugin/.simhub-sdk/` links (hard/symbolic, if present)
- `SIMHUB_DIR` environment variable
- default `C:\Program Files (x86)\SimHub`

Deployment destination is fixed and always uses:
- `C:\Program Files (x86)\SimHub\PluginsData\`

## Requirements

- [SimHub](https://www.simhubdash.com/) installed
- SimHub SDK assemblies available from install directory (`SimHub.Plugins.dll`, `GameReaderCommon.dll`)
- .NET Framework 4.8 build tools (Visual Studio Build Tools or Visual Studio)

## Status Observability

The plugin now exposes a live runtime status model to support manual validation and NOC-style monitoring.

### Surfaces

- **SimHub settings tab** (`Sim Steward`) shows live indicators for runtime, iRacing connectivity, telemetry counters, feature readiness, and errors.
- **SimHub log stream** includes status transition entries in the format `[COMPONENT] Status: OLD -> NEW`.
- **Plugin properties** expose status under `SimSteward.Status.*` for Dash Studio and external observability.

### Exposed Status Properties

- `SimSteward.Status.Plugin.State` (`string`)
- `SimSteward.Status.Plugin.LastStatusChangeUtc` (`string`)
- `SimSteward.Status.Plugin.LastHeartbeatUtc` (`string`)
- `SimSteward.Status.iRacing.IsConnected` (`bool`)
- `SimSteward.Status.iRacing.State` (`string`)
- `SimSteward.Status.iRacing.GameName` (`string`)
- `SimSteward.Status.Telemetry.UpdateCount` (`long`)
- `SimSteward.Status.Telemetry.SessionTime` (`double`)
- `SimSteward.Status.Telemetry.SessionNum` (`int`)
- `SimSteward.Status.Telemetry.IncidentCount` (`int`)
- `SimSteward.Status.OBS.State` (`string`)
- `SimSteward.Status.Incident.State` (`string`)
- `SimSteward.Status.Recording.State` (`string`)
- `SimSteward.Status.Replay.State` (`string`)
- `SimSteward.Status.Error.HasError` (`bool`)
- `SimSteward.Status.Error.LastMessage` (`string`)

### Status Values

- Runtime: `Starting`, `Running`, `Shutdown`, `Error`
- Connection: `Connected`, `Disconnected`, `Error`
- Features: `NotConfigured`, `Waiting`, `Active`, `Warning`, `Error`, `Disabled`

## Telemetry (Grafana Cloud)

The plugin sends lightweight health events (heartbeats, status transitions, exceptions) to Grafana Cloud Loki. Heartbeats are enqueued every 2 seconds by default (configurable); network I/O is batched on a flush timer (default 5s). Optionally, the same log lines can be written to a local file. See [Logging schema](../../docs/tech/plugin-logging-schema.md).

### Configuration (SimHub settings tab)

Configure these in the **Sim Steward** settings tab under **TELEMETRY (GRAFANA CLOUD)**:

- **Loki URL** — Full push URL. Use one of:
  - **Loki push:** `https://logs-prod-<region>.grafana.net/loki/api/v1/push`
  - **OTLP logs:** `https://otlp-gateway-prod-<region>.grafana.net/otlp/v1/logs`
  The plugin auto-detects OTLP mode when the URL path contains `/otlp/` or ends with `/v1/logs`.
- **Loki Username** — Grafana Cloud instance ID (numeric), from the same “Send data” / “Details” section in your stack. **Optional:** if you have a pre-encoded Basic token (Base64 of `instanceId:apiKey`), leave this empty and put the token in **Loki API Key**.
- **Loki API Key** — Grafana Cloud API token with permission to write logs (e.g. “MetricsPublisher” or “Logs” role). Create one in the stack under API keys or “Send data.” If **Loki Username** is empty, this field is used as the raw Basic auth value (plugin sends `Authorization: Basic <this value>`).

- **Flush Interval (sec)** — How often to send batched events to Loki (default 5).
- **Heartbeat Interval (sec)** — How often to enqueue a heartbeat when telemetry is active (1–60, default 2).
- **Log to disk** — If enabled, the same log lines are written to a file (daily rotation). Default directory: `%LocalAppData%\Sim Steward\logs` unless **Log directory** is set. (Takes effect on next SimHub start.)
- **Log directory (optional)** — Override directory for disk log files; empty uses the default above.

After saving, click **Connect telemetry** to run a test heartbeat push. Connected status is based on successful heartbeat delivery.

### Grafana dashboard

A **Sim Steward Plugin** dashboard exists in the Sim Steward folder in Grafana. It shows all plugin logs, heartbeats per minute, status transitions, and exceptions. Open your Grafana stack and go to **Dashboards → Sim Steward → Sim Steward Plugin**.

### Retention

Log retention is configured in Grafana Cloud for your stack (stack → Logs → retention or plan limits), not in the plugin.
