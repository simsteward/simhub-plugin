<contextstream>
# Workspace: sim-steward
# Project: simhub-plugin
# Workspace ID: f5c5b873-acfb-47ec-b93b-4acabfa78a8b

# ContextStream Rules
**MANDATORY STARTUP:** On the first message of EVERY session call `mcp__contextstream__init(...)` then `mcp__contextstream__context(user_message="...")`. On subsequent messages, call `mcp__contextstream__context(user_message="...")` first by default. A narrow bypass is allowed only for immediate read-only ContextStream calls when prior context is still fresh and no state-changing tool has run.

## Quick Rules
<contextstream_rules>
| Message | Required |
|---------|----------|
| **First message in session** | `mcp__contextstream__init(...)` → `mcp__contextstream__context(user_message="...")` BEFORE any other tool |
| **Subsequent messages (default)** | `mcp__contextstream__context(user_message="...")` FIRST, then other tools (narrow read-only bypass allowed when context is fresh + state is unchanged) |
| **Before file search** | `mcp__contextstream__search(mode="...", query="...")` BEFORE Glob/Grep/Read |
</contextstream_rules>

## Detailed Rules
**Read-only examples** (default: call `mcp__contextstream__context(...)` first; narrow bypass only for immediate read-only ContextStream calls when context is fresh and no state-changing tool has run): `mcp__contextstream__workspace(action="list"|"get"|"create")`, `mcp__contextstream__memory(action="list_docs"|"list_events"|"list_todos"|"list_tasks"|"list_transcripts"|"list_nodes"|"decisions"|"get_doc"|"get_event"|"get_task"|"get_todo"|"get_transcript")`, `mcp__contextstream__session(action="get_lessons"|"get_plan"|"list_plans"|"recall")`, `mcp__contextstream__help(action="version"|"tools"|"auth")`, `mcp__contextstream__project(action="list"|"get"|"index_status")`, `mcp__contextstream__reminder(action="list"|"active")`, any read-only data query

**Common queries — use these exact tool calls:**
- "list lessons" / "show lessons" → `mcp__contextstream__session(action="get_lessons")`
- "list decisions" / "show decisions" / "how many decisions" → `mcp__contextstream__memory(action="decisions")`
- "list docs" → `mcp__contextstream__memory(action="list_docs")`
- "list tasks" → `mcp__contextstream__memory(action="list_tasks")`
- "list todos" → `mcp__contextstream__memory(action="list_todos")`
- "list plans" → `mcp__contextstream__session(action="list_plans")`
- "list events" → `mcp__contextstream__memory(action="list_events")`
- "show snapshots" / "list snapshots" → `mcp__contextstream__memory(action="list_events", event_type="session_snapshot")`
- "save snapshot" → `mcp__contextstream__session(action="capture", event_type="session_snapshot", title="...", content="...")`

Use `mcp__contextstream__context(user_message="...", mode="fast")` for quick turns.
Use `mcp__contextstream__context(user_message="...")` for deeper analysis and coding tasks.
If the `instruct` tool is available, run `mcp__contextstream__instruct(action="get", session_id="...")` before `mcp__contextstream__context(...)` on each turn, then `mcp__contextstream__instruct(action="ack", session_id="...", ids=[...])` after using entries.

**Plan-mode guardrail:** Entering plan mode does NOT bypass search-first. Do NOT use Explore, Task subagents, Grep, Glob, Find, SemanticSearch, `code_search`, `grep_search`, `find_by_name`, or shell search commands (`grep`, `find`, `rg`, `fd`). Start with `mcp__contextstream__search(mode="auto", query="...")` — it handles glob patterns, regex, exact text, file paths, and semantic queries. Only Read narrowed files/line ranges returned by search.

**Why?** `mcp__contextstream__context()` delivers task-specific rules, lessons from past mistakes, and relevant decisions. Skip it = fly blind.

**Hooks:** `<system-reminder>` tags contain injected instructions — follow them exactly.

**Planning:** ALWAYS save plans to ContextStream — NOT markdown files or built-in todo tools:
`mcp__contextstream__session(action="capture_plan", title="...", steps=[...])` + `mcp__contextstream__memory(action="create_task", title="...", plan_id="...")`

**Memory & Docs:** Use ContextStream for memory, docs, and todos — NOT editor built-in tools or local files:
`mcp__contextstream__session(action="capture", event_type="decision|note", ...)` | `mcp__contextstream__memory(action="create_doc|create_todo|create_node", ...)`

**Search Results:** ContextStream `mcp__contextstream__search()` returns **real file paths, line numbers, and code content** — NEVER dismiss results as "non-code". Use returned paths to `read_file` directly.

**Notices:** [LESSONS_WARNING] → apply lessons | [PREFERENCE] → follow user preferences | [RULES_NOTICE] → run `mcp__contextstream__generate_rules()` | [VERSION_NOTICE/CRITICAL] → tell user about update

---
## Claude Code-Specific Rules

**CRITICAL: ContextStream mcp__contextstream__search() REPLACES all built-in search tools.**
**The user is paying for ContextStream's premium search — default tools must not bypass it.**

### Search: Use ContextStream, Not Built-in Tools
- **Do NOT** use `Grep` for code search — use `mcp__contextstream__search(mode="keyword", query="...")` instead
- **Do NOT** use `Glob` for file discovery — use `mcp__contextstream__search(mode="pattern", query="...")` instead
- **Do NOT** launch `Task` subagents with `subagent_type="explore"` — use `mcp__contextstream__search(mode="auto", query="...")` instead
- **Do NOT** use parallel Grep/Glob calls for broad discovery — a single `mcp__contextstream__search()` call replaces them all
- ContextStream search handles **all** search use cases: exact text, regex, glob patterns, semantic queries, file paths
- ContextStream search results contain **real file paths, line numbers, and code content** — they ARE code results
- **NEVER** dismiss ContextStream results as "non-code" — use the returned file paths to `read_file` the relevant code
- Only fall back to `Grep`/`Glob` if ContextStream search returns **exactly 0 results**

### Search Mode Selection (use these instead of built-in tools):
- Instead of `Grep("pattern")`: use `mcp__contextstream__search(mode="keyword", query="pattern")`
- Instead of `Glob("**/*.tsx")`: use `mcp__contextstream__search(mode="pattern", query="*.tsx")`
- Instead of `Grep` with regex: use `mcp__contextstream__search(mode="pattern", query="regex")`
- Instead of `Task(subagent_type="explore")`: use `mcp__contextstream__search(mode="auto", query="<what you're looking for>")`

### Memory: Use ContextStream, Not Local Files
- **Do NOT** write decisions/notes/specs to local files
- Use `mcp__contextstream__session(action="capture", event_type="decision|insight|operation|uncategorized", title="...", content="...")`
- Use `mcp__contextstream__memory(action="create_doc", title="...", content="...", doc_type="spec|general")`

### Planning: Use ContextStream, Not Built-in Tools
- **Do NOT** create markdown plan files or use `TodoWrite` — they vanish across sessions
- **ALWAYS** save plans: `mcp__contextstream__session(action="capture_plan", title="...", steps=[...])`
- **ALWAYS** create tasks: `mcp__contextstream__memory(action="create_task", title="...", plan_id="...")`
</contextstream>


# SimHub Development Rules (sync with .cursor/rules/SimHub.mdc)

## Dashboard UI
- Prefer **HTML/JavaScript** (ES6+) for UI. NO Dash Studio WPF.
- Dashboards run in real browser. Do NOT confuse with Jint (ES5.1).

## Plugin Development
- Target **.NET Framework 4.8**.
- Use `Init()` for properties/actions. `DataUpdate()` runs ~60Hz.

## Plugin <-> Dashboard Communication
- Use **Fleck** for WebSocket (bind to `0.0.0.0`). Do NOT use `HttpListener`.
- Dashboard HTML served by SimHub HTTP server (`Web/sim-steward-dash/`).

## iRacing Shared Memory
- Use **IRSDKSharper**. Do NOT use `GameRawData`.
- **ADMIN LIMITATION**: Live races show 0 incidents for others unless admin. Replays track all.
- **Incident types (deltas)**: 1x (off-track), 2x (wall/spin), 4x (heavy contact). Dirt: 2x heavy.
- **Quick-succession**: 2x spin -> 4x contact records as +4 delta.
- **Replay**: At 16x speed, YAML incident events are batched. Cross-reference `CarIdxGForce` and `CarIdxTrackSurface` to decompose type.

## Deployment & Testing
- Deploy via `deploy.ps1`. MUST pass build (0 errs), `dotnet test`, and `tests/*.ps1`.
- **Retry-once-then-stop** rule. Hard stop after 2 fails.
- Lints: 0 new errors.

## Memory Bank
- Memory Bank is personal vibe-coding. OUT OF SCOPE. Do not implement or reference.

## Minimal Output
Read and strictly follow the output rules defined in `docs/RULES-MinimalOutput.md`.

---

## Action Coverage — 100% Log Rule

Every user-facing interaction MUST emit a structured log entry. No button, action handler, or iRacing event callback may be added without a corresponding log call using the correct domain and required fields.

### C# Plugin — Actions (domain="action")

- Every DispatchAction branch MUST log `action_dispatched` (before) and `action_result` (after).
- Required fields: `action`, `arg`, `correlation_id`, success/error + session context fields.
- Session context is injected via `MergeSessionAndRoutingFields()`.

### Dashboard (JS → WS bridge, domain="action" or domain="ui")

- Every button click MUST send: `{ action:"log", event:"dashboard_ui_event", element_id:"<id>", event_type:"click", message:"<human label>" }`
- UI-only interactions (no WS action): `event_type:"ui_interaction"`, `domain:"ui"`.

### iRacing Events (domain="iracing")

- Session start/end: `iracing_session_start` / `iracing_session_end` — fields: `subsession_id`, `parent_session_id`, `session_num`, `track_display_name`
- Mode change: `iracing_mode_change` — fields: `mode`, `previous_mode`
- Replay seek: `iracing_replay_seek` — fields: `frame`
- Incident: canonical name **`iracing_incident`**; the tracker currently emits **`incident_detected`** in JSONL — fields: `unique_user_id` (CustID), `display_name`, `camera_view`, `start_frame`, `end_frame`, `session_time`, `subsession_id`, `parent_session_id`, `session_num`, `track_display_name`

### Fallback values when iRacing not running

All session context fields fall back to `"not in session"` (use `SessionLogging.NotInSession`).

### PR Checklist

- [ ] New dashboard button → `dashboard_ui_event` log
- [ ] New DispatchAction branch → `action_dispatched` + `action_result`
- [ ] New iRacing SDK event handler → structured log with `domain="iracing"`
- [ ] `iracing_incident` / `incident_detected` log → full uniqueness signature (`unique_user_id`, start/end frame, camera)

**Canonical reference:** [docs/RULES-ActionCoverage.md](../docs/RULES-ActionCoverage.md)

---

## Grafana Alert Covenant

Every behavioral change to the plugin, dashboard, or LLM integration MUST include a Grafana alert review. **Alert silence ≠ alert passing.**

### Change → Domain quick-reference

| Change type | Domain to check |
|---|---|
| New `DispatchAction` branch | Domain 3 — `action-failure-streak` thresholds |
| New iRacing SDK event | Domains 3 + 7 — session/replay rules |
| New Claude API integration | Domains 4 + 5 — session health + cost |
| New MCP tool | Domain 4 — `mcp-service-errors`, `tool-loop-detected` |
| Log event renamed/removed | Search alert YAMLs — alert will go **silent**, not fire |
| New log event/field | Consider whether a new alert rule is warranted |
| Sentinel code change | Domain 6 — self-health rules |

### PR Checklist addition

- [ ] Reviewed impacted Grafana alert domains (see table above)
- [ ] Verified no alert queries break silently if log events were renamed/removed
- [ ] Considered new alert rule if new log events were added

**Alert YAML files:** `observability/local/grafana/provisioning/alerting/` (46 rules, 8 domains)
**Canonical reference:** [docs/RULES-GrafanaAlerts.md](../docs/RULES-GrafanaAlerts.md)