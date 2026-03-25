# ContextStream MCP PostToolUse Hook → Loki

**Date:** 2026-03-24
**Status:** Draft
**Schema version:** 1
**Scope:** A single PowerShell script registered as a Claude Code `PostToolUse` hook that intercepts every `mcp__contextstream__*` tool call, infers structural metadata, extracts object manifests from responses, and POSTs directly to Loki. No user content, titles, prompts, or descriptions are ever logged.

---

## 1. Goals

1. Full observability on every ContextStream MCP tool call in Grafana.
2. Log commands verbosely (tool, action, object type, IDs, counts, sizes, statuses).
3. Protect product IP — never log prompt text, content bodies, titles, descriptions, or query strings.
4. Capture object manifests so support can reference the structural state of ContextStream objects without seeing what they contain.
5. Fallback to local JSONL file when Loki is unreachable.

## 2. Non-Goals

- Auto-draining the fallback file into Loki on recovery.
- Logging non-ContextStream tool calls.
- Measuring call duration (not available in PostToolUse contract).
- Replacing or altering Claude's view of tool results.

---

## 3. Architecture

```
Claude Code
  │
  ├─ mcp__contextstream__* call executes
  │
  └─ PostToolUse hook fires
       │
       └─ scripts/contextstream-loki-hook.ps1
            │
            ├─ Parse stdin JSON (tool_name, tool_input, tool_response)
            ├─ Filter: exit 0 immediately if not mcp__contextstream__*
            ├─ Truncate tool_response to 16 KB before parsing
            ├─ Build contextstream_tool_call JSONL line
            ├─ Build contextstream_object_manifest JSONL line (if objects found)
            ├─ POST to http://localhost:3100/loki/api/v1/push
            │    ├─ Success → exit 0 (no stdout)
            │    └─ Failure → append to fallback JSONL, exit 1
            └─ Total budget: < 2 seconds
```

## 4. Hook Registration

File: `.claude/settings.json`

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "mcp__contextstream__.*",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/contextstream-loki-hook.ps1",
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```

Key decisions:
- `powershell.exe` (Windows PowerShell 5.1) not `pwsh` — faster cold start (~100-200ms vs ~300-800ms).
- `timeout: 10` — hard cap; prevents any edge case from stalling Claude Code.
- `-NoProfile -ExecutionPolicy Bypass` — minimal startup, no user profile overhead.
- Matcher is **regex** (`.*`), not glob (`*`).

## 5. Hook Input Contract

Claude Code provides JSON on stdin. Read via `[Console]::In.ReadToEnd()` — do **not** use `$input` which is unreliable for piped stdin in `powershell.exe -File` mode.

```json
{
  "session_id": "string",
  "transcript_path": "/path/to/transcript.jsonl",
  "cwd": "/working/directory",
  "hook_event_name": "PostToolUse",
  "tool_name": "mcp__contextstream__memory",
  "tool_use_id": "toolu_01ABC123...",
  "tool_input": {
    "action": "create_doc",
    "doc_type": "spec",
    "title": "SENSITIVE — NOT LOGGED",
    "content": "SENSITIVE — NOT LOGGED"
  },
  "tool_response": { }
}
```

## 6. Hook Output Contract

- **Success:** Exit 0, no stdout. Silent pass-through — Claude sees the original tool result unmodified.
- **Failure:** Exit 1, no stdout. Non-blocking — Claude Code logs in verbose mode only; user is not affected.
- **Never** write JSON to stdout — Claude Code would interpret it as hook decisions (block/modify behavior).

## 7. Loki Labels

Four labels only (consistent with project schema):

| Label | Value |
|-------|-------|
| `app` | `contextstream` |
| `env` | Value of `$env:SIMSTEWARD_LOG_ENV`, default `local` |
| `component` | `contextstream-test-cases` |
| `level` | `INFO` (normal) or `ERROR` (hook caught a failure) |

## 8. Event Types

### 8.1 `contextstream_tool_call`

Emitted for every intercepted tool call.

```json
{
  "schema_version": 1,
  "timestamp": "2026-03-24T14:30:00.000Z",
  "event": "contextstream_tool_call",
  "tool": "mcp__contextstream__memory",
  "action": "create_doc",
  "tool_use_id": "toolu_01ABC123",
  "session_id": "sess_xyz",
  "object_type": "doc",
  "object_id": "abc-123-def",
  "mode": null,
  "query_length": 0,
  "content_length": 2450,
  "response_size_bytes": 312,
  "result_count": null,
  "success": true,
  "error_summary": null
}
```

**Success detection:** The `success` field is determined by:
1. If `tool_response` is not valid JSON → `success: false`, `error_summary` = first 200 chars of raw string.
2. If `tool_response` contains an `error` or `message` field at top level → `success: false`, `error_summary` = that field truncated to 200 chars (checked against sensitive field blocklist — strip any content that matches).
3. Otherwise → `success: true`.

**Action field:** When `tool_input.action` is absent (e.g., `init`, `context`, `search` tools), `action` is derived from the tool name suffix (e.g., `mcp__contextstream__init` → `"init"`).

### 8.2 `contextstream_object_manifest`

Emitted when the response contains or references objects (single or collection).

```json
{
  "timestamp": "2026-03-24T14:30:00.000Z",
  "event": "contextstream_object_manifest",
  "tool": "mcp__contextstream__memory",
  "action": "list_docs",
  "tool_use_id": "toolu_01ABC123",
  "session_id": "sess_xyz",
  "manifest": {
    "object_type": "doc",
    "total_count": 5,
    "truncated": false,
    "objects": [
      {
        "id": "abc-123",
        "doc_type": "spec",
        "content_length": 4500,
        "created_at": "2026-03-20T10:00:00Z",
        "updated_at": "2026-03-22T15:30:00Z"
      }
    ]
  }
}
```

## 9. Allowlisted Fields

The hook uses an **allowlist** approach — only these fields are extracted from `tool_input`. Everything else is ignored.

### 9.1 Safe `tool_input` fields (logged)

| Field | Logged as |
|-------|-----------|
| `action` | `action` |
| `session_id` | `session_id` (structural ID, safe) |
| `client_name` | `client_name` |
| `mode` | `mode` |
| `node_type` | inferred → `object_type` |
| `doc_type` | manifest → `doc_type` |
| `diagram_type` | manifest → `diagram_type` |
| `event_type` | manifest → `event_type` |
| `node_id` | `object_id` |
| `doc_id` | `object_id` |
| `diagram_id` | `object_id` |
| `task_id` | `object_id` |
| `todo_id` | `object_id` |
| `plan_id` | `object_id` or manifest field |
| `event_id` | `object_id` |
| `reminder_id` | `object_id` |
| `skill_id` | `object_id` |
| `transcript_id` | `object_id` |
| `task_status` | manifest → `task_status` |
| `todo_status` | manifest → `todo_status` |
| `todo_priority` | manifest → `todo_priority` |
| `priority` | manifest → `priority` |
| `limit` | `result_count` context |
| `is_personal` | manifest field |

### 9.2 Sensitive `tool_input` fields (never logged)

- `title`, `content`, `description`, `query`, `user_message`, `assistant_message`
- `new_content`, `blocked_reason`, `reason`, `modified_instruction`
- `steps`, `milestones`, `events`, `entries`, `keywords`
- `impact`, `prevention`, `trigger`

For these, only the **character count** is logged (e.g., `content_length: 2450`).

**Note:** Any field not on the allowlist is automatically excluded. The blocklist above is documentation-only; the implementation uses allowlist-only extraction. New fields added to ContextStream in the future are excluded by default.

### 9.3 Safe `tool_response` fields for manifests

Per object type:

| Object type | Extracted fields |
|-------------|-----------------|
| `doc` | `id`, `doc_type`, `content_length`*, `created_at`, `updated_at` |
| `node` | `id`, `node_type`, `created_at`, `updated_at`, `superseded_by` |
| `plan` | `id`, `status`, `step_count`*, `created_at`, `updated_at` |
| `task` | `id`, `task_status`, `priority`, `plan_id`, `created_at`, `updated_at` |
| `todo` | `id`, `todo_status`, `todo_priority`, `created_at`, `updated_at` |
| `diagram` | `id`, `diagram_type`, `content_length`*, `created_at`, `updated_at` |
| `skill` | `id`, `status`, `category_count`*, `created_at`, `updated_at` |
| `event` | `id`, `event_type`, `content_length`*, `created_at` |
| `reminder` | `id`, `priority`, `status`, `remind_at`, `created_at` |
| `transcript` | `id`, `client_name`, `started_at` |

*`content_length` and `step_count` are computed by the hook (string length / array length), not raw content.

## 10. Object Type Inference

The hook infers `object_type` from the combination of tool name and action:

```
mcp__contextstream__memory + action contains "doc"       → doc
mcp__contextstream__memory + action contains "node"      → node
mcp__contextstream__memory + action contains "task"      → task
mcp__contextstream__memory + action contains "todo"      → todo
mcp__contextstream__memory + action contains "diagram"   → diagram
mcp__contextstream__memory + action contains "event"     → event
mcp__contextstream__memory + action contains "transcript"→ transcript
mcp__contextstream__memory + action = "search"           → memory_search
mcp__contextstream__memory + action = "decisions"        → decision
mcp__contextstream__session + action contains "plan"     → plan
mcp__contextstream__session + action contains "lesson"   → lesson
mcp__contextstream__session + action = "capture"         → session_event
mcp__contextstream__session + action = "remember"        → memory
mcp__contextstream__session + action = "recall"          → memory
mcp__contextstream__search                               → search
mcp__contextstream__skill                                → skill
mcp__contextstream__reminder                             → reminder
mcp__contextstream__project                              → project
mcp__contextstream__workspace                            → workspace
mcp__contextstream__graph                                → graph
mcp__contextstream__help                                 → help
mcp__contextstream__init                                 → init
mcp__contextstream__context                              → context
mcp__contextstream__instruct                             → instruct
mcp__contextstream__ram                                  → ram
mcp__contextstream__media                                → media
mcp__contextstream__integration                          → integration
(any other mcp__contextstream__X)                        → X (fallback: use tool suffix)
```

**Note:** The `context` and `search` tools frequently return responses exceeding 16 KB. For these tools, only the `tool_call` event is emitted; manifest extraction is skipped by design due to truncation.

## 11. Loki Push Format

HTTP POST to `http://localhost:3100/loki/api/v1/push`

```json
{
  "streams": [
    {
      "stream": {
        "app": "contextstream",
        "env": "local",
        "component": "contextstream-test-cases",
        "level": "INFO"
      },
      "values": [
        ["1711288200000000000", "{\"event\":\"contextstream_tool_call\",...}"],
        ["1711288200000000001", "{\"event\":\"contextstream_object_manifest\",...}"]
      ]
    }
  ]
}
```

- Timestamp is Unix nanoseconds (required by Loki push API).
- Both lines share the same stream labels.
- The manifest line gets timestamp +1 ns to ensure ordering.

## 12. Response Parsing Safety

1. **Input truncation:** Truncate `tool_response` string to 16 KB before attempting JSON parse. This prevents memory issues on large list responses.
2. **Defensive parsing:** If `tool_response` is not valid JSON (plain string, error text), skip manifest extraction. Still emit the `tool_call` line with `success: false`.
3. **Size cap on manifest output:** If the serialized manifest exceeds 8 KB, truncate the `objects` array and set `truncated: true` with `original_count`.
4. **`result_count` extraction heuristic:** If the response contains a `results` array, count its length. If it contains `files`, count those. If it contains a top-level `total` or `count` field, use that. Otherwise `null`.

### 12.1 PowerShell 5.1 JSON constraints

- **`ConvertFrom-Json`:** Does not support `-Depth` parameter. After parsing, validate the result is an object (`-is [PSCustomObject]` or `[Hashtable]`), not a string. Deeply nested responses may silently truncate.
- **`ConvertTo-Json`:** Defaults to depth 2. The manifest has 3+ levels of nesting. **Every `ConvertTo-Json` call MUST use `-Depth 10`** to prevent `objects` array entries serializing as `System.Collections.Hashtable`.
- **Nanosecond timestamps:** Must be emitted as **strings** in the JSON payload (as shown in Section 11). Use `.ToString()` — do not rely on `ConvertTo-Json` which may emit scientific notation for 19-digit integers.
- **Content-Type:** `Invoke-RestMethod` POST must include `-ContentType 'application/json'`.

## 13. Fallback File

**Path:** `observability/local/logs/contextstream-loki-fallback.jsonl`

Behavior:
- On Loki POST failure (connection refused, timeout, non-2xx), append the same JSONL lines to this file.
- Create the directory on first write if it doesn't exist.
- Use `[System.IO.File]::AppendAllText` — atomic at the OS level for appends under typical NTFS conditions.
- No auto-drain on recovery. Manual concern for now.

## 14. Concurrency

Multiple Claude Code tool calls can fire hooks in parallel. Mitigations:
- **Loki POST:** Stateless HTTP — parallel POSTs are safe.
- **Fallback file:** Use a named mutex (`'Global\ContextStreamLokiFallback'` — single-quoted to prevent backslash interpolation) around file writes to prevent interleaved lines.

## 15. File Layout

```
scripts/
  contextstream-loki-hook.ps1          # The hook script
observability/
  local/
    logs/
      contextstream-loki-fallback.jsonl # Fallback (created on first failure)
.claude/
  settings.json                        # Hook registration (updated)
```

## 16. Testing

### Hook fires verification (do this first)
1. Add a temporary canary line at the top of `contextstream-loki-hook.ps1`: `[System.IO.File]::AppendAllText("$PSScriptRoot\canary.txt", "fired $(Get-Date)`n")`
2. Make any ContextStream MCP call.
3. Verify `scripts/canary.txt` was created. If not, the hook registration is wrong.
4. Remove the canary line after confirming.

### Manual verification
1. Start the local Loki stack (`docker compose up` in `observability/local/`).
2. Open Grafana at `localhost:3000`.
3. Run the ContextStream test suite from `tests/contextstream-mcp-test-prompt.md`.
4. Query Grafana: `{app="contextstream", component="contextstream-test-cases"}`.
5. Verify: tool_call lines for every MCP call, manifest lines for object-returning calls.

### Fallback verification
1. Stop Loki.
2. Make a ContextStream MCP call.
3. Verify `contextstream-loki-fallback.jsonl` contains the lines.

### Sensitive data audit
1. Run the test suite.
2. Grep all logged lines for known test content strings (e.g., "[CS-TEST]", "Round-trip test node").
3. Verify: zero matches. Only IDs, types, counts, and timestamps should appear.

## 17. Grafana Query Examples

**All ContextStream calls in the last hour:**
```logql
{app="contextstream"} | json | event = "contextstream_tool_call"
```

**Failed calls:**
```logql
{app="contextstream", level="ERROR"} | json | success = false
```

**Object manifest for docs:**
```logql
{app="contextstream"} | json | event = "contextstream_object_manifest" | manifest_object_type = "doc"
```

**All actions on a specific object ID:**
```logql
{app="contextstream"} | json | object_id = "abc-123-def"
```

**Call volume by action (last 24h):**
```logql
sum by (action) (count_over_time({app="contextstream"} | json | event = "contextstream_tool_call" [24h]))
```
