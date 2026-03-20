# Local Grafana/Loki Log Push

This setup pushes **file-based** logs into Loki (via Alloy and a token-protected gateway) and visualizes them in Grafana. For **plugin → Loki direct push** (SimSteward plugin to Grafana Cloud or local Loki), see **docs/GRAFANA-LOGGING.md**. This doc focuses on the local file-tail/gateway topology and least-privilege ingress control.

## Topology

- `grafana` on `http://localhost:3000`
- `loki` on `http://localhost:3100` (internal use and optional direct debugging)
- `loki-gateway` on `http://localhost:3500` (external write ingress, token-protected)
- `alloy` tails local files and pushes to `loki-gateway`

All files live under `observability/local/`.

## Security model (least privilege)

- A dedicated `LOKI_PUSH_TOKEN` is required for `POST /loki/api/v1/push`.
- `loki-gateway` denies every other route (`403`).
- Shipper (`alloy`) receives only the push token via env var.
- Grafana reads Loki directly over internal Docker network and does not need the push token.

This is intentionally write-only at ingress, so the token cannot be used for read/query/admin paths.

## Dependencies and order

1. Docker Engine running.
2. Create local env file with admin credentials and push token.
3. Start Compose stack.
4. Confirm Grafana/Loki health.
5. Append synthetic logs to the sample log file.
6. Validate ingestion in Grafana and via MCP Loki query tools.

## Setup

1. Create local env file:
   - Copy `observability/local/.env.observability.example` to `observability/local/.env.observability.local`.
2. Generate a strong token (PowerShell example):
   - `[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))`
   - Put that value into `LOKI_PUSH_TOKEN`.
3. Bring the stack up:
   - `docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml up -d`
4. Verify health:
   - `docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml ps`

## Shipper behavior

- Input path: `observability/local/sample-logs/*.log` mounted to `/var/log/simsteward/*.log`.
- Labels:
  - `app=sim-steward`
  - `env=local`
  - `host=dev-machine`
  - `stack=sim-steward-local`
- Push endpoint:
  - `http://loki-gateway:3500/loki/api/v1/push`
- Auth header:
  - `Authorization: Bearer <LOKI_PUSH_TOKEN>`

Append a test line:

- `Add-Content observability/local/sample-logs/app.log "$(Get-Date -Format o) level=info component=test msg=""local loki test"""` 

## Validation with Grafana/Loki MCP

These tools are provided by the Grafana/Loki MCP (e.g. user-MCP_DOCKER). Ensure Grafana base URL (and auth if needed) is configured for the MCP. The local stack uses datasource UID `loki_local` (see `observability/local/grafana/provisioning/datasources/loki.yml`).

1. Discover Loki datasource UID:
   - `list_datasources` with type `loki`.
   - Expected UID in this setup: `loki_local`.
2. Confirm labels:
   - `list_loki_label_names` for datasource UID.
   - `list_loki_label_values` for label `app`.
3. Query logs:
   - `query_loki_logs` with `logql: {app="sim-steward",env="local"}` and recent time window.
4. Optional volume check:
   - `query_loki_stats` with same selector.

## Success criteria

- Grafana datasource health is green.
- New test log lines appear in Explore for `{app="sim-steward",env="local"}`.
- MCP label discovery returns expected labels and values.
- MCP log queries return newly appended lines within expected delay.

## Negative tests (least privilege)

- Missing token to gateway push endpoint returns `401`.
- Wrong token to gateway push endpoint returns `401`.
- Any non-push path on gateway returns `403`.

## Rotation and revocation

1. Generate a new token.
2. Update `LOKI_PUSH_TOKEN` in `observability/local/.env.observability.local`.
3. Restart `alloy` and `loki-gateway`:
   - `docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml up -d loki-gateway alloy`
4. Confirm new logs ingest.
5. Remove old token from local secret history/storage.

## Troubleshooting

- No logs in Grafana:
  - Check `alloy` container logs for auth errors.
  - Confirm token in env file matches gateway expectation.
  - Verify test log file is being appended.
- Gateway auth failing:
  - Confirm `Authorization` header is exactly `Bearer <token>`.
  - Ensure no whitespace or quoting issues in env file.
- Datasource not found by MCP:
  - Confirm Grafana is up and datasource provisioning file exists at `observability/local/grafana/provisioning/datasources/loki.yml`.
  - Ensure Grafana base URL (e.g. `http://localhost:3000`) is configured for the Grafana/Loki MCP (e.g. user-MCP_DOCKER).

### Logs not reaching Grafana?

- **Plugin / iRacing logs:** Alloy must tail the **same directory** where the plugin writes `plugin-structured.jsonl`. Set `SIMSTEWARD_DATA_PATH` in `.env.observability.local` (or in the environment when starting Docker) to the **exact** SimHub plugin data directory, e.g. `C:\Users\<you>\AppData\Local\SimHubWpf\PluginsData\SimSteward` on Windows (or `%LOCALAPPDATA%\SimHubWpf\PluginsData\SimSteward`). Then restart Alloy so the new mount is used: `docker compose -f observability/local/docker-compose.yml restart alloy` (run from the directory that has your env file). If this is not set, Alloy tails only the default `./sample-logs` and will not see plugin or iRacing logs.
- **Alloy container logs:** After bringing up the stack, check the Alloy container logs for messages about "targets" or any errors related to `/var/log/simsteward` to confirm it is actively tailing the plugin structured log.

### ContextStream / Session Start Note

For full session tracking and plan synchronization, remember to initialize ContextStream (`init()` then `context()`) at the start of each new chat session, as per project rules.
