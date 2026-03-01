---
name: simsteward-deploy
description: Deploys SimSteward to SimHub both manually and via a watch mode that reruns the existing deploy.ps1 workflow after relevant source changes. Use when the user mentions deployment, redeployment, hot reload, automatic deploy-after-change, watch mode, or any request to refresh the dashboard/plugin in SimHub after a code edit.
---

# SimSteward Deploy Workflow

## Quick start
- **Manual deploy:** `pwsh -File .\deploy.ps1`
- **Watch deploy:** `pwsh -File .\scripts\watch-deploy.ps1`

## SimHub deployment locations (one root, two targets)

SimHub uses a **single installation root** (default `C:\Program Files (x86)\SimHub`). Both the plugin and the dashboard deploy under that root, in different places:

| What | Deploy path (under SimHub root) | Purpose |
|------|----------------------------------|--------|
| **Plugin** | Root folder (no subfolder) | DLLs sit next to SimHub's own DLLs; SimHub discovers and loads them automatically. No config file. |
| **Dashboard (HTML)** | `Web\sim-steward-dash\` | SimHub's built-in server (port 8888) serves static files from `Web/`. Opened via a Web Page / Web View component with URL `http://localhost:8888/Web/sim-steward-dash/index.html`. |

- **Plugin path**: e.g. `C:\Program Files (x86)\SimHub\` (or `$env:SIMHUB_PATH`). Files: `SimSteward.Plugin.dll`, `Fleck.dll`, `Newtonsoft.Json.dll`, `IRSDKSharper.dll`, `YamlDotNet.dll`.
- **Dashboard path**: `SimHub\Web\sim-steward-dash\` — contains `index.html`, `README.txt`, and any other static assets. Do **not** use `DashTemplates` for this HTML UI; `DashTemplates` is for SimHub's dashboard catalog (`.djson` layouts), not standalone HTML served via Web Page URL.

## Testing gate — hard requirement

No deployment is successful unless **all tests pass at 100%**. The deploy pipeline enforces this automatically:

### Test phases

| Phase | When | What runs | Blocking? |
|-------|------|-----------|-----------|
| **Build** | Before deploy | `dotnet build` — 0 errors, 0 warnings-as-errors | Yes |
| **Unit tests** | After build | `dotnet test` on any test project in the solution | Yes |
| **Post-deploy** | After file copy | All `*.ps1` scripts in `tests/` (e.g. `WebSocketConnectTest.ps1`) | Yes (if SimHub is running) |

### Retry-once-then-stop rule

When a test phase fails:

1. **First failure** — the agent gets **one additional pass** to fix the issue and rerun. Fixes might include correcting code, adjusting a threshold, or updating a test expectation.
2. **Second failure** — **hard stop**. Do NOT keep iterating. Either:
   - **Stop the deploy** entirely if downstream work depends on it, OR
   - **Skip and move on** to the next independent task if nothing else depends on the failing item.
3. Never silently swallow test failures. Always surface the failing test name, output, and exit code in the logs and the event stream.

### What counts as a test

- `dotnet build` returning exit code 0 with 0 errors.
- `dotnet test` (if a test project exists) returning exit code 0 with 0 failed tests.
- Every `*.ps1` file under `tests/` exiting with code 0 and printing only `PASS:` lines (any `FAIL:` line or non-zero exit = failure).
- Linter checks (`ReadLints`) on edited files showing 0 new errors introduced.

### Agent behaviour during deploy

When the deploy skill is invoked (manually or by the watcher):

1. Run `dotnet build`. If it fails → attempt fix → rebuild. If still fails → **stop**.
2. Run `dotnet test` (if test projects exist). If failures → attempt fix → retest. If still fails → **stop**.
3. Copy files to SimHub.
4. Run post-deploy tests (if SimHub is running). If failures → attempt fix → retest. If still fails → **report failure, skip further dependent steps**.
5. Only after all green: report "Deploy successful".

## Cursor tab hygiene
- Before running a deploy (manual or from the watcher), close any open SimSteward tabs in Cursor so the IDE picks up the fresh dashboard files. Closing both the plugin/dashboard files and the Web view avoids stale previews.
- After deployment, relaunch the dashboard in the browser the usual way (for example, `browser_navigate http://localhost:8888/Web/sim-steward-dash/index.html` followed by `browser_unlock` if you had it locked) so Cursor loads the updated assets.

## Manual deploy steps
1. Ensure the SimHub SDK DLLs live or are copied into `lib\SimHub\` (the script will copy them from the detected SimHub install if needed).
2. Run `pwsh -File .\deploy.ps1` from the repo root. The script:
   - Locates SimHub via `SIMHUB_PATH`, registry, running process, or the default `C:\Program Files (x86)\SimHub`.
   - Builds `src\SimSteward.Plugin\SimSteward.Plugin.csproj` in Release and finds the `bin\Plugin\` output.
   - **Runs tests** — executes `dotnet test` on any test project in the solution. Aborts deploy on failure.
   - Copies `SimSteward.Plugin.dll` plus `Fleck.dll`, `Newtonsoft.Json.dll`, `IRSDKSharper.dll`, and `YamlDotNet.dll` into the **SimHub installation root**.
   - Deploys `src\SimSteward.Dashboard\index.html` (and optional README) into **SimHub\Web\sim-steward-dash**.
   - Closes running SimHub, verifies the copied DLLs, and relaunches SimHub (unless `SIMHUB_SKIP_LAUNCH=1`).
   - **Runs post-deploy tests** — executes all `tests/*.ps1` scripts. Reports pass/fail.

## Watch deploy mode
- Start `pwsh -File .\scripts\watch-deploy.ps1`. It watches `src/SimSteward.Plugin/`, `src/SimSteward.Dashboard/`, and `deploy.ps1` for edits.
- Each time a burst of changes settles, the watcher runs `deploy.ps1` with `SIMHUB_SKIP_LAUNCH=1` so SimHub stays running while binaries refresh.
- The watcher debounces filesystem noise (ignores `bin/`, `obj/`, and `.git/`), ensures only one deploy executes at a time, and queues exactly one rerun if new events arrive mid-deploy.
- Interrupt with `Ctrl+C` to exit gracefully; the script prints change/ deploy status and final success/failure.

## Environment knobs
- `SIMHUB_PATH`: override the auto-detected SimHub location (used by both manual and watch modes).
- `SIMHUB_SKIP_LAUNCH=1`: prevent the deploy script from relaunching SimHub (recommended during watch mode or if you prefer managing SimHub outside the script).
- `SIMSTEWARD_SKIP_TESTS=1`: skip the test phase (escape hatch for emergencies only — never use in normal workflow).
- `SIMSTEWARD_WS_BIND` / `SIMSTEWARD_WS_PORT`: document existing WebSocket defaults if the skill needs to mention them.

## Troubleshooting
- If `deploy.ps1` cannot find `SimHubWPF.exe`, ensure SimHub is installed or point `SIMHUB_PATH` to the correct folder.
- If the build output is missing, rerun the script manually to inspect the log; the watcher echoes and aborts the failing deploy so you can fix compilation errors.
- If tests fail after the one-retry attempt, do NOT continue retrying. Read the error output, diagnose the root cause, and fix it before deploying again.
- When the watcher reports constant file updates, confirm `bin/`, `obj/`, and `.git/` paths exist outside `src/` (the watcher ignores them) and that your editor isn't writing to the dashboard target folder inside SimHub.

## References
- `[deploy.ps1](deploy.ps1)` – existing build + copy workflow for plugin DLLs and dashboard files.
- `[scripts/watch-deploy.ps1](scripts/watch-deploy.ps1)` – file-watcher helper that reruns `deploy.ps1` after debounced changes.
- `[tests/WebSocketConnectTest.ps1](tests/WebSocketConnectTest.ps1)` – post-deploy integration test for WebSocket connectivity.
