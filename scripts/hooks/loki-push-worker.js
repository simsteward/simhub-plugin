// Detached Loki push worker — spawned by loki-log.js, never called directly.
// Reads a JSON batch from stdin, sends one HTTP POST to Loki, exits.
// Runs decoupled from the Claude Code hook process so the hook doesn't wait on network I/O.
//
// Stdin payload: { lokiUrl, lokiAuth, pushes: [{ stream, logLine }] }
// Each push becomes one stream entry; all pushes are batched into a single HTTP request.

const https = require('https');
const http = require('http');

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', c => { raw += c; });
process.stdin.on('end', () => {
  let payload;
  try { payload = JSON.parse(raw); } catch { process.exit(0); }

  const { lokiUrl, lokiAuth, pushes } = payload;
  if (!lokiUrl || !Array.isArray(pushes) || pushes.length === 0) process.exit(0);

  const ts = String(Date.now()) + '000000';
  const streams = pushes.map(p => ({ stream: p.stream, values: [[ts, p.logLine]] }));
  const body = JSON.stringify({ streams });

  let parsed;
  try { parsed = new URL(lokiUrl + '/loki/api/v1/push'); } catch { process.exit(0); }

  const mod = parsed.protocol === 'https:' ? https : http;
  const req = mod.request(parsed, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Content-Length': Buffer.byteLength(body),
      ...(lokiAuth ? { Authorization: lokiAuth } : {}),
    },
    timeout: 4000,
  }, res => { res.resume(); });

  req.on('error', () => {});
  req.on('timeout', () => req.destroy());
  req.write(body);
  req.end();
});
