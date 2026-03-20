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

## Operations
- **Plans:** ALWAYS `session(action="capture_plan")` + `memory(action="create_task")`. NO markdown files.
- **Docs:** Populate via `project(action="index")`.
- **Todos:** `memory(action="create_todo")`.
- **Memory/Notes:** `session(action="capture", event_type="decision|note|lesson")`.

## Maintenance
- **Long Sessions:** `session(action="compress")` after 30+ turns or milestones.
- **Milestones:** `session(action="capture", event_type="operation")` + `reminder(action="create")`.
