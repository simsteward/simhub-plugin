#!/usr/bin/env node
// One-time delta correction: the pricing-gap backfill (backfill-pricing-gap.js) already
// pushed corrected claude_turn_metrics entries using MODEL_PRICING at the time it ran.
// If MODEL_PRICING changes again afterward (e.g. switching claude-sonnet-5 from standard
// to intro pricing), those already-pushed entries are now stale — but Loki is append-only,
// so they can't be edited. Instead of pushing new absolute values (which would double-count
// when summed with the first correction), this pushes the DELTA (new - old) for every
// cost-bearing field so sum_over_time(...) nets out to the new correct total.
//
// Usage:
//   node scripts/backfill-pricing-delta.js                # DRY RUN
//   node scripts/backfill-pricing-delta.js --push          # actually push deltas
//   node scripts/backfill-pricing-delta.js --since 24h      # lookback window (default 24h)

const https = require('https');
const http  = require('http');
const fs    = require('fs');
const path  = require('path');
const os    = require('os');

const { computeCostBreakdown } = require('./hooks/loki-log.js');

const PUSH = process.argv.includes('--push');
const sinceArg = (() => {
  const i = process.argv.indexOf('--since');
  return i >= 0 ? process.argv[i + 1] : '24h';
})();

function parseSince(s) {
  const m = /^(\d+)([dhm])$/.exec(s);
  if (!m) throw new Error(`--since must look like 14d / 24h / 30m, got "${s}"`);
  return Number(m[1]) * { d: 86400, h: 3600, m: 60 }[m[2]];
}

function loadEnv() {
  const candidates = [
    path.join(process.cwd(), '.env'),
    path.join(os.homedir(), 'dev', 'sim-steward', 'simhub-plugin', '.env'),
  ];
  for (const f of candidates) {
    try {
      for (const line of fs.readFileSync(f, 'utf8').split(/\r?\n/)) {
        const t = line.replace(/#.*$/, '').trim();
        if (!t || !t.includes('=')) continue;
        const eq = t.indexOf('=');
        const key = t.slice(0, eq).trim();
        let val = t.slice(eq + 1).trim().replace(/^["']|["']$/g, '');
        if (key && (key.startsWith('SIMSTEWARD_') || !(key in process.env))) process.env[key] = val;
      }
      break;
    } catch {}
  }
}
loadEnv();

const lokiUrl      = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
const lokiUser     = process.env.SIMSTEWARD_LOKI_USER  || '';
const lokiToken    = process.env.SIMSTEWARD_LOKI_TOKEN || '';
const grafanaUrl   = (process.env.GRAFANA_URL || process.env.SIMSTEWARD_GRAFANA_BASE_URL || '').replace(/\/+$/, '');
const grafanaToken = (process.env.GRAFANA_DEPLOY_TOKEN || process.env.CURSOR_ELEVATED_GRAFANA_TOKEN || process.env.GRAFANA_API_TOKEN || '').replace(/^"|"$/g, '');

function lokiQueryRange(startNs, endNs) {
  return new Promise((resolve, reject) => {
    const q = encodeURIComponent('{app="claude-token-metrics"}');
    const url = new URL(`${grafanaUrl}/api/datasources/proxy/uid/grafanacloud-logs/loki/api/v1/query_range?query=${q}&start=${startNs}&end=${endNs}&limit=1000&direction=backward`);
    const lib = url.protocol === 'https:' ? https : http;
    lib.get({ hostname: url.hostname, port: url.port || 443, path: url.pathname + url.search,
      headers: { Authorization: `Bearer ${grafanaToken}` } }, res => {
      let data = ''; res.on('data', d => data += d);
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) return reject(new Error(`HTTP ${res.statusCode}: ${data.slice(0, 300)}`));
        try { resolve(JSON.parse(data)); } catch (e) { reject(e); }
      });
    }).on('error', reject);
  });
}

async function fetchAlreadyCorrected(startNs, endNs) {
  const out = [];
  let end = endNs;
  for (;;) {
    const resp = await lokiQueryRange(startNs, end);
    const streams = resp?.data?.result || [];
    let rawCount = 0, oldestRawTs = null;
    for (const s of streams) {
      for (const [ts, line] of s.values) {
        rawCount++;
        if (oldestRawTs === null || BigInt(ts) < BigInt(oldestRawTs)) oldestRawTs = ts;
        let body;
        try { body = JSON.parse(line); } catch { continue; }
        if (body.event !== 'claude_turn_metrics' || body.pricing_gap_corrected !== true) continue;
        out.push({
          stream: { app: 'claude-token-metrics', env: body.env || 'unknown', model: body.model || 'unknown', project: body.project || 'unknown', effort: body.effort || 'unknown' },
          ts, body,
        });
      }
    }
    if (rawCount === 0) break;
    const nextEnd = (BigInt(oldestRawTs) - 1n).toString();
    if (nextEnd === end) break;
    end = nextEnd;
    if (rawCount < 1000) break;
  }
  const seen = new Set();
  return out.filter(e => { const k = e.ts; if (seen.has(k)) return false; seen.add(k); return true; });
}

function buildDelta(entry) {
  const { body } = entry;
  const recomputed = computeCostBreakdown({
    model: body.model,
    total_input_tokens: body.total_input_tokens || 0,
    total_output_tokens: body.total_output_tokens || 0,
    total_cache_creation_tokens: body.total_cache_creation_tokens || 0,
    total_cache_read_tokens: body.total_cache_read_tokens || 0,
  });
  if (!recomputed || recomputed.pricing_known !== true) return null;

  const round5 = n => Math.round(n * 100000) / 100000;
  const delta = {
    cost_usd:             round5(recomputed.cost_usd             - (body.cost_usd             || 0)),
    input_cost_usd:       round5(recomputed.input_cost_usd       - (body.input_cost_usd        || 0)),
    output_cost_usd:      round5(recomputed.output_cost_usd      - (body.output_cost_usd       || 0)),
    cache_write_cost_usd: round5(recomputed.cache_write_cost_usd - (body.cache_write_cost_usd  || 0)),
    cache_read_cost_usd:  round5(recomputed.cache_read_cost_usd  - (body.cache_read_cost_usd   || 0)),
    cache_savings_usd:    round5(recomputed.cache_savings_usd    - (body.cache_savings_usd     || 0)),
  };
  if (delta.cost_usd === 0) return null; // pricing unchanged for this model — nothing to correct

  const correctedLine = {
    ...body,
    ...delta,
    pricing_known: true,
    pricing_gap_corrected: undefined,       // this is a v2 delta, not the original correction marker
    pricing_delta_correction: true,
    pricing_delta_correction_from_ts: entry.ts,
  };
  delete correctedLine.pricing_gap_corrected;
  const correctedTs = (BigInt(entry.ts) + 2000n).toString(); // +2000ns: after the +1000ns v1 correction
  return { stream: entry.stream, ts: correctedTs, line: JSON.stringify(correctedLine), delta: delta.cost_usd, model: body.model };
}

function pushOne(entry) {
  return new Promise(resolve => {
    const body = JSON.stringify({ streams: [{ stream: entry.stream, values: [[entry.ts, entry.line]] }] });
    const url = new URL(lokiUrl + '/loki/api/v1/push');
    const lib = url.protocol === 'https:' ? https : http;
    const auth = (lokiUser && lokiToken) ? 'Basic ' + Buffer.from(lokiUser + ':' + lokiToken).toString('base64') : undefined;
    const req = lib.request({ hostname: url.hostname, port: url.port || 443, path: url.pathname, method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body), ...(auth ? { Authorization: auth } : {}) } }, res => {
      let data = ''; res.on('data', d => data += d);
      res.on('end', () => resolve({ ok: res.statusCode >= 200 && res.statusCode < 300, status: res.statusCode, body: data }));
    });
    req.on('error', e => resolve({ ok: false, status: 0, body: e.message }));
    req.write(body); req.end();
  });
}

(async () => {
  if (!grafanaUrl || !grafanaToken) { console.error('GRAFANA_URL / a Grafana token not set.'); process.exit(1); }
  if (PUSH && (!lokiUrl || !lokiUser || !lokiToken)) { console.error('SIMSTEWARD_LOKI_URL / USER / TOKEN not set.'); process.exit(1); }

  const windowSec = parseSince(sinceArg);
  const endNs = BigInt(Date.now()) * 1_000_000n;
  const startNs = endNs - BigInt(windowSec) * 1_000_000_000n;

  console.log(`Window : last ${sinceArg}`);
  console.log(`Mode   : ${PUSH ? 'PUSH → ' + lokiUrl : 'DRY RUN'}\n`);

  const already = await fetchAlreadyCorrected(startNs.toString(), endNs.toString());
  console.log(`Already-corrected (v1) entries found : ${already.length}`);

  const deltas = already.map(buildDelta).filter(Boolean);
  let totalDelta = 0; const byModel = {};
  for (const d of deltas) { totalDelta += d.delta; byModel[d.model] = (byModel[d.model] || 0) + d.delta; }
  console.log(`Entries needing a delta correction    : ${deltas.length}`);
  console.log(`Net delta (new pricing - old pricing) : $${totalDelta.toFixed(4)}`);
  console.log('By model                              :', Object.entries(byModel).map(([m, c]) => `${m}=$${c.toFixed(4)}`).join('  ') || '(none)');

  if (!PUSH) { console.log('\nDry run complete. Re-run with --push to write delta corrections to Loki.'); return; }
  if (deltas.length === 0) { console.log('\nNothing to push.'); return; }

  let pushed = 0, failed = 0;
  for (const d of deltas) {
    const r = await pushOne(d);
    if (r.ok) pushed++;
    else { failed++; if (failed <= 3) console.error(`  push failed [${r.status}]: ${r.body.slice(0, 160)}`); }
  }
  console.log(`\nPushed ${pushed}/${deltas.length}  |  failed: ${failed}`);
  if (failed > 0) process.exit(1);
})();
