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
- For **other drivers**, live detection is telemetry-based, not points-based: `ReplayIncidentIndexDetector` watches `CarIdxTrackSurface` (off-track) and `CarIdxSessionFlags` (per-car flags) every tick and classifies the cause via `IncidentCauseMapping` — no admin required (see **IRACING-DATA-AVAILABILITY.md** Group 2). Their **official incident points are not available live**: `Sessions[].ResultsPositions[].Incidents` does **not** update progressively during a live session (confirmed empirically — see **IRACING-DATA-AVAILABILITY.md** Group 1/5, and the `live_yaml_incident_probe` log event). Points only resolve after the fact, via the replay/Index-tab path (`ReplayIncidentYamlDiff`/`ReplayIncidentIndexResultsYaml`), once results are final. On the dashboard, a live incident row with `pointsResolved: false` was detected but its points value is still unknown. At high replay speeds (e.g. 16x), iRacing batches YAML updates — the replay path may see a single +6x delta instead of separate 2x+2x+2x; the total is correct, the per-incident breakdown is approximated (`IsAggregateDelta`).
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

### Live incident detection

Live incident detection runs per-tick off `CarIdxTrackSurface`/`CarIdxSessionFlags`/`PlayerCarMyIncidentCount` (`ReplayIncidentIndexDetector`, orchestrated from `SimStewardPlugin.LiveIncidentDetection.cs`), not from a YAML incident counter. It re-baselines (clears pending state) when iRacing disconnects, when you seek the replay backward, or when the session changes (`live_incident_detection_baseline_ready`).

- **No live incident rows appear**: check that a session baseline has been established (`live_incident_detection_baseline_ready` in plugin.log) and that the focused car is actually going off-track or triggering a flag.
- **Live incident rows show no points (`pointsResolved: false`)**: expected for other cars — official points are not exposed live; they resolve later via the replay/Index-tab path.

---

## 8. Logs not appearing in Grafana / Loki

For a step-by-step to get plugin data into **local** Grafana, see **docs/observability-local.md**.

If you expect SimSteward logs in Grafana (Cloud or local) but see none:

1. **Plugin output** — The plugin writes **plugin-structured.jsonl** only (plus WebSocket to the dashboard). It does **not** batch-POST those lines to Loki in-process yet. **`deploy.ps1`** can POST a **`deploy_marker`** when **`SIMSTEWARD_LOKI_URL`** is set (see **`send-deploy-loki-marker.ps1`**). For full logs in Loki, use an external shipper to tail **plugin-structured.jsonl**.
2. **Env metadata** — Set `SIMSTEWARD_LOKI_URL` and `SIMSTEWARD_LOG_ENV` before SimHub starts (e.g. `.env` loaded by **`deploy.ps1`** / **`run-simhub-local-observability.ps1`**) so JSON includes `loki_push_target` / `log_env`.
3. **Local stack** — Start observability from `observability/local/` (`pnpm run obs:up`) so Loki (3100) and Grafana (3000) run; compose does **not** ingest **plugin-structured.jsonl** automatically.
4. **Auth (Grafana Cloud / gateway)** — For **deploy markers**: Grafana Cloud uses **Basic** (`SIMSTEWARD_LOKI_USER` + **`SIMSTEWARD_LOKI_TOKEN`**); local **loki-gateway** uses **Bearer `LOKI_PUSH_TOKEN`**. Push failures print in the deploy script output.
5. **Data source in Grafana** — Point the Loki data source at your Loki URL (e.g. `http://localhost:3100` for local). Explore: `{app="sim-steward"}`.
6. **Debug vs production** — With `SIMSTEWARD_LOG_DEBUG=1`, many more lines (e.g. `tick_stats`, `yaml_update`) are sent. For AI or production dashboards, filter with `| level != "DEBUG"` to avoid noise.

See **docs/GRAFANA-LOGGING.md** for label schema, event taxonomy, and LogQL examples.

---

## 9. Prometheus / OTLP metrics (local stack)

For the full pipeline (collector, ports, Grafana datasource URL), see **docs/observability-local.md** § Canonical path and § Metrics / OTLP troubleshooting.

1. **Nothing in Explore (Prometheus Local)** — Confirm **`pnpm run obs:up`** is running and **`http://localhost:9090/-/healthy`** returns OK. Smoke: **`pnpm run obs:poll:prometheus`**.
2. **No `simsteward_*` metrics** — OTLP is disabled unless **`OTEL_EXPORTER_OTLP_ENDPOINT`** or **`SIMSTEWARD_OTLP_ENDPOINT`** is set **before** SimHub starts (SimHub does not load `.env` automatically). Use **`scripts/run-simhub-local-observability.ps1`** or set env in the user/session environment.
3. **`connection refused` to port 4317** — OpenTelemetry Collector is not up or ports are not mapped; restart compose from the repo root.
4. **Wrong protocol** — gRPC defaults for **`http://127.0.0.1:4317`**. For HTTP/protobuf on **4318**, set **`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`** and point the endpoint at **4318**.

---

**Note:** "Memory bank" (file-based state sync, MCP server, `memory-bank/` directory) is **not** a project feature. It is only for the developer's personal Cursor/vibe coding. Do not treat it as a feature, task, or reference. It is expected to be missing.
