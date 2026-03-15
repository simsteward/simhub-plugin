# ContextStream Usage Metrics — Gaps, Issues & Recommendations

This review identifies gaps, risks, and recommended improvements for the ContextStream usage metrics implementation (Cursor Usage Logger MCP + Grafana). The analysis was requested to be performed by the local LLM; Ollama MCP was not available in the session, so the review was done by the main agent.

---

## 1. Gaps

### 1.1 Op cost lookup incomplete

**Gap:** `getContextStreamOpCost()` only handles `init`, `context`, `search`, `session`, `memory`, `graph`, `help`, `instruct`/`ram`. ContextStream also exposes **project**, **workspace**, **reminder**, **integration**, **rules**, and **media** (see `.cursor/skills/contextstream/SKILL.md`). Calls to those tools fall through to `default: 0`, so op cost is undercounted.

| Tool        | Example actions              | Ops (from skill)      |
|------------|-----------------------------|------------------------|
| project    | list/get=0, create/update/index/overview/statistics=1, ingest_local=1/file | 0–1 (or per-file) |
| workspace  | list/get=0, bootstrap/associate=1 | 0–1 |
| reminder   | list/active=0, create/snooze/complete/dismiss=1 | 0–1 |
| integration| status=0, search/knowledge/summary=5, stats/activity/contributors=2, etc. | 0–5 |
| rules      | import_rules_file, diff_rules, bulk_* = 5 | 5 |
| media      | status/list=0, search=5, index/delete=1 | 0–5 |

**Impact:** Session op totals and “op budget” gauge will be too low when project/index, reminders, or integrations are used.

### 1.2 No automatic logging

**Gap:** The agent must remember to call `log_contextstream_usage` after every ContextStream call. There is no middleware or wrapper that intercepts ContextStream MCP traffic to log automatically.

**Impact:** Missing or inconsistent logs when the agent skips the call (e.g. under token pressure or in long sessions).

### 1.3 Token estimation not applied

**Gap:** The logger exposes `estimateTokens()` (tiktoken or chars/4) but `log_contextstream_usage` does not use it. Callers can pass `input_tokens`/`output_tokens`, but if they omit them they stay 0.

**Impact:** Token panels (tokens in/out over time) are empty unless the agent or a wrapper explicitly estimates and passes tokens.

### 1.4 Session summary scope is process-bound

**Gap:** `log_contextstream_session_summary` aggregates only from the in-memory buffer (last 500 calls in this MCP process). If the Cursor Usage Logger is restarted, or if “session” is defined as “one Cursor chat” that spans multiple MCP restarts, the summary does not cover the full session.

**Impact:** Session rollups are accurate only for the current process lifetime, not for the full user session.

### 1.5 No “average duration by tool” panel

**Gap:** The plan called for “Average duration by tool”; the dashboard has time series and gauge but no table or stat panel that shows avg `duration_ms` grouped by `cs_tool`.

**Impact:** Harder to see which tools are slow without ad‑hoc LogQL.

### 1.6 ContextStream tool name mismatch (documentation)

**Gap:** The ContextStream skill sometimes refers to “context_smart”; the MCP tool name is `context`. The logger’s tool list correctly says `context`. The log-contextstream-usage wrapper example previously used `context_smart`; the skill was updated but any external docs or scripts might still use the old name.

**Impact:** Minor; possible confusion when wiring wrappers or writing queries.

---

## 2. Issues & Risks

### 2.1 Large `request_params` / `response_metrics` can blow log line size

**Risk:** `request_params` and `response_metrics` are passed through and merged into the log payload. If the agent sends large objects (e.g. full `user_message`, big `content`), `safePayload()` will truncate strings and drop keys once near 8 KB, but the event can still be noisy and approach the line limit.

**Mitigation:** Truncate or omit large request/response fields (e.g. cap `request_params.user_message` at 500 chars, or omit `request_params` for context/search when very large).

### 2.2 Session summary buffer is not persisted

**Risk:** The buffer is in-memory only. If the process crashes or is restarted, the last N calls are lost and cannot be summarized.

**Mitigation:** Accept as a design choice (summary is “recent session” only), or optionally persist the buffer to a small JSON file and reload on startup (with a max file size/count).

### 2.3 Grafana LogQL: `unwrap` and JSON labels

**Risk:** Loki’s `| json` parses the log line and can expose numeric fields as labels. The exact label names (e.g. `op_cost`, `input_tokens`) depend on Loki’s behavior. If those are not labels but only in the JSON body, `unwrap op_cost` may need a different pipeline (e.g. `| json | op_cost="" | unwrap op_cost` or similar). Dashboard queries should be validated against the actual log format.

**Mitigation:** Run a few test logs through Alloy → Loki and confirm the time series and gauge queries return data; adjust LogQL if needed.

### 2.4 Double-counting if both `log_mcp_usage` and `log_contextstream_usage` are used

**Risk:** If the agent (or a wrapper) logs the same ContextStream call to both `log_mcp_usage` (server=contextstream) and `log_contextstream_usage`, MCP usage and ContextStream usage will be double-counted in different event types.

**Mitigation:** Document that for ContextStream, **only** `log_contextstream_usage` should be used (richer and op-aware); do not also call `log_mcp_usage` for the same call.

---

## 3. Recommendations

### 3.1 Extend op cost lookup (high value)

- Add cases in `getContextStreamOpCost()` for `project`, `workspace`, `reminder`, `integration`, `rules`, and `media` using the action sets from the ContextStream skill.
- For `project`, handle `index` and `ingest_local` (e.g. 1 per call or 1 per file if available); default 1 for create/update/index/overview/statistics, 0 for list/get/files/index_status.
- For `integration`, use action to choose 0 (status), 2 (stats, activity, etc.), or 5 (search, knowledge, summary).
- Add a short comment in code pointing to the skill as the source of truth so future op changes are easy to sync.

### 3.2 Encourage token estimation at the call site

- In the cursor-usage-logging SKILL, state that if the caller has access to the raw request/response (e.g. in a wrapper), they should use `estimateTokens()` (or chars/4) and pass `input_tokens` and `output_tokens` so token panels are populated.
- Optionally: add an optional param to `log_contextstream_usage` such as `request_text` / `response_text` (max length) and, when provided, call `estimateTokens()` inside the logger and set input_tokens/output_tokens. That keeps token logic in one place but increases payload size if not truncated.

### 3.3 Add “Average duration by tool” to the dashboard

- Add a panel (table or stat with multiple series) that computes average `duration_ms` by `cs_tool` over the selected time range, e.g. via LogQL that groups by `cs_tool` and uses a suitable aggregation (or a table with one row per tool and avg duration).

### 3.4 Cap or trim heavy payload fields

- In `log_contextstream_usage`, before merging `request_params` into the payload:
  - If `request_params.user_message` exists and is long, replace with a truncated copy (e.g. 500 chars).
  - Optionally omit or trim other large keys (e.g. `content`, `query`) to keep the line under the 8 KB budget more predictably.

### 3.5 Document “ContextStream only → log_contextstream_usage”

- In both cursor-usage-logging and log-contextstream-usage skills, add one line: “Do not log the same ContextStream call with both `log_mcp_usage` and `log_contextstream_usage`; use only `log_contextstream_usage` for ContextStream.”

### 3.6 Validate Grafana queries on real logs

- After deploying, trigger a few `contextstream_usage` events with non-zero `op_cost` and token counts, then confirm in Grafana that:
  - “ContextStream op cost over time” and “tokens in/out over time” show data.
  - “Session op budget” gauge increases as expected.
- If `unwrap` fails, adjust the pipeline (e.g. ensure the parsed JSON fields are exposed as Loki labels or use a different extraction method).

### 3.7 Optional: session summary from Loki

- For “full session” rollups that survive MCP restarts, add a note in the skill or docs: “To get session-level totals for a given `session_id`, use LogQL over the selected time range,” and provide an example query that sums `op_cost`, counts calls, and sums tokens by `session_id` from `contextstream_usage` events. That gives a session summary without relying on the in-memory buffer.

---

## Summary

| Priority | Item |
|----------|------|
| High     | Extend op cost lookup for project, workspace, reminder, integration, rules, media. |
| High     | Validate Grafana LogQL with real logs (op_cost, tokens, gauge). |
| Medium   | Add “Average duration by tool” panel. |
| Medium   | Document “ContextStream → log_contextstream_usage only” to avoid double-counting. |
| Medium   | Cap/trim large request_params (e.g. user_message) to avoid log line bloat. |
| Low      | Encourage or support token estimation (docs or optional request/response text param). |
| Low      | Document session summary scope (process-bound) and optional LogQL-based session rollup. |
