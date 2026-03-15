# Skill: Log ContextStream Usage

This skill supports logging ContextStream MCP usage with full metrics. Prefer the **Cursor Usage Logger MCP** tool `log_contextstream_usage` after every ContextStream call (see `.cursor/skills/cursor-usage-logging/SKILL.md`). It captures tokens in/out, op cost, duration, request params, response metrics, and context identifiers; op cost is auto-computed when omitted.

## Agent usage (recommended)

After each ContextStream MCP call, call the Cursor Usage Logger:

- **Tool:** `log_contextstream_usage`
- **Required:** `tool` (init, context, search, session, memory, graph, instruct, help)
- **Optional:** `action`, `input_tokens`, `output_tokens`, `op_cost`, `duration_ms`, `request_params`, `response_metrics`, `workspace_id`, `project_id`, `cs_session_id`, `has_error`, `error_message`, `cache_hit`, `fallback_used`, `correlation_id`

Example payload: `{ tool: "context", action: undefined, input_tokens: 120, output_tokens: 400, duration_ms: 800, request_params: { mode: "fast", format: "minified", max_tokens: 400 } }`

## Node wrapper (optional)

For Node scripts that invoke ContextStream, this skill provides a wrapper that calls the MCP and then logs via the same metrics.

```
const { log_contextstream_usage } = require('./log-contextstream-usage');

const context = await log_contextstream_usage('context', {
  user_message: 'Hello, world!',
  mode: 'fast',
});
```

### `log_contextstream_usage(toolName, args)`

Calls the ContextStream MCP tool and logs the call. When Cursor Usage Logger MCP is available, prefer calling its `log_contextstream_usage` tool with full fields (tokens, duration, request_params) for richer Grafana metrics.
