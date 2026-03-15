# Skill: Log Ollama Usage

Log local LLM (Ollama) calls to the cursor-usage-logger MCP with the same rich metadata captured from Cursor agent events.

## Why Log Local LLM Usage?

When offloading reasoning tasks to a local LLM (e.g., DeepSeek-R1 via Ollama), you want the same observability as Cursor agent events:
- Model used
- Provider (ollama, openai, anthropic)
- Token counts (input/output)
- Duration
- Purpose (reasoning_offload, subagent, chat)
- Request/response summaries
- Correlation IDs for tracing

This enables unified dashboards in Grafana showing both Cursor agent activity and local LLM usage.

## Preferred Tool: log_model_usage

Use `log_model_usage` (not `log_mcp_usage`) for LLM invocations. It's specifically designed for model calls with provider and purpose tracking.

## Workflow

After calling an Ollama MCP tool (`chat` or `generate`), log the usage:

```
1. Record start time
2. Call project-0-plugin-ollama/chat (or generate)
3. Record duration and estimate tokens
4. Call project-0-plugin-cursor-usage-logger/log_model_usage with metrics
```

## Example Agent Workflow

```javascript
// Step 1: Prepare and time the call
const prompt = "Analyze the trade-offs between X and Y...";
const startTime = Date.now();

// Step 2: Call Ollama MCP
const response = await CallMcpTool("project-0-plugin-ollama", "chat", {
  model: "deepseek-r1:8b",
  message: prompt,
  system: "You are a technical analyst..."
});

// Step 3: Calculate metrics
const durationMs = Date.now() - startTime;
const inputTokens = Math.ceil(prompt.length / 4);  // ~4 chars per token
const outputTokens = Math.ceil(response.length / 4);

// Step 4: Log usage with log_model_usage
await CallMcpTool("project-0-plugin-cursor-usage-logger", "log_model_usage", {
  model: "deepseek-r1:8b",
  provider: "ollama",
  purpose: "reasoning_offload",
  input_tokens: inputTokens,
  output_tokens: outputTokens,
  duration_ms: durationMs,
  prompt_summary: prompt.slice(0, 500),
  response_summary: response.slice(0, 500),
  has_error: false,
  correlation_id: "my-task-id"
});
```

## log_model_usage Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `model` | string | Yes | Model name (e.g., "deepseek-r1:8b", "gpt-4") |
| `provider` | string | No | Provider: openai, ollama, anthropic, cursor |
| `purpose` | string | No | Why invoked: reasoning_offload, subagent, chat, completion |
| `input_tokens` | number | No | Input token count (estimated if not available) |
| `output_tokens` | number | No | Output token count (estimated if not available) |
| `duration_ms` | number | No | Call duration in milliseconds |
| `has_error` | boolean | No | Whether the call resulted in an error |
| `error_message` | string | No | Error message if has_error is true |
| `prompt_summary` | string | No | Brief summary of the prompt (first 500 chars) |
| `response_summary` | string | No | Brief summary of the response (first 500 chars) |
| `correlation_id` | string | No | Optional correlation ID for tracing |

## Logged Event

The `log_model_usage` tool appends a `model_usage` event to the cursor-usage JSONL log with:
- All static metadata (workspace, git info, OS, etc.)
- All dynamic metadata (event_id, session_uptime, etc.)
- Model-specific fields: model, provider, purpose, total_tokens

Query in Grafana with:
```logql
{app="cursor-usage"} | json | event="model_usage" | provider="ollama"
```

## Token Estimation

The MCP server includes a `estimateTokens()` function that:
1. Tries to use `tiktoken` (accurate, if installed)
2. Falls back to `chars / 4` (~75% accurate for English)

## See Also

- `docs/DEEPSEEK-R1-CURSOR-SETUP.md` - DATA_REQUEST protocol for local LLM MCP access
- `.cursor/skills/cursor-usage-logging/SKILL.md` - All 7 cursor-usage-logger tools
