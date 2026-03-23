# Sim Steward — Troubleshooting

If the dashboard or plugin "does not work", use this checklist to find the cause.

---

## 1. Plugin is loaded by SimHub

- **Check:** In SimHub, open the left menu (hamburger or sidebar). You should see **"Sim Steward"** (or the name from the plugin attributes). Click it to open the plugin settings; the panel shows "WebSocket server on port 19847", client count, and iRacing connection status.
- **If the plugin does not appear:** SimHub loads plugins from its **installation root** (e.g. `C:\Program Files (x86)\SimHub`). Deploy copies plugin DLLs to the **SimHub root** and the dashboard to **SimHub\Web\sim-steward-dash\**. Required DLLs: `SimSteward.Plugin.dll`, `Fleck.dll`, `Newtonsoft.Json.dll`, `IRSDKSharper.dll`, `YamlDotNet.dll`. If any are missing or SimHub was installed to a different path, set `SIMHUB_PATH` and run `deploy.ps1` again. Restart SimHub after copying.

---

## 2. WebSocket server is listening on port 19847

- **Check:** With SimHub running, open a browser and go to the dashboard URL (see step 3). The status indicator should turn green and show "Connected". If it stays red and shows "Cannot reach plugin (Sim Steward not running or port 19847 blocked?)", the plugin is not listening or something is blocking the port.
- **Causes:** (1) Plugin failed to start (see plugin log below). (2) Another app is using port 19847. (3) Firewall or security software blocking localhost.
- **Optional:** From PowerShell run `Test-NetConnection -ComputerName localhost -Port 19847` (or `netstat -an | findstr 19847`) while SimHub is running to see if the port is open.

---

## 3. Dashboard URL in SimHub

- The UI mounts inside SimHub _only_ as a **Web Page** (or **Web View**) component with the dashboard URL. It does **not** show up as a standalone template in the dashboard browser.
- **Steps:** Dash Studio → create or open a dashboard → add a Web Page/Web View component → set the URL to `http://localhost:8888/Web/sim-steward-dash/index.html`.
- **Check:** The page should show "Sim Steward" with connection status, mode, and replay controls. If the component stays blank or returns 404, the DashTemplate wasn’t deployed or SimHub cannot reach port 8888—run `deploy.ps1` again so `SimHub\Web\sim-steward-dash\` exists.
- If you have configured `SIMSTEWARD_WS_TOKEN`, append `?token=<value>` (or `?wsToken=<value>`) to the URL in Dash Studio so the dashboard forwards the token when it opens the WebSocket.

### 3b. Browser says connection refused on `localhost:8888` (or `127.0.0.1:8888`)

**Deploy is not an HTTP server.** `deploy.ps1` copies HTML/CSS/JS into `SimHub\Web\sim-steward-dash\`. **SimHub** must run its **built-in web server** on the configured port (default **8888**) so those files are reachable.

- **Smoke test:** With SimHub running, open **`http://127.0.0.1:8888/`** — you should see SimHub’s dash list (same check as [SimHub wiki: Dashstudio Web access](https://github.com/SHWotever/SimHub/wiki/Troubleshoot-Dashstudio-Web-access#check-is-simhub-server-is-running)). If that refuses, the problem is SimHub’s HTTP stack or port (not this plugin).
- **Check:** SimHub **Settings** → confirm the **HTTP / web / Dash** port matches **8888** (or use your configured port in every URL). Try another port if something else owns 8888, then restart SimHub.
- **Firewall / VPN:** Allow **SimHubWPF** (incoming **8888**). VPNs can block localhost routing on some setups.
- **WebSocket vs HTTP:** The plugin can listen on **19847** while **8888** is still down — green WS in Dash Studio does not prove **8888** is up.
- **404 on `data-capture-suite.html`:** Older `deploy.ps1` only copied `index.html` and `replay-incident-index.html`. Run **`.\deploy.ps1`** again so `data-capture-suite.html` is copied to `SimHub\Web\sim-steward-dash\`.

---

## 4. Plugin log

- The plugin writes to: `%LocalAppData%\SimHubWpf\PluginsData\SimSteward\plugin.log`
- **Check:** After starting SimHub, open that file. You should see lines like "SimSteward plugin Init", "DashboardBridge: WebSocket server started on port 19847", and "iRacing connected" when iRacing is running. If you see "WebSocket server could not start on port 19847", the port is in use or not bindable.

---

## 4b. Dashboard log stream empty (no entries when clicking Play or other buttons)

If the in-dashboard log stream stays empty when you click Play, capture, or other actions:

1. **Dashboard connected** — The status indicator must be green ("Connected"). If it is red, the WebSocket is not connected and log events are not sent to the dashboard.
2. **broadcast-errors.log** — When the plugin fails to send log events to the dashboard (e.g. WebSocket closed or Send threw), it writes a line to **`%LocalAppData%\SimHubWpf\PluginsData\SimSteward\broadcast-errors.log`**. This file is **not** written through the main logger (to avoid recursion). Check it if the log stream is empty but the dashboard shows connected:
   - **"Send:logEvents"** + exception message — Sending the log payload to the client failed (e.g. connection closed).
   - **"Broadcast skipped: 0 clients"** — No dashboard client was connected when the plugin tried to broadcast (throttled to at most once per 10 seconds).
   - **"OnLogWritten"** + exception — Serialization or broadcast failed in the log pipeline.
3. **Browser console** — Open DevTools (F12) → Console. If the dashboard receives `logEvents` but fails to render them, you will see `[SimSteward] logEvents display error` and the exception.

---

## 5. iRacing (optional for connection)

- Telemetry comes from iRacing via IRSDKSharper (shared memory). The dashboard can connect to the plugin even when iRacing is not running; mode will show "Unknown" and session time 0:00.
- For iRacing data: edit `%USERPROFILE%\Documents\iRacing\app.ini` and set `irsdkEnableMem=1`. Start a session or replay so the plugin can connect.

---

## 6. Incidents not detected during replay

If you run a replay and incidents are not captured or signaled:

**What iRacing exposes when:** See [docs/IRACING-DATA-AVAILABILITY.md](IRACING-DATA-AVAILABILITY.md). Live race vs replay vs post-results differ (especially for **per-car** incidents and **YAML results** fields). Do not assume a field populated in replay is populated the same way during a live race.

### Required checks

1. **iRacing shared memory enabled** — Edit `%USERPROFILE%\Documents\iRacing\app.ini` and ensure `irsdkEnableMem=1`. (Some iRacing versions expose this under Options > Graphics.) Without this, the plugin cannot connect.
2. **Sim Steward connected** — In the plugin settings, "iRacing connection status" should show "Connected" when a replay is loaded and playing. If it shows "Not connected", start the replay first, then ensure SimHub is running.
3. **Dashboard connected** — The status dot should be green ("Connected"). If it is red, the WebSocket is not connected and you will not see real-time feedback (although incidents are still stored; reconnect to see them).
4. **Focused car in replay** — The plugin uses **CamCarIdx** (camera-focused car) when valid, otherwise **DriverCarIdx** (your car). So when you "follow" another driver in replay, the incident count and feed show **that driver's** data, not the car you drove. `PlayerCarMyIncidentCount` from iRacing tracks the currently focused car in replay. If you are in an external camera view and no car is focused, CamCarIdx may be invalid and we fall back to DriverCarIdx; switch to a car's cockpit/view to get that driver's incidents.

5. **Seeking to an earlier point** — When you seek the replay backward (e.g. to lap 2), the plugin detects this and re-baselines. The incident feed clears and only incidents from that point forward are shown. Ensure the replay is **playing** (not paused) when you expect incidents; telemetry updates as the replay advances. If you seek and then hit Play, incidents should appear as they occur. If nothing appears, check that you're focused on a car that had incidents in that segment (see #4).

### Diagnostic: plugin log

- **Check:** Open `%LocalAppData%\SimHubWpf\PluginsData\SimSteward\plugin.log`.
- When an incident is detected, you should see lines like: `Incident captured: +2x #42 DriverName (source=player, sessionTime=123.4s)`.
- If you never see these lines during a replay that has incidents, the SDK is not receiving the data (shared memory off, wrong car focused, or session YAML not yet populated).
- If you see these lines but the dashboard does not show them, the WebSocket or dashboard URL may be incorrect.

### Incident point accuracy

- For the **player/focused car**, the plugin uses `PlayerCarMyIncidentCount` at 60 Hz. The **delta is the incident type** (1=off-track, 2=wall/spin, 4=heavy contact). Values should match iRacing.
- For **other drivers**, the plugin compares **per-driver `CurDriverIncidentCount`** from the session YAML (`DriverInfo`) on each `SessionInfoUpdate` — not `ResultsPositions` directly (see **IRACING-DATA-AVAILABILITY.md** Group 5 for when **final** `ResultsPositions[].Incidents` is meaningful). **Live race:** no per-car telemetry incident count for others; YAML/session-info behavior may still differ from replay — see the availability doc. At high replay speeds (e.g. 16x), iRacing batches updates — you may see a single +6x event instead of separate 2x+2x+2x. The total is correct; the per-incident breakdown is approximated.
- iRacing's **quick-succession rule**: multiple incidents in rapid succession can be merged. A 2x spin followed by 4x contact may show as +4x only (highest counts).

---

## Quick recap

| Symptom | What to check |
|--------|----------------|
| No "Sim Steward" in SimHub | DLLs in SimHub root; restart SimHub |
| Red status, "Cannot reach plugin" | Plugin log; port 19847 free; firewall |
| Incidents not detected in replay | Section 6: shared memory, connection, focused car, plugin.log |
| Blank or 404 in Web Page | URL = `http://localhost:8888/Web/sim-steward-dash/index.html`; run deploy |
| **Connection refused** on `:8888` | §3b: SimHub HTTP not listening — open `http://127.0.0.1:8888/`; Settings port/firewall |
| Mode always "Unknown" | iRacing running and shared memory enabled |
| No logs in Grafana / Loki | Section 8: SIMSTEWARD_LOKI_URL, local stack, auth, data source |
| Log stream empty when clicking buttons | Section 4b: connection, broadcast-errors.log, browser console |
| ContextStream 401 / index missing | Section 9: `.env` key, verify-key, ingest in interactive terminal |

---

## 7. Diagnostics & Metrics panel

The dashboard includes a collapsible **Diagnostics & Metrics** panel just below the connection status bar. It starts collapsed; click the header to expand it.

### Infrastructure indicators (coloured dots)

| Dot | What it shows | Green means | Yellow/Red means |
|-----|---------------|-------------|------------------|
| iRacing SDK | IRSDKSharper started | SDK loaded OK | Plugin failed to start SDK (check plugin.log) |
| WebSocket | Fleck server running on port | Server is listening | Bridge failed to start — port in use or firewall |
| Player Car | iRacing player car identified | Car index known | No focused car — switch to cockpit/TV camera |

**"Player car: Unknown"** means the player car index is not yet known from session YAML. Incident counts and feed still work for other drivers once the YAML baseline is established.

### YAML incident counter

Incident detection uses session YAML `CurDriverIncidentCount` (`yamlIncidentEvents`). The count accumulates from the moment iRacing connects and resets when iRacing disconnects, when you seek the replay backward, or when the session changes.

- **`yamlIncidentEvents` = 0 and YAML updates > 0**: the session YAML is being parsed but no other-driver incident deltas have been found yet (may be correct early in a session, or non-admin in race).
- **`yamlIncidentEvents` = 0**: iRacing is not connected or the replay has not advanced past an incident.

---

## 8. Logs not appearing in Grafana / Loki

For a step-by-step to get plugin data into **local** Grafana, see **docs/observability-local.md**.

If you expect SimSteward logs in Grafana (Cloud or local) but see none:

1. **Plugin output** — The plugin writes **plugin-structured.jsonl** only (plus WebSocket to the dashboard). It does **not** batch-POST those lines to Loki in-process yet. **`deploy.ps1`** can POST a **`deploy_marker`** when **`SIMSTEWARD_LOKI_URL`** is set (see **`send-deploy-loki-marker.ps1`**). For full logs in Loki, tail **plugin-structured.jsonl** with Alloy/Promtail or similar.
2. **Env metadata** — Set `SIMSTEWARD_LOKI_URL` and `SIMSTEWARD_LOG_ENV` before SimHub starts (e.g. `.env` loaded by **`deploy.ps1`** / **`run-simhub-local-observability.ps1`**) so JSON includes `loki_push_target` / `log_env`.
3. **Local stack** — Start observability from `observability/local/` (`npm run obs:up`) so Loki (3100) and Grafana (3000) run; compose does **not** ingest **plugin-structured.jsonl** automatically.
4. **Auth (Grafana Cloud / gateway)** — For **deploy markers**: Grafana Cloud uses **Basic** (`SIMSTEWARD_LOKI_USER` + **`SIMSTEWARD_LOKI_TOKEN`**); local **loki-gateway** uses **Bearer `LOKI_PUSH_TOKEN`**. Push failures print in the deploy script output.
5. **Data source in Grafana** — Point the Loki data source at your Loki URL (e.g. `http://localhost:3100` for local). Explore: `{app="sim-steward"}`.
6. **Debug vs production** — With `SIMSTEWARD_LOG_DEBUG=1`, many more lines (e.g. `tick_stats`, `yaml_update`) are sent. For AI or production dashboards, filter with `| level != "DEBUG"` to avoid noise.

See **docs/GRAFANA-LOGGING.md** for label schema, event taxonomy, and LogQL examples.

---

## 9. Prometheus / OTLP metrics (local stack)

For the full pipeline (collector, ports, Grafana datasource URL), see **docs/observability-local.md** § Canonical path and § Metrics / OTLP troubleshooting.

1. **Nothing in Explore (Prometheus Local)** — Confirm **`npm run obs:up`** is running and **`http://localhost:9090/-/healthy`** returns OK. Smoke: **`npm run obs:poll:prometheus`**.
2. **No `simsteward_*` metrics** — OTLP is disabled unless **`OTEL_EXPORTER_OTLP_ENDPOINT`** or **`SIMSTEWARD_OTLP_ENDPOINT`** is set **before** SimHub starts (SimHub does not load `.env` automatically). Use **`scripts/run-simhub-local-observability.ps1`** or set env in the user/session environment.
3. **`connection refused` to port 4317** — OpenTelemetry Collector is not up or ports are not mapped; restart compose from the repo root.
4. **Wrong protocol** — gRPC defaults for **`http://127.0.0.1:4317`**. For HTTP/protobuf on **4318**, set **`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`** and point the endpoint at **4318**.

---

## 10. ContextStream MCP (index / search / 401)

**Default workflow:** Keep the repo in sync with ContextStream using the **ContextStream MCP** **`project` tool** — `project(action="index")` or `project(action="ingest_local", path="<repo>")` — then log the run with `session(action="capture", event_type="operation", …)` per **docs/CONTEXTSTREAM-UPLOAD-PLAN.md**. Do **not** use ad-hoc HTTP/API scripts for routine sync. The CLI steps below are **troubleshooting only** when MCP or env is misconfigured.

- **401 on ingest or `verify-key`** — The ContextStream API key must be in `.env` (`CONTEXTSTREAM_API_KEY`, etc.) and loaded for CLI commands. From the repo root:  
  `npx -y envmcp --env-file .env cmd /c "%LocalAppData%\ContextStream\contextstream-mcp.exe verify-key"`  
  If that fails, rotate the key in the ContextStream account and update `.env` (do not commit real secrets).
- **`ingest` fails with "not a terminal"`** — From repo root (with `.env`): `powershell -ExecutionPolicy Bypass -File scripts/contextstream-ingest.ps1` (spawns `cmd` so the CLI sees a console). Or run `contextstream-mcp.exe ingest --path <repo>` manually in Windows Terminal / `cmd`. The MCP server uses the same key via Cursor env.
- **Search says index freshness `missing`** — After a successful ingest from step above, keyword search still works; semantic/index metadata syncs once ingestion completes.

### ContextStream KB links

| Spec | Doc ID |
|------|--------|
| Observability — Local Stack | `25ed8579-c142-4040-b9a2-87b14523475f` |
| Grafana Loki (summary) | `58a20aaf-bdde-4318-88f7-1ec8ec44377b` |
| Observability — Scaling | `99bd9e71-2b08-4eea-b2d4-f7bb22b38af0` |
| Sim Steward — Data Routing (OTel / Loki / Prometheus) | `cbae1c33-c778-4e9a-9a8d-6b3e3c8c368b` |

---

**Note:** "Memory bank" (file-based state sync, MCP server, `memory-bank/` directory) is **not** a project feature. It is only for the developer's personal Cursor/vibe coding. Do not treat it as a feature, task, or reference. It is expected to be missing.
