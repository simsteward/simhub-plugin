# Cursor Usage Logging

This document outlines the system for logging Cursor usage to a local Grafana/Loki stack. This provides detailed visibility into agent behavior, tool usage, and the state of the IDE.

## Log Cursor usage to Grafana (setup)

To get Cursor usage events into Grafana:

1. **Enable the Cursor Usage Logger MCP**  
   In Cursor: **Settings → MCP** → enable `cursor-usage-logger` (or `project-0-plugin-cursor-usage-logger`). Restart Cursor if needed.

2. **Point the MCP at the directory Alloy will read**  
   The MCP writes one JSONL file; Alloy (in Docker) reads from a mounted directory. They must be the same path on the host:
   - In `.cursor/mcp.json` (or your MCP config), set `CURSOR_USAGE_LOG_PATH` to a path **under** your Grafana storage dir, e.g.  
     `S:/sim-steward-grafana-storage/cursor-usage/cursor-usage.jsonl`  
     or `C:/path/to/grafana-storage/cursor-usage/cursor-usage.jsonl`.
   - Create that directory so the file can be created: e.g. `cursor-usage` inside your `GRAFANA_STORAGE_PATH`.

3. **Start the local observability stack**  
   From the repo:
   ```bash
   cd observability/local
   # Set GRAFANA_STORAGE_PATH and LOKI_PUSH_TOKEN (see .env.example or below)
   docker compose up -d
   ```
   Alloy will mount `$GRAFANA_STORAGE_PATH/cursor-usage` as `/var/log/cursor-usage` and tail `cursor-usage.jsonl`, then push to Loki.

4. **Open Grafana**  
   Go to **http://localhost:3000** (or your Grafana URL), log in, then:
   - **Dashboards** → open **Cursor Usage (MCP Logger)** or **Cursor Usage (Comprehensive)**.
   - Or **Explore** → Loki → query `{app="cursor-usage", env="local"}`.

**Required env for Docker (e.g. in `observability/local/.env` or shell):**

- `GRAFANA_STORAGE_PATH` — directory for Loki, Grafana, and **cursor-usage** (e.g. `S:/sim-steward-grafana-storage` or `C:/grafana-storage`). Alloy mounts `$GRAFANA_STORAGE_PATH/cursor-usage` for the MCP log file.
- `LOKI_PUSH_TOKEN` — optional; set if your Loki gateway requires auth (see `observability/local/docker-compose.yml`).

Use the same base path for `CURSOR_USAGE_LOG_PATH` (e.g. `$GRAFANA_STORAGE_PATH/cursor-usage/cursor-usage.jsonl`) so the MCP writes the file that Alloy is reading.

### Critical Path Alignment for Alloy

It is crucial that the `CURSOR_USAGE_LOG_PATH` configured in `.cursor/mcp.json` (or your MCP config) points to a file *within* the directory mounted to Alloy as `/var/log/cursor-usage`. If these paths do not align, Alloy will not be able to tail the `cursor-usage.jsonl` file, and no Cursor usage logs will reach Grafana.

## Cursor Usage Tracking Checklist

To ensure Cursor usage logs successfully reach Grafana via Alloy:

1.  **Enable Cursor Usage Logger MCP:** Go to Cursor **Settings → MCP** and ensure `cursor-usage-logger` (or `project-0-plugin-cursor-usage-logger`) is enabled. Restart Cursor if prompted.
2.  **Configure `CURSOR_USAGE_LOG_PATH`:** In `.cursor/mcp.json`, set `CURSOR_USAGE_LOG_PATH` to the full path, ensuring it is under your `GRAFANA_STORAGE_PATH` and matches the `cursor-usage.jsonl` filename. Example: `$GRAFANA_STORAGE_PATH/cursor-usage/cursor-usage.jsonl`.
3.  **Create Storage Directory:** Manually create the directory `$GRAFANA_STORAGE_PATH/cursor-usage` on your host filesystem if it doesn't already exist. This is where the MCP will write the log file.
4.  **Start Docker Stack with Correct Paths:** When starting the local observability Docker stack, ensure that the `GRAFANA_STORAGE_PATH` (and `SIMSTEWARD_DATA_PATH` for plugin logs) environment variables are correctly set and passed to `docker compose up -d`. These paths dictate the host directories mounted into the Alloy container, which must match where the MCP writes its logs.

## Overview

The system leverages the "Extension RPC tracer" in Cursor to automatically capture a rich stream of data about user and agent activity. This data is processed by the **Cursor Usage Logger MCP** and written to a local log file, which is then ingested by Grafana/Loki for visualization.

## Automated Logging Events

The following events are the core of the new logging system. They are captured automatically by the runtime.

### `user_request`

-   **Trigger:** Fired at the beginning of each user turn.
-   **Payload:** Contains the full context of the user's request.
    -   `user_request` (string): The user's message text.
    -   `current_file_info` (object): The user's active file, including path, content, and cursor position.
    -   `selection_info` (object): The user's current code selection.
    -   `diagnostics` (array): Any linter errors or diagnostics.

### `agent_response`

-   **Trigger:** Fired at the end of each agent turn.
-   **Payload:** Contains the agent's full response.
    -   `message_text` (string): The agent's response text.
    -   `code_chunks` (array): Any code chunks from the agent.
    -   `tool_calls` (array): The tools the agent decided to call.

### `tool_result_logged`

-   **Trigger:** Fired after each tool call.
-   **Payload:** Contains the detailed result of the tool call.
    -   `tool_name` (string): The name of the tool.
    -   `run_terminal_command_result` (object): Output from terminal commands.
    -   `edit_file_result` (object): File diffs from edits.
    -   `ripgrep_search_result` (object): Code search results.
    -   `error` (object): Any tool call errors.

## On-Demand Usage Snapshots

The `/log-usage` skill can be used to generate an on-demand `usage_snapshot` event, which aggregates the recent automated events into a summary.

## Dashboards

The following provisioned dashboards are available in Grafana:

- **Cursor Usage (Comprehensive)**: The **Token-First** view of the system. Visual-first dashboard focusing on `input_tokens`, `output_tokens`, and `length_chars`.
- **Cursor Usage (MCP Logger)**: Operational overview of event rates, errors, and replan signals.
- **Cursor Usage Analytics**: Performance, velocity, and resource health (latency, uptime, heap usage).
- **Cursor Agent Deep Dive**: Detailed log-level view of user requests, agent responses, and tool results.

## Example LogQL

Use these patterns in Grafana Explore for custom analysis:

- **Global Token Consumption**:
  ```logql
  sum(sum_over_time({app="cursor-usage", env="local"} | json | input_tokens != "" | unwrap input_tokens [$__range]))
  ```
- **Model Usage by Provider**:
  ```logql
  {app="cursor-usage", env="local"} | json | event="model_usage" | provider="ollama"
  ```
- **MCP Tool Performance**:
  ```logql
  avg_over_time({app="cursor-usage", env="local"} | json | event="mcp_usage" | duration_ms != "" | unwrap duration_ms [$__interval]) by (mcp_tool)
  ```

## What we can't see (Current Limitations)

Due to limitations in the Cursor runtime, the following metrics are **not** currently captured automatically and will show as 0 or missing in dashboards:

- **Main LLM Token Usage**: Tokens consumed by the primary agent for response generation.
- **Native Tool Usage**: Usage metrics (tokens, time) for built-in `default_api` tools like `Read`, `Grep`, and `Shell`.

See `docs/RUNTIME-ENHANCEMENT-RECOMMENDATION.md` for more details on these gaps.
