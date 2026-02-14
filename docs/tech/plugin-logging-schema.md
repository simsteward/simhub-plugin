# Sim Steward Plugin Logging Schema

All plugin telemetry events use a single line format and are sent to Grafana Cloud log ingestion endpoints (and optionally written to disk). This document defines the line schema and transport mappings.

## Endpoint and payload mapping

The plugin supports two HTTP ingestion formats, chosen from the configured URL path:

- `/loki/api/v1/push` -> Loki push JSON payload (`streams`/`values`)
- `/otlp/v1/logs` (or other `/otlp/.../v1/logs` path) -> OTLP JSON payload (`resourceLogs`/`scopeLogs`/`logRecords`)

Authentication for both is `Authorization: Basic ...`:

- Username + API key: plugin sends `Basic base64(username:apiKey)`
- Pre-encoded token: leave username empty and put the base64 token in the API key field, plugin sends `Basic <token>`

## Event types

| Event | Description |
|-------|-------------|
| `heartbeat` | Periodic health ping; includes plugin/iRacing state and counters. |
| `status_transition` | Runtime status change (e.g. plugin state, connection state). |
| `exception` | Caught exception with context. |
| `telemetry_disconnected` | User disconnected telemetry from the settings UI. |

## Line format

- One event per line.
- Space-separated `key=value` pairs.
- Values containing spaces or quotes are sanitized: newlines/tabs/carriage returns replaced by space, double quotes replaced by single quotes, trimmed.
- Timestamp is provided by Loki (or the log sink) from ingestion time; the line itself does not include a `ts` field (Loki uses the push payload timestamp per entry).

## Required and optional keys by event

### heartbeat

| Key | Required | Description |
|-----|----------|-------------|
| `event` | Yes | Literal `heartbeat`. |
| `run_id` | Yes | Plugin run GUID (format `N`). |
| `reason` | Yes | Trigger: `startup`, `periodic`, `manual_connect`, `shutdown`. |
| `plugin_state` | No | Current plugin runtime state (e.g. Running, Shutdown). |
| `iracing_state` | No | iRacing connection state (e.g. Connected, Disconnected). |
| `game` | No | Last game name from telemetry. |
| `updates` | No | Telemetry update count. |
| `has_error` | No | Whether an error is currently set (True/False). |

### status_transition

| Key | Required | Description |
|-----|----------|-------------|
| `event` | Yes | Literal `status_transition`. |
| `run_id` | Yes | Plugin run GUID. |
| `transition` | Yes | Human-readable transition text (e.g. "Plugin.State: Starting -> Running"). |

### exception

| Key | Required | Description |
|-----|----------|-------------|
| `event` | Yes | Literal `exception`. |
| `run_id` | Yes | Plugin run GUID. |
| `context` | Yes | Where the exception occurred (e.g. DataUpdate, Init). |
| `ex_type` | Yes | Exception type full name. |
| `ex_msg` | Yes | Exception message (sanitized). |

### telemetry_disconnected

| Key | Required | Description |
|-----|----------|-------------|
| `event` | Yes | Literal `telemetry_disconnected`. |
| `run_id` | Yes | Plugin run GUID. |

## Loki stream labels

The plugin pushes to Loki with one stream per batch (Option A). Event type is not a label; use LogQL line filters to filter by event.

| Label | Description |
|-------|-------------|
| `app` | Always `simsteward`. |
| `device_id` | Stable device hash derived from install ID. |
| `install_id` | Stable per-install identifier. |
| `plugin_version` | Plugin assembly version (e.g. 0.1.0). |
| `schema` | Schema version (e.g. 1). |

### LogQL examples

- All plugin logs: `{app="simsteward"}`
- Heartbeats only: `{app="simsteward"} |= "event=heartbeat"`
- Status transitions: `{app="simsteward"} |= "event=status_transition"`
- Exceptions: `{app="simsteward"} |= "event=exception"`
- Heartbeat count per minute: `count_over_time({app="simsteward"} |= "event=heartbeat" [1m])`

## Disk logging (optional)

When disk logging is enabled, the same log line format is written to a file. Default directory: `%LocalAppData%\Sim Steward\logs` (overridable via the Log directory setting). No extra fields; the schema above applies. Rotation (daily or size-based) is implementation-defined.

Note: "Log to disk" and "Log directory" settings take effect when the plugin starts (i.e. next SimHub launch). Changing them in settings mid-session does not start or stop disk logging until restart.
