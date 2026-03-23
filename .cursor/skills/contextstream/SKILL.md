---
name: contextstream
description: ContextStream tool patterns.
---
# ContextStream Skill

Use only when the ContextStream MCP is enabled; otherwise skip and use local tools.

## Mandatory Start
- **Turn 1:** `init(include_decisions=false, include_recent_memory=false)`
- **Turn N:** `context(user_message="...", format="minified", max_tokens=100)` BEFORE any tool.

## Search-First
- Always `search()` before Glob/Grep/Read.
- Ops cost: keyword (2), pattern (2), semantic (5), hybrid (5).
- Output format: `minimal` or `paths`.

## Semantic vs code graph (pairing)
- **Concepts / “what files matter”:** `search` with `mode` `semantic`, `hybrid`, or `auto` — not `graph` for keyword or content lookup.
- **Structure / deps / usages:** `graph` actions (`related`, `dependencies`, `impact`, `usages`, `call_path`, `path`, etc.) — not a substitute for text search; use **after** `search` narrows targets when you need edges.
- **Corpus:** Rely on `project(action="index_status")` / `ingest_local` so both search and graph see the repo. After large refactors or if structural queries look empty or wrong, run **`graph(action="ingest", wait=true)`** (or queue ingest and retry later).

## Context and search density
- **`context`:** Default before tools: `format="minified"`, `max_tokens=100`, `mode="fast"` for quick turns. For deep refactors or broad changes, raise **`max_tokens`** toward **200–400** and/or use full `context(...)` without `fast`. Use **`distill=true`** or **`mode="pack"`** when the session is long or the pack must shrink; optional **`session_tokens`** / **`context_threshold`** when the client tracks cumulative usage.
- **`search`:** Prefer **`output_format`** `minimal` or `paths`; default **`limit`** **3–5** unless you need exhaustive hits. Lower **`content_max_chars`** when you only need locations; add **`context_lines`** when you need local snippet context around matches.
- **Long threads:** Keep **`session(action="compress")`** after 30+ turns or milestones (see Maintenance).

## Operations
- **Plans:** ALWAYS `session(action="capture_plan")` + `memory(action="create_task")`. NO markdown files.
- **Repo ↔ ContextStream sync:** Use MCP **`project(action="index")`** or **`project(action="ingest_local", path="…")`** — the server-side ingest/index task. Do **not** sync via custom HTTP/API scripts, committed JSON arg files, or non-MCP CLI automation (see `docs/CONTEXTSTREAM-UPLOAD-PLAN.md`). After a sync, log with **`session(action="capture", event_type="operation", …)`**.
- **Optional Memory mirror:** `memory(create_doc|update_doc)` only through MCP in Cursor when you intentionally duplicate a spec in Memory — not via external API clients.
- **Todos:** `memory(action="create_todo")`.
- **Memory/Notes:** `session(action="capture", event_type="decision|note|lesson")`.

## Maintenance
- **Long Sessions:** `session(action="compress")` after 30+ turns or milestones.
- **Milestones:** `session(action="capture", event_type="operation")` + `reminder(action="create")`.
