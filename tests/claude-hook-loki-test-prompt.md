# Claude Code Hook → Loki Pipeline — Test Suite

> **Usage:** Paste this entire prompt into a new Claude Code session in the `simhub-plugin` project directory.
> Claude will execute all test cases in order, report pass/fail, and leave no persistent artifacts.

---

You are a QA engineer testing the Claude Code hook-to-Loki observability pipeline. The system under test is `~/.claude/hooks/loki-log.js` — a hook script that runs on every Claude Code event, enriches it, and pushes structured JSON to a local Grafana Loki instance.

## Prerequisites

Before starting, verify the stack is running:

1. Run `curl -s http://localhost:3100/ready` — expect `ready`
2. Run `curl -s http://localhost:3000/api/health` — expect `{"database":"ok"}`
3. If either fails, run `docker compose -f observability/local/docker-compose.yml up -d` and re-check

If prerequisites fail after compose up, report and stop.

---

## Suite 1: Hook Script Integrity (3 tests)

**T1.1 — Syntax check**
Run `node -c ~/.claude/hooks/loki-log.js`.
- PASS if: exit code 0, no output
- FAIL if: syntax error

**T1.2 — Hook registered in settings**
Read `~/.claude/settings.json`. Check that ALL of these hook types reference `loki-log.js`:
`PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `SessionStart`, `SessionEnd`, `Stop`, `SubagentStart`, `SubagentStop`, `PreCompact`, `UserPromptSubmit`, `Notification`, `PermissionRequest`, `TaskCompleted`, `TeammateIdle`
- PASS if: all 14 hook types have a `loki-log.js` command entry
- FAIL if: any hook type is missing

**T1.3 — .env accessible**
Check that `~/.claude/hooks/loki-log.js` can resolve `SIMSTEWARD_LOKI_URL`. Run:
```bash
echo '{}' | node ~/.claude/hooks/loki-log.js unknown 2>&1; echo "exit: $?"
```
- PASS if: exits cleanly (code 0), no crash
- FAIL if: crash or non-zero exit

---

## Suite 2: Loki Ingestion — Live Verification (6 tests)

> These tests verify that the hooks running RIGHT NOW in this session are producing Loki data.
> Each test queries Loki for data from the current session.

**T2.1 — Discover current session ID**
Query Loki for the most recent `session-start` event:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component="lifecycle"} | json | hook_type="session-start"' \
  --data-urlencode 'limit=1' \
  --data-urlencode 'direction=backward'
```
Extract the `session_id` from the result.
- PASS if: returns a session_id (UUID format) — **save as `CURRENT_SESSION_ID`**
- FAIL if: no results or parse error

**T2.2 — Tool events being logged**
Query for tool events from the current session:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component=~"tool|mcp-.*"} | json | session_id="CURRENT_SESSION_ID"' \
  --data-urlencode 'limit=5' \
  --data-urlencode 'direction=backward'
```
- PASS if: returns >= 1 tool event with `hook_type` of `pre-tool-use` or `post-tool-use`
- FAIL if: no results

**T2.3 — Enrichment: duration_ms present on post-tool-use**
From T2.2 results, find a `post-tool-use` entry.
- PASS if: the entry JSON contains `duration_ms` as a number > 0
- FAIL if: `duration_ms` missing or 0

**T2.4 — Enrichment: payload sizes present**
From T2.2 results, find a `post-tool-use` entry.
- PASS if: entry contains `tool_input_bytes` and/or `tool_response_bytes` as numbers > 0
- FAIL if: both missing

**T2.5 — Stream labels correct**
From any result in T2.2, check the Loki stream labels.
- PASS if: stream contains `app="claude-dev-logging"`, `env` is set, `component` is one of: `tool`, `mcp-contextstream`, `mcp-sentry`, `mcp-ollama`, `lifecycle`, `agent`, `user`, `tokens`, `other`
- FAIL if: any required label missing or unexpected value

**T2.6 — hook_payload is nested JSON (not flattened)**
From any `post-tool-use` entry, parse the log line JSON.
- PASS if: `hook_payload` key exists and its value is an **object** (not a string), containing at least `session_id` and `cwd`
- FAIL if: `hook_payload` missing, or is a string, or fields are flattened to top level instead

---

## Suite 3: MCP Service Detection (3 tests)

**T3.1 — ContextStream tools tagged correctly**
Query for ContextStream MCP events:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component="mcp-contextstream"}' \
  --data-urlencode 'limit=3' \
  --data-urlencode 'direction=backward'
```
- PASS if: returns results where `tool_name` starts with `mcp__contextstream__` and `service` is `contextstream`
- FAIL if: no results (ContextStream is always called via CLAUDE.md rules, so data should exist)

**T3.2 — Non-MCP tools use component=tool**
Query for plain tool events:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component="tool"} | json | session_id="CURRENT_SESSION_ID"' \
  --data-urlencode 'limit=3' \
  --data-urlencode 'direction=backward'
```
- PASS if: returns results where `tool_name` is a built-in tool (e.g., `Read`, `Bash`, `Glob`, `Grep`)
- FAIL if: no results

**T3.3 — Service field absent for non-MCP tools**
From T3.2 results, check the JSON log line.
- PASS if: `service` field is absent or undefined
- FAIL if: `service` has a value for a non-MCP tool

---

## Suite 4: Lifecycle Events (4 tests)

**T4.1 — Session start logged**
Query:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component="lifecycle"} | json | hook_type="session-start" | session_id="CURRENT_SESSION_ID"' \
  --data-urlencode 'limit=1'
```
- PASS if: returns 1 result
- FAIL if: no results

**T4.2 — User prompt submit logged**
Query for `user-prompt-submit` events in current session.
- PASS if: returns >= 1 result (this test suite conversation has multiple user turns)
- FAIL if: no results

**T4.3 — User think time calculated**
From T4.2 results, check for `user_think_time_ms`.
- PASS if: at least one entry has `user_think_time_ms` > 0 (may not be present on first prompt)
- NOTE: first prompt in session won't have this field — that's expected

**T4.4 — Stop hook logged**
Query for `stop` events in current session.
- PASS if: returns >= 1 result with `hook_type="stop"`
- FAIL if: no results (stop fires after each assistant turn)

---

## Suite 5: Token Usage Pipeline (4 tests)

> Token data is extracted from the transcript JSONL and pushed to Loki on `stop` and `session-end` hooks.

**T5.1 — Token usage events emitted on stop**
Query:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={app="claude-dev-logging", component="tokens"} | json | session_id="CURRENT_SESSION_ID"' \
  --data-urlencode 'limit=5' \
  --data-urlencode 'direction=backward'
```
- PASS if: returns >= 1 result with `event="claude_token_usage"`
- FAIL if: no token events found

**T5.2 — Token fields present**
From T5.1 results, check an entry's fields.
- PASS if: entry contains ALL of: `total_input_tokens`, `total_output_tokens`, `total_tokens`, `total_cache_creation_tokens`, `total_cache_read_tokens`, `assistant_turns`, `tool_use_count`, `model`
- FAIL if: any field missing

**T5.3 — Token values are reasonable**
From T5.1 results, validate token counts.
- PASS if: `total_input_tokens` > 0, `total_output_tokens` > 0, `total_tokens` == `total_input_tokens` + `total_output_tokens`, `assistant_turns` >= 1
- FAIL if: any value is 0 or math doesn't add up

**T5.4 — Incremental tracking (not re-reading whole file)**
Check that timing files exist for offset tracking:
```bash
ls $TMPDIR/claude-hook-timing/token-offset-CURRENT_SESSION_ID.json 2>/dev/null
ls $TMPDIR/claude-hook-timing/token-totals-CURRENT_SESSION_ID.json 2>/dev/null
```
(On Windows, use `$TEMP` or `%TEMP%`)
- PASS if: both files exist
- FAIL if: neither exists (means incremental tracking isn't working)

---

## Suite 6: Secret Scrubbing (3 tests)

**T6.1 — AWS key pattern scrubbed**
Run the hook with a fake payload containing a secret:
```bash
echo '{"session_id":"test","cwd":"/tmp","tool_name":"test","tool_input":{"key":"AKIAIOSFODNN7EXAMPLE"}}' | node ~/.claude/hooks/loki-log.js pre-tool-use
```
Then query Loki for the most recent `pre-tool-use` from session `test`. Check the log line.
- PASS if: `AKIAIOSFODNN7EXAMPLE` is replaced with `[REDACTED]`
- FAIL if: the AWS key appears in plain text

**T6.2 — Bearer token scrubbed**
Run:
```bash
echo '{"session_id":"test-scrub","cwd":"/tmp","tool_name":"test","tool_response":"Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.abc123"}' | node ~/.claude/hooks/loki-log.js post-tool-use
```
Query for `session_id="test-scrub"`.
- PASS if: Bearer token is `[REDACTED]`
- FAIL if: token visible

**T6.3 — Path compression applied**
From any log entry in this session, check the `cwd` field.
- PASS if: `cwd` starts with `~/` (not `C:\Users\<username>\`)
- FAIL if: full Windows path visible in `cwd`

---

## Suite 7: Error and Retry Detection (3 tests)

**T7.1 — Error classification works**
Run:
```bash
echo '{"session_id":"test-err","cwd":"/tmp","tool_name":"Bash","tool_response":"Error: ENOENT: no such file or directory"}' | node ~/.claude/hooks/loki-log.js post-tool-use-failure
```
Query for `session_id="test-err"`, check log line.
- PASS if: `error_type` is `not_found` and `level` label is `ERROR`
- FAIL if: `error_type` is `unknown` or `level` is not `ERROR`

**T7.2 — Timeout classification**
Run:
```bash
echo '{"session_id":"test-timeout","cwd":"/tmp","tool_name":"WebFetch","tool_response":"Request timeout after 30000ms"}' | node ~/.claude/hooks/loki-log.js post-tool-use-failure
```
- PASS if: `error_type` is `timeout`
- FAIL if: classified differently

**T7.3 — Retry detection**
Run two identical tool calls with different tool_use_ids within 10 seconds:
```bash
echo '{"session_id":"test-retry","cwd":"/tmp","tool_name":"Bash","tool_use_id":"aaa","tool_input":{"command":"ls"}}' | node ~/.claude/hooks/loki-log.js pre-tool-use
sleep 1
echo '{"session_id":"test-retry","cwd":"/tmp","tool_name":"Bash","tool_use_id":"bbb","tool_input":{"command":"ls"}}' | node ~/.claude/hooks/loki-log.js pre-tool-use
```
Query for `session_id="test-retry"` and find the second entry (tool_use_id `bbb`).
- PASS if: second entry has `is_retry: true` and `retry_of: "aaa"`
- FAIL if: retry not detected

---

## Suite 8: Agent Topology (2 tests)

**T8.1 — Subagent depth tracking**
Run:
```bash
echo '{"session_id":"test-agent","cwd":"/tmp","agent_id":"agent-001"}' | node ~/.claude/hooks/loki-log.js subagent-start
echo '{"session_id":"test-agent","cwd":"/tmp","agent_id":"agent-002"}' | node ~/.claude/hooks/loki-log.js subagent-start
```
Query for `session_id="test-agent"`, `hook_type="subagent-start"`.
- PASS if: second entry has `agent_depth` >= 2
- FAIL if: `agent_depth` missing or wrong

**T8.2 — Subagent duration tracked**
Run:
```bash
echo '{"session_id":"test-agent","cwd":"/tmp","agent_id":"agent-001"}' | node ~/.claude/hooks/loki-log.js subagent-stop
```
- PASS if: entry has `agent_duration_ms` > 0 (will be nonzero since start was logged above)
- FAIL if: `agent_duration_ms` missing or 0

---

## Suite 9: Session Metrics Sidecar (2 tests)

**T9.1 — Sidecar file exists**
Check for the session metrics sidecar file:
```bash
cat logs/claude-session-metrics.jsonl 2>/dev/null | tail -1
```
- PASS if: file exists and last line is valid JSON with `event="claude_session_metrics"`
- FAIL if: file missing or not valid JSON
- NOTE: this file is only written on `session-end`, so it may contain data from prior sessions only

**T9.2 — Sidecar contains token data**
Parse the last line of `logs/claude-session-metrics.jsonl`.
- PASS if: contains `total_input_tokens`, `total_output_tokens`, `model`
- FAIL if: token fields missing

---

## Suite 10: Grafana Explore Verification (3 tests)

> These tests validate that logged data is queryable and explorable in Grafana.

**T10.1 — Basic label query returns data**
Query Grafana via API:
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query" \
  --data-urlencode 'query={app="claude-dev-logging"}' \
  --data-urlencode 'limit=1'
```
- PASS if: returns at least 1 stream
- FAIL if: no data

**T10.2 — Component label values**
```bash
curl -s "http://localhost:3100/loki/api/v1/label/component/values"
```
- PASS if: response includes at least: `tool`, `lifecycle`, `mcp-contextstream`
- FAIL if: fewer than 3 component values

**T10.3 — JSON extraction works in LogQL**
```bash
curl -s -G "http://localhost:3100/loki/api/v1/query" \
  --data-urlencode 'query={app="claude-dev-logging"} | json | hook_type="post-tool-use" | duration_ms > 0' \
  --data-urlencode 'limit=1'
```
- PASS if: returns at least 1 result (proves `| json` extracts top-level fields)
- FAIL if: no results

---

## Suite 11: Performance (3 tests)

**T11.1 — Hook execution time**
Time a hook invocation:
```bash
time echo '{"session_id":"perf","cwd":"/tmp","tool_name":"Read","tool_use_id":"perf1"}' | node ~/.claude/hooks/loki-log.js pre-tool-use
```
- PASS if: completes in < 2 seconds (well within the 5s hook timeout)
- FAIL if: takes > 2 seconds

**T11.2 — Timing directory not bloated**
Count files in the timing directory:
```bash
ls $TMPDIR/claude-hook-timing/*.json 2>/dev/null | wc -l
```
- PASS if: < 100 files (stale cleanup is working)
- FAIL if: > 100 files (cleanup not running)

**T11.3 — Hook doesn't block on Loki failure**
Stop Loki temporarily, run a hook, verify it exits cleanly:
```bash
# Don't actually stop Loki — just test with an unreachable URL
echo '{"session_id":"perf-fail","cwd":"/tmp","tool_name":"test"}' | SIMSTEWARD_LOKI_URL=http://localhost:19999 node ~/.claude/hooks/loki-log.js pre-tool-use
```
- PASS if: exits within 5 seconds (doesn't hang waiting for connection)
- FAIL if: hangs or crashes

---

## Final Report

After completing all suites, produce this output:

### Summary Table

```
| Suite | Test  | Description                          | Result | Notes |
|-------|-------|--------------------------------------|--------|-------|
| 1     | T1.1  | Syntax check                         | ?      |       |
| 1     | T1.2  | Hook registered in settings          | ?      |       |
| ...   | ...   | ...                                  | ...    | ...   |
```

### Statistics
- Total tests: {N}
- Passed: {N}
- Failed: {N}
- Skipped: {N}

### Key Results
1. **Loki ingestion working (T2.1-T2.6):** PASS/FAIL
2. **Token pipeline (T5.1-T5.4):** PASS/FAIL — token counts: input={N}, output={N}
3. **Secret scrubbing (T6.1-T6.3):** PASS/FAIL
4. **Error/retry detection (T7.1-T7.3):** PASS/FAIL
5. **Performance (T11.1-T11.3):** PASS/FAIL — hook execution time: {N}ms

### LogQL Queries for Manual Exploration

Use these in **Grafana → Explore → Loki Local** (datasource UID: `loki_local`):

```logql
-- All hook events (last 24h)
{app="claude-dev-logging"}

-- Tool events with duration
{app="claude-dev-logging", component=~"tool|mcp-.*"} | json | duration_ms > 0

-- Token usage over time
{app="claude-dev-logging", component="tokens"} | json

-- Errors only
{app="claude-dev-logging", level="ERROR"} | json

-- ContextStream MCP calls
{app="claude-dev-logging", component="mcp-contextstream"} | json

-- Session lifecycle
{app="claude-dev-logging", component="lifecycle"} | json

-- Filter to specific session
{app="claude-dev-logging"} | json | session_id="<paste-session-id>"
```
