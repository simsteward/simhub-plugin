# FPS Capture → Grafana Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship real game-FPS and display-FPS metrics for iRacing and the pit-wall browser into Grafana Cloud, via a standalone always-on `fps-exporter` service that Alloy scrapes alongside its existing `windows_exporter` target.

**Architecture:** `PresentMon.exe` streams live per-frame CSV to `fps-exporter` (a zero-dependency Node.js service), which aggregates a trailing 5s rolling window per tracked process into three Prometheus metrics and serves them on `http://127.0.0.1:9101/metrics`. Alloy gets one new `prometheus.scrape` block forwarding to its existing `prometheus.remote_write.grafana_cloud` component. `fps-exporter` runs as a Windows Service (NSSM, `LocalSystem`) so PresentMon's admin/ETW requirement is satisfied once at install time.

**Tech Stack:** Node.js (built-in `node:test`, `node:child_process`, `node:http` only — no new npm dependencies), PowerShell (service install), Grafana Alloy (flow-mode config), PresentMon 2.5.1 (already downloaded + SHA-256 verified at `%USERPROFILE%\Tools\PresentMon\PresentMon.exe`).

## Global Constraints

- Zero new npm dependencies for `fps-exporter` itself (project's `package.json` has only `dotenv-cli`/`secretlint` as devDependencies — matches that lean footprint).
- Test convention: Node's built-in `node:test` + `node:assert/strict`, CommonJS (`require`/`module.exports`), `<name>.js` + `<name>.test.js` pairs — matches `scripts/test-rig/run.js` / `run.test.js`.
- Curated process allowlist only: `iRacingSim64DX11.exe`, `chrome.exe`. Do not track all processes.
- Metrics: `game_fps{process}` (gauge), `display_fps{process}` (gauge), `frames_dropped_total{process}` (counter). No fabricated `0` for FPS gauges when a process has no recent data — omit the line so Grafana shows a genuine gap. `frames_dropped_total` is always emitted (a true 0 is not fabricated data).
- Rolling window: 5 seconds. Alloy scrape interval for this target specifically: 5s (per-target override, does not change other targets).
- `C:\Program Files\GrafanaLabs\Alloy\config.alloy` is ACL-restricted to SYSTEM/Administrators — confirmed during design. Any task touching it requires an elevated session; it cannot be edited from a standard Claude Code session on this machine.
- Full design context: `docs/superpowers/specs/2026-07-26-fps-capture-grafana-design.md`.

**Before Task 1:** create and switch to a feature branch (e.g. `git checkout -b feat/fps-capture-grafana`) before making any changes. The design spec for this feature landed on `main` by accident (branch had changed underneath an earlier session) — don't repeat that for the implementation itself.

---

### Task 1: `presentmon-csv.js` — parse PresentMon's streamed CSV lines

**Files:**
- Create: `scripts/fps-exporter/presentmon-csv.js`
- Test: `scripts/fps-exporter/presentmon-csv.test.js`

**Interfaces:**
- Produces: `parseHeader(line: string): string[]`, `parseRow(headerCols: string[], line: string): Record<string, string>` (values stay as raw strings; callers parse numerics themselves)

- [ ] **Step 1: Write the failing tests**

```js
// scripts/fps-exporter/presentmon-csv.test.js
'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseHeader, parseRow } = require('./presentmon-csv.js');

test('parseHeader: splits and trims column names', () => {
  const cols = parseHeader('Application,ProcessID,MsBetweenPresents,MsBetweenDisplayChange,DisplayedTime');
  assert.deepEqual(cols, ['Application', 'ProcessID', 'MsBetweenPresents', 'MsBetweenDisplayChange', 'DisplayedTime']);
});

test('parseRow: zips values with header into an object', () => {
  const cols = ['Application', 'ProcessID', 'MsBetweenPresents', 'MsBetweenDisplayChange', 'DisplayedTime'];
  const row = parseRow(cols, 'iRacingSim64DX11.exe,12345,16.683,16.683,16.683');
  assert.deepEqual(row, {
    Application: 'iRacingSim64DX11.exe',
    ProcessID: '12345',
    MsBetweenPresents: '16.683',
    MsBetweenDisplayChange: '16.683',
    DisplayedTime: '16.683',
  });
});

test('parseRow: preserves NA for dropped frames', () => {
  const cols = ['Application', 'DisplayedTime'];
  const row = parseRow(cols, 'chrome.exe,NA');
  assert.equal(row.DisplayedTime, 'NA');
});

test('parseRow: throws on column/value count mismatch', () => {
  const cols = ['Application', 'ProcessID'];
  assert.throws(() => parseRow(cols, 'chrome.exe'), /column count mismatch/);
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test scripts/fps-exporter/presentmon-csv.test.js`
Expected: FAIL — `Cannot find module './presentmon-csv.js'`

- [ ] **Step 3: Write the implementation**

```js
// scripts/fps-exporter/presentmon-csv.js
'use strict';

function parseHeader(line) {
  return line.split(',').map((s) => s.trim());
}

function parseRow(headerCols, line) {
  const values = line.split(',');
  if (values.length !== headerCols.length) {
    throw new Error(`column count mismatch: expected ${headerCols.length}, got ${values.length}`);
  }
  const row = {};
  for (let i = 0; i < headerCols.length; i++) {
    row[headerCols[i]] = values[i];
  }
  return row;
}

module.exports = { parseHeader, parseRow };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/fps-exporter/presentmon-csv.test.js`
Expected: 4 tests pass

- [ ] **Step 5: Commit**

```bash
git add scripts/fps-exporter/presentmon-csv.js scripts/fps-exporter/presentmon-csv.test.js
git commit -m "feat(fps-exporter): parse PresentMon streamed CSV lines"
```

---

### Task 2: `metrics-aggregator.js` — rolling window + Prometheus text rendering

**Files:**
- Create: `scripts/fps-exporter/metrics-aggregator.js`
- Test: `scripts/fps-exporter/metrics-aggregator.test.js`

**Interfaces:**
- Consumes: nothing from Task 1 directly (accepts already-parsed row objects, i.e. the shape `parseRow` returns)
- Produces: `class MetricsAggregator { constructor(allowlist: string[]); recordRow(row: Record<string,string>, now: number): void; renderPrometheusText(now: number): string }`, plus exported constant `WINDOW_MS`

- [ ] **Step 1: Write the failing tests**

```js
// scripts/fps-exporter/metrics-aggregator.test.js
'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { MetricsAggregator } = require('./metrics-aggregator.js');

test('recordRow + renderPrometheusText: computes game_fps from MsBetweenPresents', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  for (let i = 0; i < 10; i++) {
    agg.recordRow({
      Application: 'iRacingSim64DX11.exe',
      MsBetweenPresents: '16.667',
      MsBetweenDisplayChange: '16.667',
      DisplayedTime: '16.667',
    }, 1000 + i * 10);
  }
  const text = agg.renderPrometheusText(1100);
  const match = text.match(/game_fps\{process="iRacingSim64DX11\.exe"\} ([\d.]+)/);
  assert.ok(match, 'game_fps line present');
  assert.ok(Math.abs(parseFloat(match[1]) - 60) < 0.5, `expected ~60 fps, got ${match[1]}`);
});

test('renderPrometheusText: omits game_fps/display_fps for a process with no recent data', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('game_fps{process="iRacingSim64DX11.exe"}'));
  assert.ok(!text.includes('display_fps{process="iRacingSim64DX11.exe"}'));
});

test('renderPrometheusText: still emits frames_dropped_total 0 for a process with no drops yet', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  const text = agg.renderPrometheusText(1000);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe"} 0'));
});

test('recordRow: DisplayedTime "NA" counts as a dropped frame and is excluded from display_fps', () => {
  const agg = new MetricsAggregator(['chrome.exe']);
  agg.recordRow({ Application: 'chrome.exe', MsBetweenPresents: '10', MsBetweenDisplayChange: '10', DisplayedTime: '10' }, 1000);
  agg.recordRow({ Application: 'chrome.exe', MsBetweenPresents: '10', MsBetweenDisplayChange: 'NA', DisplayedTime: 'NA' }, 1010);
  const text = agg.renderPrometheusText(1020);
  assert.ok(text.includes('frames_dropped_total{process="chrome.exe"} 1'));
});

test('recordRow: ignores processes not in the allowlist', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'notepad.exe', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(1000);
  assert.ok(!text.includes('notepad.exe'));
});

test('recordRow + renderPrometheusText: prunes entries older than the 5s window', () => {
  const agg = new MetricsAggregator(['iRacingSim64DX11.exe']);
  agg.recordRow({ Application: 'iRacingSim64DX11.exe', MsBetweenPresents: '16.667', MsBetweenDisplayChange: '16.667', DisplayedTime: '16.667' }, 1000);
  const text = agg.renderPrometheusText(7001);
  assert.ok(!text.includes('game_fps{process="iRacingSim64DX11.exe"}'));
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test scripts/fps-exporter/metrics-aggregator.test.js`
Expected: FAIL — `Cannot find module './metrics-aggregator.js'`

- [ ] **Step 3: Write the implementation**

```js
// scripts/fps-exporter/metrics-aggregator.js
'use strict';

const WINDOW_MS = 5000;

class MetricsAggregator {
  constructor(allowlist) {
    this.allowlist = new Set(allowlist);
    this.presents = new Map();
    this.displayed = new Map();
    this.dropped = new Map();
    for (const proc of allowlist) {
      this.presents.set(proc, []);
      this.displayed.set(proc, []);
      this.dropped.set(proc, 0);
    }
  }

  recordRow(row, now) {
    const proc = row.Application;
    if (!this.allowlist.has(proc)) return;

    const msBetweenPresents = parseFloat(row.MsBetweenPresents);
    if (Number.isFinite(msBetweenPresents)) {
      this.presents.get(proc).push({ ts: now, ms: msBetweenPresents });
    }

    if (row.DisplayedTime === 'NA') {
      this.dropped.set(proc, this.dropped.get(proc) + 1);
    } else {
      const msBetweenDisplayChange = parseFloat(row.MsBetweenDisplayChange);
      if (Number.isFinite(msBetweenDisplayChange)) {
        this.displayed.get(proc).push({ ts: now, ms: msBetweenDisplayChange });
      }
    }

    this._prune(proc, now);
  }

  _prune(proc, now) {
    const cutoff = now - WINDOW_MS;
    this.presents.set(proc, this.presents.get(proc).filter((e) => e.ts >= cutoff));
    this.displayed.set(proc, this.displayed.get(proc).filter((e) => e.ts >= cutoff));
  }

  _avgFps(entries) {
    if (entries.length === 0) return null;
    const avgMs = entries.reduce((sum, e) => sum + e.ms, 0) / entries.length;
    if (avgMs <= 0) return null;
    return 1000 / avgMs;
  }

  renderPrometheusText(now) {
    const gameFpsLines = [];
    const displayFpsLines = [];
    const droppedLines = [];

    for (const proc of this.allowlist) {
      this._prune(proc, now);

      const gameFps = this._avgFps(this.presents.get(proc));
      if (gameFps !== null) {
        gameFpsLines.push(`game_fps{process="${proc}"} ${gameFps.toFixed(2)}`);
      }

      const displayFps = this._avgFps(this.displayed.get(proc));
      if (displayFps !== null) {
        displayFpsLines.push(`display_fps{process="${proc}"} ${displayFps.toFixed(2)}`);
      }

      droppedLines.push(`frames_dropped_total{process="${proc}"} ${this.dropped.get(proc)}`);
    }

    const lines = [];
    if (gameFpsLines.length > 0) lines.push('# TYPE game_fps gauge', ...gameFpsLines);
    if (displayFpsLines.length > 0) lines.push('# TYPE display_fps gauge', ...displayFpsLines);
    lines.push('# TYPE frames_dropped_total counter', ...droppedLines);

    return lines.join('\n') + '\n';
  }
}

module.exports = { MetricsAggregator, WINDOW_MS };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/fps-exporter/metrics-aggregator.test.js`
Expected: 6 tests pass

- [ ] **Step 5: Commit**

```bash
git add scripts/fps-exporter/metrics-aggregator.js scripts/fps-exporter/metrics-aggregator.test.js
git commit -m "feat(fps-exporter): rolling-window FPS aggregation + Prometheus rendering"
```

---

### Task 3: `backoff.js` — respawn backoff calculation

**Files:**
- Create: `scripts/fps-exporter/backoff.js`
- Test: `scripts/fps-exporter/backoff.test.js`

**Interfaces:**
- Produces: `INITIAL_BACKOFF_MS: number`, `MAX_BACKOFF_MS: number`, `HEALTHY_RUN_RESET_MS: number`, `nextBackoffMs(currentBackoffMs: number): number`, `backoffAfterExit(currentBackoffMs: number, runDurationMs: number): number`

- [ ] **Step 1: Write the failing tests**

```js
// scripts/fps-exporter/backoff.test.js
'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { INITIAL_BACKOFF_MS, MAX_BACKOFF_MS, backoffAfterExit } = require('./backoff.js');

test('backoffAfterExit: doubles backoff after a short-lived run (crash loop)', () => {
  const next = backoffAfterExit(INITIAL_BACKOFF_MS, 1000);
  assert.equal(next, INITIAL_BACKOFF_MS * 2);
});

test('backoffAfterExit: caps backoff at MAX_BACKOFF_MS', () => {
  const next = backoffAfterExit(MAX_BACKOFF_MS, 1000);
  assert.equal(next, MAX_BACKOFF_MS);
});

test('backoffAfterExit: resets to INITIAL_BACKOFF_MS after a healthy long run', () => {
  const next = backoffAfterExit(MAX_BACKOFF_MS, 120000);
  assert.equal(next, INITIAL_BACKOFF_MS);
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test scripts/fps-exporter/backoff.test.js`
Expected: FAIL — `Cannot find module './backoff.js'`

- [ ] **Step 3: Write the implementation**

```js
// scripts/fps-exporter/backoff.js
'use strict';

const INITIAL_BACKOFF_MS = 5000;
const MAX_BACKOFF_MS = 60000;
const HEALTHY_RUN_RESET_MS = 60000;

function nextBackoffMs(currentBackoffMs) {
  return Math.min(currentBackoffMs * 2, MAX_BACKOFF_MS);
}

function backoffAfterExit(currentBackoffMs, runDurationMs) {
  if (runDurationMs >= HEALTHY_RUN_RESET_MS) {
    return INITIAL_BACKOFF_MS;
  }
  return nextBackoffMs(currentBackoffMs);
}

module.exports = { INITIAL_BACKOFF_MS, MAX_BACKOFF_MS, HEALTHY_RUN_RESET_MS, nextBackoffMs, backoffAfterExit };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `node --test scripts/fps-exporter/backoff.test.js`
Expected: 3 tests pass

- [ ] **Step 5: Commit**

```bash
git add scripts/fps-exporter/backoff.js scripts/fps-exporter/backoff.test.js
git commit -m "feat(fps-exporter): PresentMon respawn backoff logic"
```

---

### Task 4: `fps-exporter.js` — spawn PresentMon, serve `/metrics`

**Files:**
- Create: `scripts/fps-exporter/fps-exporter.js`

**Interfaces:**
- Consumes: `parseHeader`, `parseRow` (Task 1); `MetricsAggregator` (Task 2); `INITIAL_BACKOFF_MS`, `backoffAfterExit` (Task 3)
- Produces: a runnable entry point (`node fps-exporter.js`) — no exports consumed by later tasks; Task 5 references it only by file path

This task is integration/glue (real child-process spawn + real HTTP server) — verified by manual smoke test, not `node:test`, since it needs `PresentMon.exe` and an elevated shell to actually produce data.

- [ ] **Step 1: Write the implementation**

```js
// scripts/fps-exporter/fps-exporter.js
'use strict';

const { spawn } = require('node:child_process');
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');

const { parseHeader, parseRow } = require('./presentmon-csv.js');
const { MetricsAggregator } = require('./metrics-aggregator.js');
const { INITIAL_BACKOFF_MS, backoffAfterExit } = require('./backoff.js');

const ALLOWLIST = ['iRacingSim64DX11.exe', 'chrome.exe'];
const PRESENTMON_PATH = path.join(os.homedir(), 'Tools', 'PresentMon', 'PresentMon.exe');
const METRICS_PORT = 9101;
const METRICS_HOST = '127.0.0.1';
const LOG_DIR = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'FpsExporter');
const LOG_FILE = path.join(LOG_DIR, 'fps-exporter.log');

const aggregator = new MetricsAggregator(ALLOWLIST);

function log(message) {
  const line = `${new Date().toISOString()} ${message}\n`;
  process.stdout.write(line);
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    fs.appendFileSync(LOG_FILE, line);
  } catch (err) {
    process.stderr.write(`failed to write log file: ${err.message}\n`);
  }
}

function startPresentMon(backoffMs) {
  log(`starting PresentMon.exe (backoff was ${backoffMs}ms)`);
  const startedAt = Date.now();
  const child = spawn(PRESENTMON_PATH, [
    '--process_name', 'iRacingSim64DX11.exe',
    '--process_name', 'chrome.exe',
    '--output_stdout',
    '--no_csv',
    '--no_console_stats',
  ]);

  let headerCols = null;
  let carry = '';

  child.stdout.on('data', (chunk) => {
    carry += chunk.toString('utf8');
    const lines = carry.split('\n');
    carry = lines.pop();

    for (const rawLine of lines) {
      const line = rawLine.trim();
      if (line.length === 0) continue;

      if (headerCols === null) {
        headerCols = parseHeader(line);
        continue;
      }

      try {
        const row = parseRow(headerCols, line);
        aggregator.recordRow(row, Date.now());
      } catch (err) {
        log(`skipping unparseable row: ${err.message}`);
      }
    }
  });

  child.stderr.on('data', (chunk) => {
    log(`PresentMon stderr: ${chunk.toString('utf8').trim()}`);
  });

  child.on('exit', (code, signal) => {
    const runDurationMs = Date.now() - startedAt;
    log(`PresentMon.exe exited (code=${code}, signal=${signal}, ran for ${runDurationMs}ms)`);
    const nextBackoff = backoffAfterExit(backoffMs, runDurationMs);
    setTimeout(() => startPresentMon(nextBackoff), nextBackoff);
  });

  child.on('error', (err) => {
    log(`failed to spawn PresentMon.exe: ${err.message}`);
  });
}

function startMetricsServer() {
  const server = http.createServer((req, res) => {
    if (req.url === '/metrics') {
      const body = aggregator.renderPrometheusText(Date.now());
      res.writeHead(200, { 'Content-Type': 'text/plain; version=0.0.4' });
      res.end(body);
    } else {
      res.writeHead(404);
      res.end();
    }
  });

  server.on('error', (err) => {
    log(`metrics server failed to start: ${err.message}`);
    process.exit(1);
  });

  server.listen(METRICS_PORT, METRICS_HOST, () => {
    log(`metrics endpoint listening on http://${METRICS_HOST}:${METRICS_PORT}/metrics`);
  });
}

if (!fs.existsSync(PRESENTMON_PATH)) {
  log(`PresentMon.exe not found at ${PRESENTMON_PATH}`);
  process.exit(1);
}

startMetricsServer();
startPresentMon(INITIAL_BACKOFF_MS);
```

- [ ] **Step 2: Manual smoke test — no tracked process running**

Run (from an elevated PowerShell, since PresentMon needs ETW/admin rights):
```powershell
node scripts/fps-exporter/fps-exporter.js
```
In a second terminal: `curl http://127.0.0.1:9101/metrics`
Expected: HTTP 200, body contains `# TYPE frames_dropped_total counter` with `frames_dropped_total{process="iRacingSim64DX11.exe"} 0` and `frames_dropped_total{process="chrome.exe"} 0`, and no `game_fps`/`display_fps` lines (nothing running yet).

- [ ] **Step 3: Manual smoke test — with a tracked process running**

With `fps-exporter.js` still running, launch iRacing (or open Chrome to any page that's actively rendering, e.g. a video). Wait ~5s, then: `curl http://127.0.0.1:9101/metrics`
Expected: `game_fps{process="..."}` and `display_fps{process="..."}` lines appear with plausible values (e.g. 30–240 range depending on vsync/settings). Close the app, wait 6s, curl again: those lines should disappear (gap, not `0`).

- [ ] **Step 4: Manual resilience test**

With `fps-exporter.js` running, find and kill the `PresentMon.exe` child process in Task Manager. Watch the console/log output.
Expected: a log line `PresentMon.exe exited (code=..., ran for ...ms)`, followed by a respawn ~5s later (`starting PresentMon.exe (backoff was 5000ms)`), and `curl http://127.0.0.1:9101/metrics` works again once respawned.

- [ ] **Step 5: Commit**

```bash
git add scripts/fps-exporter/fps-exporter.js
git commit -m "feat(fps-exporter): spawn PresentMon and serve Prometheus /metrics"
```

---

### Task 5: `install-service.ps1` — register `fps-exporter` as a Windows Service

**Files:**
- Create: `scripts/fps-exporter/install-service.ps1`

**Interfaces:**
- Consumes: `scripts/fps-exporter/fps-exporter.js` (Task 4) by relative path
- Produces: a Windows Service named `FpsExporter`, `Start=SERVICE_AUTO_START`, running under `LocalSystem`

- [ ] **Step 1: Write the implementation**

```powershell
<#
.SYNOPSIS
  Install fps-exporter as a Windows Service (LocalSystem) via NSSM, so PresentMon's
  admin/ETW requirement is satisfied once at install time instead of on every run.

.EXAMPLE
  # From an elevated PowerShell:
  .\scripts\fps-exporter\install-service.ps1
#>
param(
    [string]$ServiceName = "FpsExporter"
)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script installs a Windows Service and must run from an elevated (Administrator) PowerShell."
    exit 1
}

$nodePath = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $nodePath) {
    Write-Error "node.exe not found on PATH. Install Node.js first."
    exit 1
}

function Find-Nssm {
    Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "nssm.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'win64' } | Select-Object -First 1 -ExpandProperty FullName
}

$nssmPath = Find-Nssm
if (-not $nssmPath) {
    Write-Output "NSSM not found, installing via winget..."
    winget install --id NSSM.NSSM -e --accept-package-agreements --accept-source-agreements
    $nssmPath = Find-Nssm
}

if (-not $nssmPath) {
    Write-Error "NSSM install via winget did not produce nssm.exe under $env:LOCALAPPDATA\Microsoft\WinGet\Packages. Install NSSM manually and re-run."
    exit 1
}

Write-Output "Using NSSM at: $nssmPath"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$entryScript = Join-Path $scriptDir "fps-exporter.js"

if (-not (Test-Path $entryScript)) {
    Write-Error "fps-exporter.js not found at $entryScript"
    exit 1
}

New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\FpsExporter" | Out-Null

& $nssmPath install $ServiceName $nodePath $entryScript
& $nssmPath set $ServiceName AppDirectory $scriptDir
& $nssmPath set $ServiceName Start SERVICE_AUTO_START
& $nssmPath set $ServiceName AppStdout "$env:LOCALAPPDATA\FpsExporter\service-stdout.log"
& $nssmPath set $ServiceName AppStderr "$env:LOCALAPPDATA\FpsExporter\service-stderr.log"
& $nssmPath set $ServiceName AppRotateFiles 1
& $nssmPath set $ServiceName AppRotateBytes 10485760

& $nssmPath start $ServiceName

Write-Output "Service '$ServiceName' installed and started."
Write-Output "Check status: nssm status $ServiceName  (or Get-Service $ServiceName)"
Write-Output "Verify metrics: curl http://127.0.0.1:9101/metrics"
```

- [ ] **Step 2: Run it (elevated) and verify**

Run (from an elevated PowerShell):
```powershell
.\scripts\fps-exporter\install-service.ps1
```
Then: `Get-Service FpsExporter` → expect `Status = Running`.
Then: `curl http://127.0.0.1:9101/metrics` → expect the same output shape verified manually in Task 4, now without needing an elevated shell open (the service itself runs elevated).

- [ ] **Step 3: Commit**

```bash
git add scripts/fps-exporter/install-service.ps1
git commit -m "feat(fps-exporter): install as a Windows Service via NSSM"
```

---

### Task 6: Alloy scrape target (manual, elevated — cannot be scripted by the assistant)

**Files:**
- Modify (elevated, outside the repo): `C:\Program Files\GrafanaLabs\Alloy\config.alloy`

This task has no automatable steps in a standard session — `config.alloy` is ACL-restricted to SYSTEM/Administrators, confirmed during design (non-elevated `Get-Content` returns Access Denied). Perform these steps from an elevated PowerShell or text editor run as Administrator.

- [ ] **Step 1: Back up the current config**

```powershell
Copy-Item "C:\Program Files\GrafanaLabs\Alloy\config.alloy" "C:\Program Files\GrafanaLabs\Alloy\config.alloy.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
```

- [ ] **Step 2: Append the new scrape block**

Add this block to `config.alloy` (the existing remote-write component is labeled `grafana_cloud`, confirmed via Alloy's own Application-log entries referencing `component_id=prometheus.remote_write.grafana_cloud`):

```alloy
prometheus.scrape "fps_exporter" {
  targets = [
    {"__address__" = "127.0.0.1:9101"},
  ]
  scrape_interval = "5s"
  forward_to      = [prometheus.remote_write.grafana_cloud.receiver]
}
```

- [ ] **Step 3: Restart the Alloy service**

```powershell
Restart-Service Alloy
Get-Service Alloy   # expect Status = Running
```

- [ ] **Step 4: Verify the new series reaches Grafana Cloud**

From a non-elevated session (this is a read-only Grafana Cloud query, no local file access needed), use the same check already used earlier in this investigation to confirm `windows_gpu_*` existed:

```
mcp__MCP_DOCKER__list_prometheus_metric_names with datasourceUid "grafanacloud-prom"
```
Expected: `game_fps`, `display_fps`, `frames_dropped_total` appear in the result within a few scrape intervals (~15-30s) of Task 5's service being up and Alloy restarting.

If they don't appear: check `%LOCALAPPDATA%\FpsExporter\fps-exporter.log` for spawn/parse errors, and check Alloy's own logs (`Get-WinEvent -LogName Application -ProviderName Alloy -MaxEvents 20`) for scrape errors (e.g. connection refused, if the service isn't actually listening).

---

### Task 7: Grafana dashboard panels (manual, Grafana Cloud UI)

No files in this repo — `docs/GRAFANA-LOGGING.md` notes there are no provisioned dashboard JSON files checked in; panels are added directly in the Grafana Cloud UI, same as the existing GPU/VRAM panels.

- [ ] **Step 1: Add "Game vs. display FPS" panel**

On the same dashboard as the existing "GPU engine utilization by type" / "VRAM usage" panels, add a new time-series panel with query:
```
game_fps
```
and a second series override / query:
```
display_fps
```
Legend format: `{{process}} — game` / `{{process}} — display` (or split into two panels if overlapping reads as cluttered — the useful signal is where the two lines for the same process diverge).

- [ ] **Step 2: Add "Dropped frames" panel**

Smaller panel below, query:
```
rate(frames_dropped_total[$__interval])
```
Legend format: `{{process}}`. Used to correlate spikes against the FPS panel above.

- [ ] **Step 3: Verify against a live session**

With the service running and iRacing open, confirm both panels populate and that a deliberate stutter (e.g. alt-tabbing briefly) shows up as a visible dip/gap in `display_fps` and/or a bump in dropped frames.
