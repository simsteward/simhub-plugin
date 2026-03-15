---
name: cursor-usage-logging
description: Understand the rich, automated logging data captured by the Cursor Usage Logger MCP.
---

# Cursor Usage Logging (local Grafana)

The **Cursor Usage Logger** MCP server (v2.2.0) captures detailed information about agent activity. All data stays **local** (file → Alloy → Loki → Grafana).

**P0 (mandatory):** When this MCP is available, you **MUST** log **all possible data**: after every MCP tool call use **log_mcp_usage** (or **log_contextstream_usage** for ContextStream); after every LLM call use **log_model_usage**; use **log_user_request** / **log_agent_response** or **log_event** for turn boundaries; and **log_event** for session/milestone/error events. "All possible" means every field you can observe, derive, or measure—omit tokens/duration when the runtime does not expose them (see `docs/RUNTIME-ENHANCEMENT-RECOMMENDATION.md`). Do not call log_mcp_usage for calls to this MCP (recursion). See `.cursor/rules/00_CoreDirectives.mdc` (P0.2) and `CursorUsageLogging.mdc`.

## Available Tools (9 total)

| Tool | Event Type | Purpose |
|------|------------|---------|
| `log_event` | (custom) | Generic events (session_start, milestone, error, etc.) |
| `log_user_request` | `user_request` | User request with IDE context |
| `log_agent_response` | `agent_response` | Agent response with tool calls |
| `log_tool_result` | `tool_result_logged` | Tool result logging |
| `log_mcp_usage` | `mcp_usage` | MCP tool call metrics |
| `log_contextstream_usage` | `contextstream_usage` | **ContextStream call metrics (tokens, op cost, params)** |
| `log_contextstream_session_summary` | `contextstream_session_summary` | Session-level ContextStream rollup |
| `log_model_usage` | `model_usage` | **LLM/model invocation metrics (preferred for Ollama)** |
| `get_usage_review` | `usage_review_request` | Efficiency analysis |

## Dashboards

The following Grafana dashboards are provisioned to visualize this data:

- **Cursor Usage (Comprehensive)**: The **Token-First** overview. Focuses on `input_tokens`, `output_tokens`, and `length_chars`.
- **Cursor Usage (MCP Logger)**: Operational health and event rates.
- **Cursor Usage Analytics**: Performance metrics (latency, uptime, heap usage).
- **Cursor Agent Deep Dive**: Detailed log-level investigation.

## log_model_usage (Preferred for LLM Logging)

Use `log_model_usage` for logging local LLM calls (Ollama, DeepSeek-R1) and reasoning offloads. This is the **preferred tool** for model invocations.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | Yes | Model name (e.g., "deepseek-r1:8b", "gpt-4") |
| `provider` | string | No | Provider: openai, ollama, anthropic, cursor |
| `input_tokens` | number | No | Input token count (estimated if not available) |
| `output_tokens` | number | No | Output token count (estimated if not available) |
| `duration_ms` | number | No | Call duration in milliseconds |
| `has_error` | boolean | No | Whether the call resulted in an error |
| `error_message` | string | No | Error message if has_error is true |
| `purpose` | string | No | Why invoked: reasoning_offload, subagent, chat, completion |
| `prompt_summary` | string | No | Brief summary of the prompt (first 500 chars) |
| `response_summary` | string | No | Brief summary of the response (first 500 chars) |
| `correlation_id` | string | No | Optional correlation ID for tracing |

**Example:**
```json
{
  "model": "deepseek-r1:8b",
  "provider": "ollama",
  "purpose": "reasoning_offload",
  "input_tokens": 150,
  "output_tokens": 800,
  "duration_ms": 5200,
  "prompt_summary": "Analyze WebSocket patterns...",
  "response_summary": "Recommend thread-safe client management..."
}
```

**Grafana query:**
```logql
{app="cursor-usage"} | json | event="model_usage" | provider="ollama"
```

## log_mcp_usage (For MCP Tool Calls)

Use `log_mcp_usage` for logging MCP tool calls (ContextStream, Docker MCP, etc.) - not for direct LLM invocations.

**Parameters:**
- `server` (required): MCP server identifier (e.g., "contextstream")
- `tool_name` (required): Tool that was called (e.g., "search", "session")
- `model`: Model used (if applicable)
- `input_tokens`, `output_tokens`, `duration_ms`: Metrics
- `request_summary`, `response_summary`: Brief summaries (max 500 chars)
- `has_error`, `error_message`: Error info
- `correlation_id`: Optional tracing ID
- `metadata`: Additional custom metadata

**Grafana query:**
```logql
{app="cursor-usage"} | json | event="mcp_usage" | mcp_server="contextstream"
```

## log_contextstream_usage (ContextStream Metrics)

Use **after every ContextStream MCP call** to capture full metrics: tool/action, tokens in/out, op cost, duration, request params, response metrics, and context identifiers. Op cost is auto-computed from tool/action when omitted.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `tool` | string | Yes | ContextStream tool: init, context, search, session, memory, graph, instruct, help |
| `action` | string | No | Action for multi-action tools (e.g. capture, create_node) |
| `correlation_id` | string | No | Optional tracing ID |
| `input_tokens` | number | No | Estimated input token count |
| `output_tokens` | number | No | Estimated output token count |
| `op_cost` | number | No | Op cost (0–20). Auto-computed if omitted. |
| `duration_ms` | number | No | Call duration in milliseconds |
| `request_params` | object | No | Tool-specific: mode, format, max_tokens, output_format, limit, event_type, etc. |
| `response_metrics` | object | No | result_count, empty_result, response_bytes, truncated |
| `workspace_id`, `project_id`, `cs_session_id` | string | No | ContextStream identifiers |
| `has_error`, `error_message`, `error_code` | - | No | Error info |
| `cache_hit`, `fallback_used` | boolean | No | Quality signals |

**Grafana queries:**
```logql
# All ContextStream calls
{app="cursor-usage"} | json | event="contextstream_usage"

# By tool
{app="cursor-usage"} | json | event="contextstream_usage" | cs_tool="search"

# High op-cost calls
{app="cursor-usage"} | json | event="contextstream_usage" | op_cost > 5

# Token usage over time (by tool)
sum by (cs_tool) (sum_over_time({app="cursor-usage"} | json | event="contextstream_usage" | unwrap output_tokens [1h]))

# Op usage per session
sum by (session_id) (sum_over_time({app="cursor-usage"} | json | event="contextstream_usage" | unwrap op_cost [24h]))
```

## log_contextstream_session_summary (Session Rollup)

Call periodically (e.g. every 10–15 turns or at milestone) to log a session-level rollup of ContextStream usage.

**Parameters:** `turn_id` (optional) — turn identifier for correlation.

**Logged fields:** `total_ops`, `total_calls`, `total_tokens_in`, `total_tokens_out`, `error_count`, `error_rate_pct`, `call_breakdown` (counts per tool/action), `avg_duration_ms`.

**Grafana query:**
```logql
{app="cursor-usage"} | json | event="contextstream_session_summary"
```

## log_event (Generic Events)

For semantic/milestone events: `session_start`, `session_end`, `milestone`, `error`, `replan_signal`, `usage_snapshot`, `tool_failure`.

**Parameters:**
- `event` (required): Event name
- `payload`: Event payload object

## Automated Events (Runtime)

These are captured automatically by the runtime:

- **`user_request`**: User message with IDE context (file, selection, diagnostics)
- **`agent_response`**: Agent response with code chunks and tool calls
- **`tool_result_logged`**: Tool execution results

## get_usage_review (Efficiency Analysis)

Call periodically (every 10-15 turns) to check for inefficiencies:

```logql
{app="cursor-usage"} | json | event="usage_review_request"
```

Returns: `{ efficient: boolean, reason?: string, suggestion?: "replan" | "stop" | "continue" }`

## See Also

- `.cursor/skills/log-ollama-usage/SKILL.md` - Detailed workflow for local LLM logging
- `docs/DEEPSEEK-R1-CURSOR-SETUP.md` - DATA_REQUEST protocol for local LLM MCP access
