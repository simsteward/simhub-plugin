// Test Rig — Node orchestrator.
//
// Pre-condition: user has iRacing open with the replay loaded and IRSDK ready.
// Drives: WS connect -> anchor at frame 0 -> run scenario -> assert -> [teardown].
//
// Contract:  docs/RULES-TestRig-Contract.md
//
// Usage:
//   node scripts/test-rig/run.js --subsession <id> --scenario <sweep|live-counters|jump-test|smoke>
//                                [--teardown]
//                                [--ws ws://localhost:19847]
//                                [--scenario-timeout-ms 1200000]
//
// Cloud-only Loki: SIMSTEWARD_LOKI_URL/USER/TOKEN read from process.env (load .env via npm script if needed).
// All ws/loki failures are non-fatal for logging — the orchestrator never crashes because Loki is down.

'use strict';

const fs        = require('node:fs');
const fsp       = require('node:fs/promises');
const path      = require('node:path');
const os        = require('node:os');
const http      = require('node:http');
const https     = require('node:https');
const { spawn } = require('node:child_process');

// ────────────────────────────────────────────────────────────────────────────
//  Constants
// ────────────────────────────────────────────────────────────────────────────

const DEFAULT_WS_URL              = 'ws://localhost:19847';
const DEFAULT_SCENARIO_TIMEOUT_MS = {
  sweep:           20 * 60 * 1000,                          // 20 min
  'live-counters':  35 * 1000,                              // 30s play + 5s settle
  'jump-test':      45 * 1000,                              // 10 jumps × 2s + buffer
  smoke:           20 * 60 * 1000,                          // 20 min — sweep is the long pole
};
const ANCHOR_TIMEOUT_MS           = 30 * 1000;
const TICK_PROGRESS_INTERVAL_MS   = 5 * 1000;
const JUMP_INTERVAL_MS            = 2000;
const JUMP_COUNT                  = 10;
const LIVE_COUNTERS_DURATION_MS   = 30 * 1000;
const ANCHOR_FRAME_MAX            = 100;

const REPO_ROOT      = path.resolve(__dirname, '..', '..');
const TEST_RIG_DIR   = __dirname;
const STOP_ALL_PS1   = path.join(TEST_RIG_DIR, 'stop-all.ps1');

// Index folder per ReplayIncidentIndexOutputPaths.cs (LocalApplicationData\SimSteward\replay-incident-index)
function indexFilePath(subSessionId) {
  const localAppData = process.env.LOCALAPPDATA
    || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(localAppData, 'SimSteward', 'replay-incident-index', `${subSessionId}.json`);
}

// ────────────────────────────────────────────────────────────────────────────
//  CLI parsing  (exported for unit tests)
// ────────────────────────────────────────────────────────────────────────────

const SCENARIOS = ['sweep', 'live-counters', 'jump-test', 'smoke'];

const VALID_IMPACT_CLASSES = ['free', 'partial', 'full'];

function parseArgs(argv) {
  const args = {
    subsession: null,
    scenario: null,
    teardown: false,
    wsUrl: DEFAULT_WS_URL,
    scenarioTimeoutMs: null, // resolve from scenario after parse
    help: false,
  };

  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '-h':
      case '--help':              args.help = true; break;
      case '--subsession':        args.subsession = parseInt(next(), 10); break;
      case '--scenario':          args.scenario = next(); break;
      case '--teardown':          args.teardown = true; break;
      case '--ws':                args.wsUrl = next(); break;
      case '--scenario-timeout-ms': args.scenarioTimeoutMs = parseInt(next(), 10); break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown_arg:${a}`);
    }
  }

  if (args.help) return args;

  // --subsession is optional: omitted → auto-detect from session_hello.
  // Supplied → must be a positive int (becomes a mismatch guardrail).
  if (args.subsession !== null &&
      (!Number.isFinite(args.subsession) || args.subsession <= 0)) {
    throw new Error('invalid:--subsession');
  }
  if (!args.scenario || !SCENARIOS.includes(args.scenario)) {
    throw new Error(`missing_or_invalid:--scenario (must be one of ${SCENARIOS.join('|')})`);
  }
  if (args.scenarioTimeoutMs == null) {
    args.scenarioTimeoutMs = DEFAULT_SCENARIO_TIMEOUT_MS[args.scenario];
  }
  return args;
}

function printUsage() {
  process.stdout.write([
    'Test Rig orchestrator',
    '',
    'Pre-condition: iRacing is open with the replay loaded and IRSDK ready.',
    '',
    'Usage:',
    '  node scripts/test-rig/run.js --subsession <id> --scenario <name> [opts]',
    '',
    'Required:',
    '  --subsession <int>             iRacing subsession id (used to locate index file)',
    `  --scenario <name>              one of: ${SCENARIOS.join(' | ')}`,
    '',
    'Options:',
    '  --teardown                     run stop-all.ps1 after scenario completes',
    `  --ws <url>                     WebSocket URL (default: ${DEFAULT_WS_URL})`,
    '  --scenario-timeout-ms <ms>     scenario-specific default',
    '',
    'Scenarios:',
    '  sweep           Trigger replay_incident_index_build; wait for index JSON file.',
    '  live-counters   Play 30s @ 1×, capture every replay_state_tick.',
    '  jump-test       Fire 10 replay_jump_next_incident actions; record misfires.',
    '  smoke           Rig health: WS liveness + sweep + per-impact-class breakdown.',
    '',
    'Artifacts:',
    '  logs/test-rig/<UTC ISO>/{run.json,events.jsonl,index.json,console.log}',
    '',
  ].join('\n'));
}

// ────────────────────────────────────────────────────────────────────────────
//  Loki push (best-effort, cloud-only, no crash on failure)
// ────────────────────────────────────────────────────────────────────────────

function pushLoki(event, level, fields) {
  const rawUrl = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
  if (!rawUrl) return;
  if (/^https?:\/\/(localhost|127\.0\.0\.1)/i.test(rawUrl)) return;

  const envLabel = process.env.SIMSTEWARD_LOG_ENV || 'local';
  const machine  = process.env.COMPUTERNAME || os.hostname() || 'unknown';

  const payload = {
    event,
    domain:    'system',
    component: 'test-rig',
    level,
    timestamp: new Date().toISOString(),
    machine,
    env:       envLabel,
    source:    'test-rig',
    ...(fields || {}),
  };

  const stream = {
    app:       'sim-steward',
    env:       envLabel,
    component: 'test-rig',
    level,
  };

  const ts = String(Date.now()) + '000000';
  const body = JSON.stringify({ streams: [{ stream, values: [[ts, JSON.stringify(payload)]] }] });

  let parsed;
  try { parsed = new URL(rawUrl + '/loki/api/v1/push'); } catch { return; }
  const mod = parsed.protocol === 'https:' ? https : http;

  const headers = { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) };
  const user = process.env.SIMSTEWARD_LOKI_USER;
  const tok  = process.env.SIMSTEWARD_LOKI_TOKEN;
  if (user && tok) {
    headers.Authorization = 'Basic ' + Buffer.from(`${user}:${tok}`).toString('base64');
  }

  const req = mod.request(parsed, { method: 'POST', headers, timeout: 4000 }, res => res.resume());
  req.on('error',   () => {});
  req.on('timeout', () => req.destroy());
  req.write(body);
  req.end();
}

// ────────────────────────────────────────────────────────────────────────────
//  Artifact helpers
// ────────────────────────────────────────────────────────────────────────────

async function makeArtifactDir() {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const dir = path.join(REPO_ROOT, 'logs', 'test-rig', stamp);
  await fsp.mkdir(dir, { recursive: true });
  return dir;
}

class Artifacts {
  constructor(dir) {
    this.dir          = dir;
    this.eventsStream = fs.createWriteStream(path.join(dir, 'events.jsonl'), { flags: 'a' });
    this.consoleStream = fs.createWriteStream(path.join(dir, 'console.log'), { flags: 'a' });
  }
  appendEvent(obj) {
    try { this.eventsStream.write(JSON.stringify(obj) + '\n'); } catch {}
  }
  appendConsole(line) {
    try { this.consoleStream.write(line.endsWith('\n') ? line : line + '\n'); } catch {}
  }
  async writeJson(name, obj) {
    await fsp.writeFile(path.join(this.dir, name), JSON.stringify(obj, null, 2), 'utf8');
  }
  async close() {
    await new Promise(r => this.eventsStream.end(r));
    await new Promise(r => this.consoleStream.end(r));
  }
}

function runStopAll({ artifacts, prefix = '[stop-all]' }) {
  return new Promise((resolve) => {
    const args = ['-NoProfile', '-File', STOP_ALL_PS1];
    log(`${prefix} spawning pwsh.exe ${args.join(' ')}`, artifacts);
    const proc = spawn('pwsh.exe', args, { cwd: REPO_ROOT, env: process.env });
    proc.stdout.on('data', d => artifacts?.appendConsole(`${prefix} ${d.toString('utf8').trimEnd()}`));
    proc.stderr.on('data', d => artifacts?.appendConsole(`${prefix}!${d.toString('utf8').trimEnd()}`));
    proc.on('exit', () => resolve());
    proc.on('error', () => resolve());
  });
}

// ────────────────────────────────────────────────────────────────────────────
//  WS client (Node 22+ built-in WebSocket)
// ────────────────────────────────────────────────────────────────────────────

class WsClient {
  constructor(url, artifacts) {
    this.url        = url;
    this.artifacts  = artifacts;
    this.ws         = null;
    this.lastTick   = null;     // last replay_state_tick observed
    this.lastHello  = null;     // last session_hello observed
    this.handlers   = new Set();
  }
  connect({ timeoutMs = 15000 } = {}) {
    return new Promise((resolve, reject) => {
      let settled = false;
      let ws;
      try {
        ws = new WebSocket(this.url);
      } catch (err) {
        return reject(err);
      }
      this.ws = ws;
      const t = setTimeout(() => {
        if (settled) return;
        settled = true;
        try { ws.close(); } catch {}
        reject(new Error(`ws_connect_timeout_after_${timeoutMs}ms`));
      }, timeoutMs);

      ws.addEventListener('open', () => {
        if (settled) return;
        settled = true;
        clearTimeout(t);
        log(`[ws] connected: ${this.url}`, this.artifacts);
        resolve();
      });
      ws.addEventListener('error', (ev) => {
        if (settled) return;
        settled = true;
        clearTimeout(t);
        reject(new Error(`ws_error: ${(ev && ev.message) || 'unknown'}`));
      });
      ws.addEventListener('close', () => {
        log(`[ws] closed`, this.artifacts);
      });
      ws.addEventListener('message', (ev) => {
        const text = typeof ev.data === 'string' ? ev.data : '';
        let parsed;
        try { parsed = JSON.parse(text); } catch { return; }
        this.artifacts?.appendEvent({ ts: new Date().toISOString(), dir: 'in', msg: parsed });
        if (parsed.type === 'replay_state_tick') this.lastTick = parsed;
        if (parsed.type === 'session_hello') this.lastHello = parsed;
        for (const h of this.handlers) {
          try { h(parsed); } catch (err) { log(`[ws] handler error: ${err.message}`, this.artifacts); }
        }
      });
    });
  }
  send(obj) {
    const text = JSON.stringify(obj);
    log(`[ws←] ${text}`, this.artifacts);
    this.artifacts?.appendEvent({ ts: new Date().toISOString(), dir: 'out', msg: obj });
    this.ws.send(text);
  }
  on(handler)   { this.handlers.add(handler);    return () => this.handlers.delete(handler); }
  close()       { try { this.ws && this.ws.close(); } catch {} }
  waitFor(predicate, { timeoutMs, label }) {
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => {
        unsubscribe();
        reject(new Error(`waitFor_timeout(${label})_after_${timeoutMs}ms`));
      }, timeoutMs);
      const unsubscribe = this.on((msg) => {
        let ok;
        try { ok = predicate(msg); } catch { ok = false; }
        if (ok) {
          clearTimeout(t);
          unsubscribe();
          resolve(msg);
        }
      });
    });
  }
}

function log(line, artifacts) {
  process.stdout.write(line + '\n');
  artifacts?.appendConsole(line);
}

// ────────────────────────────────────────────────────────────────────────────
//  Anchor at frame 0
// ────────────────────────────────────────────────────────────────────────────

async function anchorAtStart(ws, artifacts) {
  log('[anchor] waiting for first replay_state_tick…', artifacts);
  await ws.waitFor(m => m.type === 'replay_state_tick',
    { timeoutMs: ANCHOR_TIMEOUT_MS, label: 'first_replay_state_tick' });

  ws.send({ action: 'replay_jump',  arg: 'start' });
  ws.send({ action: 'replay_pause', arg: '' });

  await ws.waitFor(
    m => m.type === 'replay_state_tick' && m.paused === true && (m.frame ?? Infinity) < ANCHOR_FRAME_MAX,
    { timeoutMs: ANCHOR_TIMEOUT_MS, label: 'paused_at_start' }
  );
  log(`[anchor] paused at frame ${ws.lastTick.frame}`, artifacts);
}

// ────────────────────────────────────────────────────────────────────────────
//  Scenarios
// ────────────────────────────────────────────────────────────────────────────

const scenarios = {
  sweep:          runSweepScenario,
  'live-counters': runLiveCountersScenario,
  'jump-test':    runJumpTestScenario,
  smoke:          runSmokeScenario,
};

async function runSweepScenario({ ws, args, artifacts }) {
  const indexPath = indexFilePath(args.subsession);
  log(`[sweep] expecting index file at ${indexPath}`, artifacts);

  // Pre-clean: ignore if missing.
  try { await fsp.unlink(indexPath); log(`[sweep] removed pre-existing index`, artifacts); } catch {}

  let lastProgressLogAt = 0;
  let progressTickCount = 0;
  let completeBroadcastReceived = false;

  const off = ws.on(msg => {
    if (msg.type === 'replay_sweep_progress_tick') {
      progressTickCount++;
      const now = Date.now();
      if (now - lastProgressLogAt > TICK_PROGRESS_INTERVAL_MS) {
        lastProgressLogAt = now;
        log(`[sweep] progress: ${msg.est_completion_pct?.toFixed?.(1) ?? '??'}% `
          + `frame=${msg.frame}/${msg.frame_end} samples=${msg.samples_so_far} `
          + `eta_ms=${msg.est_remaining_ms}`, artifacts);
      }
    }
    // The broadcast log event would arrive via DashboardBridge.Broadcast → we'd see logEvents push.
    // SimHub log push schema may differ; we treat the file appearing as primary signal.
    if (msg.event === 'replay_incident_index_fast_forward_complete') completeBroadcastReceived = true;
  });

  ws.send({ action: 'replay_incident_index_build', arg: 'start' });
  log('[sweep] dispatched replay_incident_index_build; polling for index file…', artifacts);

  // Poll for index file existence with watchdog timeout.
  const deadline = Date.now() + args.scenarioTimeoutMs;
  let indexExists = false;
  while (Date.now() < deadline) {
    try {
      await fsp.access(indexPath);
      indexExists = true;
      break;
    } catch {}
    await new Promise(r => setTimeout(r, 1000));
  }
  off();

  if (!indexExists) {
    return {
      assertions: [
        { name: 'index_file_exists', passed: false, details: { path: indexPath, timeout_ms: args.scenarioTimeoutMs } },
      ],
      summary: { progress_ticks: progressTickCount, complete_broadcast: completeBroadcastReceived },
    };
  }

  // Read + summarize index.
  let indexJson = null;
  try {
    const text = await fsp.readFile(indexPath, 'utf8');
    indexJson = JSON.parse(text.charCodeAt(0) === 0xFEFF ? text.slice(1) : text); // tolerate plugin UTF-8 BOM
    await artifacts.writeJson('index.json', indexJson);
  } catch (err) {
    return {
      assertions: [
        { name: 'index_file_exists',   passed: true,  details: { path: indexPath } },
        { name: 'index_file_parsable', passed: false, details: { error: err.message } },
      ],
      summary: { progress_ticks: progressTickCount, complete_broadcast: completeBroadcastReceived },
    };
  }

  const incidents = Array.isArray(indexJson.Incidents) ? indexJson.Incidents
                  : Array.isArray(indexJson.incidents) ? indexJson.incidents
                  : [];
  const validation  = indexJson.Validation || indexJson.validation || {};
  const discrepancies = Array.isArray(validation.Discrepancies) ? validation.Discrepancies
                      : Array.isArray(validation.discrepancies) ? validation.discrepancies
                      : [];
  const fingerprints = new Set();
  for (const row of incidents) {
    const fp = row.Fingerprint ?? row.fingerprint;
    if (fp) fingerprints.add(fp);
  }

  return {
    assertions: [
      { name: 'index_file_exists',   passed: true, details: { path: indexPath } },
      { name: 'index_file_parsable', passed: true, details: { incident_rows: incidents.length } },
      { name: 'incidents_nonempty',  passed: incidents.length > 0, details: { count: incidents.length } },
    ],
    summary: {
      progress_ticks:           progressTickCount,
      complete_broadcast:       completeBroadcastReceived,
      incident_row_count:       incidents.length,
      discrepancy_count:        discrepancies.length,
      unique_fingerprints:      fingerprints.size,
      total_race_incidents:     indexJson.TotalRaceIncidents ?? indexJson.totalRaceIncidents ?? null,
    },
  };
}

async function runLiveCountersScenario({ ws, artifacts }) {
  let tickCount = 0;
  let lastTick  = null;
  const off = ws.on(m => { if (m.type === 'replay_state_tick') { tickCount++; lastTick = m; } });

  ws.send({ action: 'replay_play', arg: '' });
  log(`[live-counters] playing for ${LIVE_COUNTERS_DURATION_MS / 1000}s…`, artifacts);
  await new Promise(r => setTimeout(r, LIVE_COUNTERS_DURATION_MS));
  ws.send({ action: 'replay_pause', arg: '' });

  // Allow the pause confirmation tick to land.
  await new Promise(r => setTimeout(r, 1500));
  off();

  const expectedMin = Math.floor((LIVE_COUNTERS_DURATION_MS / 250) * 0.5); // tolerant: ≥ half of cadence
  return {
    assertions: [
      { name: 'tick_stream_active',
        passed: tickCount >= expectedMin,
        details: { count: tickCount, expected_min: expectedMin } },
    ],
    summary: {
      tick_count:     tickCount,
      final_frame:    lastTick?.frame ?? null,
      final_paused:   lastTick?.paused ?? null,
      final_aggregates: lastTick?.aggregates ?? null,
    },
  };
}

async function runJumpTestScenario({ ws, artifacts }) {
  const misfires = [];
  const offMisfire = ws.on(m => {
    if (m.type === 'replay_state_tick' && m.misfire && m.misfire.active === true) {
      misfires.push({
        ts:                  new Date().toISOString(),
        direction:           m.misfire.direction,
        expected_frame:      m.misfire.expected_frame,
        landed_frame:        m.misfire.landed_frame,
        delta_frames:        m.misfire.delta_frames,
        delta_ms:            m.misfire.delta_ms,
        expected_fingerprint: m.misfire.expected_fingerprint,
        nearest_fingerprint:  m.misfire.nearest_fingerprint,
      });
    }
  });

  for (let i = 0; i < JUMP_COUNT; i++) {
    ws.send({ action: 'replay_jump_next_incident', arg: '' });
    log(`[jump-test] jump ${i + 1}/${JUMP_COUNT}`, artifacts);
    await new Promise(r => setTimeout(r, JUMP_INTERVAL_MS));
  }
  // Allow trailing misfire ticks to flow.
  await new Promise(r => setTimeout(r, 2500));
  offMisfire();

  // Dedup misfires by expected_frame so a single mismatch held active for ~2s
  // (multiple ticks) doesn't inflate the count.
  const uniqueByExpected = new Map();
  for (const m of misfires) {
    const key = `${m.direction}|${m.expected_frame}`;
    if (!uniqueByExpected.has(key)) uniqueByExpected.set(key, m);
  }
  const unique = [...uniqueByExpected.values()];

  return {
    assertions: [
      { name: 'jumps_dispatched', passed: true, details: { count: JUMP_COUNT } },
    ],
    summary: {
      jumps_sent:           JUMP_COUNT,
      misfire_tick_count:   misfires.length,
      misfire_unique_count: unique.length,
      first_misfire_frame:  unique[0]?.landed_frame ?? null,
      misfires:             unique,
    },
  };
}

// ────────────────────────────────────────────────────────────────────────────
//  Smoke scenario helpers (pure — exported for unit tests)
// ────────────────────────────────────────────────────────────────────────────

// Pull the case-tolerant array of incidents/sessions out of the index JSON.
function _smokeIncidents(indexJson) {
  if (!indexJson || typeof indexJson !== 'object') return [];
  if (Array.isArray(indexJson.incidents)) return indexJson.incidents;
  if (Array.isArray(indexJson.Incidents)) return indexJson.Incidents;
  return [];
}
function _smokeSessions(indexJson) {
  if (!indexJson || typeof indexJson !== 'object') return null;
  if (Array.isArray(indexJson.sessions)) return indexJson.sessions;
  if (Array.isArray(indexJson.Sessions)) return indexJson.Sessions;
  return null;
}
function _sessionNum(o) {
  if (!o || typeof o !== 'object') return undefined;
  return o.sessionNum ?? o.SessionNum;
}
function _impactClass(o) {
  if (!o || typeof o !== 'object') return undefined;
  return o.impactClass ?? o.ImpactClass;
}

function aggregateByImpactClass(incidents, sessions) {
  const incidentList = Array.isArray(incidents) ? incidents : [];
  const sessionList  = Array.isArray(sessions)  ? sessions  : [];

  // Look up sessionNum → impactClass.
  const sessionImpactBySessionNum = new Map();
  for (const s of sessionList) {
    const n = _sessionNum(s);
    if (n === undefined || n === null) continue;
    sessionImpactBySessionNum.set(n, _impactClass(s));
  }

  const totals = { incidents: incidentList.length, free: 0, partial: 0, full: 0 };
  for (const inc of incidentList) {
    const n = _sessionNum(inc);
    const cls = sessionImpactBySessionNum.get(n);
    if (cls === 'free' || cls === 'partial' || cls === 'full') {
      totals[cls]++;
    }
  }
  return totals;
}

function validateSmokeIndex(indexJson) {
  const failures = [];
  if (!indexJson || typeof indexJson !== 'object') {
    return { passed: false, failures: ['index_json_not_an_object'] };
  }

  const sessions = _smokeSessions(indexJson);
  if (sessions === null) {
    failures.push('sessions_array_missing');
  } else if (sessions.length < 1) {
    failures.push('sessions_array_empty');
  }

  const incidents = _smokeIncidents(indexJson);
  if (!Array.isArray(indexJson.incidents) && !Array.isArray(indexJson.Incidents)) {
    failures.push('incidents_array_missing');
  }

  // Validate impactClass values on each session.
  if (Array.isArray(sessions)) {
    for (const s of sessions) {
      const cls = _impactClass(s);
      if (!VALID_IMPACT_CLASSES.includes(cls)) {
        failures.push(`session_invalid_impact_class:sessionNum=${_sessionNum(s)} impactClass=${cls === undefined ? '<missing>' : cls}`);
      }
    }
  }

  // Every incident.sessionNum must match some sessions[].sessionNum (no orphans).
  if (Array.isArray(sessions) && (Array.isArray(indexJson.incidents) || Array.isArray(indexJson.Incidents))) {
    const sessionNums = new Set(sessions.map(_sessionNum).filter(n => n !== undefined && n !== null));
    for (const inc of incidents) {
      const n = _sessionNum(inc);
      if (n === undefined || n === null) {
        failures.push('incident_missing_sessionNum');
        continue;
      }
      if (!sessionNums.has(n)) {
        failures.push(`incident_orphan_sessionNum:${n}`);
      }
    }
  }

  return { passed: failures.length === 0, failures };
}

function buildSmokeReport(indexJson, durationMs, subSessionId) {
  const { passed, failures } = validateSmokeIndex(indexJson);
  const incidents = _smokeIncidents(indexJson);
  const sessions  = _smokeSessions(indexJson) || [];

  const totals = aggregateByImpactClass(incidents, sessions);

  // Per-session breakdown — count incidents whose sessionNum matches.
  const perSession = sessions.map(s => {
    const n = _sessionNum(s);
    const count = incidents.reduce((acc, inc) => acc + (_sessionNum(inc) === n ? 1 : 0), 0);
    return {
      sessionNum:  n,
      name:        s.name        ?? s.Name        ?? null,
      type:        s.type        ?? s.Type        ?? null,
      impactClass: _impactClass(s) ?? null,
      incidents:   count,
    };
  });

  return {
    passed,
    sub_session_id: subSessionId,
    duration_ms:    durationMs,
    totals,
    sessions:       perSession,
    failures,
  };
}

async function runSmokeScenario({ ws, args, artifacts }) {
  const startedTs = Date.now();
  const indexPath = indexFilePath(args.subsession);
  log(`[smoke] expecting index file at ${indexPath}`, artifacts);

  // 1. Confirm liveness — pause + wait for one tick.
  ws.send({ action: 'replay_pause', arg: '' });
  let liveness;
  try {
    liveness = await ws.waitFor(
      m => m.type === 'replay_state_tick',
      { timeoutMs: ANCHOR_TIMEOUT_MS, label: 'smoke_liveness_tick' }
    );
    log(`[smoke] liveness ok — frame=${liveness.frame} paused=${liveness.paused}`, artifacts);
  } catch (err) {
    const report = buildSmokeReport({}, Date.now() - startedTs, args.subsession);
    report.passed = false;
    report.failures = [`liveness_tick_timeout:${err.message}`, ...(report.failures || [])];
    await artifacts.writeJson('smoke.json', report);
    return {
      assertions: [
        { name: 'ws_liveness',     passed: false, details: { error: err.message } },
        { name: 'index_file_exists', passed: false, details: { skipped: true } },
      ],
      summary: report,
    };
  }

  // 2. Pre-clean index file so we know the sweep produced it fresh.
  try { await fsp.unlink(indexPath); log('[smoke] removed pre-existing index', artifacts); } catch {}

  // 3. Trigger sweep.
  ws.send({ action: 'replay_incident_index_build', arg: 'start' });
  log('[smoke] dispatched replay_incident_index_build; polling for index file…', artifacts);

  // 4. Poll for the file.
  const deadline = Date.now() + args.scenarioTimeoutMs;
  let indexExists = false;
  while (Date.now() < deadline) {
    try { await fsp.access(indexPath); indexExists = true; break; } catch {}
    await new Promise(r => setTimeout(r, 1000));
  }

  if (!indexExists) {
    const report = buildSmokeReport({}, Date.now() - startedTs, args.subsession);
    report.passed = false;
    report.failures = [`index_file_missing_after_${args.scenarioTimeoutMs}ms:${indexPath}`, ...(report.failures || [])];
    await artifacts.writeJson('smoke.json', report);
    return {
      assertions: [
        { name: 'ws_liveness',       passed: true,  details: { frame: liveness.frame } },
        { name: 'index_file_exists', passed: false, details: { path: indexPath, timeout_ms: args.scenarioTimeoutMs } },
      ],
      summary: report,
    };
  }

  // 5. Read + parse.
  let indexJson = null;
  let parseError = null;
  try {
    const text = await fsp.readFile(indexPath, 'utf8');
    indexJson = JSON.parse(text.charCodeAt(0) === 0xFEFF ? text.slice(1) : text); // tolerate plugin UTF-8 BOM
    await artifacts.writeJson('index.json', indexJson);
  } catch (err) {
    parseError = err;
  }

  if (parseError) {
    const report = buildSmokeReport({}, Date.now() - startedTs, args.subsession);
    report.passed = false;
    report.failures = [`index_file_unparsable:${parseError.message}`, ...(report.failures || [])];
    await artifacts.writeJson('smoke.json', report);
    return {
      assertions: [
        { name: 'ws_liveness',         passed: true,  details: { frame: liveness.frame } },
        { name: 'index_file_exists',   passed: true,  details: { path: indexPath } },
        { name: 'index_file_parsable', passed: false, details: { error: parseError.message } },
      ],
      summary: report,
    };
  }

  // 6. Build smoke report (validates + aggregates).
  const report = buildSmokeReport(indexJson, Date.now() - startedTs, args.subsession);
  await artifacts.writeJson('smoke.json', report);
  log(`[smoke] passed=${report.passed} totals=${JSON.stringify(report.totals)} `
    + `sessions=${report.sessions.length} failures=${report.failures.length}`, artifacts);

  // Each failure becomes its own assertion entry so they show up in run.json + Loki push.
  const v = validateSmokeIndex(indexJson);
  const assertions = [
    { name: 'ws_liveness',                passed: true, details: { frame: liveness.frame } },
    { name: 'index_file_exists',          passed: true, details: { path: indexPath } },
    { name: 'index_file_parsable',        passed: true, details: { incident_rows: _smokeIncidents(indexJson).length } },
    { name: 'sessions_present',           passed: !v.failures.includes('sessions_array_missing') && !v.failures.includes('sessions_array_empty'),
      details: { count: (_smokeSessions(indexJson) || []).length } },
    { name: 'incidents_array_present',    passed: !v.failures.includes('incidents_array_missing'),
      details: { count: _smokeIncidents(indexJson).length } },
    { name: 'all_impact_classes_valid',   passed: !v.failures.some(f => f.startsWith('session_invalid_impact_class')),
      details: {} },
    { name: 'no_orphan_incident_sessions', passed: !v.failures.some(f => f.startsWith('incident_orphan_sessionNum') || f === 'incident_missing_sessionNum'),
      details: {} },
  ];

  return {
    assertions,
    summary: report,
  };
}

// ────────────────────────────────────────────────────────────────────────────
//  Assertion evaluator (exported for tests)
// ────────────────────────────────────────────────────────────────────────────

function evaluateAssertions(assertions) {
  let pass = 0, fail = 0;
  for (const a of assertions || []) {
    if (a.passed) pass++; else fail++;
  }
  return { pass_count: pass, fail_count: fail, ok: fail === 0 };
}

// ────────────────────────────────────────────────────────────────────────────
//  Subsession resolution (pure — exported for tests)
// ────────────────────────────────────────────────────────────────────────────

// flagValue: number|null (from --subsession), helloValue: number|null (from session_hello).
function resolveSubsession({ flagValue, helloValue }) {
  const hello = Number.isFinite(helloValue) && helloValue > 0 ? helloValue : null;
  const flag  = Number.isFinite(flagValue)  && flagValue  > 0 ? flagValue  : null;
  if (hello === null) return { ok: false, error: 'no_replay_loaded' };
  if (flag === null)  return { ok: true, subsession: hello, source: 'hello' };
  if (flag === hello) return { ok: true, subsession: hello, source: 'flag' };
  return { ok: false, error: 'subsession_mismatch', flag, loaded: hello };
}

// ────────────────────────────────────────────────────────────────────────────
//  Main
// ────────────────────────────────────────────────────────────────────────────

async function main(argv) {
  let args;
  try {
    args = parseArgs(argv);
  } catch (err) {
    process.stderr.write(`error: ${err.message}\n\n`);
    printUsage();
    process.exit(2);
  }
  if (args.help) { printUsage(); return 0; }

  const artifactDir = await makeArtifactDir();
  const artifacts   = new Artifacts(artifactDir);
  const startedAt   = new Date();
  const startedTs   = Date.now();

  log(`[run] artifacts: ${artifactDir}`, artifacts);
  log(`[run] subsession=${args.subsession} scenario=${args.scenario}`, artifacts);

  pushLoki('test_rig_run_started', 'INFO', {
    sub_session_id: args.subsession,
    scenario:       args.scenario,
    pid:            process.pid,
    artifact_dir:   artifactDir,
  });

  let phase     = 'init';
  let scenarioResult = null;
  let runError  = null;

  try {
    phase = 'ws_connect';
    const ws = new WsClient(args.wsUrl, artifacts);
    await ws.connect({ timeoutMs: 30000 });

    phase = 'session_hello';
    const helloIsReady = m =>
      m && m.type === 'session_hello' &&
      Number.isFinite(m.sub_session_id) && m.sub_session_id > 0;
    let helloSubId = null;
    if (helloIsReady(ws.lastHello)) {
      helloSubId = ws.lastHello.sub_session_id;
    } else {
      try {
        const hello = await ws.waitFor(helloIsReady,
          { timeoutMs: ANCHOR_TIMEOUT_MS, label: 'session_hello' });
        helloSubId = hello.sub_session_id;
      } catch { helloSubId = null; }
    }

    const resolved = resolveSubsession({ flagValue: args.subsession, helloValue: helloSubId });
    if (!resolved.ok) {
      const message = resolved.error === 'subsession_mismatch'
        ? `subsession_mismatch: --subsession=${resolved.flag} but loaded replay=${resolved.loaded}`
        : 'no_replay_loaded: open iRacing and load a replay first';
      throw new Error(message);
    }
    args.subsession = resolved.subsession;
    log(`[run] subsession=${args.subsession} (source=${resolved.source})`, artifacts);

    phase = 'anchor';
    await anchorAtStart(ws, artifacts);

    phase = 'scenario';
    log(`[run] scenario=${args.scenario} timeout=${args.scenarioTimeoutMs}ms`, artifacts);
    const scenarioFn = scenarios[args.scenario];
    scenarioResult = await scenarioFn({ ws, args, artifacts });

    // Per-assertion log push.
    for (const a of (scenarioResult.assertions || [])) {
      pushLoki('test_rig_run_assertion', 'INFO', {
        sub_session_id: args.subsession,
        scenario:       args.scenario,
        assertion:      a.name,
        passed:         !!a.passed,
        details:        a.details ?? null,
      });
    }

    phase = 'teardown';
    ws.close();
    if (args.teardown) {
      await runStopAll({ artifacts });
    }
  } catch (err) {
    runError = err;
    log(`[run] FAILED in phase=${phase}: ${err.message}`, artifacts);
    pushLoki('test_rig_run_failed', 'ERROR', {
      sub_session_id: args.subsession,
      scenario:       args.scenario,
      phase,
      error:          err.message || String(err),
    });
  }

  const totals = scenarioResult
    ? evaluateAssertions(scenarioResult.assertions)
    : { pass_count: 0, fail_count: 1, ok: false };
  const totalMs = Date.now() - startedTs;

  pushLoki('test_rig_run_completed', runError ? 'ERROR' : 'INFO', {
    sub_session_id: args.subsession,
    scenario:       args.scenario,
    total_ms:       totalMs,
    pass_count:     totals.pass_count,
    fail_count:     totals.fail_count,
    error:          runError?.message ?? null,
  });

  await artifacts.writeJson('run.json', {
    cli:        { argv: argv },
    args,
    started_at: startedAt.toISOString(),
    ended_at:   new Date().toISOString(),
    total_ms:   totalMs,
    phase_at_exit: runError ? phase : 'completed',
    error:      runError?.message ?? null,
    pass_count: totals.pass_count,
    fail_count: totals.fail_count,
    ok:         totals.ok && !runError,
    scenario_result: scenarioResult,
  });
  await artifacts.close();

  log(`[run] done — pass=${totals.pass_count} fail=${totals.fail_count} total_ms=${totalMs} ok=${totals.ok && !runError}`);
  return (totals.ok && !runError) ? 0 : 1;
}

// ────────────────────────────────────────────────────────────────────────────
//  Entry / exports
// ────────────────────────────────────────────────────────────────────────────

if (require.main === module) {
  main(process.argv.slice(2)).then(
    code => process.exit(code),
    err  => { process.stderr.write(`fatal: ${err.stack || err.message || err}\n`); process.exit(99); }
  );
}

module.exports = {
  parseArgs,
  evaluateAssertions,
  indexFilePath,
  SCENARIOS,
  // smoke helpers (pure — used by run.test.js)
  aggregateByImpactClass,
  validateSmokeIndex,
  buildSmokeReport,
  resolveSubsession,
  VALID_IMPACT_CLASSES,
  // for completeness — exposed for ad-hoc reuse
  pushLoki,
};
