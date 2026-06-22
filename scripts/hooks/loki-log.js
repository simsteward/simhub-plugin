// Claude Code hook -> Loki push
// Self-contained: loads .env, reads stdin, pushes to Loki. No shell wrapper needed.
// Enrichments: tool duration, payload sizes, retry detection, error classification,
//              agent topology, session lifecycle, per-turn cost breakdown.
// Usage: node loki-log.js <hook-type>
//
// DATA ARCHITECTURE:
//   app="claude-dev-logging"   — ALL hook events: tool calls, lifecycle, agents, user prompts,
//                                intermediate token snapshots (component="tokens", stop hook)
//   app="claude-token-metrics" — ONE entry per completed TURN (stop hook), with is_final=false.
//                                cost_usd is the turn's incremental cost. sum_over_time gives session total.
//                                Join key: session_id (present in both streams)
//   claude_session_summary      — pushed to claude-dev-logging at session-end with full token totals
//                                and session metadata. NOT pushed to claude-token-metrics (avoids
//                                double-counting with per-turn sum_over_time queries).
//
// NEW SIGNALS (session_type, plan_mode_entries, tool_time_ms):
//   session_type — extracted from session-start payload hp.type ("startup"|"resume"|"compact").
//                  Emitted as a Loki stream label on the session-start event, and as a JSON field
//                  on all subsequent lifecycle events for that session.
//   plan_mode_entries — count of EnterPlanMode tool calls in the session. Included in session-end.
//   tool_time_ms — per-turn total wall-clock time spent in tool calls (sum of post-tool-use
//                  duration_ms values for all tools fired since the last stop hook). Added to the
//                  claude-token-metrics push so dashboards can correlate tool overhead vs token cost.
//                  Also emits a companion claude_turn_tool_timing event (component="tool-timing")
//                  to claude-dev-logging with per-tool breakdown: {toolName: {total_ms, calls}}.

const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawn } = require('child_process');

const hookType = process.argv[2] || 'unknown';
const hookSource = (process.argv.find(a => a.startsWith('--source=')) || '--source=unknown').slice(9);

// --- Hook-level wall-clock timing (written to hook-timing.log for profiling) ---
const HOOK_TIMING_LOG = path.join(os.tmpdir(), 'claude-hook-timing', 'hook-timing.log');
const HOOK_START_MS = Date.now();
const HOOK_START_TS = new Date().toISOString();

// --- Timing state directory ---
const TIMING_DIR = path.join(os.tmpdir(), 'claude-hook-timing');
const STALE_MS = 5 * 60 * 1000;
const RETRY_WINDOW_MS = 10 * 1000;
try { fs.mkdirSync(TIMING_DIR, { recursive: true }); } catch {}

// --- Secret scrubbing (compiled once at module load) ---
const SECRET_PATTERNS = new RegExp([
  'AKIA[0-9A-Z]{16}',                                          // AWS access key
  '(?:ghp|gho|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}',         // GitHub tokens
  'sntrys_[A-Za-z0-9_]{20,}',                                  // Sentry auth tokens
  'sk-(?:proj-)?[A-Za-z0-9_-]{20,}',                           // OpenAI / Stripe sk- keys
  'eyJ[A-Za-z0-9_-]{10,}\\.eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}', // JWT
  'Bearer\\s+[A-Za-z0-9_.~+/=-]{20,}',                         // Bearer tokens
  '-----BEGIN\\s+(?:RSA |EC |OPENSSH )?PRIVATE KEY-----',       // PEM private keys
  '://[^:@\\s"]{1,64}:[^@\\s"]{1,64}@',                        // URI credentials user:pass@
  'https://[a-f0-9]{32}@[^"\\s]*\\.sentry\\.io',               // Sentry DSN with key
  '(?:PASSWORD|SECRET|TOKEN|API_KEY|PRIVATE_KEY)\\s*[=:]\\s*\\S{8,}', // env var assignments
].join('|'), 'gi');

function scrubSecrets(str) {
  return str.replace(SECRET_PATTERNS, '[REDACTED]');
}

// --- .env loading ---
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
        let val = trimmed.slice(eq + 1).trim();
        val = val.replace(/^["']|["']$/g, '');
        if (key) {
          // Always override SIMSTEWARD_* vars from .env so the file wins over
          // stale inherited env (e.g. old localhost URL in a long-running shell).
          if (key.startsWith('SIMSTEWARD_') || !(key in process.env)) process.env[key] = val;
        }
      }
      break;
    } catch { /* file not found, try next */ }
  }
}
loadEnv();

const rawLokiUrl = (process.env.SIMSTEWARD_LOKI_URL || '').replace(/\/+$/, '');
if (!rawLokiUrl || /^https?:\/\/(localhost|127\.0\.0\.1)/i.test(rawLokiUrl)) {
  // Cloud-only: no local fallback. Exit silently if SIMSTEWARD_LOKI_URL is unset or points at localhost.
  process.exit(0);
}
const lokiUrl = rawLokiUrl;
const lokiUser = process.env.SIMSTEWARD_LOKI_USER || '';
const lokiToken = process.env.SIMSTEWARD_LOKI_TOKEN || '';
const lokiAuth = (lokiUser && lokiToken)
  ? 'Basic ' + Buffer.from(lokiUser + ':' + lokiToken).toString('base64')
  : undefined;
const envLabel = process.env.SIMSTEWARD_LOG_ENV || 'local';
const machine = process.env.COMPUTERNAME || os.hostname() || 'unknown';

// --- MCP service detection ---
const MCP_SERVICES = [
  [/^mcp__claude_ai_Sentry__/,    'sentry'],
  [/^mcp__plugin_sentry_sentry__/, 'sentry'],
  [/^mcp__ollama__/,              'ollama'],
  [/^mcp__playwright__/,          'playwright'],
];

function detectService(toolName) {
  if (!toolName) return undefined;
  const match = MCP_SERVICES.find(([re]) => re.test(toolName));
  if (match) return match[1];
  // Unknown MCP tool — extract the service segment so it's still queryable
  // as component="mcp-<service>" rather than falling through to "tool".
  const m = toolName.match(/^mcp__([^_]+(?:_[^_]+)*?)__/);
  return m ? m[1].replace(/_/g, '-') : undefined;
}

function inferProject(cwd) {
  if (!cwd || typeof cwd !== 'string') return undefined;
  const normalized = cwd.replace(/\\/g, '/');
  const segments = normalized.split('/').filter(Boolean);
  return segments.length > 0 ? segments[segments.length - 1] : undefined;
}

// --- Path compression: ~ for user home ---
function compress(s) {
  if (typeof s !== 'string') return s;
  return s
    .replace(/C:[\\\/]Users[\\\/][^\\\/"'\s]+[\\\/]/gi, '~/')
    .replace(/\/[a-z]\/Users\/[^\/]+\//gi, '~/');
}
function walk(o) {
  if (o == null) return o;
  if (typeof o === 'string') return compress(o);
  if (Array.isArray(o)) return o.map(walk);
  if (typeof o === 'object') {
    const r = {};
    for (const [k, v] of Object.entries(o)) r[k] = walk(v);
    return r;
  }
  return o;
}

// --- Timing file utilities ---
function writeTimingFile(id, data) {
  try {
    fs.writeFileSync(path.join(TIMING_DIR, id + '.json'), JSON.stringify(data));
  } catch {}
}

function readTimingFile(id, del = true) {
  const fp = path.join(TIMING_DIR, id + '.json');
  try {
    const data = JSON.parse(fs.readFileSync(fp, 'utf8'));
    if (del) try { fs.unlinkSync(fp); } catch {}
    return data;
  } catch { return null; }
}

function cleanStaleFiles() {
  try {
    const now = Date.now();
    for (const f of fs.readdirSync(TIMING_DIR)) {
      if (!f.endsWith('.json')) continue;
      const fp = path.join(TIMING_DIR, f);
      try {
        const age = now - fs.statSync(fp).mtimeMs;
        // Session-scoped files live for the whole session (24h max)
        // Retry markers expire quickly; everything else at STALE_MS
        const threshold = (
          f.startsWith('token-offset-') || f.startsWith('token-totals-') ||
          f.startsWith('session-type-') || f.startsWith('plan-count-') ||
          f.startsWith('turn-tool-timing-')
        )
          ? 24 * 60 * 60 * 1000
          : f.startsWith('retry-') ? RETRY_WINDOW_MS : STALE_MS;
        if (age > threshold) fs.unlinkSync(fp);
      } catch {}
    }
  } catch {}
}

// --- Payload sizes ---
function computePayloadSizes(input, response) {
  const r = {};
  try { if (input != null) r.tool_input_bytes = Buffer.byteLength(JSON.stringify(input), 'utf8'); } catch {}
  try { if (response != null) r.tool_response_bytes = Buffer.byteLength(JSON.stringify(response), 'utf8'); } catch {}
  return r;
}

// --- djb2 hash for retry detection ---
function djb2(str) {
  let hash = 5381;
  for (let i = 0; i < str.length; i++) hash = ((hash << 5) + hash + str.charCodeAt(i)) >>> 0;
  return hash.toString(36);
}

function detectRetry(toolName, toolInput, toolUseId) {
  try {
    const hash = djb2(toolName + ':' + JSON.stringify(toolInput || {}));
    const now = Date.now();
    let isRetry = false, retryOf;

    for (const f of fs.readdirSync(TIMING_DIR)) {
      if (!f.startsWith('retry-') || !f.endsWith('.json')) continue;
      const fp = path.join(TIMING_DIR, f);
      try {
        const d = JSON.parse(fs.readFileSync(fp, 'utf8'));
        if (now - d.timestamp > RETRY_WINDOW_MS) { fs.unlinkSync(fp); continue; }
        if (d.hash === hash && d.tool_use_id !== toolUseId) { isRetry = true; retryOf = d.tool_use_id; }
      } catch {}
    }

    writeTimingFile('retry-' + toolUseId, { hash, tool_use_id: toolUseId, timestamp: now });
    return isRetry ? { is_retry: true, retry_of: retryOf } : {};
  } catch { return {}; }
}

// --- Error type extraction ---
function classifyError(toolResponse) {
  try {
    const s = typeof toolResponse === 'string' ? toolResponse : JSON.stringify(toolResponse || '');
    const lower = s.toLowerCase();
    if (lower.includes('timeout')) return 'timeout';
    if (lower.includes('permission')) return 'permission_denied';
    if (lower.includes('not found') || lower.includes('enoent')) return 'not_found';
    if (lower.includes('econnrefused') || lower.includes('connection refused')) return 'connection_refused';
    if (lower.includes('rate limit') || lower.includes('429')) return 'rate_limited';
    return 'unknown';
  } catch { return 'unknown'; }
}

// --- Plan type configuration ---
// Set CLAUDE_PLAN_TYPE env var to match your subscription: 'pro' ($20), 'max' ($100), 'ultra' ($200).
// Defaults to 'max'. Used for ROI/savings context in Grafana dashboards.
const PLAN_TYPE = process.env.CLAUDE_PLAN_TYPE || 'max'; // 'pro' | 'max' | 'ultra'
const PLAN_MONTHLY_COST = { pro: 20, max: 100, ultra: 200 }[PLAN_TYPE] ?? 100;

// --- Model pricing (per 1M tokens) ---
// Override by placing a JSON file at ~/.claude/model-pricing.json:
// { "claude-opus-4": { "input": 15, "output": 75, "cacheWrite": 18.75, "cacheRead": 1.50 }, ... }
function loadModelPricing() {
  const candidates = [
    path.join(os.homedir(), '.claude', 'model-pricing.json'),
    path.join(process.cwd(), 'model-pricing.json'),
  ];
  for (const f of candidates) {
    try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch {}
  }
  return null;
}

const MODEL_PRICING = loadModelPricing() || {
  'claude-opus-4':   { input: 15,   output: 75,   cacheWrite: 18.75, cacheRead: 1.50 },
  'claude-sonnet-4': { input: 3,    output: 15,   cacheWrite: 3.75,  cacheRead: 0.30 },
  'claude-haiku-4':  { input: 0.80, output: 4,    cacheWrite: 1.00,  cacheRead: 0.08 },
};

function getPricing(model) {
  if (!model) return null;
  if (MODEL_PRICING[model]) return MODEL_PRICING[model];
  // Substring match — longest key wins so 'claude-opus-4-7' beats 'claude-opus-4'
  const m = model.toLowerCase();
  const keys = Object.keys(MODEL_PRICING).sort((a, b) => b.length - a.length);
  for (const key of keys) {
    if (m.includes(key.toLowerCase())) return MODEL_PRICING[key];
  }
  return null; // unknown model — caller must handle gracefully
}

// Returns { cost_usd, input_cost_usd, output_cost_usd, cache_write_cost_usd,
//           cache_read_cost_usd, cache_savings_usd } at 5-decimal precision.
// cache_savings_usd = what cache_read tokens would have cost at full input price minus
// what they actually cost — quantifies caching ROI for Grafana cache-efficiency panels.
// Output tokens are 5× input on Sonnet and dominate session cost; isolating them lets
// dashboards show the real cost driver instead of a single collapsed cost_usd number.
function computeCostBreakdown(tokenData) {
  try {
    const p = getPricing(tokenData.model);
    if (!p) return { pricing_known: false };

    const M = 1_000_000;
    const round5 = n => Math.round(n * 100000) / 100000;

    const inputTokens      = tokenData.total_input_tokens          || 0;
    const outputTokens     = tokenData.total_output_tokens         || 0;
    const cacheWriteTokens = tokenData.total_cache_creation_tokens || 0;
    const cacheReadTokens  = tokenData.total_cache_read_tokens     || 0;

    const inputCost      = (inputTokens      / M) * p.input;
    const outputCost     = (outputTokens     / M) * p.output;
    const cacheWriteCost = (cacheWriteTokens / M) * p.cacheWrite;
    const cacheReadCost  = (cacheReadTokens  / M) * p.cacheRead;
    const cacheSavings   = (cacheReadTokens  / M) * (p.input - p.cacheRead);

    return {
      pricing_known:        true,
      cost_usd:             round5(inputCost + outputCost + cacheWriteCost + cacheReadCost),
      input_cost_usd:       round5(inputCost),
      output_cost_usd:      round5(outputCost),
      cache_write_cost_usd: round5(cacheWriteCost),
      cache_read_cost_usd:  round5(cacheReadCost),
      cache_savings_usd:    round5(cacheSavings),
    };
  } catch { return undefined; }
}

// --- Incremental token extraction from transcript ---
// Reads only new bytes since last offset, accumulates totals in timing files.
// Returns { turn, total } where `turn` is the delta for THIS stop event and
// `total` is the running session total. Used by the `stop` hook for per-call logging.
function extractTokensIncremental(transcriptPath, sessionId) {
  try {
    const offsetKey = 'token-offset-' + sessionId;
    const totalsKey = 'token-totals-' + sessionId;
    const prev = readTimingFile(offsetKey, false) || { offset: 0 };
    const accum = readTimingFile(totalsKey, false) || {
      input: 0, output: 0, cacheCreate: 0, cacheRead: 0,
      turns: 0, tools: 0, model: undefined, thinking: false,
    };

    const stat = fs.statSync(transcriptPath);
    if (stat.size <= prev.offset) {
      return { turn: null, total: formatTokenResult(accum) };
    }

    const fd = fs.openSync(transcriptPath, 'r');
    const buf = Buffer.alloc(stat.size - prev.offset);
    fs.readSync(fd, buf, 0, buf.length, prev.offset);
    fs.closeSync(fd);

    const chunk = buf.toString('utf8');
    const lines = chunk.split('\n').filter(Boolean);

    // Track this turn's delta separately from the running total
    const delta = { input: 0, output: 0, cacheCreate: 0, cacheRead: 0, turns: 0, tools: 0, model: undefined, extraUsage: {} };
    const KNOWN_USAGE_FIELDS = new Set(['input_tokens','output_tokens','cache_creation_input_tokens','cache_read_input_tokens']);

    for (const line of lines) {
      try {
        const obj = JSON.parse(line);
        if (obj.type === 'assistant' && obj.message && obj.message.usage) {
          const u = obj.message.usage;
          delta.input      += u.input_tokens || 0;
          delta.output     += u.output_tokens || 0;
          delta.cacheCreate += u.cache_creation_input_tokens || 0;
          delta.cacheRead  += u.cache_read_input_tokens || 0;
          delta.turns++;
          accum.input      += u.input_tokens || 0;
          accum.output     += u.output_tokens || 0;
          accum.cacheCreate += u.cache_creation_input_tokens || 0;
          accum.cacheRead  += u.cache_read_input_tokens || 0;
          accum.turns++;
          // Capture any future/unknown numeric usage fields (e.g. thinking_tokens)
          for (const [k, v] of Object.entries(u)) {
            if (!KNOWN_USAGE_FIELDS.has(k) && typeof v === 'number') {
              delta.extraUsage[k] = (delta.extraUsage[k] || 0) + v;
              if (!accum.extraUsage) accum.extraUsage = {};
              accum.extraUsage[k] = (accum.extraUsage[k] || 0) + v;
            }
          }
          // Track model per-turn (last model in this chunk wins) and session-first
          if (obj.message.model) {
            delta.model = obj.message.model;
            if (!accum.model) accum.model = obj.message.model;
          }
        }
        if (obj.type === 'tool_use' || (obj.type === 'progress' && obj.data && obj.data.type === 'tool_use')) {
          delta.tools++;
          accum.tools++;
        }
      } catch {}
    }

    const turnHasThinking = /"type"\s*:\s*"thinking"/.test(chunk);
    if (!accum.thinking && turnHasThinking) accum.thinking = true;
    writeTimingFile(offsetKey, { offset: stat.size });
    writeTimingFile(totalsKey, accum);

    return {
      turn: {
        input_tokens:          delta.input,
        output_tokens:         delta.output,
        cache_creation_tokens: delta.cacheCreate,
        cache_read_tokens:     delta.cacheRead,
        total_tokens:          delta.input + delta.output,
        assistant_turns:       delta.turns,
        tool_use_count:        delta.tools,
        model:                 delta.model || undefined,
        turn_number:           accum.turns,  // 1-indexed; includes this turn
        has_thinking:          turnHasThinking,
        ...(Object.keys(delta.extraUsage).length > 0 ? { extra_usage_fields: delta.extraUsage } : {}),
      },
      total: formatTokenResult(accum),
      chunk, // raw new bytes — reused by extractToolAttribution so we don't re-read
    };
  } catch { return null; }
}

function formatTokenResult(accum) {
  return {
    total_input_tokens:          accum.input,
    total_output_tokens:         accum.output,
    total_cache_creation_tokens: accum.cacheCreate,
    total_cache_read_tokens:     accum.cacheRead,
    total_tokens:                accum.input + accum.output,
    assistant_turns:             accum.turns,
    tool_use_count:              accum.tools,
    model:                       accum.model || undefined,
    thinking:                    accum.thinking || false,
    ...(accum.extraUsage && Object.keys(accum.extraUsage).length > 0 ? { extra_usage_fields: accum.extraUsage } : {}),
  };
}

// --- Per-tool token attribution ---
// Walks the new transcript chunk to pair each tool round with the token delta it caused.
// A "tool round" = one assistant message calling tools → tool results → next assistant message.
// The delta = total_input(next assistant) - total_input(prev assistant), where
// total_input = input_tokens + cache_creation_input_tokens + cache_read_input_tokens.
// For parallel tool calls (multiple tool_results in one user message), emits:
//   - one group event (full delta, parallel_tool_count)
//   - one per-tool event each (delta / count, equal split)
function extractToolAttribution(chunk, sessionId, project) {
  try {
    const lines = chunk.split('\n').filter(Boolean);
    const records = [];

    let prevAssistant = null; // { uuid, usage, toolUseMap: { [id]: name } }
    let pendingTools  = [];   // [{ id, name }] — tools called by prevAssistant

    function totalInput(u) {
      return (u.input_tokens || 0)
           + (u.cache_creation_input_tokens || 0)
           + (u.cache_read_input_tokens     || 0);
    }

    for (const line of lines) {
      let obj;
      try { obj = JSON.parse(line); } catch { continue; }

      if (obj.type === 'assistant' && obj.message && obj.message.usage) {
        const usage   = obj.message.usage;
        const content = Array.isArray(obj.message.content) ? obj.message.content : [];

        // If previous assistant called tools and results came back, attribute the delta.
        if (prevAssistant && pendingTools.length > 0) {
          const delta      = totalInput(usage) - totalInput(prevAssistant.usage);
          const toolCount  = pendingTools.length;
          const perTool    = toolCount > 1 ? Math.round(delta / toolCount) : delta;

          // Group event — full delta, all tools in this round
          records.push({
            event:                'claude_tool_token_attribution',
            attribution_type:     'group',
            session_id:           sessionId,
            project,
            machine,
            env:                  envLabel,
            assistant_uuid_before: prevAssistant.uuid,
            assistant_uuid_after:  obj.uuid,
            tools:                pendingTools.map(t => t.name),
            tool_use_ids:         pendingTools.map(t => t.id),
            parallel_tool_count:  toolCount,
            total_input_delta:    delta,
            input_tokens_delta:         (usage.input_tokens                  || 0) - (prevAssistant.usage.input_tokens                  || 0),
            cache_creation_delta:       (usage.cache_creation_input_tokens   || 0) - (prevAssistant.usage.cache_creation_input_tokens   || 0),
            cache_read_delta:           (usage.cache_read_input_tokens        || 0) - (prevAssistant.usage.cache_read_input_tokens        || 0),
          });

          // Per-tool events — equal split
          for (const tool of pendingTools) {
            records.push({
              event:               'claude_tool_token_attribution',
              attribution_type:    'per_tool',
              session_id:          sessionId,
              project,
              machine,
              env:                 envLabel,
              tool_use_id:         tool.id,
              tool_name:           tool.name,
              parallel_tool_count: toolCount,
              total_input_delta:   perTool,
            });
          }
        }

        // Build tool_use map for tools this assistant is about to call
        const toolUseMap = {};
        for (const c of content) {
          if (c && c.type === 'tool_use' && c.id) toolUseMap[c.id] = c.name || 'unknown';
        }

        prevAssistant = { uuid: obj.uuid || '', usage, toolUseMap };
        pendingTools  = [];
      }

      else if (obj.type === 'user' && obj.message && prevAssistant) {
        const content = obj.message.content;
        if (Array.isArray(content)) {
          for (const c of content) {
            if (c && c.type === 'tool_result' && c.tool_use_id) {
              const name = prevAssistant.toolUseMap[c.tool_use_id] || 'unknown';
              // Deduplicate: only add if not already pending (shouldn't happen, but safe)
              if (!pendingTools.find(t => t.id === c.tool_use_id)) {
                pendingTools.push({ id: c.tool_use_id, name });
              }
            }
          }
        }
      }
    }

    return records;
  } catch { return []; }
}

function cleanupTokenFiles(sessionId) {
  try { fs.unlinkSync(path.join(TIMING_DIR, 'token-offset-' + sessionId + '.json')); } catch {}
  try { fs.unlinkSync(path.join(TIMING_DIR, 'token-totals-' + sessionId + '.json')); } catch {}
  try { fs.unlinkSync(path.join(TIMING_DIR, 'session-type-' + sessionId + '.json')); } catch {}
  try { fs.unlinkSync(path.join(TIMING_DIR, 'plan-count-' + sessionId + '.json')); } catch {}
  try { fs.unlinkSync(path.join(TIMING_DIR, 'turn-tool-timing-' + sessionId + '.json')); } catch {}
}

// --- Full session token extraction (session-end only) ---
// Single file read; includes effort detection. Authoritative — used for the permanent record.
function extractSessionTokens(transcriptPath) {
  try {
    const text = fs.readFileSync(transcriptPath, 'utf8');
    const lines = text.split('\n').filter(Boolean);
    let totalInput = 0, totalOutput = 0, totalCacheCreate = 0, totalCacheRead = 0;
    let assistantTurns = 0, toolUseCalls = 0;
    const modelsUsed = new Set();
    const extraUsageTotals = {};
    const KNOWN_USAGE_FIELDS = new Set(['input_tokens','output_tokens','cache_creation_input_tokens','cache_read_input_tokens']);

    for (const line of lines) {
      try {
        const obj = JSON.parse(line);
        if (obj.type === 'assistant' && obj.message && obj.message.usage) {
          const u = obj.message.usage;
          totalInput += u.input_tokens || 0;
          totalOutput += u.output_tokens || 0;
          totalCacheCreate += u.cache_creation_input_tokens || 0;
          totalCacheRead += u.cache_read_input_tokens || 0;
          assistantTurns++;
          if (obj.message.model) modelsUsed.add(obj.message.model);
          for (const [k, v] of Object.entries(u)) {
            if (!KNOWN_USAGE_FIELDS.has(k) && typeof v === 'number') {
              extraUsageTotals[k] = (extraUsageTotals[k] || 0) + v;
            }
          }
        }
        if (obj.type === 'tool_use' || (obj.type === 'progress' && obj.data && obj.data.type === 'tool_use')) {
          toolUseCalls++;
        }
      } catch {}
    }

    // Detect thinking (separate from effort — presence of thinking blocks in transcript)
    const thinking = /"type"\s*:\s*"thinking"/.test(text);

    // Detect effort level: check transcript metadata first, fall back to settings.json.
    // Unknown effort values pass through as-is rather than silently defaulting to 'high'.
    const EFFORT_MAP = { low: 'low', medium: 'med', med: 'med', high: 'high', max: 'max' };
    let effort;
    for (const line of lines) {
      try {
        const obj = JSON.parse(line);
        if (obj.effort) {
          effort = EFFORT_MAP[obj.effort.toLowerCase()] || obj.effort;
          break;
        }
      } catch {}
    }
    if (!effort) {
      try {
        const settings = JSON.parse(fs.readFileSync(
          path.join(os.homedir(), '.claude', 'settings.json'), 'utf8'));
        const raw = (settings.effortLevel || '').toLowerCase();
        effort = EFFORT_MAP[raw] || settings.effortLevel || 'high';
      } catch { effort = 'high'; }
    }

    const modelsList = [...modelsUsed];
    return {
      total_input_tokens:          totalInput,
      total_output_tokens:         totalOutput,
      total_cache_creation_tokens: totalCacheCreate,
      total_cache_read_tokens:     totalCacheRead,
      total_tokens:                totalInput + totalOutput,
      assistant_turns:             assistantTurns,
      tool_use_count:              toolUseCalls,
      model:                       modelsList.length === 1 ? modelsList[0] : undefined,
      models_used:                 modelsList.length > 0 ? modelsList : undefined,
      effort,
      thinking,
      ...(Object.keys(extraUsageTotals).length > 0 ? { extra_usage_fields: extraUsageTotals } : {}),
    };
  } catch { return null; }
}

// --- Build enriched log line ---
function buildEnrichedLogLine(hp, hType, enrichments, base) {
  return JSON.stringify({
    event: 'claude_hook',
    hook_type: hType,
    hook_source: hookSource,
    tool_name: base.toolName || undefined,
    service: base.service || undefined,
    project: base.project || undefined,
    session_id: base.sessionId || undefined,
    machine,
    cwd: compress(hp.cwd || process.cwd()),
    env: envLabel,
    ...enrichments,
    hook_payload: walk(hp),
  });
}

// --- Push queue (flushed once via detached worker at end of hook) ---
// Accumulates all Loki pushes for this hook invocation. Dispatched in a single
// detached child process so the hook exits without waiting for network I/O.
const pushQueue = [];
function queuePush(stream, logLine) {
  pushQueue.push({ stream, logLine });
}


// --- Main (CLI mode only) ---
// Guarded with require.main so this module can be require()'d for its pure helpers
// (MODEL_PRICING / getPricing / computeCostBreakdown / extractSessionTokens) without
// reading stdin. scripts/backfill-subagent-usage.js depends on this.
if (require.main === module) {
let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', c => { raw += c; });
process.stdin.on('end', () => {
  let hp;
  try { hp = JSON.parse(raw); } catch { hp = {}; }

  let hookError;
  try {
  const toolName = hp.tool_name || '';
  const sessionId = hp.session_id || '';
  const toolUseId = hp.tool_use_id || '';
  const service = detectService(toolName);
  const project = inferProject(hp.cwd || process.cwd());
  const base = { toolName, sessionId, service, project };

  // Component bucket — MCP services get dedicated labels per GRAFANA-LOGGING.md
  const isToolHook = ['pre-tool-use', 'post-tool-use', 'post-tool-use-failure'].includes(hookType);
  const component = isToolHook && service ? `mcp-${service}`
    : isToolHook ? 'tool'
    : ['session-start', 'session-end', 'pre-compact', 'stop'].includes(hookType) ? 'lifecycle'
    : ['subagent-start', 'subagent-stop', 'task-completed', 'teammate-idle'].includes(hookType) ? 'agent'
    : ['user-prompt-submit', 'notification', 'permission-request'].includes(hookType) ? 'user'
    : 'other';

  const level = hookType === 'post-tool-use-failure' ? 'ERROR'
    : hookType === 'permission-request' ? 'WARN'
    : 'INFO';

  // --- Enrichments per hook type ---
  let enrichments = {};

  if (hookType === 'pre-tool-use') {
    cleanStaleFiles();
    writeTimingFile(toolUseId, { start: Date.now(), tool_name: toolName });
    Object.assign(enrichments, detectRetry(toolName, hp.tool_input, toolUseId));
    Object.assign(enrichments, computePayloadSizes(hp.tool_input, null));

    // Track plan mode entries per session
    if (toolName === 'EnterPlanMode') {
      const planData = readTimingFile('plan-count-' + sessionId, false) || { count: 0 };
      writeTimingFile('plan-count-' + sessionId, { count: planData.count + 1 });
      enrichments.plan_mode_entry = true;
    }
  }

  else if (hookType === 'post-tool-use') {
    const timing = readTimingFile(toolUseId);
    if (timing) enrichments.duration_ms = Date.now() - timing.start;
    Object.assign(enrichments, computePayloadSizes(hp.tool_input, hp.tool_response));
    writeTimingFile('last-complete-' + sessionId, { timestamp: Date.now() });
    // Accumulate per-turn tool timing for stop-hook correlation with tokens
    if (enrichments.duration_ms !== undefined && toolName) {
      const turnTimingKey = 'turn-tool-timing-' + sessionId;
      const existing = readTimingFile(turnTimingKey, false) || {};
      if (!existing[toolName]) existing[toolName] = { total_ms: 0, calls: 0 };
      existing[toolName].total_ms += enrichments.duration_ms;
      existing[toolName].calls++;
      writeTimingFile(turnTimingKey, existing);
    }
  }

  else if (hookType === 'post-tool-use-failure') {
    const timing = readTimingFile(toolUseId);
    if (timing) enrichments.duration_ms = Date.now() - timing.start;
    Object.assign(enrichments, computePayloadSizes(hp.tool_input, hp.tool_response));
    enrichments.error_type = classifyError(hp.tool_response);
    writeTimingFile('last-complete-' + sessionId, { timestamp: Date.now() });
    // Accumulate failed calls too — they still consume time
    if (enrichments.duration_ms !== undefined && toolName) {
      const turnTimingKey = 'turn-tool-timing-' + sessionId;
      const existing = readTimingFile(turnTimingKey, false) || {};
      if (!existing[toolName]) existing[toolName] = { total_ms: 0, calls: 0 };
      existing[toolName].total_ms += enrichments.duration_ms;
      existing[toolName].calls++;
      writeTimingFile(turnTimingKey, existing);
    }
  }

  else if (hookType === 'subagent-start') {
    const agentId = hp.agent_id || toolUseId || 'unknown';
    writeTimingFile('agent-' + agentId, { start: Date.now(), session_id: sessionId });
    try {
      const agentFiles = fs.readdirSync(TIMING_DIR).filter(f =>
        f.startsWith('agent-') && f.endsWith('.json'));
      let depth = 0;
      for (const f of agentFiles) {
        try {
          const d = JSON.parse(fs.readFileSync(path.join(TIMING_DIR, f), 'utf8'));
          if (d.session_id === sessionId) depth++;
        } catch {}
      }
      enrichments.agent_depth = depth;
    } catch {}
  }

  else if (hookType === 'subagent-stop') {
    const agentId = hp.agent_id || toolUseId || 'unknown';
    const agentData = readTimingFile('agent-' + agentId);
    if (agentData) enrichments.agent_duration_ms = Date.now() - agentData.start;
  }

  else if (hookType === 'session-start') {
    cleanStaleFiles();
    const sessionType = hp.type || 'unknown';
    writeTimingFile('session-' + sessionId, { start: Date.now() });
    writeTimingFile('compactions-' + sessionId, { count: 0 });
    writeTimingFile('session-type-' + sessionId, { session_type: sessionType });
    enrichments.session_type = sessionType;
    enrichments.plan_type = PLAN_TYPE;
    enrichments.plan_monthly_cost_usd = PLAN_MONTHLY_COST;
  }

  else if (hookType === 'session-end') {
    const sessionData = readTimingFile('session-' + sessionId);
    if (sessionData) enrichments.session_duration_ms = Date.now() - sessionData.start;
    const compData = readTimingFile('compactions-' + sessionId);
    if (compData) enrichments.compaction_count = compData.count;
    // Retrieve session_type and plan_mode_entries for the final log entry
    const sessionTypeData = readTimingFile('session-type-' + sessionId, false);
    if (sessionTypeData) enrichments.session_type = sessionTypeData.session_type;
    const planData = readTimingFile('plan-count-' + sessionId, false);
    if (planData) enrichments.plan_mode_entries = planData.count;
  }

  else if (hookType === 'user-prompt-submit') {
    const lastComplete = readTimingFile('last-complete-' + sessionId, false);
    if (lastComplete) enrichments.user_think_time_ms = Date.now() - lastComplete.timestamp;
  }

  else if (hookType === 'pre-compact') {
    const compData = readTimingFile('compactions-' + sessionId, false);
    const newCount = compData ? compData.count + 1 : 1;
    writeTimingFile('compactions-' + sessionId, { count: newCount });
    enrichments.compaction_count = newCount;
  }

  // Attach session_type to all lifecycle events (read from timing file, don't delete)
  if (['stop', 'pre-compact', 'session-end', 'pre-tool-use', 'post-tool-use',
       'subagent-start', 'subagent-stop'].includes(hookType) && !enrichments.session_type) {
    const stData = readTimingFile('session-type-' + sessionId, false);
    if (stData) enrichments.session_type = stData.session_type;
  }

  // --- Main push to claude-dev-logging (all hook types) ---
  // Add session_type as a Loki stream label on session-start for efficient filtering
  const stream = hookType === 'session-start' && enrichments.session_type
    ? { app: 'claude-dev-logging', env: envLabel, component, level, session_type: enrichments.session_type }
    : { app: 'claude-dev-logging', env: envLabel, component, level };
  const logLine = scrubSecrets(buildEnrichedLogLine(hp, hookType, enrichments, base));
  queuePush(stream, logLine);

  // --- Stop hook: per-turn token delta → claude-dev-logging + claude-token-metrics ---
  // Fires after every Claude response. Pushes this turn's token burn + running total.
  if (hookType === 'stop' && hp.transcript_path) {
    const result = extractTokensIncremental(hp.transcript_path, sessionId);
    if (result) {
      queuePush(
        { app: 'claude-dev-logging', env: envLabel, component: 'tokens', level: 'INFO' },
        scrubSecrets(JSON.stringify({
          event: 'claude_turn_tokens',
          session_id: sessionId,
          project,
          machine,
          env: envLabel,
          model:       result.turn ? (result.turn.model || result.total.model) : result.total.model || undefined,
          turn_number: result.turn ? result.turn.turn_number : undefined,
          has_thinking: result.turn ? result.turn.has_thinking : false,
          // This turn's delta — what was just burned
          turn_input_tokens:          result.turn ? result.turn.input_tokens          : 0,
          turn_output_tokens:         result.turn ? result.turn.output_tokens         : 0,
          turn_cache_creation_tokens: result.turn ? result.turn.cache_creation_tokens : 0,
          turn_cache_read_tokens:     result.turn ? result.turn.cache_read_tokens     : 0,
          turn_total_tokens:          result.turn ? result.turn.total_tokens          : 0,
          turn_tool_use_count:        result.turn ? result.turn.tool_use_count        : 0,
          // Running session totals (for trend lines)
          total_input_tokens:          result.total.total_input_tokens,
          total_output_tokens:         result.total.total_output_tokens,
          total_cache_creation_tokens: result.total.total_cache_creation_tokens,
          total_cache_read_tokens:     result.total.total_cache_read_tokens,
          total_tokens:                result.total.total_tokens,
          assistant_turns:             result.total.assistant_turns,
        }))
      );

      // Read and reset per-turn tool timing accumulator (written by post-tool-use)
      const turnTimingKey = 'turn-tool-timing-' + sessionId;
      const turnToolTiming = readTimingFile(turnTimingKey); // reads + deletes → resets per turn
      const totalToolTimeMs = turnToolTiming
        ? Object.values(turnToolTiming).reduce((s, t) => s + t.total_ms, 0)
        : 0;
      const totalToolCallsTurn = turnToolTiming
        ? Object.values(turnToolTiming).reduce((s, t) => s + t.calls, 0)
        : 0;

      // Emit per-turn tool timing breakdown to claude-dev-logging
      if (turnToolTiming && totalToolCallsTurn > 0) {
        queuePush(
          { app: 'claude-dev-logging', env: envLabel, component: 'tool-timing', level: 'INFO' },
          scrubSecrets(JSON.stringify({
            event:              'claude_turn_tool_timing',
            session_id:         sessionId,
            project,
            machine,
            env:                envLabel,
            tool_time_ms_total: totalToolTimeMs,
            tool_call_count:    totalToolCallsTurn,
            breakdown:          turnToolTiming,
          }))
        );
      }

      // Push per-turn delta to claude-token-metrics so dashboards update in real-time.
      // Each stop event pushes this turn's incremental cost/tokens; sum_over_time accumulates
      // correctly. The session-end hook pushes claude_session_summary to claude-dev-logging
      // (not claude-token-metrics) to avoid double-counting against sum_over_time queries.
      if (result.turn && (result.turn.input_tokens > 0 || result.turn.output_tokens > 0
          || result.turn.cache_creation_tokens > 0 || result.turn.cache_read_tokens > 0)) {
        // Read effort from settings.json (same fallback used by full session extraction)
        const EFFORT_MAP_STOP = { low: 'low', medium: 'med', med: 'med', high: 'high', max: 'max' };
        let stopEffort = 'med';
        try {
          const settings = JSON.parse(fs.readFileSync(
            path.join(os.homedir(), '.claude', 'settings.json'), 'utf8'));
          const mapped = EFFORT_MAP_STOP[(settings.effortLevel || '').toLowerCase()];
          if (mapped) stopEffort = mapped;
        } catch {}
        const sessionTypeForTurn = enrichments.session_type || 'unknown';
        // Use this turn's model for cost accuracy (handles mid-session model switches).
        // Fall back to the session's first-seen model if the turn didn't surface one.
        const turnModel = result.turn.model || result.total.model;
        const turnCosts = computeCostBreakdown({
          model:                       turnModel,
          total_input_tokens:          result.turn.input_tokens,
          total_output_tokens:         result.turn.output_tokens,
          total_cache_creation_tokens: result.turn.cache_creation_tokens,
          total_cache_read_tokens:     result.turn.cache_read_tokens,
        }) || {};
        queuePush(
          {
            app: 'claude-token-metrics',
            env: envLabel,
            model: turnModel || 'unknown',
            project: project || 'unknown',
            effort: stopEffort,
          },
          scrubSecrets(JSON.stringify({
            event:                       'claude_turn_metrics',
            session_id:                  sessionId,
            project,
            machine,
            env:                         envLabel,
            is_final:                    false,
            timestamp:                   new Date().toISOString(),
            model:                       turnModel || undefined,
            effort:                      stopEffort,
            session_type:                sessionTypeForTurn,
            turn_number:                 result.turn.turn_number,
            has_thinking:                result.turn.has_thinking,
            thinking:                    result.total.thinking,
            pricing_known:               turnCosts.pricing_known,
            cost_usd:                    turnCosts.cost_usd,
            input_cost_usd:              turnCosts.input_cost_usd,
            output_cost_usd:             turnCosts.output_cost_usd,
            cache_write_cost_usd:        turnCosts.cache_write_cost_usd,
            cache_read_cost_usd:         turnCosts.cache_read_cost_usd,
            cache_savings_usd:           turnCosts.cache_savings_usd,
            plan_type:                   PLAN_TYPE,
            plan_monthly_cost_usd:       PLAN_MONTHLY_COST,
            pricing_model:               'api-retail',
            total_input_tokens:          result.turn.input_tokens,
            total_output_tokens:         result.turn.output_tokens,
            total_cache_creation_tokens: result.turn.cache_creation_tokens,
            total_cache_read_tokens:     result.turn.cache_read_tokens,
            total_tokens:                result.turn.total_tokens,
            assistant_turns:             result.turn.assistant_turns,
            tool_use_count:              result.turn.tool_use_count,
            tool_time_ms:                totalToolTimeMs || undefined,
          }))
        );
      }

      // --- Per-tool token attribution ---
      // Uses the same chunk already read by extractTokensIncremental (no second disk read).
      if (result.chunk) {
        const attributions = extractToolAttribution(result.chunk, sessionId, project);
        for (const attr of attributions) {
          queuePush(
            { app: 'claude-dev-logging', env: envLabel, component: 'tool-attribution', level: 'INFO' },
            scrubSecrets(JSON.stringify(attr))
          );
        }
      }
    }
  }

  // --- Session-end: full token summary → claude-dev-logging (not claude-token-metrics to
  //     avoid double-counting with per-turn stop events that sum_over_time(cost_usd)). ---
  if (hookType === 'session-end' && hp.transcript_path) {
    const tokenData = extractSessionTokens(hp.transcript_path);
    if (tokenData) {
      Object.assign(tokenData, computeCostBreakdown(tokenData) || {});
      queuePush(
        { app: 'claude-dev-logging', env: envLabel, component: 'lifecycle', level: 'INFO' },
        scrubSecrets(JSON.stringify({
          event: 'claude_session_summary',
          session_id: sessionId,
          project,
          machine,
          env: envLabel,
          timestamp: new Date().toISOString(),
          session_duration_ms: enrichments.session_duration_ms,
          compaction_count: enrichments.compaction_count,
          session_type: enrichments.session_type,
          plan_mode_entries: enrichments.plan_mode_entries,
          ...tokenData,
        }))
      );
    } else {
      queuePush(
        { app: 'claude-dev-logging', env: envLabel, component: 'lifecycle', level: 'WARN' },
        scrubSecrets(JSON.stringify({
          event: 'claude_session_summary_error',
          session_id: sessionId,
          project,
          machine,
          env: envLabel,
          error: 'transcript_parse_failed',
          transcript_path: compress(hp.transcript_path),
        }))
      );
    }
    cleanupTokenFiles(sessionId);
  }

  } catch (err) {
    hookError = err;
  } finally {
    // Timing log records blocking time only — before the network dispatch.
    const errSuffix = hookError ? `\tERROR:${String(hookError.message || hookError)}` : '';
    try { fs.appendFileSync(HOOK_TIMING_LOG, `${HOOK_START_TS}\tloki/${hookType}\t${Date.now() - HOOK_START_MS}ms${errSuffix}\n`); } catch {}

    // Dispatch all queued pushes in one detached child — hook exits immediately.
    if (pushQueue.length > 0) {
      try {
        const child = spawn(process.execPath, [path.join(__dirname, 'loki-push-worker.js')], {
          detached: true,
          stdio: ['pipe', 'ignore', 'ignore'],
        });
        child.stdin.end(JSON.stringify({ lokiUrl, lokiAuth, pushes: pushQueue }));
        child.unref();
      } catch {}
    }
  }
});
}

// --- Module exports (require() consumers; no side effects) ---
// Pure cost/token helpers reused by scripts/backfill-subagent-usage.js so it computes
// subagent cost from their separate transcripts with the exact same pricing logic.
module.exports = { MODEL_PRICING, getPricing, computeCostBreakdown, extractSessionTokens, loadModelPricing };
