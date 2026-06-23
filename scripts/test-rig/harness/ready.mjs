#!/usr/bin/env node
// ─────────────────────────────────────────────────────────────────────────────
//  ready.mjs — Readiness probe
//
//  Purpose: after a SimHub/deploy restart, IRSDK takes 10–30 s to reconnect to
//  iRacing. This probe holds a WS connection (auto-reconnecting) until the plugin
//  emits its first `replay_state_tick` — the signal that the replay is loaded and
//  the test rig can be driven. Run it before triggering a sweep so dashboard
//  clicks / WS commands don't no-op against a "Waiting…" plugin.
//
//  Usage:
//    node scripts/test-rig/harness/ready.mjs [--ws ws://localhost:19847] [--timeout-ms 45000]
//
//  Exit codes:
//    0  a replay_state_tick arrived (prints sub / frame / session_time)
//    1  timed out — no replay_state_tick (IRSDK still settling or replay not loaded)
//
//  Node 22+ (built-in global WebSocket). No npm deps.
// ─────────────────────────────────────────────────────────────────────────────

'use strict';

// ── tiny arg parser ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const args = { ws: 'ws://localhost:19847', timeoutMs: 45000 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    switch (a) {
      case '--ws':         args.ws = next(); break;
      case '--timeout-ms': args.timeoutMs = parseInt(next(), 10); break;
      case '-h':
      case '--help':       args.help = true; break;
      default:
        if (a.startsWith('--')) throw new Error(`unknown_arg:${a}`);
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
if (args.help) {
  console.log('Usage: node ready.mjs [--ws ws://localhost:19847] [--timeout-ms 45000]');
  process.exit(0);
}

const log = (s) => console.log(new Date().toISOString(), s);

const deadline = Date.now() + args.timeoutMs;
let ws;
let attempts = 0;
let gotHello = false;
let gotTick = false;

function connect() {
  ws = new WebSocket(args.ws);
  ws.addEventListener('open', () =>
    log('[connected, waiting for IRSDK/replay readiness]'));
  ws.addEventListener('error', () => { /* swallowed — reconnect loop handles it */ });
  ws.addEventListener('message', (ev) => {
    let m;
    try { m = JSON.parse(ev.data); } catch { return; }
    if (m.type === 'session_hello' && !gotHello) {
      gotHello = true;
      log('  session_hello: sub=' + m.sub_session_id + ' mode=' + m.plugin_mode);
    }
    if (m.type === 'replay_state_tick' && !gotTick) {
      gotTick = true;
      log('  READY ✓ replay_state_tick: sub=' + (m.sub_session_id ?? '?') +
          ' frame=' + m.frame + ' frame_end=' + m.frame_end +
          ' paused=' + m.paused + ' session_time=' + m.session_time);
      process.exit(0);
    }
  });
}

connect();

setInterval(() => {
  if (Date.now() > deadline) {
    log('  NOT READY after ' + args.timeoutMs + 'ms — no replay_state_tick ' +
        '(IRSDK may still be settling or replay not loaded)');
    process.exit(1);
  }
  if (ws.readyState > 1) { // CLOSING or CLOSED
    attempts++;
    log('  reconnecting (' + attempts + ')...');
    connect();
  }
}, 5000);
