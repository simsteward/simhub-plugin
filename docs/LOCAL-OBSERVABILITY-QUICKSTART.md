# Quick start: local data into Grafana

Get SimSteward plugin logs into the local Grafana/Loki stack in five steps.

## 1. Create Loki storage path

The Docker stack uses a host path for Loki and Grafana data. Default in `observability/local/docker-compose.yml` is `S:\sim-steward-grafana-storage\`. Create that directory, or set `GRAFANA_STORAGE_PATH` in `observability/local/.env.observability.local` and use it in the compose volumes (see [Storage path override](#storage-path-override) below).

## 2. Start the stack

From the repo root:

```powershell
npm run obs:up
```

Or with custom Grafana credentials: copy `observability/local/.env.observability.example` to `observability/local/.env.observability.local`, set `GRAFANA_ADMIN_PASSWORD` (and optionally `LOKI_PUSH_TOKEN` if using the file-tail profile), then:

```powershell
npm run obs:up:env
```

Check containers: `npm run obs:ps`.

## 3. Configure the plugin for local Loki

The plugin reads environment variables at startup. SimHub does not load a `.env` file by default. Use either:

- **Launcher (recommended):** Run SimHub via the script so it gets local Loki env and starts SimHub:
  ```powershell
  .\scripts\run-simhub-local-observability.ps1
  ```
  The script sets `SIMSTEWARD_LOKI_URL=http://localhost:3100`, `SIMSTEWARD_LOG_ENV=local`, and optionally loads other vars from repo `.env` if present.

- **Windows user environment:** Set `SIMSTEWARD_LOKI_URL=http://localhost:3100`, `SIMSTEWARD_LOG_ENV=local` (and optionally `SIMSTEWARD_LOG_DEBUG=1`) in your user environment variables, then start SimHub normally and restart it after any change.

You can copy the "Local Loki" block from `.env.example` into `.env` for use with the launcher script.

## 4. Open Grafana and query

1. Open **http://localhost:3000** and sign in (default `admin` / `admin` unless overridden).
2. Go to **Explore**, select the **Loki** datasource (Loki Local).
3. Run a query: `{app="sim-steward", env="local"}` and choose a recent time range.

## 5. Generate events and confirm

Use SimHub and the SimSteward dashboard: open the dashboard, connect to iRacing if available, trigger actions (e.g. replay controls). Logs should appear in Explore within a few seconds. Provisioned dashboards (Command Audit, Incident Timeline, Plugin Health, Session Overview) will show data for `app="sim-steward"`, `env="local"`.

## Storage path override

The stack stores Loki, Grafana, and Alloy data under a single host path. Default is `S:/sim-steward-grafana-storage`. To use a different path (e.g. `C:/data/grafana`), set `GRAFANA_STORAGE_PATH` in `observability/local/.env.observability.local` and start the stack with `npm run obs:up:env` so the env file is loaded. The compose file uses `${GRAFANA_STORAGE_PATH:-S:/sim-steward-grafana-storage}` for all three service volumes. Create the directory before first run.

## Watch logs in the terminal

To see SimSteward log lines as they land in Loki (without opening Grafana), run in a separate terminal:

```powershell
npm run obs:poll
```

This polls Loki every 2 seconds for `{app="sim-steward"}` and prints new lines. Ctrl+C to stop. Optional: `.\scripts\poll-loki.ps1 -LokiUrl http://localhost:3100 -Query '{app="sim-steward",env="local"}' -IntervalSeconds 3`.

## See also

- **docs/GRAFANA-LOGGING.md** — label schema, event taxonomy, LogQL.
- **docs/LOCAL-LOKI-LOGGING.md** — file-tail/gateway topology (Alloy → gateway → Loki).
- **docs/TROUBLESHOOTING.md** (Section 8) — logs not appearing in Grafana.
