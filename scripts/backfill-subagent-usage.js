#!/usr/bin/env node
// One-time historical backfill: push per-subagent token/cost into Loki so dashboards
// stop undercounting subagent-heavy sessions.
//
// WHY: scripts/hooks/loki-log.js parses ONLY the parent transcript (<sessionId>.jsonl).
// Subagents persist separately at <sessionId>/subagents/agent-<id>.jsonl and are never
// read by the Stop hook, so their tokens/cost are invisible to Grafana. Going forward
// this is fixed by native Claude Code OpenTelemetry (query_source="subagent"); OTEL
// cannot replay history, so this script corrects the past in Loki.
//
// It emits one claude_turn_metrics-shaped entry per subagent to app="claude-token-metrics"
// with is_subagent="true" and query_source="subagent", so existing sum_over_time(... unwrap
// cost_usd) queries pick them up. Entry timestamp = the subagent transcript's own time, so
// history lands in the right window (and re-runs dedupe on identical ts+line per stream).
//
// Usage:
//   node scripts/backfill-subagent-usage.js            # DRY RUN — summarize, push nothing
//   node scripts/backfill-subagent-usage.js --push     # actually push to Loki
//   node scripts/backfill-subagent-usage.js --project-dir <path-to-~/.claude/projects/<slug>>
//
// CAVEAT: Grafana Cloud Loki rejects entries older than its retention / reject_old_samples
// window (free tier ~14 days). Older subagents are reported as skipped, not pushed.

const https = require('https');
const http  = require('http');
const fs    = require('fs');
const path  = require('path');
const os    = require('os');

const { getPricing, computeCostBreakdown, extractSessionTokens } = require('./hooks/loki-log.js');

const PUSH = process.argv.includes('--push');
const projectDirArg = (() => {
  const i = process.argv.indexOf('--project-dir');
  return i >= 0 ? process.argv[i + 1] : null;
})();

// --- .env loading (identical to loki-log.js / marker.js) ---
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

const rawUrl   = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
const user     = process.env.SIMSTEWARD_LOKI_USER  || '';
const token    = process.env.SIMSTEWARD_LOKI_TOKEN || '';
const envLabel = process.env.SIMSTEWARD_LOG_ENV    || 'local';
const project  = path.basename(process.cwd());

// --- Locate this repo's Claude project dir (slug casing varies; match case-insensitively) ---
function resolveProjectDir() {
  if (projectDirArg) return projectDirArg;
  const root = path.join(os.homedir(), '.claude', 'projects');
  const slug = process.cwd().replace(/[\\/:]/g, '-'); // C:\a\b -> C--a-b
  let entries = [];
  try { entries = fs.readdirSync(root); } catch { return null; }
  const match = entries.find(e => e.toLowerCase() === slug.toLowerCase());
  return match ? path.join(root, match) : null;
}

// --- Enumerate <sessionId>/subagents/agent-*.jsonl under the project dir ---
function findSubagentFiles(projectDir) {
  const out = [];
  let sessionDirs = [];
  try { sessionDirs = fs.readdirSync(projectDir, { withFileTypes: true }); } catch { return out; }
  for (const d of sessionDirs) {
    if (!d.isDirectory()) continue;
    const subDir = path.join(projectDir, d.name, 'subagents');
    let files = [];
    try { files = fs.readdirSync(subDir); } catch { continue; }
    for (const f of files) {
      if (f.startsWith('agent-') && f.endsWith('.jsonl')) {
        out.push({ parentSessionId: d.name, agentId: f.replace(/^agent-|\.jsonl$/g, ''), file: path.join(subDir, f) });
      }
    }
  }
  return out;
}

// --- Timestamp of a transcript = last line's ISO `timestamp`, else file mtime ---
function transcriptTimeNs(file) {
  try {
    const lines = fs.readFileSync(file, 'utf8').split('\n').filter(Boolean);
    for (let i = lines.length - 1; i >= 0; i--) {
      try {
        const t = JSON.parse(lines[i]).timestamp;
        if (t) { const ms = Date.parse(t); if (!Number.isNaN(ms)) return BigInt(ms) * 1_000_000n; }
      } catch {}
    }
  } catch {}
  try { return BigInt(Math.floor(fs.statSync(file).mtimeMs)) * 1_000_000n; } catch {}
  return BigInt(Date.now()) * 1_000_000n;
}

// --- Build one claude_turn_metrics entry for a subagent ---
function buildEntry(sub) {
  const tok = extractSessionTokens(sub.file);
  if (!tok) return null;
  const model = tok.model || (tok.models_used && tok.models_used[0]) || undefined;
  if ((tok.total_input_tokens + tok.total_output_tokens
     + tok.total_cache_creation_tokens + tok.total_cache_read_tokens) === 0) return null;

  const costs = computeCostBreakdown({ ...tok, model }) || { pricing_known: false };
  const payload = {
    event:                       'claude_turn_metrics',
    is_final:                    false,
    is_subagent:                 true,
    query_source:                'subagent',
    parent_session_id:           sub.parentSessionId,
    subagent_id:                 sub.agentId,
    model:                       model || 'unknown',
    project,
    machine:                     process.env.COMPUTERNAME || os.hostname() || 'unknown',
    backfilled:                  true,
    pricing_known:               costs.pricing_known === true,
    cost_usd:                    costs.cost_usd || 0,
    input_cost_usd:              costs.input_cost_usd || 0,
    output_cost_usd:             costs.output_cost_usd || 0,
    cache_write_cost_usd:        costs.cache_write_cost_usd || 0,
    cache_read_cost_usd:         costs.cache_read_cost_usd || 0,
    cache_savings_usd:           costs.cache_savings_usd || 0,
    total_input_tokens:          tok.total_input_tokens,
    total_output_tokens:         tok.total_output_tokens,
    total_cache_creation_tokens: tok.total_cache_creation_tokens,
    total_cache_read_tokens:     tok.total_cache_read_tokens,
    total_tokens:                tok.total_tokens,
    assistant_turns:             tok.assistant_turns,
  };
  const stream = {
    app: 'claude-token-metrics', env: envLabel,
    model: model || 'unknown', project,
    effort: tok.effort || 'unknown',
    is_subagent: 'true', query_source: 'subagent',
  };
  return { stream, ts: transcriptTimeNs(sub.file).toString(), line: JSON.stringify(payload), cost: payload.cost_usd };
}

// --- Push a single stream to Loki; resolves { ok, status, body } ---
function pushOne(entry) {
  return new Promise(resolve => {
    const body = JSON.stringify({ streams: [{ stream: entry.stream, values: [[entry.ts, entry.line]] }] });
    const url = new URL(rawUrl + '/loki/api/v1/push');
    const lib = url.protocol === 'https:' ? https : http;
    const auth = (user && token) ? 'Basic ' + Buffer.from(user + ':' + token).toString('base64') : undefined;
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
  const projectDir = resolveProjectDir();
  if (!projectDir) { console.error('Could not locate ~/.claude/projects/<slug> for this repo. Pass --project-dir.'); process.exit(1); }

  const subs = findSubagentFiles(projectDir);
  console.log(`Project dir : ${projectDir}`);
  console.log(`Subagents   : ${subs.length} transcript(s) found`);
  console.log(`Mode        : ${PUSH ? 'PUSH → ' + (rawUrl || '(no SIMSTEWARD_LOKI_URL!)') : 'DRY RUN (use --push to send)'}  env=${envLabel}\n`);

  const entries = subs.map(buildEntry).filter(Boolean);
  let totalCost = 0; const byModel = {};
  for (const e of entries) {
    totalCost += e.cost;
    const m = e.stream.model; byModel[m] = (byModel[m] || 0) + e.cost;
  }
  console.log(`Billable subagents : ${entries.length}`);
  console.log(`Recovered cost     : $${totalCost.toFixed(4)} (previously invisible to dashboards)`);
  console.log('By model           :', Object.entries(byModel).map(([m, c]) => `${m}=$${c.toFixed(4)}`).join('  ') || '(none)');

  if (!PUSH) { console.log('\nDry run complete. Re-run with --push to write to Loki.'); return; }
  if (!rawUrl || !user || !token) { console.error('\nSIMSTEWARD_LOKI_URL / USER / TOKEN not set — cannot push.'); process.exit(1); }

  let pushed = 0, skippedOld = 0, failed = 0;
  for (const e of entries) {
    const r = await pushOne(e);
    if (r.ok) { pushed++; }
    else if (r.status === 400 && /too old|out of order|greater than/i.test(r.body)) { skippedOld++; }
    else { failed++; if (failed <= 3) console.error(`  push failed [${r.status}]: ${r.body.slice(0, 160)}`); }
  }
  console.log(`\nPushed ${pushed}/${entries.length}  |  skipped (too old for retention): ${skippedOld}  |  failed: ${failed}`);
  if (failed > 0) process.exit(1);
})();
