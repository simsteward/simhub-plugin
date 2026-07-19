#!/usr/bin/env node
// One-time historical backfill: recompute cost for main-session claude_turn_metrics
// entries that were logged with pricing_known=false, now that MODEL_PRICING covers
// the model(s) that were missing (see scripts/hooks/loki-log.js).
//
// WHY: Loki is append-only — the original pricing_known=false log lines can't be
// edited in place. This script reads them back out of Loki (they already carry the
// raw token counts), recomputes cost_usd with the current, corrected pricing table,
// and pushes a second, corrected entry per turn. The original entries have no
// cost_usd field at all, so existing sum_over_time(... unwrap cost_usd) queries
// already skip them — pushing the corrected entry adds the real number without
// double-counting.
//
// Usage:
//   node scripts/backfill-pricing-gap.js                 # DRY RUN — summarize, push nothing
//   node scripts/backfill-pricing-gap.js --push           # actually push to Loki
//   node scripts/backfill-pricing-gap.js --since 14d       # lookback window (default 14d,
//                                                            matches Loki free-tier retention)

const https = require('https');
const http  = require('http');
const fs    = require('fs');
const path  = require('path');
const os    = require('os');

const { computeCostBreakdown } = require('./hooks/loki-log.js');

const PUSH = process.argv.includes('--push');
const sinceArg = (() => {
  const i = process.argv.indexOf('--since');
  return i >= 0 ? process.argv[i + 1] : '14d';
})();

function parseSince(s) {
  const m = /^(\d+)([dhm])$/.exec(s);
  if (!m) throw new Error(`--since must look like 14d / 24h / 30m, got "${s}"`);
  const n = Number(m[1]);
  const mult = { d: 86400, h: 3600, m: 60 }[m[2]];
  return n * mult;
}

// --- .env loading (identical to backfill-subagent-usage.js) ---
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

// Push uses the direct Loki push endpoint (write-scoped token, same as
// backfill-subagent-usage.js). Query uses the Grafana datasource-proxy instead —
// SIMSTEWARD_LOKI_TOKEN is a write-only access-policy token and 401s
// ("invalid scope requested") against Loki's own query API; the Grafana-side
// service-account token is what actually has read scope.
const lokiUrl       = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
const lokiUser      = process.env.SIMSTEWARD_LOKI_USER  || '';
const lokiToken     = process.env.SIMSTEWARD_LOKI_TOKEN || '';
const grafanaUrl    = (process.env.GRAFANA_URL || process.env.SIMSTEWARD_GRAFANA_BASE_URL || '').replace(/\/+$/, '');
const grafanaToken  = (process.env.GRAFANA_DEPLOY_TOKEN || process.env.CURSOR_ELEVATED_GRAFANA_TOKEN || process.env.GRAFANA_API_TOKEN || '').replace(/^"|"$/g, '');

function lokiRequest(pathAndQuery) {
  return new Promise((resolve, reject) => {
    const url = new URL(grafanaUrl + '/api/datasources/proxy/uid/grafanacloud-logs' + pathAndQuery);
    const lib = url.protocol === 'https:' ? https : http;
    lib.get({
      hostname: url.hostname, port: url.port || (url.protocol === 'https:' ? 443 : 80),
      path: url.pathname + url.search,
      headers: { Authorization: `Bearer ${grafanaToken}` },
    }, res => {
      let data = ''; res.on('data', d => data += d);
      res.on('end', () => {
        if (res.statusCode < 200 || res.statusCode >= 300) return reject(new Error(`Loki query HTTP ${res.statusCode}: ${data.slice(0, 300)}`));
        try { resolve(JSON.parse(data)); } catch (e) { reject(e); }
      });
    }).on('error', reject);
  });
}

// --- Fetch every {app="claude-token-metrics"} line with pricing_known=false in the window ---
// Deliberately no `| json` in the query itself — Loki's query API expands parsed fields
// into each result's `stream` object when json-parsing happens query-side, which would
// hand back a high-cardinality label set (assistant_turns, turn_number, etc.) unsafe to
// push back as real stream labels. Fetch raw lines, filter/parse in JS instead, and
// rebuild the stream from the parsed body using the same 5 labels the live hook writes
// (app/env/model/project/effort — see scripts/hooks/loki-log.js's queuePush call).
async function fetchBrokenEntries(startNs, endNs) {
  const out = [];
  let end = endNs;
  // Loki caps result size per request; page backward by narrowing `end` to the oldest
  // returned entry's timestamp minus 1ns until a request returns nothing new.
  for (;;) {
    const q = encodeURIComponent('{app="claude-token-metrics"}');
    const resp = await lokiRequest(`/loki/api/v1/query_range?query=${q}&start=${startNs}&end=${end}&limit=1000&direction=backward`);
    const streams = resp?.data?.result || [];
    let rawCount = 0;
    let oldestRawTs = null;
    for (const s of streams) {
      for (const [ts, line] of s.values) {
        rawCount++;
        if (oldestRawTs === null || BigInt(ts) < BigInt(oldestRawTs)) oldestRawTs = ts;
        let body;
        try { body = JSON.parse(line); } catch { continue; }
        if (body.event !== 'claude_turn_metrics' || body.pricing_known !== false) continue;
        const stream = {
          app: 'claude-token-metrics',
          env: body.env || 'unknown',
          model: body.model || 'unknown',
          project: body.project || 'unknown',
          effort: body.effort || 'unknown',
        };
        out.push({ stream, ts, line });
      }
    }
    if (rawCount === 0) break; // nothing left in this window at all
    const nextEnd = (BigInt(oldestRawTs) - 1n).toString();
    if (nextEnd === end) break; // no progress — stop to avoid an infinite loop
    end = nextEnd;
    if (rawCount < 1000) break; // last page was partial (raw) — nothing older left to fetch
  }
  // Dedupe (ts+line is the natural key; paging can overlap at the boundary)
  const seen = new Set();
  return out.filter(e => {
    const k = e.ts + '|' + e.line;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
}

function buildCorrectedEntry(entry) {
  let body;
  try { body = JSON.parse(entry.line); } catch { return null; }
  if (body.event !== 'claude_turn_metrics') return null;

  const costs = computeCostBreakdown({
    model: body.model,
    total_input_tokens: body.total_input_tokens || 0,
    total_output_tokens: body.total_output_tokens || 0,
    total_cache_creation_tokens: body.total_cache_creation_tokens || 0,
    total_cache_read_tokens: body.total_cache_read_tokens || 0,
  });
  if (!costs || costs.pricing_known !== true) return null; // still unpriced — nothing to correct

  const corrected = {
    ...body,
    pricing_known: true,
    cost_usd: costs.cost_usd,
    input_cost_usd: costs.input_cost_usd,
    output_cost_usd: costs.output_cost_usd,
    cache_write_cost_usd: costs.cache_write_cost_usd,
    cache_read_cost_usd: costs.cache_read_cost_usd,
    cache_savings_usd: costs.cache_savings_usd,
    pricing_gap_corrected: true,
    pricing_gap_corrected_from_ts: entry.ts,
  };
  // Nudge the timestamp by 1000ns so it never collides with the original line
  // (same stream, different content — Loki would accept an exact-ts duplicate too,
  // but this keeps the two visibly distinct and orderable).
  const correctedTs = (BigInt(entry.ts) + 1000n).toString();
  return { stream: entry.stream, ts: correctedTs, line: JSON.stringify(corrected), cost: costs.cost_usd, model: body.model };
}

function pushOne(entry) {
  return new Promise(resolve => {
    const body = JSON.stringify({ streams: [{ stream: entry.stream, values: [[entry.ts, entry.line]] }] });
    const url = new URL(lokiUrl + '/loki/api/v1/push');
    const lib = url.protocol === 'https:' ? https : http;
    const auth = (lokiUser && lokiToken) ? 'Basic ' + Buffer.from(lokiUser + ':' + lokiToken).toString('base64') : undefined;
    const req = lib.request({
      hostname: url.hostname, port: url.port || (url.protocol === 'https:' ? 443 : 80),
      path: url.pathname, method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body),
        ...(auth ? { Authorization: auth } : {}) },
    }, res => {
      let data = ''; res.on('data', d => data += d);
      res.on('end', () => resolve({ ok: res.statusCode >= 200 && res.statusCode < 300, status: res.statusCode, body: data }));
    });
    req.on('error', e => resolve({ ok: false, status: 0, body: e.message }));
    req.write(body); req.end();
  });
}

(async () => {
  if (!grafanaUrl || !grafanaToken) { console.error('GRAFANA_URL / a Grafana token not set — cannot query Loki via the datasource proxy.'); process.exit(1); }
  if (PUSH && (!lokiUrl || !lokiUser || !lokiToken)) { console.error('SIMSTEWARD_LOKI_URL / USER / TOKEN not set — cannot push to Loki.'); process.exit(1); }

  const windowSec = parseSince(sinceArg);
  const endNs = BigInt(Date.now()) * 1_000_000n;
  const startNs = endNs - BigInt(windowSec) * 1_000_000_000n;

  console.log(`Window      : last ${sinceArg}`);
  console.log(`Mode        : ${PUSH ? 'PUSH → ' + lokiUrl : 'DRY RUN (use --push to send)'}\n`);

  const broken = await fetchBrokenEntries(startNs.toString(), endNs.toString());
  console.log(`pricing_known=false entries found : ${broken.length}`);

  const corrected = broken.map(buildCorrectedEntry).filter(Boolean);
  const stillUnknown = broken.length - corrected.length;
  let totalCost = 0; const byModel = {};
  for (const e of corrected) {
    totalCost += e.cost;
    byModel[e.model] = (byModel[e.model] || 0) + e.cost;
  }
  console.log(`Now correctable (model in pricing table) : ${corrected.length}`);
  console.log(`Still unknown (model still unpriced)     : ${stillUnknown}`);
  console.log(`Recovered cost                           : $${totalCost.toFixed(4)}`);
  console.log('By model                                 :', Object.entries(byModel).map(([m, c]) => `${m}=$${c.toFixed(4)}`).join('  ') || '(none)');

  if (!PUSH) { console.log('\nDry run complete. Re-run with --push to write corrected entries to Loki.'); return; }
  if (corrected.length === 0) { console.log('\nNothing to push.'); return; }

  let pushed = 0, skippedOld = 0, failed = 0;
  for (const e of corrected) {
    const r = await pushOne(e);
    if (r.ok) { pushed++; }
    else if (r.status === 400 && /too old|out of order|greater than/i.test(r.body)) { skippedOld++; }
    else { failed++; if (failed <= 3) console.error(`  push failed [${r.status}]: ${r.body.slice(0, 160)}`); }
  }
  console.log(`\nPushed ${pushed}/${corrected.length}  |  skipped (too old for retention): ${skippedOld}  |  failed: ${failed}`);
  if (failed > 0) process.exit(1);
})();
