#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
//  capture.mjs — Telemetry recorder
//
//  Purpose: record EVERY inbound WS message to a JSONL file — the full progress-
//  tick trajectory plus all `logEvents` (including the build completion audit and
//  any cap-hit / speed-loss warnings). This is the audit trail that surfaces a
//  silent sample-cap truncation after the fact. Run it alongside monitor.mjs.
//
//  Usage:
//    node scripts/test-rig/harness/capture.mjs [opts]
//
//  Flags:
//    --label <name>   default "run"          — used in the default --out filename
//    --out <path>     default ./logs/test-rig/telemetry_<label>.jsonl
//                     (a real path under the repo logs dir; NOT /tmp, which Node
//                      resolves to C:\tmp on Windows)
//    --ws <url>       default ws://localhost:19847
//    --cap-min <n>    default 45             — watchdog: hard stop after N minutes
//
//  Console: flags any sample_cap_hit / *_speed_lost / cancel / error log events,
//  and prints the completion_audit fields when seen. Self-terminates ~12 s after
//  completion is detected, or at the watchdog cap.
//
//  Node 22+ (built-in global WebSocket). No npm deps.
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

import fs   from 'node:fs';
import path from 'node:path';
import url  from 'node:url';

// repo-root-relative default out dir: <repo>/logs/test-rig
const __dirname = path.dirname(url.fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(__dirname, '..', '..', '..');
const LOG_DIR   = path.join(REPO_ROOT, 'logs', 'test-rig');

// ── tiny arg parser ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const args = { label: 'run', out: null, ws: 'ws://localhost:19847', capMin: 45 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '--label':   args.label = next(); break;
      case '--out':     args.out = next(); break;
      case '--ws':      args.ws = next(); break;
      case '--cap-min': args.capMin = parseInt(next(), 10); break;
      case '-h':
      case '--help':    args.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown_arg:${a}`);
    }
  }
  if (!args.out) args.out = path.join(LOG_DIR, `telemetry_${args.label}.jsonl`);
  return args;
}

const args = parseArgs(process.argv.slice(2));
if (args.help) {
  console.log('Usage: node capture.mjs [--label run] [--out path] [--ws url] [--cap-min 45]');
  process.exit(0);
}

const log = (s) => console.log(new Date().toISOString(), s);

fs.mkdirSync(path.dirname(args.out), { recursive: true });
const stream = fs.createWriteStream(args.out, { flags: 'w' });

const counts = {};
let sawSweep = false;
let lastSweepAt = 0;
let lastState = false;
let auditSeen = false;
let doneAt = 0;

const ws = new WebSocket(args.ws);
ws.addEventListener('open',  () => log('[telemetry capture ' + args.label + ' -> ' + args.out + ']'));
ws.addEventListener('error', (e) => log('[ws_error] ' + (e?.message || 'unknown')));
ws.addEventListener('message', (ev) => {
  let m;
  try { m = JSON.parse(ev.data); } catch { return; }
  const k = m.type || '?';
  counts[k] = (counts[k] || 0) + 1;
  const rx = new Date().toISOString();

  if (k === 'replay_sweep_progress_tick') {
    sawSweep = true;
    lastSweepAt = Date.now();
    stream.write(JSON.stringify({ rx, ...m }) + '\n');
  } else if (k === 'replay_state_tick') {
    lastState = true;
    stream.write(JSON.stringify({
      rx, t: 'state', frame: m.frame, paused: m.paused,
      session_time: m.session_time, aggregates: m.aggregates,
    }) + '\n');
  } else if (k === 'logEvents' && Array.isArray(m.entries)) {
    for (const e of m.entries) {
      stream.write(JSON.stringify({
        rx, t: 'log', level: e.level, event: e.event, message: e.message, fields: e.fields,
      }) + '\n');

      const tag = (e.event || '') + (e.message || '');
      // completion audit / coverage / fast-forward-complete
      if (/completion_audit|index_build.*complete|fast_forward_complete|coverage/i.test(tag)) {
        auditSeen = true;
        log('  >> AUDIT/COMPLETE event: ' + (e.event || '') + ' :: ' + (e.message || '').slice(0, 80));
        if (e.fields) log('     fields: ' + JSON.stringify(e.fields));
      }
      // sample-cap-hit — the silent killer this whole harness exists to catch
      if (/sample_cap_hit|cap_hit/i.test(e.event || '')) {
        log('  >> !! SAMPLE CAP HIT: ' + (e.event || '') + ' :: ' + (e.message || '').slice(0, 100));
        if (e.fields) log('     fields: ' + JSON.stringify(e.fields));
      }
      // speed-loss / cancel / prereq / error WARN+
      if (/speed_lost|_warn|prereq|cancel|error/i.test(e.event || '') &&
          (e.level === 'WARN' || e.level === 'ERROR')) {
        log('  >> ' + e.level + ' ' + (e.event || '') + ' :: ' + (e.message || '').slice(0, 80));
      }
    }
  }
});

// completion: sweep stopped + state resumed -> capture trailing 12s then exit
setInterval(() => {
  if (sawSweep && (Date.now() - lastSweepAt) > 10000 && lastState && !doneAt) {
    doneAt = Date.now();
    log('[completion detected; capturing trailing audit for 12s]');
  }
  if (doneAt && Date.now() - doneAt > 12000) {
    log('  [done] message counts: ' + JSON.stringify(counts) + ' auditSeen=' + auditSeen);
    stream.end(() => process.exit(0));
  }
}, 2000);

setTimeout(() => {
  log('[watchdog ' + args.capMin + 'min] counts: ' + JSON.stringify(counts));
  stream.end(() => process.exit(0));
}, args.capMin * 60 * 1000);
