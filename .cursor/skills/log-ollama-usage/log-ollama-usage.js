/**
 * Wrapper for logging Ollama MCP calls to cursor-usage-logger.
 * 
 * This module provides helper functions that:
 * 1. Call Ollama MCP tools (chat, generate)
 * 2. Automatically log usage metrics to cursor-usage-logger MCP
 * 
 * Usage (from Cursor agent):
 *   After calling ollama/chat or ollama/generate, call cursor-usage-logger/log_mcp_usage
 *   with the captured metrics.
 * 
 * Example agent workflow:
 *   1. const startTime = Date.now();
 *   2. Call ollama/chat with your prompt
 *   3. const durationMs = Date.now() - startTime;
 *   4. Call cursor-usage-logger/log_mcp_usage with:
 *      - server: "ollama"
 *      - tool_name: "chat"
 *      - model: "deepseek-r1:8b"
 *      - input_tokens: (estimate from prompt length / 4)
 *      - output_tokens: (estimate from response length / 4)
 *      - duration_ms: durationMs
 *      - request_summary: (first 500 chars of prompt)
 *      - response_summary: (first 500 chars of response)
 */

// Token estimation: ~4 chars per token for English text
function estimateTokens(text) {
  return Math.ceil((text || '').length / 4);
}

// Truncate text to max length
function truncate(text, maxLen = 500) {
  if (!text || text.length <= maxLen) return text;
  return text.slice(0, maxLen) + '...';
}

module.exports = { estimateTokens, truncate };
