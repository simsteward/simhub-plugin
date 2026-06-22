#!/usr/bin/env node
// Trigger replay_incident_index_build:start over the plugin WebSocket and stream phase
// updates until finalized or the caller Ctrl-Cs. Uses Node 22+ built-in WebSocket.
const URL = process.argv[2] || 'ws://localhost:19847';
const MAX_RUNTIME_MS = parseInt(process.argv[3] || '270000', 10); // default ~4.5 min

const ws = new WebSocket(URL);
let started = false;
let lastPhase = null;
let lastPct = -1;
let lastFrame = -1;

const t0 = Date.now();

ws.addEventListener('open', () => {
  console.log(`[ws] connected ${URL}`);
});

ws.addEventListener('message', (ev) => {
  let m;
  try { m = JSON.parse(ev.data); } catch { return; }

  // Plugin emits state messages periodically. Trigger start once we see the first.
  if (!started) {
    started = true;
    console.log(`[trigger] -> {action:'replay_incident_index_build', arg:'start'}`);
    ws.send(JSON.stringify({ action: 'replay_incident_index_build', arg: 'start' }));
  }

  // Pull phase / progress fields from anywhere they might live.
  const d = m.diagnostics || m.replayIndexBuild || m.replay_index_build || m;
  const phase = d?.phase || d?.buildPhase || d?.replayIndexBuildPhase || m?.phase;
  const pct   = d?.percent ?? d?.percentComplete ?? d?.progressPct ?? null;
  const frame = d?.replayFrameNum ?? d?.replay_frame_num ?? null;

  if (phase && phase !== lastPhase) {
    console.log(`[${tStamp()}] phase → ${phase}`);
    lastPhase = phase;
  }
  if (pct !== null && pct !== lastPct && Number.isFinite(pct)) {
    console.log(`[${tStamp()}] progress ${pct}%`);
    lastPct = pct;
  }
  if (frame !== null && lastFrame >= 0 && Math.abs(frame - lastFrame) > 5000) {
    console.log(`[${tStamp()}] replayFrame=${frame}`);
  }
  if (frame !== null) lastFrame = frame;

  if (m.action === 'replay_incident_index_build') {
    console.log(`[ack] success=${m.success} error=${m.error || ''}`);
  }

  if (Date.now() - t0 > MAX_RUNTIME_MS) {
    console.log(`[timeout] max runtime ${MAX_RUNTIME_MS}ms reached — closing (build continues server-side)`);
    try { ws.close(); } catch {}
  }
});

ws.addEventListener('close', () => { console.log('[ws] closed'); process.exit(0); });
ws.addEventListener('error', (e) => { console.error('[ws] error', e?.message || e); });

process.on('SIGINT', () => { console.log('\n[ctrl-c] closing'); try { ws.close(); } catch {} });

function tStamp() {
  const sec = ((Date.now() - t0) / 1000).toFixed(1);
  return `+${sec}s`;
}
