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
- **C# / SimStewardPlugin:** Smoke tests (`graph(dependencies)` on `src/SimSteward.Plugin/SimStewardPlugin.cs`) may return **0 edges** — do not assume a rich C# call graph. Prefer **`search`** plus the **Code map** table at the top of [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md) for module ↔ file mapping.
- **Corpus:** Rely on `project(action="index_status")` / `ingest_local` so both search and graph see the repo. After large refactors or if structural queries look empty or wrong, run **`graph(action="ingest", wait=true)`** (or queue ingest and retry later).

## Corpus hygiene (Cursor vs ContextStream)
- **[`.cursorignore`](../../.cursorignore)** reduces noise for **Cursor** (plans, build outputs, `.claude/projects/`, `.claude/file-history/`, etc.). It is **not guaranteed** to be applied by ContextStream server ingest; treat it as **local IDE** hygiene plus a signal for what should not dominate embeddings.
- **Refresh remote index:** `npm run contextstream:ingest:force` from repo root (runs [scripts/contextstream-ingest.ps1](../../scripts/contextstream-ingest.ps1) `-Force`), or MCP **`project(action="ingest_local", path="<repo>", force=true)`**. Poll **`project(action="index_status")`** until idle/fresh.

## Workspace binding (mapping quality)
- Open **only** this repo root as the Cursor workspace when doing ContextStream-heavy work (avoids cross-root hits under unrelated paths).
- Keep the ContextStream **project** path aligned with that same folder.

## When to force ingest
- After **`.cursorignore`** edits, **large moves** under `src/`, **architecture doc** reshuffles, or when **`index_status`** / search results look **stale** or polluted. Then: `contextstream:ingest:force` and optionally `graph(ingest, wait=true)`.

## Context and search density
- **`context`:** Default before tools: `format="minified"`, `max_tokens=100`, `mode="fast"` for quick turns. For deep refactors or broad changes, raise **`max_tokens`** toward **200–400** and/or use full `context(...)` without `fast`. Use **`distill=true`** or **`mode="pack"`** when the session is long or the pack must shrink; optional **`session_tokens`** / **`context_threshold`** when the client tracks cumulative usage.
- **`search`:** Prefer **`output_format`** `minimal` or `paths`; default **`limit`** **3–5** unless you need exhaustive hits. Lower **`content_max_chars`** when you only need locations; add **`context_lines`** when you need local snippet context around matches.
- **Long threads:** Keep **`session(action="compress")`** after 30+ turns or milestones (see Maintenance).

## Operations
- **Plans:** ALWAYS `session(action="capture_plan")` + `memory(action="create_task")`. NO markdown files.
- **Repo ↔ ContextStream sync:** Use MCP **`project(action="index")`** or **`project(action="ingest_local", path="…")`** — the server-side ingest/index task. Prefer **`npm run contextstream:ingest[:force]`** for CLI ingest with `.env` via envmcp. Do **not** sync via ad-hoc HTTP clients or committed JSON payload dumps. After a sync, log with **`session(action="capture", event_type="operation", …)`**.
- **Optional Memory mirror:** `memory(create_doc|update_doc)` only through MCP in Cursor when you intentionally duplicate a spec in Memory — not via external API clients.
- **Todos:** `memory(action="create_todo")`.
- **Memory/Notes:** `session(action="capture", event_type="decision|note|lesson")`.

## Maintenance
- **Long Sessions:** `session(action="compress")` after 30+ turns or milestones.
- **Milestones:** `session(action="capture", event_type="operation")` + `reminder(action="create")`.
