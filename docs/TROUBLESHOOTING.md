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
- For **other drivers**, data comes from `ResultsPositions[].Incidents` in the session YAML. At high replay speeds (e.g. 16x), iRacing batches updates — you may see a single +6x event instead of separate 2x+2x+2x. The total is correct; the per-incident breakdown is approximated.
- iRacing's **quick-succession rule**: multiple incidents in rapid succession can be merged. A 2x spin followed by 4x contact may show as +4x only (highest counts).

---

## Quick recap

| Symptom | What to check |
|--------|----------------|
| No "Sim Steward" in SimHub | DLLs in SimHub root; restart SimHub |
| Red status, "Cannot reach plugin" | Plugin log; port 19847 free; firewall |
| Incidents not detected in replay | Section 6: shared memory, connection, focused car, plugin.log |
| Blank or 404 in Web Page | URL = `http://localhost:8888/Web/sim-steward-dash/index.html`; run deploy |
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

### YAML incident counter

Incident detection uses session YAML `CurDriverIncidentCount` (`yamlIncidentEvents`). The count accumulates from the moment iRacing connects and resets when iRacing disconnects, when you seek the replay backward, or when the session changes.

- **`yamlIncidentEvents` = 0 and YAML updates > 0**: the session YAML is being parsed but no other-driver incident deltas have been found yet (may be correct early in a session, or non-admin in live race).
- **`yamlIncidentEvents` = 0**: iRacing is not connected or the replay has not advanced past an incident.

---

## 8. Logs not appearing in Grafana / Loki

For a step-by-step to get plugin data into **local** Grafana, see **docs/observability-local.md**.

If you expect SimSteward logs in Grafana (Cloud or local) but see none:

1. **Loki URL** — The plugin only pushes when `SIMSTEWARD_LOKI_URL` is set. Copy `.env.example` to `.env` and set `SIMSTEWARD_LOKI_URL` (e.g. `http://localhost:3100` for local Docker, or your Grafana Cloud Loki URL). Restart SimHub after changing `.env`.
2. **Local stack** — For local dev, start the stack from `observability/local/`: run `docker compose up -d` so Loki is listening on port 3100. Ensure the host path for Loki storage (e.g. `S:\sim-steward-grafana-storage`) exists before starting.
3. **Auth (Grafana Cloud)** — For Grafana Cloud, set `SIMSTEWARD_LOKI_USER` and `SIMSTEWARD_LOKI_TOKEN` in `.env` to your instance user ID and log-write token. Wrong or missing credentials cause push failures (check plugin.log for LokiSink warnings).
4. **Data source in Grafana** — In Grafana, add a Loki data source pointing at the same URL the plugin uses (e.g. `http://localhost:3100` for local). Use Explore and query `{app="sim-steward"}` to see streams.
5. **Debug vs production** — With `SIMSTEWARD_LOG_DEBUG=1`, many more lines (e.g. `state_broadcast_summary`, `tick_stats`, `yaml_update`) are sent. For AI or production dashboards, filter with `| level != "DEBUG"` to avoid noise.

See **docs/GRAFANA-LOGGING.md** for label schema, event taxonomy, and LogQL examples.

---

**Note:** "Memory bank" (file-based state sync, MCP server, `memory-bank/` directory) is **not** a project feature. It is only for the developer's personal Cursor/vibe coding. Do not treat it as a feature, task, or reference. It is expected to be missing.
