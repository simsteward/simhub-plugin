# Grafana Loki Observability Test Harness

This document describes how to run the Grafana/Loki test harness locally and in CI, how to filter test data in LogQL, and how to exclude test logs from production dashboards.

## Overview

The test harness:

1. **Emits** structured log events (action_result, incident_detected, session_digest) to the configured Loki endpoint using the same format and label schema as the plugin.
2. **Tags** every line with `testing="true"` and `test_tag="grafana-harness"` (or `TEST_TAG` env) so test data can be filtered in or out.
3. **Asserts** via the Loki HTTP API that the expected events arrived and have the required fields.

All test data is identifiable and can be excluded from production dashboards and alerts.

## Running the full test (local stack + harness + assertions)

From the repo root:

```powershell
.\tests\observability\run_grafana_tests.ps1
```

Requirements:

- **Docker** (Docker Compose v2 or `docker-compose`) for Loki and Grafana.
- **.NET SDK** to build and run the harness and assertion projects.
- **Storage path** for Docker volumes: by default the compose file uses `S:\sim-steward-grafana-storage`. If drive `S:\` exists, the script creates the required subdirs. Otherwise create them manually or set `GRAFANA_STORAGE_PATH` to a path that exists (note: the compose file may need to be adjusted to use that env; see `observability/local/docker-compose.yml`).

The script:

1. Builds the harness and assertion projects.
2. Starts the local Loki and Grafana stack (`observability/local/docker-compose.yml`).
3. Waits for Loki to be ready (`/ready`).
4. Runs the harness to emit test logs.
5. Waits 5 seconds for ingestion.
6. Runs the assertion tool (with internal retries up to 30s).
7. Tears down the stack.
8. Exits 0 on success, 1 on failure.

## Running the harness only (emit test logs)

Use this when Loki is already running (e.g. local Docker or Grafana Cloud) and you only want to push test data:

```powershell
$env:SIMSTEWARD_LOKI_URL = "http://localhost:3100"
$env:SIMSTEWARD_LOG_ENV = "local"
$env:TEST_TAG = "grafana-harness"
dotnet run --project harness\SimSteward.GrafanaTestHarness\SimSteward.GrafanaTestHarness.csproj -- --count 3
```

- **SIMSTEWARD_LOKI_URL** (required): Loki base URL (e.g. `http://localhost:3100` or Grafana Cloud logs URL).
- **SIMSTEWARD_LOKI_USER** / **SIMSTEWARD_LOKI_TOKEN**: Set when pushing to Grafana Cloud (Basic/Bearer auth).
- **SIMSTEWARD_LOG_ENV**: Label value for `env` (default `local`).
- **TEST_TAG**: Value for the `test_tag` field (default `grafana-harness`).
- **--count N**: Number of action_result events to emit per type (success/fail); default 3, max 100.

The harness also emits incident_detected and session_digest events (fixed count per run).

## Running the assertion tool only

Use this when test data has already been emitted and you only want to validate that Loki has the expected lines:

```powershell
$env:LOKI_QUERY_URL = "http://localhost:3100"
$env:TEST_TAG = "grafana-harness"
dotnet run --project tests\observability\AssertLokiQueries\AssertLokiQueries.csproj
```

- **LOKI_QUERY_URL**: Loki base URL for query_range (default `http://localhost:3100`).
- **TEST_TAG**: Must match the tag used when emitting (default `grafana-harness`).

The tool retries for up to 30 seconds with 3-second backoff. It asserts:

- At least 2 action_result lines (success and failure).
- At least 1 incident_detected and 1 session_digest.
- Required fields on action_result (correlation_id, success, action).
- Dashboard panel queries (command-audit, incident-timeline, session-overview) return at least one row for test-tagged data.

## Asserting via MCP

Observability assertions can be run via MCP instead of (or in addition to) the .NET AssertLokiQueries tool. Use this when the Grafana/Loki MCP (e.g. user-MCP_DOCKER) or SimSteward MCP is available and configured for Loki.

**After running the harness** (so test-tagged logs exist in Loki):

1. **SimSteward MCP:** Call `simsteward_loki_query` with:
   - `query`: `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`
   - `start`: e.g. `1h` (or a time that includes the harness run)
   - `limit`: e.g. `100` to get enough lines to assert counts

2. **Grafana/Loki MCP (user-MCP_DOCKER):** Call `query_loki_logs` with:
   - `datasourceUid`: `loki_local` (local stack; discover via `list_datasources` with `type="loki"` if needed)
   - `logql`: `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`
   - `limit`: increase if default 10 is too low (e.g. 100)

**Assert the same as AssertLokiQueries:**

- At least 2 lines with `event` = `action_result` (success and failure).
- At least 1 line with `event` = `incident_detected` and 1 with `event` = `session_digest`.
- For at least one `action_result` line: parsed JSON has `correlation_id`, `success`, and `action` in the log line or fields.

If Loki is slow to index, retry the MCP query after a few seconds (AssertLokiQueries uses up to 30s retry with 3s backoff). See [tests/observability/assert_via_mcp.md](../tests/observability/assert_via_mcp.md) for a step-by-step procedure.

## LogQL: filtering test data

### Show only test harness data

```logql
{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"
```

### Command-audit (test data only)

```logql
{app="sim-steward", component="simhub-plugin"} | json | event = "action_result" | testing = "true"
```

### Incident timeline (test data only)

```logql
{app="sim-steward", component="tracker"} | json | event = "incident_detected" | testing = "true"
```

### Exclude test data from dashboards and alerts

Add this filter to any query that should show only production traffic:

```logql
{app="sim-steward"} | json | testing != "true"
```

If the field is missing, `testing != "true"` still matches (missing is not equal to "true"). To be explicit:

```logql
{app="sim-steward"} | json | (testing != "true" or testing == "")
```

## CI expectations

- **run_grafana_tests.ps1** is intended to be run as part of CI or manually. It does not run automatically from `deploy.ps1`; add it to your pipeline if you want observability tests on every build.
- The script exits 0 only when the stack starts, the harness emits logs, and all assertions pass within the retry window.
- On failure, the script tears down the stack and prints the assertion error.

## Troubleshooting

| Issue | What to do |
|------|------------|
| Loki not ready | Ensure Docker is running and no other process is using port 3100. Wait longer or increase the ready-check attempts in the script. |
| No log lines found | Confirm SIMSTEWARD_LOKI_URL when running the harness. For assertions, ensure the harness was run recently (last 10 minutes) and TEST_TAG matches. |
| Docker volume errors | Create the host path used in docker-compose (e.g. `S:\sim-steward-grafana-storage\loki` and `...\grafana`) or adjust the compose file for your environment. |
| Assertion timeout | Loki may be slow to index. The assertion tool retries 30s; if you see timeout, run the harness again and then the assertion tool immediately. |

## References

- [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) — label schema, event taxonomy, LogQL reference.
- [observability/local/](../observability/local/) — Docker Compose and Grafana provisioning.
