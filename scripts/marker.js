#!/usr/bin/env node
// Push a test_run_marker event to Loki (app="sim-steward") to stake a clean start point.
// Usage: node scripts/marker.js [label]
//   label  — optional short string surfaced in Grafana (default: "clean_start")

const https = require('https');
const http  = require('http');
const fs    = require('fs');
const path  = require('path');
const os    = require('os');

const label = process.argv[2] || 'clean_start';

// --- .env loading (identical to loki-log.js) ---
function loadEnv() {
  const candidates = [
    path.join(process.cwd(), '.env'),
    path.join(os.homedir(), 'dev', 'sim-steward', 'simhub-plugin', '.env'),
  ];
  for (const f of candidates) {
    try {
      const text = fs.readFileSync(f, 'utf8');
      for (const line of text.split(/\r?\n/)) {
        const trimmed = line.replace(/#.*$/, '').trim();
        if (!trimmed || !trimmed.includes('=')) continue;
        const eq = trimmed.indexOf('=');
        const key = trimmed.slice(0, eq).trim();
        let val = trimmed.slice(eq + 1).trim().replace(/^["']|["']$/g, '');
        if (key && (key.startsWith('SIMSTEWARD_') || !(key in process.env)))
          process.env[key] = val;
      }
      break;
    } catch { /* try next */ }
  }
}
loadEnv();

const rawUrl  = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
const user    = process.env.SIMSTEWARD_LOKI_USER  || '';
const token   = process.env.SIMSTEWARD_LOKI_TOKEN || '';
const envLabel = process.env.SIMSTEWARD_LOG_ENV   || 'local';

if (!rawUrl) { console.error('SIMSTEWARD_LOKI_URL not set'); process.exit(1); }
if (!user || !token) { console.error('SIMSTEWARD_LOKI_USER / TOKEN not set'); process.exit(1); }

const auth = 'Basic ' + Buffer.from(user + ':' + token).toString('base64');
const nowNs = BigInt(Date.now()) * 1_000_000n;

const payload = {
  event:     'test_run_marker',
  level:     'INFO',
  component: 'marker',
  message:   'Clean start marker — ' + label,
  label,
  machine:   process.env.COMPUTERNAME || os.hostname() || 'unknown',
  ts:        new Date().toISOString(),
};

const body = JSON.stringify({
  streams: [{
    stream: { app: 'sim-steward', env: envLabel, component: 'marker' },
    values: [[nowNs.toString(), JSON.stringify(payload)]],
  }],
});

const url = new URL(rawUrl + '/loki/api/v1/push');
const lib = url.protocol === 'https:' ? https : http;

const req = lib.request({
  hostname: url.hostname,
  port:     url.port || (url.protocol === 'https:' ? 443 : 80),
  path:     url.pathname,
  method:   'POST',
  headers: {
    'Content-Type':   'application/json',
    'Authorization':  auth,
    'Content-Length': Buffer.byteLength(body),
  },
}, res => {
  const ok = res.statusCode >= 200 && res.statusCode < 300;
  let data = '';
  res.on('data', d => data += d);
  res.on('end', () => {
    if (ok) {
      console.log('Marker pushed to Loki [' + envLabel + ']  label=' + label + '  ts=' + payload.ts);
    } else {
      console.error('Loki push failed ' + res.statusCode + ': ' + data);
      process.exit(1);
    }
  });
});
req.on('error', e => { console.error('Request error:', e.message); process.exit(1); });
req.write(body);
req.end();
