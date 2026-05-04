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
**Read-only examples** (default: call `mcp__contextstream__context(...)` first; narrow bypass only for immediate read-only ContextStream calls when context is fresh and no state-changing tool has run): `mcp__contextstream__workspace(action="list"|"get"|"create")`, `mcp__contextstream__memory(action="list_docs"|"list_events"|"list_todos"|"list_tasks"|"list_transcripts"|"list_nodes"|"decisions"|"get_doc"|"get_event"|"get_task"|"get_todo"|"get_transcript")`, `mcp__contextstream__session(action="get_lessons"|"get_plan"|"list_plans"|"recall")`, `mcp__contextstream__media(action="list"|"search"|"status")`, `mcp__contextstream__help(action="version"|"tools"|"auth")`, `mcp__contextstream__project(action="list"|"get"|"index_status")`, `mcp__contextstream__reminder(action="list"|"active")`, any read-only data query

**Common queries — use these exact tool calls:**
- "list lessons" / "show lessons" → `mcp__contextstream__session(action="get_lessons")`
- "save lesson" / "remember this lesson" / "lesson learned" / "I made a mistake" → `mcp__contextstream__session(action="capture_lesson", title="...", trigger="...", impact="...", prevention="...", severity="low|medium|high|critical")` — **NEVER store lessons in local files** (e.g. `~/.claude/.../memory/`, `.cursorrules`, scratch markdown). Lessons live in ContextStream so they auto-surface as `[LESSONS_WARNING]` on future turns and across sessions.
- "list decisions" / "show decisions" / "how many decisions" → `mcp__contextstream__memory(action="decisions")`
- "save decision" / "decided to" → `mcp__contextstream__session(action="capture", event_type="decision", title="...", content="...")`
- "list docs" → `mcp__contextstream__memory(action="list_docs")`
- "list tasks" → `mcp__contextstream__memory(action="list_tasks")`
- "list todos" → `mcp__contextstream__memory(action="list_todos")`
- "list plans" → `mcp__contextstream__session(action="list_plans")`
- "list events" → `mcp__contextstream__memory(action="list_events")`
- "show snapshots" / "list snapshots" → `mcp__contextstream__memory(action="list_events", event_type="session_snapshot")`
- "save snapshot" → `mcp__contextstream__session(action="capture", event_type="session_snapshot", title="...", content="...")`
- "what did we do last session" / "past sessions" / "previous work" / "pick up where we left off" → `mcp__contextstream__session(action="recall", query="...")` (ranked context) OR `mcp__contextstream__memory(action="list_transcripts", limit=10)` (chronological list)
- "search past sessions" / "find in past transcripts" / "when did we discuss X" → `mcp__contextstream__memory(action="search_transcripts", query="...")` — full-text search over saved conversation transcripts
- "show transcript" / "read session <id>" → `mcp__contextstream__memory(action="get_transcript", transcript_id="...")`
- "list media" / "show assets" / "show photos/videos/audio/docs" → `mcp__contextstream__media(action="list", content_types=["image"])` (use `image|video|audio|document`; omit `content_types` for all assets)
- "find media" / "search photos/videos/audio/docs" / "what's in this PDF/video/audio?" → `mcp__contextstream__media(action="search", query="...", content_types=["document"])` (use `image|video|audio|document` as needed)
- "index media" / "upload asset" / "read this photo/video/audio/PDF" → `mcp__contextstream__media(action="index", file_path="...", content_type="image")` or `mcp__contextstream__media(action="index", external_url="...", content_type="document")`; use `image`, `video`, `audio`, or `document`, then check `mcp__contextstream__media(action="status", content_id="...")`
- "extract clip" / "trim video" / "clip audio" → `mcp__contextstream__media(action="get_clip", content_id="...", start="1:34", end="2:15", output_format="raw")` (also supports `ffmpeg` and `remotion`)
- "list skills" / "show my skills" → `mcp__contextstream__skill(action="list")`
- "create a skill" → `mcp__contextstream__skill(action="create", name="...", instruction_body="...", project_id="<current_project_id>", trigger_patterns=[...])`
- "update a skill" → `mcp__contextstream__skill(action="update", name="...", instruction_body="...", change_summary="...")`
- "run skill" / "use skill" → `mcp__contextstream__skill(action="run", name="...")`
- "import skills" / "import my CLAUDE.md" → `mcp__contextstream__skill(action="import", file_path="...", format="auto")`

**Structured-entity queries (Phase 1-3 taxonomy expansion) — use the `entity` tool:**
- "create ticket" / "file bug" / "track feature" / "log incident" → `entity(kind="ticket", action="create", body={"title": "...", "kind": "bug|feature|task|chore|incident|epic", "priority": "low|medium|high|urgent"})`
- "list tickets" / "show open bugs" / "active features" → `entity(kind="ticket", action="list", query={"status": "open", "kind": "bug"})`
- "update ticket" / "close ticket" / "resolve bug" → `entity(kind="ticket", action="update", id="...", body={"status": "resolved"})`
- "create handoff" / "package context for handoff" → `entity(kind="handoff", action="create", body={"title": "...", "summary": "...", "scope": "...", "to_user_id": "...", "next_steps": [...]})`
- "list handoffs" / "pending handoffs for me" → `entity(kind="handoff", action="list", query={"to_user_id": "<me>", "status": "pending"})`
- "log incident" / "open incident" / "sev1" → `entity(kind="incident", action="create", body={"title": "...", "severity": "sev1|sev2|sev3|sev4", "status": "detected", "services_affected": ["..."]})`
- "list incidents" / "active incidents" → `entity(kind="incident", action="list", query={"status": "investigating"})`
- "create release" / "track release" / "deployment" → `entity(kind="release", action="create", body={"version": "1.4.0", "status": "planned", "environments": ["prod"], "git_ref": "..."})`
- "list releases" / "recent deploys" → `entity(kind="release", action="list", query={"status": "released"})`
- "create experiment" / "start A/B test" → `entity(kind="experiment", action="create", body={"name": "...", "hypothesis": "...", "control": "...", "treatment": "...", "primary_metric": "..."})`
- "list experiments" / "running A/B tests" → `entity(kind="experiment", action="list", query={"status": "running"})`
- "create goal" / "new OKR" / "objective" → `entity(kind="goal", action="create", body={"objective": "...", "period": "2026-Q2", "owner_user_id": "..."})`
- "list goals" / "OKRs this quarter" → `entity(kind="goal", action="list", query={"period": "2026-Q2", "status": "active"})`
- "add key result" / "track KR progress" → `entity(kind="key_result", action="create", body={"goal_id": "<uuid>", "title": "MAU > 10k", "unit": "number", "target_value": 10000, "current_value": 6500})`
- "create sprint" / "new iteration" → `entity(kind="sprint", action="create", body={"name": "Sprint 42", "starts_at": "...", "ends_at": "...", "goal": "..."})`
- "list sprints" / "active sprint" → `entity(kind="sprint", action="list", query={"status": "active"})`
- "request review" / "PR review" / "design review" → `entity(kind="review", action="create", body={"title": "...", "kind": "pr|code|design|security|architecture|product", "subject_ref": "github:org/repo#123", "reviewer_ids": [...]})`
- "list reviews" / "pending reviews" → `entity(kind="review", action="list", query={"status": "requested"})`
- "log risk" / "track risk" / "risk register" → `entity(kind="risk", action="create", body={"title": "...", "likelihood": "possible", "impact": "major", "category": "...", "mitigation": "..."})`
- "list risks" / "open risks" / "severe risks" → `entity(kind="risk", action="list", query={"status": "open", "impact": "severe"})`
- "create backlog view" / "save backlog filter" → `entity(kind="backlog_view", action="create", body={"name": "Now/Next/Later", "bucket": "now", "filters": {...}})`
- "save runbook" / "create runbook" → `mcp__contextstream__memory(action="create_doc", doc_type="runbook", title="...", content="...")` (plus 20 other doc types: adr, rfc, postmortem, retro, release_notes, playbook, prd, user_story, persona, interview, design_spec, critique, glossary, oncall_schedule, slo, q_and_a, changelog, style_guide)
- "save goal node" / "distill OKR" → `mcp__contextstream__memory(action="create_node", node_type="goal"|"risk"|"term", summary="...", details="...")`
- "log standup" / "log status" / "log feedback" / "log achievement" → `mcp__contextstream__memory(action="create_event", event_type="standup"|"status_update"|"feedback"|"achievement"|"discovery"|"question"|"approval", title="...", content="...")`

Use `mcp__contextstream__context(user_message="...", mode="fast")` for quick turns.
Use `mcp__contextstream__context(user_message="...")` for deeper analysis and coding tasks.
If the `instruct` tool is available, run `mcp__contextstream__instruct(action="get", session_id="...")` before `mcp__contextstream__context(...)` on each turn, then `mcp__contextstream__instruct(action="ack", session_id="...", ids=[...])` after using entries.

**Plan-mode guardrail:** Entering plan mode does NOT bypass search-first. Do NOT use Explore, Task subagents, Grep, Glob, Find, SemanticSearch, `code_search`, `grep_search`, `find_by_name`, or shell search commands (`grep`, `find`, `rg`, `fd`). Start with `mcp__contextstream__search(mode="auto", query="...")` — it handles glob patterns, regex, exact text, file paths, and semantic queries. Only Read narrowed files/line ranges returned by search.

**Why?** `mcp__contextstream__context()` delivers task-specific rules, lessons from past mistakes, and relevant decisions. Skip it = fly blind.

## Finding Information — Search ContextStream Knowledge, Not Just Code

**Auto-grounding:** Every `mcp__contextstream__context(user_message="...")` call may include a `[GROUNDING]` block — pre-ranked prior work (transcripts, snapshots, docs, decisions, lessons) for **this** message. When you see it, read those hits **before** fanning out into code search; skipping search entirely is often correct. Outside `mcp__contextstream__context()`, use `mcp__contextstream__session(action="ground", user_message="...")` for the same one-shot bundle (recall + docs + decisions + lessons + skills + git).

When you need information, do not default to code search or trial-and-error. ContextStream stores far more than source — docs, decisions, lessons, preferences, plans, tasks, todos, skills, memory nodes, and full session transcripts all live behind dedicated tools. Pick the right knowledge surface by what you're looking for:

- **Source code / symbol / file** → `mcp__contextstream__search(mode="auto", query="...")`
- **Why we did X / past decisions** → `mcp__contextstream__memory(action="decisions", query="...")`
- **Architecture / spec / design doc** → `mcp__contextstream__memory(action="list_docs")` then `mcp__contextstream__memory(action="get_doc", doc_id="title or UUID")`
- **Prior mistakes ("never do X again")** → `mcp__contextstream__session(action="get_lessons", query="...")`
- **User preferences / conventions / constraints** → already surfaced as `[PREFERENCE]`; also `mcp__contextstream__memory(action="list_nodes", node_type="preference")` or `mcp__contextstream__memory(action="list_nodes", node_type="constraint")`
- **Open work / tasks / todos** → `mcp__contextstream__memory(action="list_tasks")` / `mcp__contextstream__memory(action="list_todos")`
- **Active or past plans** → `mcp__contextstream__session(action="list_plans")` then `mcp__contextstream__session(action="get_plan", plan_id="...")`
- **Reusable workflows / skills** → `mcp__contextstream__skill(action="list")` then `mcp__contextstream__skill(action="run", name="...")`
- **Media assets (photos/images, video, audio, documents/PDFs)** → `mcp__contextstream__media(action="search", query="...", content_types=["image"])`, `mcp__contextstream__media(action="list")`, or `mcp__contextstream__media(action="status", content_id="...")`. Use `image`, `video`, `audio`, or `document` in `content_types`. To make a local/URL asset readable by ContextStream, use `mcp__contextstream__media(action="index", file_path="...", content_type="image")`; friendly words like photos/images map to `image`, docs/PDFs/slides map to `document`.
- **Tickets / bugs / features / chores / incidents / epics** → `entity(kind="ticket", action="list", query={...})` then `entity(kind="ticket", action="get", id="...")`
- **Handoffs (context bundles between sessions/agents/teammates)** → `entity(kind="handoff", action="list")` — pair with `capsule(...)` for the artefact bundle
- **Incidents (severity + status timeline)** → `entity(kind="incident", action="list")` — distinct from `EventType::Incident` raw events
- **Releases (versioned deploys)** → `entity(kind="release", action="list")` — `changelog_doc_id` links to a `doc_type='release_notes'` doc
- **Experiments / A/B tests** → `entity(kind="experiment", action="list")`
- **Goals / OKRs / key results** → `entity(kind="goal", action="list")`, then `entity(kind="key_result", action="list")` per goal
- **Sprints / iterations** → `entity(kind="sprint", action="list", query={"active_at": "<now>"})`
- **Reviews (PR / code / design / security / architecture / product)** → `entity(kind="review", action="list")`
- **Risks (active risk register)** → `entity(kind="risk", action="list")` — distinct from distilled `node_type='risk'` summary nodes
- **Runbooks / ADRs / RFCs / postmortems / retros / release-notes / playbooks / PRDs / personas / glossary / SLOs / etc.** → `mcp__contextstream__memory(action="list_docs", doc_type="runbook|adr|rfc|postmortem|retro|release_notes|playbook|prd|user_story|persona|interview|design_spec|critique|glossary|oncall_schedule|slo|q_and_a|changelog|style_guide")`
- **"What did we do before?" (continuation work)** → `mcp__contextstream__session(action="recall", query="...")` — see the Past Sessions ladder below
- **Unsure which surface** → `mcp__contextstream__memory(action="search", query="...")` — hybrid across memory nodes + docs; falls back to `mcp__contextstream__session(action="recall", query="...")` for transcript/snapshot coverage

Default assumption: if the user asks "how do we do X?", "why did we choose Y?", "what's the pattern for Z?", or "did we already decide about Q?" — the answer is likely in a doc, decision, lesson, plan, or skill, NOT in the code. Check the right knowledge surface BEFORE reading source files or re-deriving the answer.

Before guessing, improvising, or struggling through a workflow you don't fully know:
- Start with `mcp__contextstream__context(...)` and obey `[GROUNDING]` (prior-work anchors), `[MATCHED_SKILLS]`, `[LESSONS_WARNING]`, `[PREFERENCE]`, `[DECISIONS]`, `[MEMORY]`, and `<system-reminder>` output — those are already filtered to the current task
- Treat `[LESSONS_WARNING]` as active working instructions for the current task, not optional background context; apply them immediately and keep them in mind until the task is done
- Prefer surfaced ContextStream knowledge over inventing a new workflow from memory


## Past Sessions Are Queryable — USE THEM

### Auto-Grounding (in `mcp__contextstream__context()`)

When `mcp__contextstream__context()` returns `[GROUNDING]`, those lines are **pre-ranked prior work for your current message** — read them first (transcript/snapshot/doc/decision/lesson entry points). Skipping code search is often correct. For the same bundle **outside** `mcp__contextstream__context()`, call `mcp__contextstream__session(action="ground", user_message="...")`.

Transcripts for every turn of every session are captured and indexed automatically. Session snapshots bookmark turning points. **Before asking the user what you did last time, or re-deriving context you built together previously, check the transcript + snapshot layer.** It's fast, it's complete, and the user is paying for it.

Triggers to query past sessions:
- User says "last time", "previous", "yesterday", "earlier", "we decided", "we talked about", "pick up where we left off", "what were we working on"
- You have a task that's clearly a continuation (e.g. finishing a refactor that's half-done on disk)
- You're about to ask a clarifying question whose answer is likely in a prior session
- You're unsure whether a decision or approach has already been made

Escalation ladder — walk it in order and stop at the first step that answers the question:

1. **`mcp__contextstream__session(action="recall", query="<what you're continuing>")`** — always the first call. Ranked fusion across transcripts, snapshots, docs, and decisions. Covers 80% of "what did we do before" questions.

2. **`mcp__contextstream__memory(action="search_transcripts", query="<keyword or phrase>")`** — fall through when `recall` returns thin or off-topic results, or when you need every mention of a specific term. Full-text search across ALL saved transcripts.

3. **`mcp__contextstream__memory(action="list_events", event_type="session_snapshot")`** — when you want the turning-point bookmarks (manual + auto pre-compaction captures). Useful for "what state were we in at the end of <session>" questions that `recall` misses because the answer isn't in conversational text.

4. **`mcp__contextstream__memory(action="list_transcripts", limit=10)`** — when you need a chronological index of recent sessions (titles, timestamps, IDs). Use when the user wants to know "when did we last work on X".

5. **`mcp__contextstream__memory(action="get_transcript", transcript_id="<uuid>")`** — read a full past session end-to-end. Use only after the steps above pointed you at a specific transcript ID and you need the complete exchange, not snippets.

6. **End of current session — save a bookmark** for the next one: `mcp__contextstream__session(action="capture", event_type="session_snapshot", title="...", content="<what we did + next step>")`.

**Never answer "I don't know what we did before" without running at least step 1, then step 2 if step 1 was thin.**


## Project Scope Discipline

- Reuse the `project_id` returned by `mcp__contextstream__init(...)` or `mcp__contextstream__context(...)` for project-scoped writes and lookups
- For project-scoped `mcp__contextstream__memory(...)`, `mcp__contextstream__session(...)`, and `mcp__contextstream__skill(...)` calls, pass explicit `project_id` instead of guessing from the folder name or title
- If `mcp__contextstream__init(...)` or `mcp__contextstream__context(...)` does not surface a current `project_id`, rerun `mcp__contextstream__init(folder_path="...")` before creating docs, skills, events, tasks, todos, or other project memory
- Use `target_project` only after init from a multi-project parent folder


**Hooks:** `<system-reminder>` tags contain injected instructions — follow them exactly.

**Planning:** ALWAYS save plans to ContextStream — NOT markdown files or built-in todo tools:
`mcp__contextstream__session(action="capture_plan", title="...", steps=[...])` + `mcp__contextstream__memory(action="create_task", title="...", plan_id="...")`

**Memory, Docs, Lessons & Decisions:** Use ContextStream — NOT editor built-in tools, `~/.claude/.../memory/`, `.cursorrules`, or scratch markdown files. Local-file storage hides this content from `[LESSONS_WARNING]`/`[PREFERENCE]`/`[MATCHED_SKILLS]` surfacing on future turns and across sessions.
- Lessons (mistakes, corrections, "never do X again"): `mcp__contextstream__session(action="capture_lesson", title="...", trigger="...", impact="...", prevention="...", severity="...")`
- Decisions / notes / insights: `mcp__contextstream__session(action="capture", event_type="decision|note|insight", ...)`
- Docs / todos / knowledge nodes: `mcp__contextstream__memory(action="create_doc|create_todo|create_node", ...)`

**Skills (IMPORTANT):** When `mcp__contextstream__context()` returns `[MATCHED_SKILLS]`, you **MUST run** the listed skills immediately via `mcp__contextstream__skill(action="run", name="...")`. High-priority skills (marked ⚡) are mandatory. Skills are reusable instruction + action bundles that persist across sessions. Browse: `mcp__contextstream__skill(action="list")`. Create: `mcp__contextstream__skill(action="create", name="...", instruction_body="...", trigger_patterns=[...])`. Import: `mcp__contextstream__skill(action="import", file_path="...", format="auto")`.

**Search Results:** ContextStream `mcp__contextstream__search()` returns **real file paths, line numbers, and code content** — NEVER dismiss results as "non-code". Use returned paths to `read_file` directly.

**Indexing:** Indexing and ingest are ALWAYS available. NEVER claim that transport mode, HTTP mode, or remote mode prevents indexing/ingest. Use `mcp__contextstream__project(action="index")` or `mcp__contextstream__project(action="ingest_local", path="<folder>")` — both work in all configurations.

**Notices:** [GROUNDING] → read ranked prior-work hits before code search | [GROUNDING_AVAILABLE] → optional hook reminder: unread grounding from last mcp__contextstream__context() | [MATCHED_SKILLS] → run surfaced skills before other work | [LESSONS_WARNING] → apply lessons immediately and keep them active for the turn | [PREFERENCE] → follow user preferences | [RULES_NOTICE] → run `mcp__contextstream__generate_rules()` | [VERSION_NOTICE/CRITICAL] → tell user about update

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
- Only fall back to `Grep`/`Glob` for known-new/edited files after ContextStream search misses, or when no usable index exists after the initial grace window

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
| Log event renamed/removed | Search Grafana Cloud alert rules — alert will go **silent**, not fire |
| New log event/field | Consider whether a new alert rule is warranted |

### PR Checklist addition

- [ ] Reviewed impacted Grafana alert domains (see table above)
- [ ] Verified no alert queries break silently if log events were renamed/removed
- [ ] Considered new alert rule if new log events were added

**Alert rules location:** Grafana Cloud (39 rules, 7 domains; provisioned directly — no local YAML)
**Canonical reference:** [docs/RULES-GrafanaAlerts.md](../docs/RULES-GrafanaAlerts.md)