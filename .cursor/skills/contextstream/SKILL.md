---
name: contextstream
description: Cost-aware usage of ContextStream MCP tools — session init, context_smart, search-before-grep, memory capture, lessons, decisions, knowledge graph, and token/op budget management. Use when working with ContextStream, session context, decisions, memory events, lessons, search-first patterns, context compression, or op budgets.
---

# ContextStream — Agent Knowledge Skill

Authority: **`.cursor/rules`** (e.g. ContextStreamOveruse.mdc) enforces the mandatory init → context → search-first call sequence. This skill adds **cost-aware patterns**, a full tool reference, and token-saving techniques.

## Workspace / Project Model

| Concept   | Limit (Pro) | Description                                        |
|-----------|-------------|----------------------------------------------------|
| Workspace | 3           | Top-level container grouping related projects       |
| Project   | Unlimited   | One repo or initiative; each maps to a Cursor root |

This repo's mapping (from `.contextstream/config.json` or `.env`):

| Key            | Value                |
|----------------|----------------------|
| workspace_name | sim-steward          |
| project_name   | simhub-plugin        |
| workspace_id   | from .env / config   |
| project_id     | from .env / config   |

## Token Usage Logging

To enable token usage logging for ContextStream, you must call the ContextStream tools through the `log_contextstream_usage` wrapper function. This function is defined in the `log-contextstream-usage` skill.

Example:

```
const { log_contextstream_usage } = require('../log-contextstream-usage/log-contextstream-usage');

const context = await log_contextstream_usage('context_smart', {
  user_message: 'Hello, world!',
  mode: 'fast',
});
```

## Mandatory Call Pattern

**Invoking ContextStream MCP is required when the server is available.** Do not skip init or context when ContextStream appears in the session's MCP server list.

```
Turn 1:
  init(folder_path=..., context_hint="...")       # 3 ops  — loads workspace, lessons, decisions
  instruct(action="get", session_id="...")         # 0 ops  — check injected instructions
  context(user_message="...", mode="fast")         # 5 ops  — task-specific context
  (if auto/composer) local LLM first pass          # P0.3   — send user message + context to Ollama; reduces expensive cloud tokens (Cursor premium, GPT, Claude, Gemini)
  (if cursor-usage-logger available) log_event("usage_snapshot", { summary: "New chat init", tool_names: ["contextstream/init", "contextstream/context"], tool_count: 2 })  — bakes usage into new chats for Grafana

Turn N:
  context(user_message="...", mode="fast")         # 5 ops per turn
```

- `mode="fast"` for quick turns; omit mode (default) for deep coding tasks.
- `format="minified"` + `max_tokens=400` cut context_smart output by ~80% (~200 tokens vs ~800+).
- Never skip `init` at conversation start. Skip only on same-session continuations where context is still fresh.

## Search-First Decision Tree

ContextStream search **replaces** Grep, Glob, and multi-file Read. Only fall back to built-in tools when ContextStream returns exactly 0 results.

| Need                   | Call                                    | Ops | Notes                                 |
|------------------------|-----------------------------------------|-----|---------------------------------------|
| Exact symbol / string  | `search(mode="keyword", query="...")`   | 2   | Replaces `Grep`                       |
| File glob / pattern    | `search(mode="pattern", query="*.cs")` | 2   | Replaces `Glob`                       |
| Meaning / concept      | `search(mode="semantic", query="...")`  | 5   | Best for "how does X work?"           |
| Combined               | `search(mode="hybrid", query="...")`    | 5   | Semantic + keyword; broadest          |
| Past decisions         | `memory(action="decisions")`            | 2   | Quick list, no query needed           |
| Specific past decision | `session(action="recall", query="...")` | 5   | Natural-language recall               |

Results include real file paths and line ranges — feed directly to `Read`.

## Full Tool Reference (op costs)

### session_init — 3 ops

Call once per conversation. Returns workspace info, recent memory, lessons, decisions.

### context_smart — 5 ops (default) / 8 ops (enhanced)

Call every turn. Returns semantically relevant context for the current user message. Use `format="minified"` and `max_tokens=400` to minimize response size.

### session — variable

| Action           | Ops | When to use                                        |
|------------------|-----|----------------------------------------------------|
| capture          | 1   | Save decision, insight, warning, preference, etc.  |
| recall           | 5   | NL query against memory + code                     |
| remember         | 1   | Quick save to memory                               |
| capture_lesson   | 1   | Structured lesson from mistake/correction           |
| get_lessons      | 2   | Retrieve lessons by category/severity/query         |
| summary          | 2   | Compact workspace/project summary                  |
| compress         | 5   | Distill long chat into memory events               |
| delta            | 2   | New context since a timestamp                      |
| smart_search     | 5   | Memory + code search with enrichment               |
| user_context     | 2   | User preferences and coding style                  |
| decision_trace   | 5   | Provenance/history of a specific decision          |
| capture_plan     | 1   | Save a plan with steps                             |
| list_plans       | 0   | List saved plans                                   |

### search — 2 or 5 ops

| Mode       | Ops | Use case                              |
|------------|-----|---------------------------------------|
| keyword    | 2   | Exact text / symbol                   |
| pattern    | 2   | Regex / glob                          |
| semantic   | 5   | Meaning-based                         |
| hybrid     | 5   | Semantic + keyword combined           |
| exhaustive | 5   | All matches, grep-like (total counts) |
| refactor   | 5   | Word-boundary safe renames            |

#### Search output_format

Control response size per query. Set via `output_format` parameter:

| Format    | Token savings | When to use                          |
|-----------|---------------|--------------------------------------|
| `full`    | 0% (default)  | Need complete code context           |
| `minimal` | ~60%          | Everyday searches (recommended default) |
| `paths`   | ~80%          | File discovery, listing              |
| `count`   | ~90%          | Existence checks, match counting     |

### memory — 0–2 ops

| Action        | Ops | Notes                                |
|---------------|-----|--------------------------------------|
| create_event  | 1   | Store a memory event                 |
| get_event     | 0   | Free retrieval by ID                 |
| list_events   | 0   | List events (filterable)             |
| update_event  | 1   | Update existing event                |
| delete_event  | 1   | Remove an event                      |
| distill_event | 2   | Extract key insights                 |
| search        | 2   | Search memory with filters           |
| decisions     | 2   | Decision summaries                   |
| timeline      | 2   | Chronological event timeline         |
| summary       | 2   | Condensed memory summary             |
| create_node   | 1   | Knowledge graph node                 |
| list_nodes    | 0   | List graph nodes                     |
| get_node      | 0   | Free retrieval by ID                 |
| update_node   | 1   | Update a node                        |
| delete_node   | 1   | Remove a node                        |
| supersede_node| 1   | Replace node, maintain history       |
| create_doc    | 1   | Store a document                     |
| create_todo   | 1   | Store a todo item                    |
| create_task   | 1   | Store a task linked to a plan        |

### graph — 3–10 ops (use sparingly)

| Action                | Ops | Notes                                 |
|-----------------------|-----|---------------------------------------|
| dependencies          | 10  | Module dependency analysis            |
| impact                | 10  | Change blast-radius analysis          |
| call_path             | 10  | Trace call path between two targets   |
| circular_dependencies | 10  | Detect circular deps                  |
| unused_code           | 10  | Detect dead code                      |
| ingest                | 10  | Build/persist the code graph (do not use under tier-1; use search() instead) |
| related               | 3   | Related knowledge nodes               |
| path                  | 3   | Path between two knowledge nodes      |
| decisions             | 3   | Decision history in graph             |
| contradictions        | 3   | Find contradicting information        |

Graph calls are expensive. Prefer `search()` for simple lookups; reserve `graph()` for true dependency/impact questions. **Under tier-1, do not call ingest; use search() instead.**

### project — 0–1 ops

| Action        | Ops | Notes                                |
|---------------|-----|--------------------------------------|
| list          | 0   | List projects                        |
| get           | 0   | Project details                      |
| create        | 1   | Create project                       |
| update        | 1   | Rename / update                      |
| index         | 1/file | Trigger indexing                  |
| overview      | 1   | Summary info                         |
| statistics    | 1   | Files, lines, complexity             |
| files         | 0   | List indexed files                   |
| index_status  | 0   | Check indexing progress              |
| ingest_local  | 1/file | Ingest local files for indexing   |

### workspace — 0–1 ops

| Action    | Ops | Notes                                   |
|-----------|-----|-----------------------------------------|
| list      | 0   | List workspaces                         |
| get       | 0   | Workspace details                       |
| bootstrap | 1   | Create workspace + onboard folder       |
| associate | 1   | Associate folder with workspace         |

### reminder — 0–1 ops

| Action   | Ops | Notes                                    |
|----------|-----|------------------------------------------|
| list     | 0   | All reminders                            |
| active   | 0   | Pending + overdue                        |
| create   | 1   | New reminder                             |
| snooze   | 1   | Delay a reminder                         |
| complete | 1   | Mark done                                |
| dismiss  | 1   | Remove                                   |

### integration — 2–5 ops

| Action        | Ops | Notes                                |
|---------------|-----|--------------------------------------|
| status        | 0   | Health/sync check                    |
| search        | 5   | Cross-provider search                |
| stats         | 2   | Overview stats                       |
| activity      | 2   | Recent activity feed                 |
| contributors  | 2   | Top contributors                     |
| knowledge     | 5   | Extracted decisions/lessons          |
| summary       | 5   | High-level activity summary          |
| channels      | 2   | Slack channels (Slack only)          |
| discussions   | 2   | High-engagement threads (Slack only) |
| repos         | 2   | Synced repos (GitHub only)           |
| issues        | 2   | GitHub issues/PRs (GitHub only)      |

### help — 0 ops

| Action       | Ops | Notes                                  |
|--------------|-----|----------------------------------------|
| tools        | 0   | List available tools                   |
| auth         | 0   | Current user info                      |
| version      | 0   | MCP server version                     |
| editor_rules | 0   | Generate rule files for editors        |

### instruct / ram — session-scoped instruction cache

| Action     | Ops | Notes                                      |
|------------|-----|--------------------------------------------|
| bootstrap  | —   | Pre-load instructions into session cache   |
| get        | 0   | Retrieve pending instructions              |
| push       | 1   | Push new instructions                     |
| ack        | 1   | Acknowledge used instructions              |
| clear      | 1   | Clear cache                                |
| stats      | 0   | Cache statistics                           |

Use for session-scoped injected instructions. Alias: `ram`.

### media — video/audio/image indexing (Pro+)

| Action    | Ops | Notes                              |
|-----------|-----|------------------------------------|
| index     | —   | Trigger ML processing of media     |
| status    | 0   | Check indexing progress            |
| search    | 5   | Semantic search over indexed media |
| get_clip  | —   | Extract clip (remotion/ffmpeg/raw) |
| list      | 0   | List indexed content               |
| delete    | 1   | Remove from index                  |

### rules — 1–5 ops (if available)

| Tool              | Ops | Notes                                |
|-------------------|-----|--------------------------------------|
| import_rules_file | 5   | Normalize rules into knowledge nodes |
| diff_rules        | 5   | Compare two rule sources             |
| bulk_import_rules | 5   | Import multiple sources at once      |
| bulk_diff_rules   | 5   | Pairwise diff across sources         |

## Capture Cheatsheet

### When to capture (mandatory)

| Trigger                              | Event type    | Action                         |
|--------------------------------------|---------------|--------------------------------|
| Architectural / tech-stack choice    | `decision`    | `session(action="capture")`    |
| User correction or mistake           | `lesson`      | `session(action="capture_lesson")` |
| User frustration                     | `frustration` | `session(action="capture")`    |
| Codebase discovery                   | `insight`     | `session(action="capture")`    |
| "Don't touch X" / legacy caution     | `warning`     | `session(action="capture")`    |
| Deployment steps taken               | `operation`   | `session(action="capture")`    |
| User preference learned              | `preference`  | `session(action="capture")`    |
| Bug documented                       | `bug`         | `session(action="capture")`    |
| Feature implemented or requested     | `feature`     | `session(action="capture")`    |

**Human-readable docs:** Decisions and other inflection points (insights, lessons, preferences) may be added to human-readable documents (e.g. DECISIONS.md, STATE_AND_ROADMAP.md, or within specs). This is expected. Also capture them in ContextStream so the web platform and AI can recall them.

### Lesson fields (capture_lesson)

```
{
  "action": "capture_lesson",
  "title": "Concise summary of what to remember",
  "severity": "critical|high|medium|low",
  "category": "workflow|code_quality|verification|communication|project_specific",
  "trigger": "What action caused the problem",
  "impact": "What went wrong as a result",
  "prevention": "How to prevent this in the future",
  "keywords": ["tag1", "tag2"]
}
```

Severity guide:
- **critical** — production outage, data loss, security issue
- **high** — breaking change, significant user impact
- **medium** — workflow inefficiency, minor bug
- **low** — style/preference correction

### Plans and tasks (use instead of local files or TodoWrite)

```
session(action="capture_plan", title="...", steps=[...])
memory(action="create_task", title="...", plan_id="...")
```

Plans and tasks persist across sessions. Local markdown plan files and built-in TodoWrite do not. **When you create or revise a plan (including via CreatePlan), you must also call `session(action="capture_plan", ...)` and `memory(action="create_task", ...)` so the ContextStream Plans tab is populated.** This includes **plan updates** (e.g. after a rebase or step change), so that multiple concurrent agents working on overlapping code see the latest plan in ContextStream. See `.cursor/rules/ContextStreamSync.mdc`.

### Visibility in ContextStream web platform

The web app has three different data sources. Use the right MCP actions so each section is populated:

- **Insights** — Memory layer: `session(action="capture", ...)` events, `memory(action="create_doc", ...)`, `memory(action="create_todo", ...)`, and similar. Already populated by bootstrap.
- **Documents** — **Indexed project files**, not create_doc. Trigger with `project(action="index")` (or `project(action="ingest_local", ...)` for specific paths). Check `project(action="index_status")` and `project(action="files")`. **If the Documents tab is empty, run `project(action="index")`** (and optionally `project(action="ingest_local", paths=["docs"])`); verify with `project(action="index_status")` and `project(action="files")`.
- **Diagrams** — Stored diagram entities shown at [dashboard/diagrams](https://contextstream.io/dashboard/diagrams). Create them with `memory(action="create_diagram", title="...", content="<mermaid or diagram spec>", diagram_type="flowchart"|"sequence"|"class"|"er"|"gantt"|"mindmap"|"pie"|"other")`. Verify with `memory(action="list_diagrams")`. The Diagrams view may also show knowledge graph (nodes via `create_node`; edges may require REST API) or **code graph** from `graph(action="ingest")` (Elite/Team).

To keep Insights populated: use `memory(action="create_doc", ...)` for specs, `session(action="capture", event_type="note", ...)` for notes, `memory(action="create_todo", ...)` for todos, `session(action="capture_plan", ...)` and `memory(action="create_task", ...)` for plans; create_node for knowledge concepts. When adding a new project doc or plan in the repo, also create the corresponding resource in ContextStream so the web UI stays in sync. **Verification:** `memory(action="list_docs")`, `memory(action="list_todos")`, `session(action="list_plans")`, `memory(action="list_events")`, `memory(action="list_nodes")`, `memory(action="list_diagrams")` confirm resources exist; notes appear as event type `manual_note` in list_events. For **Documents tab**: if the tab shows "0 docs", run `project(action="index")` (and optionally `project(action="ingest_local", paths=["docs"])`); then `project(action="index_status")` and `project(action="files")` confirm the project is indexed (e.g. 80+ files); open the ContextStream web app Documents section to confirm. For **Diagrams tab**: create diagrams with `memory(action="create_diagram", ...)` and verify with `memory(action="list_diagrams")`; the tab may also show code graph from `graph(action="ingest")` (Elite/Team) or knowledge nodes (edges may require API).

### One-time: Populate Plans and Documents (when MCP is available)

If the ContextStream web app shows empty **Plans** or **Documents**, run these in a session where ContextStream MCP is enabled:

1. **Documents tab**: Run `project(action="index")` (optionally `project(action="ingest_local", paths=["docs"])`). **Verify:** Call `project(action="index_status")` and `project(action="files")`; require a non-zero file count (or index complete). If verification fails, re-run index and re-check. Refresh the web app.
2. **Plans tab (backfill):** Backfill only includes plan files under the **repo** `.cursor/plans/*.md`. Plans in the user's global plan store are not backfilled unless saved into the repo. For each plan file in the repo `.cursor/plans/*.md`, read the file, derive a title (e.g. first `#` heading or filename) and steps (from Steps table or task list). Call `session(action="capture_plan", title="...", steps=[...])` with that title and a string array of step descriptions. For each main step, call `memory(action="create_task", title="...", plan_id="...")` using the plan_id returned from `capture_plan` if the MCP returns it. Dedupe by plan name if the same plan appears under different paths. **Verify:** Call `session(action="list_plans")`; require at least one plan. If verification fails, re-run backfill and re-check.

If ContextStream MCP is not available in the current session, the agent cannot run these. **If ContextStream is configured but not in the session's available MCP list,** tell the user to enable the server in Cursor (Settings → MCP), restart Cursor if needed, and retry in a new session. The user can also open a new session with ContextStream MCP enabled and ask the agent to "run project index and backfill plans from .cursor/plans/ per the ContextStream skill."

**Verification (required):** After `project(action="index")`, call `project(action="index_status")` and `project(action="files")`; require a non-zero file count (or index complete). After backfill, call `session(action="list_plans")`; require at least one plan. If verification fails, re-run index or backfill and re-check.

**Progressive mode:** If ContextStream MCP is available but `project`, `session`, or `memory` tools are not exposed, try disabling progressive mode (e.g. set `CONTEXTSTREAM_PROGRESSIVE_MODE=false` in the MCP env or use a session with full toolset) so that `project(action="index")` and `session(action="capture_plan", ...)` are available. Optionally call `help(action="tools")` to confirm the required tools exist before running the runbook.

## Token Budget Tips

1. **Minified context** — Prefer `format="minified"` and `max_tokens=400` on `context()` to keep responses small (~200 vs ~800+ tokens). Use full context when the task needs deeper code explanation.
2. **Compress at milestones** — Consider `session(action="compress")` after completing a plan or a build-green milestone; sessions end abruptly so "end of session" is unreliable. Optional: `reminder(action="create", title="Compress previous session")` if you skip compress after a long run.
3. **Decisions up front** — `memory(action="decisions")` at session start (2 ops) replaces re-reading multiple plan files.
4. **Keyword over semantic** — use `search(mode="keyword")` (2 ops) when you know the exact string; save `semantic`/`hybrid` (5 ops) for conceptual queries.
5. **Avoid graph for lookups** — `graph()` actions cost 10 ops each. Only use for true dependency/impact analysis, not for finding where a symbol is defined.
6. **Free reads** — `memory(action="get_event")`, `list_events`, `list_nodes`, `get_node`, `project(action="list")`, `workspace(action="list")` are all 0 ops.
7. **Progressive mode** — `CONTEXTSTREAM_PROGRESSIVE_MODE=true` env var reduces exposed tools to ~2 router meta-tools (most compact tool-list for the LLM).

### Tier-1 / token budget

When under tier-1 or strict token limits:

- Use `format="minified"` and `max_tokens=400` (or 300) on every `context()` call; prefer `mode="fast"`.
- **Do not run `graph(action="ingest")`** — it can produce 50k+ token responses. Use `search()` for code and dependency-style questions.
- Prefer progressive mode (env) to reduce tool schema size.
- Optionally use `init(..., include_decisions=false, include_recent_memory=false)` for light sessions.
- Allow skipping `context()` for read-only list/get actions when the prior turn was read-only and no state changed.

### Conversation tokens (optional levers)

Conversation token use grows with rules + full thread + tool outputs. These are **optional** ways to reduce it when you want to keep sessions lean; they are not strict limits.

- **Context size** — Prefer `format="minified"` and `max_tokens=400` (or 600) on `context()` when the turn is straightforward; use default/full when you need richer context.
- **Search result size** — Prefer `output_format="minimal"` or `paths` for discovery; use `full` when you need full snippets in one shot.
- **List size** — For `list_events`, `list_nodes`, `list_docs`, consider `limit=10` or `limit=20` when a quick scan is enough; omit or increase when the user asks for "all" or a full audit.
- **Read size** — For very long files, consider `Read` with `offset`/`limit` or run `search()` first and read only the returned ranges.
- **Compress** — Consider `session(action="compress")` after 2–3 plan phases or a build-green milestone so the thread stays shorter; skip if the session is short or the user prefers to keep full history.
- **New chat** — For a large new task, starting a new conversation keeps the thread short; summarize the previous outcome in ContextStream (snapshot or plan) if needed. Use when it makes sense, not as a requirement.

## Op Budget at a Glance

| Tier | Ops/month | Sessions/month (est.) |
|------|-----------|----------------------|
| Free | 5,000     | 50–100               |
| Pro  | 25,000    | 250–500              |
| Elite| 100,000   | 1,000–2,000          |

Top-ups (never expire): 10k = $5, 50k = $20 (save 20%), 250k = $75 (save 40%).

Typical session breakdown: init (3) + context per turn (5 × N) + a few searches (10–15) + memory stores (3–5) = **50–100 ops**.

## Session Lifecycle

### Start of session

1. `init(folder_path=..., context_hint="...")` — 3 ops. Loads workspace, recent memory, **lessons**, decisions.
2. `instruct(action="get", session_id="...")` — 0 ops. Check for injected instructions; ack after using.
3. **Beginning context prompt:** Call `context(user_message="...", format="minified", max_tokens=400)` with the **user's actual first message** (or a concise summary) as `user_message`. This ensures the first context load is task-relevant.
4. **Lessons:** Apply any `lessons` from the init response **before** starting work. If the task touches a risky area, also call `session(action="get_lessons", query="<topic>")` — 2 ops.

### During session

- **Lessons:** After every user correction or mistake: `session(action="capture_lesson", ...)` with title, severity, category, trigger, impact, prevention. Before touching risky code, call `session(action="get_lessons", query="<topic>")` and apply what’s returned.
- After every architectural/tech-stack choice: `session(action="capture", event_type="decision", ...)`
- After completing a plan step: `memory(action="update_task", task_id="...", task_status="completed")`
- After a codebase discovery: `session(action="capture", event_type="insight", ...)`

### End of session (at milestones, not on close)

Sessions end abruptly. Do not rely on "end of session" triggers. Instead:

1. After completing a plan or reaching a build-green milestone: `session(action="compress")` — 5 ops. Distills the chat into memory events.
2. Verify plans are saved: `session(action="list_plans")` — 0 ops.
3. Snapshot if valuable: `session(action="capture", event_type="session_snapshot", title="...", content="...")`
4. If a long session ends without compress: `reminder(action="create", title="Compress previous session")` — fires at next init.

## ContextStream overuse (daily, weekly, milestones)

Project preference: **almost overuse** ContextStream for maximum value. See the plan in `.cursor/plans/` (ContextStream overuse plan) for full detail. Summary:

**Daily (every session):** Init + context every turn; search before Grep/Glob/Read; when something meaningful happens, capture at least one decision/lesson/insight; before risky areas call `get_lessons`; use `memory(decisions)`, `list_plans`, `list_tasks` instead of re-reading local files.

**Weekly:** Once per week run: `memory(action="decisions")` or `memory(action="timeline")`; `reminder(action="list")` and `reminder(action="active")`; compress any long session not yet compressed; `session(action="list_plans")` and `memory(action="list_tasks")`; optionally `integration(provider="github", action="summary", days=7)` and `project(action="index_status")` or `project(action="index")`.

**Milestone notifications:** When **build green**, **all tests pass**, **deploy to SimHub done**, or **plan phase complete**, do both: (1) `session(action="capture", event_type="operation", title="Milestone: <name>", content="...")` and (2) `reminder(action="create", title="Milestone: <name>", ...)` so the user is notified at next init via active reminders. Optionally `session(action="compress")` after a big milestone.

## Context Pack

When `CONTEXTSTREAM_CONTEXT_PACK=true` (set in `.cursor/mcp.json`), use `mode="pack"` on `context_smart` for code-heavy queries:

```
context(user_message="...", mode="pack", distill=true)
```

| Parameter | Effect |
|-----------|--------|
| `mode="pack"` | Returns distilled code + graph signals + memory context | 
| `distill=true` | Compact summary when available |
| Cost | 20 ops (vs 5 for standard context_smart) |

**When to use**: code explanation, refactor planning, long sessions with context pressure. Falls back to standard `context_smart` if Context Pack is disabled.

**When NOT to use**: quick questions, memory-only queries, simple searches. Use `mode="fast"` (5 ops) instead.

## Rules Import

Normalize rule files into searchable knowledge nodes so `context_smart` can surface them selectively:

```
rules(import_rules_file, source=".cursor/rules/SimHub.mdc")
```

Benefits: rules become searchable by meaning instead of always-injected in every prompt. Useful as rule files grow. Cost: 5 ops per import.

## GitHub Integration

Two paths for GitHub access:

### 1. ContextStream auto-enrichment (passive)

GitHub is connected to the `sim-steward` workspace via ContextStream web UI. Once sync completes:

- `context_smart` automatically includes relevant issues, PRs, and discussions — zero extra calls
- `integration(provider="github", action="search", query="...")` for explicit queries (5 ops)
- `integration(provider="github", action="summary", days=7)` for weekly activity (5 ops)
- `integration(provider="github", action="knowledge")` for extracted decisions/lessons (5 ops)

### 2. Cursor GitHub MCP (active)

The `user-GitHub` MCP server provides full GitHub API access. Authenticated as `simsteward`. Repos: `simsteward/simhub-plugin`, `simsteward/com`, `simsteward/common-cursor`.

| Tool | Use case |
|------|----------|
| `search_repositories` | Find repos by name/topic |
| `list_issues` / `search_issues` | Browse and search issues |
| `issue_write` / `add_issue_comment` | Create issues, add comments |
| `list_pull_requests` / `search_pull_requests` | Browse PRs |
| `create_pull_request` / `merge_pull_request` | Create and merge PRs |
| `pull_request_read` / `pull_request_review_write` | Review PRs |
| `search_code` | Search code across GitHub |

**When to use which**: Use ContextStream path for passive context enrichment (automatic, zero effort). Use `user-GitHub` MCP for direct GitHub operations (creating issues, PRs, reviews, code search).
