# Cursor Runtime Enhancement: Exposing Agent Usage KPIs

## Overview

This document outlines a critical recommendation for enhancing the Cursor runtime to expose detailed usage key performance indicators (KPIs) directly to the agent. This is essential for enabling comprehensive logging, cost tracking, and performance monitoring of agent activities.

## Current Gap

The `cursor-usage-logger` MCP server and associated skills provide the necessary tools (`log_mcp_usage`, `log_model_usage`) and payload fields (e.g., `input_tokens`, `output_tokens`, `total_tokens`, `request_count`, `duration_ms`) to capture detailed agent usage. However, the agent itself does not currently receive this information from the Cursor runtime for its own operations, such as:

*   `default_api` tool calls (`Read`, `Shell`, `Grep`, etc.).
*   Internal LLM invocations for response generation and reasoning.
*   `Task` subagent operations.

Without access to this runtime data, the agent cannot accurately log its token consumption, API call counts, or monitor rate limits.

## Recommendation: Cursor Runtime Enhancement

The primary recommendation is to **enhance the Cursor runtime to expose this critical usage data directly to the agent**. This would enable the agent to fully utilize the `cursor-usage-logger` and provide a complete picture of its operational efficiency and cost.

### Possible Implementation Methods

1.  **Extended Tool Outputs:** Modify the output of existing `default_api` tools to include metadata about token usage and API calls.
2.  **Environment Variables:** Expose current turn's token usage, API calls, and model information through environment variables that the agent can read.
3.  **New Cursor Runtime MCP Server:** Introduce a new MCP server (e.g., `cursor-runtime-info`) with tools for the agent to query its own usage statistics.

## Immediate (but Limited) Workaround

Until the Cursor runtime is enhanced, the agent will continue to call `log_mcp_usage` and `log_model_usage` with the best available information, such as `request_count: 1` and any measurable `duration_ms`. Token-related fields will remain `undefined` or `0`.

## Conclusion

Implementing this runtime enhancement is the most effective way to close the current gap in agent usage logging. It will provide the necessary data for comprehensive cost tracking, performance analysis, and efficiency improvements.