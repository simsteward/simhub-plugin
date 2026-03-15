#!/usr/bin/env node
/**
 * Cursor Usage Logger MCP Server
 * Logs Cursor usage events to a JSONL file for Grafana/Loki (local). Exposes log_event, log_mcp_usage, and get_usage_review tools.
 */
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";
import { randomUUID } from "node:crypto";
import { execSync } from "node:child_process";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";

const MAX_LINE_BYTES = 8 * 1024;
const MAX_EVENTS_BUFFER = 100;
const REPEAT_FAILURE_THRESHOLD = 2;
const TURN_WITHOUT_MILESTONE_THRESHOLD = 20;

const CURSOR_USAGE_LOG_PATH =
  (process.env.CURSOR_USAGE_LOG_PATH?.trim() || undefined) ??
  path.join(process.cwd(), ".cursor", "cursor-usage-logs", "cursor-usage.jsonl");
const SESSION_ID = process.env.CURSOR_USAGE_SESSION_ID ?? randomUUID();
const SESSION_START_MS = Date.now();
const MCP_SERVER_VERSION = "2.2.0";

// ============================================================================
// Static Metadata (gathered once at startup, cached)
// ============================================================================

interface StaticMetadata {
  workspace_path: string;
  git_branch?: string;
  git_commit?: string;
  os_platform: string;
  os_hostname: string;
  os_username: string;
  git_user_name?: string;
  git_user_email?: string;
  git_remote_url?: string;
  git_dirty?: boolean;
  node_version: string;
  process_pid: number;
  process_ppid: number;
  os_arch: string;
  os_release: string;
  cpu_count: number;
  total_memory_mb: number;
  cursor_env_vars: Record<string, string>;
  mcp_server_version: string;
}

let _staticMetadata: StaticMetadata | null = null;

function execSafe(cmd: string): string | undefined {
  try {
    return execSync(cmd, { encoding: "utf-8", timeout: 2000, stdio: ["pipe", "pipe", "pipe"] }).trim() || undefined;
  } catch {
    return undefined;
  }
}

function sanitizeUrl(url: string | undefined): string | undefined {
  if (!url) return undefined;
  return url.replace(/\/\/[^@]+@/, "//");
}

function getCursorEnvVars(): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(process.env)) {
    if ((key.startsWith("CURSOR_") || key.startsWith("VSCODE_")) && value) {
      result[key.toLowerCase()] = value;
    }
  }
  return result;
}

// ============================================================================
// Token Estimation (tiktoken with chars/4 fallback)
// ============================================================================

let _tiktoken: { encode: (text: string) => number[] } | null = null;
let _tiktokenFailed = false;

function estimateTokens(text: string): number {
  if (!_tiktokenFailed && !_tiktoken) {
    try {
      // Dynamic import for optional dependency
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      const { encoding_for_model } = require("tiktoken");
      _tiktoken = encoding_for_model("gpt-4");
    } catch {
      _tiktokenFailed = true;
    }
  }

  if (_tiktoken) {
    return _tiktoken.encode(text).length;
  }
  // Fallback: chars / 4 (approximately 75% accurate for English text)
  return Math.ceil(text.length / 4);
}

function getStaticMetadata(): StaticMetadata {
  if (_staticMetadata) return _staticMetadata;

  _staticMetadata = {
    workspace_path: process.env.CURSOR_WORKSPACE ?? process.env.VSCODE_WORKSPACE ?? process.cwd(),
    git_branch: execSafe("git rev-parse --abbrev-ref HEAD"),
    git_commit: execSafe("git rev-parse --short HEAD"),
    os_platform: os.platform(),
    os_hostname: os.hostname(),
    os_username: os.userInfo().username,
    git_user_name: execSafe("git config user.name"),
    git_user_email: execSafe("git config user.email"),
    git_remote_url: sanitizeUrl(execSafe("git remote get-url origin")),
    git_dirty: (execSafe("git status --porcelain") ?? "") !== "",
    node_version: process.version,
    process_pid: process.pid,
    process_ppid: process.ppid,
    os_arch: os.arch(),
    os_release: os.release(),
    cpu_count: os.cpus().length,
    total_memory_mb: Math.round(os.totalmem() / 1024 / 1024),
    cursor_env_vars: getCursorEnvVars(),
    mcp_server_version: MCP_SERVER_VERSION,
  };

  return _staticMetadata;
}

// ============================================================================
// Dynamic Metadata (computed per-event)
// ============================================================================

interface DynamicMetadata {
  event_id: string;
  event_seq: number;
  since_last_event_ms: number;
  session_uptime_ms: number;
  process_uptime_s: number;
  heap_used_mb: number;
}

let _eventSeq = 0;
let _lastEventMs = SESSION_START_MS;

function getDynamicMetadata(): DynamicMetadata {
  const now = Date.now();
  const sinceLastMs = now - _lastEventMs;
  _lastEventMs = now;
  _eventSeq++;

  return {
    event_id: randomUUID(),
    event_seq: _eventSeq,
    since_last_event_ms: sinceLastMs,
    session_uptime_ms: now - SESSION_START_MS,
    process_uptime_s: Math.round(process.uptime()),
    heap_used_mb: Math.round(process.memoryUsage().heapUsed / 1024 / 1024),
  };
}

// ============================================================================
// Logging Infrastructure
// ============================================================================

interface BufferedEvent {
  event: string;
  ts: string;
  payload?: Record<string, unknown>;
}

let logStream: fs.WriteStream | null = null;
const eventBuffer: BufferedEvent[] = [];

// ============================================================================
// ContextStream Op Cost Lookup (from skill reference)
// ============================================================================

interface ContextStreamCallRecord {
  tool: string;
  action?: string;
  op_cost: number;
  input_tokens: number;
  output_tokens: number;
  duration_ms?: number;
  has_error: boolean;
}

const contextstreamBuffer: ContextStreamCallRecord[] = [];
const CONTEXTSTREAM_BUFFER_MAX = 500;

const SEARCH_LOW_OPS = new Set(["keyword", "pattern"]);
const SEARCH_HIGH_OPS = new Set(["semantic", "hybrid", "exhaustive", "refactor", "team", "crawl", "auto"]);
const SESSION_1_OP = new Set(["capture", "remember", "capture_lesson", "capture_plan"]);
const SESSION_5_OP = new Set(["recall", "compress", "smart_search", "decision_trace", "restore_context"]);
const SESSION_2_OP = new Set(["get_lessons", "summary", "delta", "user_context", "get_plan", "update_plan"]);
const SESSION_0_OP = new Set(["list_plans", "list_suggested_rules", "suggested_rule_action", "suggested_rules_stats"]);
const MEMORY_1_OP = new Set([
  "create_node", "update_node", "delete_node", "supersede_node",
  "create_event", "update_event", "delete_event", "import_batch",
  "create_task", "update_task", "delete_task", "reorder_tasks",
  "create_todo", "update_todo", "delete_todo", "complete_todo",
  "create_diagram", "update_diagram", "delete_diagram",
  "create_doc", "update_doc", "delete_doc", "create_roadmap",
  "delete_transcript"
]);
const MEMORY_0_OP = new Set(["get_node", "list_nodes", "get_event", "list_events", "get_task", "list_tasks", "get_todo", "list_todos", "get_diagram", "list_diagrams", "get_doc", "list_docs", "list_transcripts", "get_transcript", "search_transcripts", "team_tasks", "team_todos", "team_diagrams", "team_docs"]);
const MEMORY_2_OP = new Set(["search", "decisions", "timeline", "summary", "distill_event"]);
const GRAPH_3_OP = new Set(["related", "path", "decisions", "contradictions", "usages"]);
const GRAPH_10_OP = new Set(["dependencies", "impact", "ingest", "call_path", "circular_dependencies", "unused_code"]);
const INSTRUCT_0_OP = new Set(["get", "stats"]);
const INSTRUCT_1_OP = new Set(["push", "ack", "clear"]);

function getContextStreamOpCost(tool: string, action?: string, requestParams?: Record<string, unknown>): number {
  switch (tool) {
    case "init":
      return 3;
    case "context": {
      const mode = (requestParams?.mode as string) ?? "standard";
      return mode === "pack" ? 20 : 5;
    }
    case "search": {
      const mode = (requestParams?.mode as string) ?? "auto";
      if (SEARCH_LOW_OPS.has(mode)) return 2;
      if (SEARCH_HIGH_OPS.has(mode)) return 5;
      return 5; // auto / unknown defaults to higher
    }
    case "session":
      if (!action) return 2;
      if (SESSION_0_OP.has(action)) return 0;
      if (SESSION_1_OP.has(action)) return 1;
      if (SESSION_5_OP.has(action)) return 5;
      if (SESSION_2_OP.has(action)) return 2;
      return 2;
    case "memory":
      if (!action) return 0;
      if (MEMORY_0_OP.has(action)) return 0;
      if (MEMORY_1_OP.has(action)) return 1;
      if (MEMORY_2_OP.has(action)) return 2;
      return 0;
    case "graph":
      if (!action) return 3;
      if (GRAPH_10_OP.has(action)) return 10;
      if (GRAPH_3_OP.has(action)) return 3;
      return 3;
    case "help":
      return 0;
    case "instruct":
    case "ram":
      if (!action) return 0;
      if (INSTRUCT_0_OP.has(action)) return 0;
      if (INSTRUCT_1_OP.has(action)) return 1;
      return 0;
    default:
      return 0;
  }
}

function ensureLogDir(): void {
  const dir = path.dirname(CURSOR_USAGE_LOG_PATH);
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true });
  }
}

function openLogStream(): fs.WriteStream {
  if (logStream) return logStream;
  ensureLogDir();
  logStream = fs.createWriteStream(CURSOR_USAGE_LOG_PATH, { flags: "a" });
  logStream.on('error', (err) => {
    console.error('Failed to write to cursor usage log:', err);
  });
  return logStream;
}

function truncate(value: string, maxBytes: number): string {
  const enc = new TextEncoder();
  if (enc.encode(value).length <= maxBytes) return value;
  let s = value;
  while (enc.encode(s).length > maxBytes && s.length > 0) s = s.slice(0, -1);
  return s + "...";
}

function safePayload(payload: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  const enc = new TextEncoder();
  let total = 0;
  const maxTotal = MAX_LINE_BYTES - 200;
  for (const [k, v] of Object.entries(payload)) {
    if (total >= maxTotal) break;
    let val = v;
    if (typeof v === "string") val = truncate(v, 2000);
    else if (typeof v === "object" && v !== null && typeof (v as Record<string, unknown>).message === "string") {
      val = { ...(v as object), message: truncate((v as Record<string, unknown>).message as string, 1000) };
    }
    out[k] = val;
    total += enc.encode(JSON.stringify(k)).length + enc.encode(JSON.stringify(val)).length;
  }
  return out;
}

function levelForEvent(event: string): "INFO" | "WARN" | "ERROR" {
  if (event === "error" || event === "tool_failure") return "ERROR";
  if (event === "replan_signal") return "WARN";
  return "INFO";
}

function appendEvent(event: string, payload: Record<string, unknown>): void {
  const ts = new Date().toISOString();
  const level = levelForEvent(event);
  const staticMeta = getStaticMetadata();
  const dynamicMeta = getDynamicMetadata();

  const obj: Record<string, unknown> = {
    // Core fields
    ts,
    session_id: SESSION_ID,
    app: "cursor-usage",
    env: "local",
    component: "mcp-logger",
    level,
    event,
    // Injected metadata (static first, then dynamic)
    ...staticMeta,
    ...dynamicMeta,
    // Payload last (can override if needed)
    ...safePayload(payload),
  };

  const lineSize = Buffer.byteLength(JSON.stringify(obj), "utf8");
  obj.log_payload_bytes = lineSize;

  let line = JSON.stringify(obj) + "\n";
  if (Buffer.byteLength(line, "utf8") > MAX_LINE_BYTES) {
    obj.message = "payload truncated";
    // Recalculate size after truncation message
    const truncatedLineSize = Buffer.byteLength(JSON.stringify(obj), "utf8");
    obj.log_payload_bytes = truncatedLineSize;
    line = JSON.stringify(obj) + "\n";
  }

  // The message field is set to the full line for context in Grafana,
  // but if a message is already present, we'll keep it.
  if (!obj.message) {
    obj.message = line.trim();
  }

  line = JSON.stringify(obj) + "\n";

  const stream = openLogStream();
  stream.write(line);
  eventBuffer.push({ event, ts, payload });
  if (eventBuffer.length > MAX_EVENTS_BUFFER) eventBuffer.shift();
}

function analyzeEfficiency(): { efficient: boolean; reason?: string; suggestion?: "replan" | "stop" | "continue"; summary?: string } {
  const failures: { tool_name?: string; message?: string }[] = [];
  let turnCount = 0;
  let lastMilestoneTurn = -1;
  for (let i = 0; i < eventBuffer.length; i++) {
    const e = eventBuffer[i];
    if (e.event === "user_message" || e.event === "agent_response") turnCount++;
    if (e.event === "milestone") lastMilestoneTurn = turnCount;
    if (e.event === "tool_failure" || e.event === "error") {
      failures.push({
        tool_name: (e.payload?.tool_name as string) ?? undefined,
        message: (e.payload?.message as string) ?? (e.payload?.error as string) as string ?? undefined,
      });
    }
  }
  const sameFailureCount = failures.length >= 2
    ? (() => {
        const last = failures[failures.length - 1];
        const prev = failures[failures.length - 2];
        const key = `${last.tool_name ?? ""}:${(last.message ?? "").slice(0, 100)}`;
        const prevKey = `${prev.tool_name ?? ""}:${(prev.message ?? "").slice(0, 100)}`;
        if (key === prevKey) {
          let c = 2;
          for (let i = failures.length - 3; i >= 0; i--) {
            const f = failures[i];
            const k = `${f.tool_name ?? ""}:${(f.message ?? "").slice(0, 100)}`;
            if (k === key) c++;
            else break;
          }
          return c;
        }
        return 0;
      })()
    : 0;
  if (sameFailureCount >= REPEAT_FAILURE_THRESHOLD) {
    const lastFailure = failures[failures.length - 1];
    return {
      efficient: false,
      reason: `Repeated tool/error failure: '${lastFailure.tool_name ?? "unknown tool"}' with message snippet: '${(lastFailure.message ?? "").slice(0, 50)}...' repeated ${sameFailureCount} times.`,
      suggestion: "replan",
      summary: `Recent failures: ${failures.length}; identical failure count: ${sameFailureCount}. Recommend replanning. Review the logs for details on the repeated error.`,
    };
  }
  if (turnCount >= TURN_WITHOUT_MILESTONE_THRESHOLD && lastMilestoneTurn < turnCount - 10) {
    return {
      efficient: false,
      reason: `Many turns (${turnCount}) without a recent milestone. Last milestone at turn ${lastMilestoneTurn}.`,
      suggestion: "replan",
      summary: `Turn count: ${turnCount}; last milestone at turn ${lastMilestoneTurn}. Consider reviewing recent user messages and agent responses in the logs to understand where progress stalled.`,
    };
  }
  return {
    efficient: true,
    suggestion: "continue",
    summary: `Turns: ${turnCount}, failures: ${failures.length}. Usage looks efficient.`,
  };
}

// #region agent log
const DEBUG_LOG = (location: string, message: string, data: Record<string, unknown> = {}) => {
  const payload = { sessionId: "8a0c29", location, message, data: { ...data }, timestamp: Date.now() };
  fetch("http://127.0.0.1:7735/ingest/cd94f4fd-ab49-45a5-a78d-31db728d9f0d", { method: "POST", headers: { "Content-Type": "application/json", "X-Debug-Session-Id": "8a0c29" }, body: JSON.stringify(payload) }).catch(() => {});
};
// #endregion

async function main(): Promise<void> {
  // #region agent log
  DEBUG_LOG("index.ts:main:entry", "main() entered", { hypothesisId: "H0" });
  // #endregion
  const server = new McpServer(
    {
      name: "cursor-usage-logger",
      version: MCP_SERVER_VERSION,
    },
    {}
  );

  server.registerResource(
    "raw_log_file",
    `file://${CURSOR_USAGE_LOG_PATH}`,
    {
      title: "Raw Log File",
      description: "The raw JSONL log file for cursor usage.",
      mimeType: "application/jsonl",
    },
    async () => {
      const content = await fs.promises.readFile(CURSOR_USAGE_LOG_PATH, "utf-8");
      return { contents: [{ uri: `file://${CURSOR_USAGE_LOG_PATH}`, type: "text", text: content }] };
    }
  );

  server.registerResource(
    "grafana_dashboard",
    "file://observability/local/grafana/provisioning/dashboards/cursor-agent-deep-dive.json",
    {
      title: "Grafana Dashboard",
      description: "The Grafana dashboard definition for visualizing agent activity.",
      mimeType: "application/json",
    },
    async () => {
      const uri = "file://observability/local/grafana/provisioning/dashboards/cursor-agent-deep-dive.json";
      const content = await fs.promises.readFile(
        "observability/local/grafana/provisioning/dashboards/cursor-agent-deep-dive.json",
        "utf-8"
      );
      return { contents: [{ uri, type: "text", text: content }] };
    }
  );

  server.registerResource(
    "skill_documentation",
    "file://.cursor/skills/cursor-usage-logging/SKILL.md",
    {
      title: "Skill Documentation",
      description: "The skill documentation for the cursor usage logger.",
      mimeType: "text/markdown",
    },
    async () => {
      const uri = "file://.cursor/skills/cursor-usage-logging/SKILL.md";
      const content = await fs.promises.readFile(
        ".cursor/skills/cursor-usage-logging/SKILL.md",
        "utf-8"
      );
      return { contents: [{ uri, type: "text", text: content }] };
    }
  );



  server.registerTool(
    "log_event",
    {
      description: "Log a generic usage event (session_start, user_message, agent_response, milestone, error, replan_signal, usage_snapshot, etc.)",
      inputSchema: z.object({
        event: z.string().describe("Event name: session_start, user_message, agent_response, milestone, error, replan_signal, usage_snapshot, session_end, tool_failure, etc."),
        payload: z.record(z.unknown()).optional().describe("Event payload with relevant fields"),
      }),
    },
    async (params) => {
      appendEvent(params.event, params.payload ?? {});
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: params.event }) }] };
    }
  );

  server.registerTool(
    "log_user_request",
    {
      description: "Log a user request event, including IDE context. Intended for automatic use by the runtime.",
      inputSchema: z.object({
        user_request: z.string(),
        current_file_info: z.any().optional(),
        selection_info: z.any().optional(),
        diagnostics: z.any().optional(),
      }),
    },
    async (params) => {
      appendEvent("user_request", params);
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "user_request" }) }] };
    }
  );

  server.registerTool(
    "log_agent_response",
    {
      description: "Log an agent response event. Intended for automatic use by the runtime.",
      inputSchema: z.object({
        message_text: z.string(),
        code_chunks: z.any().optional(),
        tool_calls: z.any().optional(),
      }),
    },
    async (params) => {
      appendEvent("agent_response", params);
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "agent_response" }) }] };
    }
  );

  server.registerTool(
    "log_tool_result",
    {
      description: "Log a tool result event. Intended for automatic use by the runtime.",
      inputSchema: z.object({
        tool_name: z.string(),
        run_terminal_command_result: z.any().optional(),
        edit_file_result: z.any().optional(),
        ripgrep_search_result: z.any().optional(),
        error: z.any().optional(),
      }),
    },
    async (params) => {
      appendEvent("tool_result_logged", params);
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "tool_result_logged" }) }] };
    }
  );

  server.registerTool(
    "log_mcp_usage",
    {
      description: "Log an MCP tool call with usage metrics. Use this to track local LLM calls (Ollama) or other MCP tool invocations with the same rich metadata as Cursor agent events.",
      inputSchema: z.object({
        server: z.string().describe("MCP server identifier (e.g., 'ollama', 'contextstream')"),
        tool_name: z.string().describe("Tool that was called (e.g., 'chat', 'generate')"),
        model: z.string().optional().describe("Model used for LLM calls (e.g., 'deepseek-r1:8b')"),
        input_tokens: z.number().optional().describe("Estimated input token count"),
        output_tokens: z.number().optional().describe("Estimated output token count"),
        duration_ms: z.number().optional().describe("Call duration in milliseconds"),
        request_summary: z.string().optional().describe("Brief summary of the request (first 500 chars)"),
        response_summary: z.string().optional().describe("Brief summary of the response (first 500 chars)"),
        has_error: z.boolean().optional().describe("Whether the call resulted in an error"),
        error_message: z.string().optional().describe("Error message if has_error is true"),
        correlation_id: z.string().optional().describe("Optional correlation ID for tracing across calls"),
        metadata: z.record(z.unknown()).optional().describe("Additional metadata specific to the call"),
      }),
    },
    async (params) => {
      appendEvent("mcp_usage", {
        mcp_server: params.server,
        mcp_tool: params.tool_name,
        model: params.model,
        input_tokens: params.input_tokens,
        output_tokens: params.output_tokens,
        duration_ms: params.duration_ms,
        request_summary: params.request_summary ? truncate(params.request_summary, 500) : undefined,
        response_summary: params.response_summary ? truncate(params.response_summary, 500) : undefined,
        has_error: params.has_error ?? false,
        error_message: params.error_message,
        correlation_id: params.correlation_id,
        ...params.metadata,
      });
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "mcp_usage", server: params.server, tool: params.tool_name }) }] };
    }
  );

  server.registerTool(
    "log_contextstream_usage",
    {
      description: "Log a ContextStream MCP call with full metrics: tool/action, tokens in/out, op cost, duration, request params, response metrics, and context identifiers. Use after every ContextStream tool call.",
      inputSchema: z.object({
        tool: z.string().describe("ContextStream tool name: init, context, search, session, memory, graph, instruct, help"),
        action: z.string().optional().describe("Action for multi-action tools (e.g. session.capture, memory.create_node)"),
        correlation_id: z.string().optional().describe("Optional correlation ID for tracing"),
        input_tokens: z.number().optional().describe("Estimated input token count"),
        output_tokens: z.number().optional().describe("Estimated output token count"),
        op_cost: z.number().optional().describe("ContextStream op cost (0-20). Auto-computed from tool/action if omitted."),
        duration_ms: z.number().optional().describe("Call duration in milliseconds"),
        request_params: z.record(z.unknown()).optional().describe("Tool-specific params: mode, format, max_tokens, output_format, limit, event_type, etc."),
        response_metrics: z.object({
          result_count: z.number().optional(),
          empty_result: z.boolean().optional(),
          response_bytes: z.number().optional(),
          truncated: z.boolean().optional(),
        }).optional().describe("Result count, empty flag, response size, truncated flag"),
        workspace_id: z.string().optional().describe("ContextStream workspace UUID"),
        project_id: z.string().optional().describe("ContextStream project UUID"),
        cs_session_id: z.string().optional().describe("ContextStream session ID"),
        has_error: z.boolean().optional().describe("Whether the call failed"),
        error_message: z.string().optional().describe("Error message if failed"),
        error_code: z.string().optional().describe("Error code if available"),
        cache_hit: z.boolean().optional().describe("Whether result was cached"),
        fallback_used: z.boolean().optional().describe("If search returned 0 and local tools were used"),
      }),
    },
    async (params) => {
      const requestParams = (params.request_params ?? {}) as Record<string, unknown>;
      const opCost = params.op_cost ?? getContextStreamOpCost(params.tool, params.action, requestParams);
      const inputTokens = params.input_tokens ?? 0;
      const outputTokens = params.output_tokens ?? 0;

      contextstreamBuffer.push({
        tool: params.tool,
        action: params.action,
        op_cost: opCost,
        input_tokens: inputTokens,
        output_tokens: outputTokens,
        duration_ms: params.duration_ms,
        has_error: params.has_error ?? false,
      });
      if (contextstreamBuffer.length > CONTEXTSTREAM_BUFFER_MAX) contextstreamBuffer.shift();

      const payload: Record<string, unknown> = {
        cs_tool: params.tool,
        cs_action: params.action,
        correlation_id: params.correlation_id,
        input_tokens: inputTokens,
        output_tokens: outputTokens,
        op_cost: opCost,
        duration_ms: params.duration_ms,
        request_params: requestParams,
        response_metrics: params.response_metrics,
        cs_workspace_id: params.workspace_id,
        cs_project_id: params.project_id,
        cs_session_id: params.cs_session_id,
        has_error: params.has_error ?? false,
        error_message: params.error_message ? truncate(params.error_message, 500) : undefined,
        error_code: params.error_code,
        cache_hit: params.cache_hit,
        fallback_used: params.fallback_used,
      };

      appendEvent("contextstream_usage", payload);
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "contextstream_usage", cs_tool: params.tool, op_cost: opCost }) }] };
    }
  );

  server.registerTool(
    "log_contextstream_session_summary",
    {
      description: "Log a session-level rollup of ContextStream usage: total ops, total calls by tool, total tokens in/out, error rate, call breakdown, average duration. Call periodically (e.g. every 10–15 turns or at milestone).",
      inputSchema: z.object({
        turn_id: z.string().optional().describe("Optional turn identifier for correlation"),
      }),
    },
    async (params) => {
      const totalCalls = contextstreamBuffer.length;
      const totalOps = contextstreamBuffer.reduce((s, r) => s + r.op_cost, 0);
      const totalTokensIn = contextstreamBuffer.reduce((s, r) => s + r.input_tokens, 0);
      const totalTokensOut = contextstreamBuffer.reduce((s, r) => s + r.output_tokens, 0);
      const errorCount = contextstreamBuffer.filter((r) => r.has_error).length;
      const errorRate = totalCalls > 0 ? (errorCount / totalCalls) * 100 : 0;
      const durations = contextstreamBuffer.filter((r) => r.duration_ms != null).map((r) => r.duration_ms!);
      const avgDurationMs = durations.length > 0 ? Math.round(durations.reduce((a, b) => a + b, 0) / durations.length) : undefined;

      const callBreakdown: Record<string, number> = {};
      for (const r of contextstreamBuffer) {
        const key = r.action ? `${r.tool}.${r.action}` : r.tool;
        callBreakdown[key] = (callBreakdown[key] ?? 0) + 1;
      }

      const payload: Record<string, unknown> = {
        total_ops: totalOps,
        total_calls: totalCalls,
        total_tokens_in: totalTokensIn,
        total_tokens_out: totalTokensOut,
        error_count: errorCount,
        error_rate_pct: Math.round(errorRate * 100) / 100,
        call_breakdown: callBreakdown,
        avg_duration_ms: avgDurationMs,
        turn_id: params.turn_id,
      };

      appendEvent("contextstream_session_summary", payload);
      return {
        content: [{ type: "text", text: JSON.stringify({ ok: true, event: "contextstream_session_summary", ...payload }) }],
      };
    }
  );

  server.registerTool(
    "log_model_usage",
    {
      description: "Log usage metrics for an LLM/model invocation (reasoning offload, subagent, internal calls, etc.)",
      inputSchema: z.object({
        model: z.string().describe("Model name (e.g., gpt-4, deepseek-r1:8b, claude-3-opus)"),
        provider: z.string().optional().describe("Provider: openai, ollama, anthropic, cursor"),
        input_tokens: z.number().optional().describe("Input token count (estimated if not available)"),
        output_tokens: z.number().optional().describe("Output token count (estimated if not available)"),
        thinking_tokens: z.number().optional().describe("Reasoning/thinking tokens (e.g. DeepSeek R1 thinking trace)"),
        response_tokens: z.number().optional().describe("Final answer tokens (when thinking_tokens is present)"),
        duration_ms: z.number().optional().describe("Call duration in milliseconds"),
        has_error: z.boolean().optional().describe("Whether the call resulted in an error"),
        error_message: z.string().optional().describe("Error message if has_error is true"),
        purpose: z.string().optional().describe("Why invoked: reasoning_offload, subagent, chat, completion, etc."),
        prompt_summary: z.string().optional().describe("Brief summary of the prompt (first 500 chars)"),
        response_summary: z.string().optional().describe("Brief summary of the response (first 500 chars)"),
        correlation_id: z.string().optional().describe("Optional correlation ID for tracing"),
      }),
    },
    async (params) => {
      const inputTokens = params.input_tokens ?? 0;
      const outputTokens = params.output_tokens ?? 0;

      appendEvent("model_usage", {
        model: params.model,
        provider: params.provider,
        input_tokens: inputTokens,
        output_tokens: outputTokens,
        total_tokens: inputTokens + outputTokens,
        thinking_tokens: params.thinking_tokens,
        response_tokens: params.response_tokens,
        duration_ms: params.duration_ms,
        has_error: params.has_error ?? false,
        error_message: params.error_message,
        purpose: params.purpose,
        prompt_summary: params.prompt_summary ? truncate(params.prompt_summary, 500) : undefined,
        response_summary: params.response_summary ? truncate(params.response_summary, 500) : undefined,
        correlation_id: params.correlation_id,
      });
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, event: "model_usage", model: params.model }) }] };
    }
  );

  server.registerTool(
    "get_usage_review",
    {
      description: "Analyze recent events and return efficiency verdict. Call periodically (e.g. every 10–15 turns). If efficient is false and suggestion is replan or stop, present to user and stop execution.",
      inputSchema: {
        turn_id: z.string().optional().describe("Optional turn identifier for correlation"),
      },
    },
    async (params): Promise<{ content: Array<{ type: "text"; text: string }> }> => {
      const result = analyzeEfficiency();
      if (params?.turn_id) appendEvent("usage_review_request", { turn_id: params.turn_id, ...result });
      return {
        content: [{ type: "text", text: JSON.stringify(result) }],
      };
    }
  );

  const transport = new StdioServerTransport(process.stdin, process.stdout);
  // #region agent log
  DEBUG_LOG("index.ts:main:before-connect", "before server.connect(transport)", { hypothesisId: "H1" });
  // #endregion
  await server.connect(transport);
  // #region agent log
  DEBUG_LOG("index.ts:main:after-connect", "after server.connect(transport)", { hypothesisId: "H1", runId: "post-fix" });
  // #endregion
}

// Export utility functions for external use
export { estimateTokens };

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
