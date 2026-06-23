#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
//  monitor.mjs — Sweep verification engine
//
//  Purpose: connect to the plugin, HOLD the WS connection open for the whole
//  sweep (the plugin cancels an in-progress build when the last dashboard client
//  disconnects), and replace human attention with four independent on-wire
//  signals sampled over time. It RAISES the moment reality diverges from
//  expectation, detects completion, parses the resulting index, and prints a
//  per-session summary. Its EXIT CODE is the unattended verdict.
//
//  The four signals (see docs/TEST-RIG-HARNESS.md):
//    1. sweep frame advancing at speed × 60 fps   (replay_sweep_progress_tick.frame)
//    2. samples accumulating                       (.samples_so_far)
//    3. telemetry_play_speed (sim read-back)       (.telemetry_play_speed)
//    4. requested == actual speed                  (.play_speed_requested)
//
//  Usage:
//    node scripts/test-rig/harness/monitor.mjs --sub <id> [opts]
//
//  Flags:
//    --sub <id>          REQUIRED. Locates the index at
//                        %LOCALAPPDATA%/SimSteward/replay-incident-index/<sub>.json
//    --ws <url>          default ws://localhost:19847
//    --expect-speed <n>  optional; assert telemetry_play_speed==n, flag mismatch
//    --heartbeat-ms <n>  default 30000   — 4-signal heartbeat cadence
//    --stall-ms <n>      default 90000   — raise if frame hasn't advanced in this long
//    --cap-min <n>       default 45      — watchdog: hard stop after N minutes
//    --preserve <path>   optional; copy the index here on completion
//
//  Exit codes (the unattended verdict):
//    0  verified full-coverage completion (max progress-frame ≈ frame_end ≥ 99.9%)
//    2  sweep frame STALLED beyond --stall-ms
//    3  speed mismatch (telemetry_play_speed != requested, or != --expect-speed)
//    4  watchdog cap hit (--cap-min) — never completed
//    5  TRUNCATED coverage / implied sample-cap-hit (completed but max frame << frame_end)
//    6  could not parse the index on completion
//    7  timeout: no ticks at all before the cap
//
//  Node 22+ (built-in global WebSocket). No npm deps.
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

import fs   from 'node:fs';
import os   from 'node:os';
import path from 'node:path';

// ── tiny arg parser ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const args = {
    sub: null,
    ws: 'ws://localhost:19847',
    expectSpeed: null,
    heartbeatMs: 30000,
    stallMs: 90000,
    capMin: 45,
    preserve: null,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '--sub':          args.sub = next(); break;
      case '--ws':           args.ws = next(); break;
      case '--expect-speed': args.expectSpeed = Number(next()); break;
      case '--heartbeat-ms': args.heartbeatMs = parseInt(next(), 10); break;
      case '--stall-ms':     args.stallMs = parseInt(next(), 10); break;
      case '--cap-min':      args.capMin = parseInt(next(), 10); break;
      case '--preserve':     args.preserve = next(); break;
      case '-h':
      case '--help':         args.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown_arg:${a}`);
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
if (args.help) {
  console.log('Usage: node monitor.mjs --sub <id> [--ws url] [--expect-speed n] ' +
              '[--heartbeat-ms n] [--stall-ms n] [--cap-min n] [--preserve path]');
  process.exit(0);
}
if (!args.sub) {
  console.error('FATAL: --sub <id> is required');
  process.exit(2);
}

// Index folder per ReplayIncidentIndexOutputPaths.cs
function indexFilePath(sub) {
  const localAppData = process.env.LOCALAPPDATA
    || path.join(os.homedir(), 'AppData', 'Local');
  return path.join(localAppData, 'SimSteward', 'replay-incident-index', `${sub}.json`);
}

const INDEX = indexFilePath(args.sub);
const LABEL = args.expectSpeed != null ? `${args.expectSpeed}x` : 'sweep';
const START = Date.now();
const log = (s) => console.log(new Date().toISOString(), s);

log(`[monitor] sub=${args.sub} index=${INDEX}`);
log(`[monitor] expect-speed=${args.expectSpeed ?? '(none)'} heartbeat=${args.heartbeatMs}ms ` +
    `stall=${args.stallMs}ms cap=${args.capMin}min`);

// ── live state ───────────────────────────────────────────────────────────────
let lastSweep = null;
let lastState = null;
let lastSweepAt = 0;
let sawSweep = false;
let lastFrame = -1;
let lastFrameMoveAt = Date.now();
let maxFrame = -1;          // peak progress-frame observed (for coverage check)
let lastFrameEnd = null;    // frame_end snapshot (taken from progress ticks)
let exiting = false;

const ws = new WebSocket(args.ws);
ws.addEventListener('open',  () => log(`[${LABEL} monitor connected — holding connection open]`));
ws.addEventListener('close', () => log('[ws closed]'));
ws.addEventListener('error', (e) => log('[ws_error] ' + (e?.message || 'unknown')));
ws.addEventListener('message', (ev) => {
  let m;
  try { m = JSON.parse(ev.data); } catch { return; }
  if (m.type === 'replay_sweep_progress_tick') {
    sawSweep = true;
    lastSweep = m;
    lastSweepAt = Date.now();
    if (typeof m.frame_end === 'number') lastFrameEnd = m.frame_end;
    if (typeof m.frame === 'number' && m.frame > maxFrame) maxFrame = m.frame;
    if (m.frame !== lastFrame) { lastFrame = m.frame; lastFrameMoveAt = Date.now(); }
  }
  if (m.type === 'replay_state_tick') lastState = m;
});

// ── 4-signal heartbeat ───────────────────────────────────────────────────────
const hb = setInterval(() => {
  if (lastSweep) {
    const stalled = (Date.now() - lastFrameMoveAt) > args.stallMs;
    log('HEARTBEAT ' + LABEL +
        ' sweep: frame=' + lastSweep.frame + '/' + lastSweep.frame_end +
        ' samples=' + lastSweep.samples_so_far +
        ' pct=' + (lastSweep.est_completion_pct || 0).toFixed(1) +
        ' telemetry_play_speed=' + lastSweep.telemetry_play_speed +
        ' requested=' + lastSweep.play_speed_requested);

    // signal 4: requested == actual
    if (lastSweep.telemetry_play_speed !== lastSweep.play_speed_requested) {
      log('  !! EXCEPTION: telemetry_play_speed != requested (speed lost)');
      return raise(3, 'speed_lost: telemetry_play_speed != play_speed_requested');
    }
    // signal 3: matches expectation (if provided)
    if (args.expectSpeed != null && lastSweep.telemetry_play_speed !== args.expectSpeed) {
      log('  !! EXCEPTION: telemetry_play_speed != --expect-speed ' + args.expectSpeed);
      return raise(3, 'speed_mismatch: telemetry_play_speed=' +
                   lastSweep.telemetry_play_speed + ' expected=' + args.expectSpeed);
    }
    // signals 1+2: frame must keep advancing
    if (stalled) {
      log('  !! EXCEPTION: sweep frame not advanced >' + args.stallMs + 'ms — STALLED');
      return raise(2, 'stalled: frame static for >' + args.stallMs + 'ms');
    }
  } else if (lastState) {
    log('HEARTBEAT ' + LABEL + ' idle: state frame=' + lastState.frame +
        ' paused=' + lastState.paused + ' session_time=' + lastState.session_time);
  } else {
    log('HEARTBEAT ' + LABEL + ': no ticks yet');
  }

  // completion: sweep ticks stopped >8s + state ticks resumed + index file fresh
  if (sawSweep && (Date.now() - lastSweepAt) > 8000 && lastState) {
    let mt = 0;
    try { mt = fs.statSync(INDEX).mtimeMs; } catch { /* not written yet */ }
    if (mt > START) {
      log('[completion: sweep stopped + state resumed + index fresh]');
      finish();
    } else {
      log('  (sweep stopped but index not fresh yet; waiting)');
    }
  }
}, args.heartbeatMs);

// ── completion handler ───────────────────────────────────────────────────────
function finish() {
  if (exiting) return;
  exiting = true;
  clearInterval(hb);
  clearTimeout(watchdog);

  // Coverage verdict: did the sweep reach (essentially) the end of the replay?
  let coveragePct = null;
  if (typeof lastFrameEnd === 'number' && lastFrameEnd > 0 && maxFrame >= 0) {
    coveragePct = (maxFrame / lastFrameEnd) * 100;
  }
  const fullCoverage = coveragePct != null && coveragePct >= 99.9;

  let parsed;
  try {
    let t = fs.readFileSync(INDEX, 'utf8');
    if (t.charCodeAt(0) === 0xFEFF) t = t.slice(1); // strip UTF-8 BOM
    const j = JSON.parse(t);
    const inc  = j.incidents  || j.Incidents  || [];
    const sess = j.sessions   || j.Sessions   || [];
    const val  = j.validation || j.Validation || {};

    const per = {};
    for (const s of sess) {
      const n = s.sessionNum ?? s.SessionNum;
      per[n] = inc.filter(x => (x.sessionNum ?? x.SessionNum) === n).length;
    }
    const srcs = {};
    for (const x of inc) {
      const s = x.detectionSource ?? x.DetectionSource ?? '?';
      srcs[s] = (srcs[s] || 0) + 1;
    }
    const discrepancies = (val.discrepancies || val.Discrepancies || []).length;

    log('==== ' + LABEL.toUpperCase() + ' SWEEP RESULT ====');
    log('total=' + inc.length +
        ' buildMs=' + (j.indexBuildTimeMs ?? j.IndexBuildTimeMs) +
        ' discrepancies=' + discrepancies);
    for (const s of sess) {
      const n = s.sessionNum ?? s.SessionNum;
      log('  [' + n + '] ' + (s.name || s.Name) + ' ' +
          (s.impactClass || s.ImpactClass) + ' incidents=' + per[n]);
    }
    log('  detectionSource: ' + JSON.stringify(srcs));
    log('  coverage: maxFrame=' + maxFrame + ' frame_end=' + lastFrameEnd +
        ' pct=' + (coveragePct != null ? coveragePct.toFixed(2) : '?'));

    parsed = { total: inc.length };
  } catch (e) {
    log('!! EXCEPTION parsing index: ' + e.message);
    return preserveThen(6);
  }

  // Truncated-coverage / implied sample-cap-hit verdict.
  if (!fullCoverage) {
    log('  !! EXCEPTION: TRUNCATED coverage — max progress-frame << frame_end ' +
        '(' + (coveragePct != null ? coveragePct.toFixed(2) + '%' : 'unknown') + '). ' +
        'A sample-cap-hit likely dropped the tail sessions. total=' + parsed.total);
    return preserveThen(5);
  }

  log('  VERIFIED: full-coverage completion (' + coveragePct.toFixed(2) + '%).');
  return preserveThen(0);
}

// Copy the index aside (if --preserve), then exit with the given code.
function preserveThen(code) {
  if (args.preserve) {
    try {
      fs.copyFileSync(INDEX, args.preserve);
      log('[preserve] copied index -> ' + args.preserve);
    } catch (e) {
      log('!! WARN: failed to preserve index: ' + e.message);
    }
  }
  process.exit(code);
}

// raise: log already done by caller; clean up and exit non-zero.
function raise(code, why) {
  if (exiting) return;
  exiting = true;
  clearInterval(hb);
  clearTimeout(watchdog);
  log('[RAISE] exit ' + code + ' — ' + why);
  process.exit(code);
}

// ── watchdog ─────────────────────────────────────────────────────────────────
const watchdog = setTimeout(() => {
  if (exiting) return;
  exiting = true;
  clearInterval(hb);
  if (!sawSweep && !lastState) {
    log('[watchdog] ' + args.capMin + 'min cap hit; no ticks ever seen — timeout');
    process.exit(7);
  }
  log('[watchdog] ' + args.capMin + 'min cap hit; sweep never completed');
  process.exit(4);
}, args.capMin * 60 * 1000);
