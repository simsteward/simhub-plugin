---
name: log-usage
description: Log an on-demand usage snapshot to Grafana/Loki based on the rich, automated data stream.
---

# Log usage snapshot

When the user runs **`/log-usage`**, this skill will query the recent log data, aggregate it, and log a `usage_snapshot` event.

## Steps

1.  **Query recent events:**
    -   Read the last ~200-300 lines of the `cursor-usage.jsonl` file.
    -   From these lines, parse the `user_request`, `agent_response`, and `tool_result_logged` events for the current session.

2.  **Aggregate usage data:**
    -   Create a summary object containing key metrics from the recent events, such as:
        -   Number of user requests and agent responses.
        -   A count of each type of tool call.
        -   Total number of code chunks generated.
        -   Number of errors encountered.

3.  **Call `log_semantic_event`:**
    -   **Server:** `project-0-plugin-cursor-usage-logger`
    -   **Event:** `usage_snapshot`
    -   **Payload:** The aggregated summary data from step 2.

4.  **Confirm to the user:**
    -   Inform the user that the usage snapshot has been logged and can be viewed in the "Cursor Agent Deep Dive" dashboard in Grafana.
