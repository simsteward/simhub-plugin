---
name: simsteward-deploy
description: Deploy/Watch commands for SimHub plugins.
---
# SimSteward Deploy Workflow

## Quick start
- **Manual deploy:** `pwsh -File .\deploy.ps1` — optional **`-EnvFile`** `C:\path\to\secrets.env` or repo-relative (`.env.prod`); merges `observability/local/.env.observability.local` after.
- **Watch deploy:** `pwsh -File .\scripts\watch-deploy.ps1` — same **`-EnvFile`** passthrough to deploy.
- **Agent reply:** After deploy, tell the user the **plugin version** printed by the script (`=== SimSteward plugin version (deployed): … ===` and `Deploy complete. Plugin version: …`). Matches SimHub dashboard / WebSocket `pluginVersion`.

## Locations
- **Plugin:** `C:\Program Files (x86)\SimHub\` (or `$env:SIMHUB_PATH`)
- **Dashboards (always part of deploy):** Every `*.html` in `src/SimSteward.Dashboard/` is copied to `SimHub\Web\sim-steward-dash\` plus `README.txt`. Verification fails if any source `.html` is missing or empty on disk after copy. Served when SimHub HTTP is up: `http://<host>:8888/Web/sim-steward-dash/<name>.html` (main: `index.html`).

## Testing Gate
- Deploy MUST pass 100%. Pipeline enforces:
  - `dotnet build` (0 errors)
  - `dotnet test` 
  - `tests/*.ps1` (e.g. WebSocketConnectTest.ps1)
- **Retry-once-then-stop:** Agent gets 1 retry per test phase. Stop on 2nd failure.

## Setup
- `SIMHUB_PATH`: Override SimHub path
- `SIMHUB_SKIP_LAUNCH=1`: Prevent restart
- `SIMSTEWARD_WS_BIND`: Override Fleck bind (Default: `0.0.0.0`)
- `SIMSTEWARD_WS_PORT`: Override Fleck port (Default: `19847`)
