// Claude Code hook -> Loki push
// Self-contained: loads .env, reads stdin, pushes to Loki. No shell wrapper needed.
// Enrichments: tool duration, payload sizes, retry detection, error classification,
//              agent topology, session lifecycle, token sidecar.
// Usage: node loki-log.js <hook-type>
//
// DATA ARCHITECTURE:
//   app="claude-dev-logging"   — ALL hook events: tool calls, lifecycle, agents, user prompts,
//                                intermediate token snapshots (component="tokens", stop hook)
//   app="claude-token-metrics" — ONE entry per completed session: token totals, cost_usd, effort
//                                Join key: session_id (present in both streams)
//   On-disk: logs/claude-session-metrics.jsonl — local backup only (not ingested by Alloy).
//            Includes session_duration_ms + compaction_count for local debugging.

const http = require('http');
const https = require('https');
const fs = require('fs');
const path = require('path');
const os = require('os');

const hookType = process.argv[2] || 'unknown';

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
        if (key && !(key in process.env)) process.env[key] = val;
      }
      break;
    } catch { /* file not found, try next */ }
  }
}
loadEnv();

const lokiUrl = (process.env.SIMSTEWARD_LOKI_URL || 'http://localhost:3100').replace(/\/+$/, '');
const envLabel = process.env.SIMSTEWARD_LOG_ENV || 'local';
const machine = process.env.COMPUTERNAME || os.hostname() || 'unknown';

// --- MCP service detection ---
const MCP_SERVICES = [
  [/^mcp__contextstream__/, 'contextstream'],
  [/^mcp__claude_ai_Sentry__/, 'sentry'],
  [/^mcp__plugin_sentry_sentry__/, 'sentry'],
  [/^mcp__ollama__/, 'ollama'],
];

function detectService(toolName) {
  if (!toolName) return undefined;
  const match = MCP_SERVICES.find(([re]) => re.test(toolName));
  return match ? match[1] : undefined;
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
        // Token tracking files live for the whole session (24h max)
        // Retry markers expire quickly; everything else at STALE_MS
        const threshold = (f.startsWith('token-offset-') || f.startsWith('token-totals-'))
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

// --- Model pricing (per 1M tokens) ---
const MODEL_PRICING = {
  'claude-opus-4':   { input: 15,   output: 75,   cacheWrite: 18.75, cacheRead: 1.50 },
  'claude-sonnet-4': { input: 3,    output: 15,   cacheWrite: 3.75,  cacheRead: 0.30 },
  'claude-haiku-4':  { input: 0.80, output: 4,    cacheWrite: 1.00,  cacheRead: 0.08 },
};

function getPricing(model) {
  if (!model) return MODEL_PRICING['claude-opus-4'];
  const m = model.toLowerCase();
  if (m.includes('haiku'))  return MODEL_PRICING['claude-haiku-4'];
  if (m.includes('sonnet')) return MODEL_PRICING['claude-sonnet-4'];
  return MODEL_PRICING['claude-opus-4'];
}

function computeCostUsd(tokenData) {
  try {
    const p = getPricing(tokenData.model);
    const M = 1_000_000;
    return Math.round(
      ((tokenData.total_input_tokens          || 0) / M * p.input
     + (tokenData.total_output_tokens         || 0) / M * p.output
     + (tokenData.total_cache_creation_tokens || 0) / M * p.cacheWrite
     + (tokenData.total_cache_read_tokens     || 0) / M * p.cacheRead) * 100000
    ) / 100000; // 5 decimal precision
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
    const delta = { input: 0, output: 0, cacheCreate: 0, cacheRead: 0, turns: 0, tools: 0 };

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
          if (!accum.model && obj.message.model) accum.model = obj.message.model;
        }
        if (obj.type === 'tool_use' || (obj.type === 'progress' && obj.data && obj.data.type === 'tool_use')) {
          delta.tools++;
          accum.tools++;
        }
      } catch {}
    }

    if (!accum.thinking && /"type"\s*:\s*"thinking"/.test(chunk)) accum.thinking = true;
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
      },
      total: formatTokenResult(accum),
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
  };
}

function cleanupTokenFiles(sessionId) {
  try { fs.unlinkSync(path.join(TIMING_DIR, 'token-offset-' + sessionId + '.json')); } catch {}
  try { fs.unlinkSync(path.join(TIMING_DIR, 'token-totals-' + sessionId + '.json')); } catch {}
}

// --- Full session token extraction (session-end only) ---
// Single file read; includes effort detection. Authoritative — used for the permanent record.
function extractSessionTokens(transcriptPath) {
  try {
    const text = fs.readFileSync(transcriptPath, 'utf8');
    const lines = text.split('\n').filter(Boolean);
    let totalInput = 0, totalOutput = 0, totalCacheCreate = 0, totalCacheRead = 0;
    let assistantTurns = 0, toolUseCalls = 0, model;

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
          if (!model && obj.message.model) model = obj.message.model;
        }
        if (obj.type === 'tool_use' || (obj.type === 'progress' && obj.data && obj.data.type === 'tool_use')) {
          toolUseCalls++;
        }
      } catch {}
    }

    // Detect thinking (separate from effort — presence of thinking blocks in transcript)
    const thinking = /"type"\s*:\s*"thinking"/.test(text);

    // Detect effort level: check transcript metadata first, fall back to settings.json
    const EFFORT_MAP = { low: 'low', medium: 'med', med: 'med', high: 'high', max: 'max' };
    let effort = 'high'; // Claude Code default
    for (const line of lines) {
      try {
        const obj = JSON.parse(line);
        if (obj.effort) {
          const mapped = EFFORT_MAP[obj.effort.toLowerCase()];
          if (mapped) { effort = mapped; break; }
        }
      } catch {}
    }
    if (effort === 'high') {
      // Fall back to settings.json effortLevel
      try {
        const settings = JSON.parse(fs.readFileSync(
          path.join(os.homedir(), '.claude', 'settings.json'), 'utf8'));
        const mapped = EFFORT_MAP[(settings.effortLevel || '').toLowerCase()];
        if (mapped) effort = mapped;
      } catch {}
    }

    return {
      total_input_tokens: totalInput,
      total_output_tokens: totalOutput,
      total_cache_creation_tokens: totalCacheCreate,
      total_cache_read_tokens: totalCacheRead,
      total_tokens: totalInput + totalOutput,
      assistant_turns: assistantTurns,
      tool_use_count: toolUseCalls,
      model: model || undefined,
      effort,
      thinking,
    };
  } catch { return null; }
}

// --- Push token usage to Loki ---
// isFinal=true  → claude-token-metrics (called at session-end for backfill / historical writes)
// isFinal=false → claude-dev-logging (component=tokens) — only used for legacy backfill paths
function pushTokenUsage(sessionId, project, tokenData, isFinal) {
  if (isFinal) {
    const logLine = scrubSecrets(JSON.stringify({
      event: 'claude_session_token_summary',
      session_id: sessionId,
      project,
      machine,
      env: envLabel,
      is_final: true,
      timestamp: new Date().toISOString(),
      ...tokenData,
    }));
    pushToLoki(
      {
        app: 'claude-token-metrics',
        env: envLabel,
        model: tokenData.model || 'unknown',
        project: project || 'unknown',
        effort: tokenData.effort || 'standard',
      },
      logLine
    );
  } else {
    const logLine = scrubSecrets(JSON.stringify({
      event: 'claude_token_usage',
      session_id: sessionId,
      project,
      machine,
      env: envLabel,
      is_final: false,
      ...tokenData,
    }));
    pushToLoki(
      { app: 'claude-dev-logging', env: envLabel, component: 'tokens', level: 'INFO' },
      logLine
    );
  }
}

// --- Build enriched log line ---
function buildEnrichedLogLine(hp, hType, enrichments, base) {
  return JSON.stringify({
    event: 'claude_hook',
    hook_type: hType,
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

// --- Push to Loki ---
function pushToLoki(stream, logLine) {
  const ts = String(Date.now()) + '000000';
  const body = JSON.stringify({ streams: [{ stream, values: [[ts, logLine]] }] });
  const parsed = new URL(lokiUrl + '/loki/api/v1/push');
  const mod = parsed.protocol === 'https:' ? https : http;

  const req = mod.request(parsed, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
    timeout: 4000,
  }, res => { res.resume(); });

  req.on('error', () => {});
  req.on('timeout', () => req.destroy());
  req.write(body);
  req.end();
}

// --- Write session metrics sidecar (on-disk backup, not ingested into Loki) ---
// Includes session_duration_ms + compaction_count for local debugging context.
function writeSessionMetricsSidecar(hp, sessionId, project, sessionEnrichments, tokenData) {
  try {
    const metricsDir = path.join(hp.cwd || process.cwd(), 'logs');
    fs.mkdirSync(metricsDir, { recursive: true });
    const line = JSON.stringify({
      event: 'claude_session_metrics',
      session_id: sessionId,
      project,
      machine,
      env: envLabel,
      timestamp: new Date().toISOString(),
      session_duration_ms: sessionEnrichments.session_duration_ms,
      compaction_count: sessionEnrichments.compaction_count,
      ...(tokenData || {}),
    });
    fs.appendFileSync(path.join(metricsDir, 'claude-session-metrics.jsonl'), scrubSecrets(line) + '\n');
  } catch {}
}

// --- Main ---
let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', c => { raw += c; });
process.stdin.on('end', () => {
  let hp;
  try { hp = JSON.parse(raw); } catch { hp = {}; }

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
  }

  else if (hookType === 'post-tool-use') {
    const timing = readTimingFile(toolUseId);
    if (timing) enrichments.duration_ms = Date.now() - timing.start;
    Object.assign(enrichments, computePayloadSizes(hp.tool_input, hp.tool_response));
    writeTimingFile('last-complete-' + sessionId, { timestamp: Date.now() });
  }

  else if (hookType === 'post-tool-use-failure') {
    const timing = readTimingFile(toolUseId);
    if (timing) enrichments.duration_ms = Date.now() - timing.start;
    Object.assign(enrichments, computePayloadSizes(hp.tool_input, hp.tool_response));
    enrichments.error_type = classifyError(hp.tool_response);
    writeTimingFile('last-complete-' + sessionId, { timestamp: Date.now() });
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
    writeTimingFile('session-' + sessionId, { start: Date.now() });
    writeTimingFile('compactions-' + sessionId, { count: 0 });
  }

  else if (hookType === 'session-end') {
    const sessionData = readTimingFile('session-' + sessionId);
    if (sessionData) enrichments.session_duration_ms = Date.now() - sessionData.start;
    const compData = readTimingFile('compactions-' + sessionId);
    if (compData) enrichments.compaction_count = compData.count;
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

  // --- Main push to claude-dev-logging (all hook types) ---
  const stream = { app: 'claude-dev-logging', env: envLabel, component, level };
  const logLine = scrubSecrets(buildEnrichedLogLine(hp, hookType, enrichments, base));
  pushToLoki(stream, logLine);

  // --- Stop hook: per-turn token delta → claude-dev-logging + claude-token-metrics ---
  // Fires after every Claude response. Pushes this turn's token burn + running total.
  if (hookType === 'stop' && hp.transcript_path) {
    const result = extractTokensIncremental(hp.transcript_path, sessionId);
    if (result) {
      pushToLoki(
        { app: 'claude-dev-logging', env: envLabel, component: 'tokens', level: 'INFO' },
        scrubSecrets(JSON.stringify({
          event: 'claude_turn_tokens',
          session_id: sessionId,
          project,
          machine,
          env: envLabel,
          model: result.total.model || undefined,
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

      // Push per-turn delta to claude-token-metrics so dashboards update in real-time.
      // Each stop event pushes this turn's incremental cost/tokens; sum_over_time accumulates
      // correctly. The session-end hook no longer pushes to claude-token-metrics to avoid
      // double-counting (sidecar file remains the authoritative off-Loki record).
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
        const turnCostUsd = computeCostUsd({
          model:                       result.total.model,
          total_input_tokens:          result.turn.input_tokens,
          total_output_tokens:         result.turn.output_tokens,
          total_cache_creation_tokens: result.turn.cache_creation_tokens,
          total_cache_read_tokens:     result.turn.cache_read_tokens,
        });
        pushToLoki(
          {
            app: 'claude-token-metrics',
            env: envLabel,
            model: result.total.model || 'unknown',
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
            model:                       result.total.model || undefined,
            effort:                      stopEffort,
            thinking:                    result.total.thinking,
            cost_usd:                    turnCostUsd,
            total_input_tokens:          result.turn.input_tokens,
            total_output_tokens:         result.turn.output_tokens,
            total_cache_creation_tokens: result.turn.cache_creation_tokens,
            total_cache_read_tokens:     result.turn.cache_read_tokens,
            total_tokens:                result.turn.total_tokens,
            assistant_turns:             result.turn.assistant_turns,
            tool_use_count:              result.turn.tool_use_count,
          }))
        );
      }
    }
  }

  // --- Session-end: full token summary → on-disk sidecar (no Loki push — per-turn stop
  //     events have already pushed incremental deltas to claude-token-metrics, so a final
  //     push here would double-count when dashboards run sum_over_time(cost_usd)). ---
  if (hookType === 'session-end' && hp.transcript_path) {
    const tokenData = extractSessionTokens(hp.transcript_path);
    if (tokenData) {
      tokenData.cost_usd = computeCostUsd(tokenData);
      writeSessionMetricsSidecar(hp, sessionId, project, enrichments, tokenData);
    } else {
      pushToLoki(
        { app: 'claude-dev-logging', env: envLabel, component: 'lifecycle', level: 'WARN' },
        scrubSecrets(JSON.stringify({
          event: 'claude_session_metrics_error',
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
});
