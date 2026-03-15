const { CallMcpTool } = require('@modelcontextprotocol/sdk');
const { log_mcp_usage } = require('../../mcp/cursor-usage-logger/src/index');

async function log_contextstream_usage(toolName, args) {
  const inputTokens = JSON.stringify(args).length;

  const startTime = Date.now();
  const result = await CallMcpTool('contextstream', toolName, args);
  const durationMs = Date.now() - startTime;

  const outputTokens = JSON.stringify(result).length;

  await log_mcp_usage({
    server: 'contextstream',
    tool_name: toolName,
    request_count: 1,
    input_tokens: inputTokens,
    output_tokens: outputTokens,
    duration_ms: durationMs,
    has_error: false,
  });

  return result;
}

module.exports = { log_contextstream_usage };
