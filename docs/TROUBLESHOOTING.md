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
4. **Focused car in replay** — `PlayerCarMyIncidentCount` (used for the driver you control/spectate) tracks the **currently focused car** in the replay. If you are in an external camera view and no car is "focused", incidents for other cars come from the session YAML (ResultsPositions), which updates in batches. Switch to a car's cockpit view if you want per-incident detection for that driver.

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

---

## 7. Diagnostics & Metrics panel

The dashboard includes a collapsible **Diagnostics & Metrics** panel just below the connection status bar. It starts collapsed; click the header to expand it.

### Infrastructure indicators (coloured dots)

| Dot | What it shows | Green means | Yellow/Red means |
|-----|---------------|-------------|------------------|
| iRacing SDK | IRSDKSharper started | SDK loaded OK | Plugin failed to start SDK (check plugin.log) |
| WebSocket | Fleck server running on port | Server is listening | Bridge failed to start — port in use or firewall |
| Memory Bank | File-based state sync available | Files are writable | Path inaccessible — check permissions |
| Player Car | iRacing player car identified | Car index known | No focused car — switch to cockpit/TV camera |

**"Player car: Unknown"** means `PlayerCarMyIncidentCount` has no target. Incident Layer 1 will not fire until a car is focused. In replay, click a car's cockpit camera.

### Layer counters (L1–L4 and 0x)

These accumulate from the moment iRacing connects and reset when iRacing disconnects, when you seek the replay backward, or when the session changes.

- **L1 = 0 and iRacing connected**: no incident has been detected on the focused car yet — or the car has not had any.
- **L4 = 0 and YAML updates > 0**: the session YAML is being parsed but no other-driver incidents have been found (may be correct early in a session).
- **All = 0**: iRacing is not connected or the replay has not advanced past an incident.

---

## 8. Memory Bank

The plugin writes four files to the memory bank directory on every state tick (throttled to ~1 s when unchanged):

| File | Content |
|------|---------|
| `snapshot.json` | Full telemetry + incidents + metrics + diagnostics |
| `metrics.json` | Compact per-layer counts and infrastructure status |
| `HEALTH.md` | Human-readable health report with diagnostic notes |
| `activeContext.md` / `tasks.md` / `progress.md` | Project task context |

**Default path (Windows):** `%LocalAppData%\SimHubWpf\PluginsData\SimSteward\memory-bank\`

Override with the `MEMORY_BANK_PATH` environment variable before launching SimHub.

### Troubleshooting memory bank issues

- **Memory Bank dot is yellow/red in the dashboard**: the plugin could not create or write to the path. Check that `%LocalAppData%\SimHubWpf\PluginsData\SimSteward\` is writable (not on a read-only drive, not blocked by antivirus).
- **Files exist but are stale**: iRacing is not connected. The plugin only updates the memory bank when a state change occurs.
- **HEALTH.md shows "zero events detected" with iRacing connected**: see Section 6 for the incident detection checklist.

---

## 9. SimSteward MCP Server

The SimSteward MCP server lets Cursor AI actively query the plugin's live state without opening files manually.

### Setup

The server is registered in `C:\Users\winth\.cursor\mcp.json` as:
```json
"SimSteward": {
  "command": "node",
  "args": ["c:\\Users\\winth\\dev\\sim-steward\\mcp-server\\index.mjs"],
  "env": {}
}
```

Restart Cursor after editing `mcp.json`. The server reads from the same memory bank directory as the plugin.

### Verifying the server started

Open Cursor Settings → MCP → look for "SimSteward" in the server list. It should show as connected. If it shows an error, run `node c:\Users\winth\dev\sim-steward\mcp-server\index.mjs` in a terminal to see the startup error.

### Available tools

| Tool | What it returns |
|------|-----------------|
| `simsteward_health` | Full HEALTH.md report — start here |
| `simsteward_metrics` | Parsed metrics.json with layer counts and infra status |
| `simsteward_incidents` | Recent incidents (supports limit, source, minDelta filters) |
| `simsteward_drivers` | Driver standings sorted by incident count |
| `simsteward_context` | activeContext.md + tasks.md + progress.md combined |
| `simsteward_replay` | Replay state (frame position, speed, playing status) |
| `simsteward_track` | Track name, category, length |
| `simsteward_markers` | Project markers (current task, last action, notes) |

The server also exposes three resources: `simsteward://health`, `simsteward://metrics`, `simsteward://snapshot`.

### Common issues

- **"Memory bank not available"**: SimHub with the SimSteward plugin has not run yet, or `MEMORY_BANK_PATH` is set to a non-existent path.
- **Stale data**: the snapshot age (shown in every tool result as `snapshotAgeSeconds`) tells you how old the last plugin write was. If it is more than 5 seconds, the plugin may not be running.
