# Populate ContextStream Plans and Documents

**Invoking ContextStream MCP is required.** Ensure the ContextStream MCP server is enabled in Cursor (see `.cursor/mcp.json`) and that it appears in the session's available MCP list. **If ContextStream is configured but not in the session's available MCP list,** tell the user to enable the server in Cursor (Settings → MCP), restart Cursor if needed, and retry in a new session. On first turn, invoke it with `init(...)` then `context(...)` per `.cursor/rules/ContextStreamOveruse.mdc` and the ContextStream skill.

If the ContextStream web app shows **empty Plans** or **0 docs** in Documents, run the steps below in a Cursor session where **ContextStream MCP is enabled and invoked**.

## 1. Populate the Documents tab

The Documents tab is filled by **project indexing**, not by `create_doc`.

- Run: `project(action="index")`  
  Optionally first: `project(action="ingest_local", paths=["docs"])`
- **Verify:** Call `project(action="index_status")` and `project(action="files")`; require a non-zero file count (or index complete). If verification fails, re-run index and re-check.
- Refresh the ContextStream web app **Documents** section.

## 2. Backfill the Plans tab

**Backfill only includes plan files under the repo `.cursor/plans/*.md`.** Plans created by CreatePlan in the user's global plan store are not backfilled unless saved into the repo's `.cursor/plans/`. To backfill the current plan, save it to the repo's `.cursor/plans/` (e.g. copy or export), then run the backfill steps below.

Plans in the web app come from `session(action="capture_plan", ...)`. Local files in `.cursor/plans/` are not synced automatically.

- For each plan file in the **repo** `.cursor/plans/*.md`:
  - Read the file and derive a **title** (e.g. first `#` heading or filename) and **steps** (from the Steps table or task list).
  - Call `session(action="capture_plan", title="...", steps=[...])` with a string array of step descriptions.
  - For each main step, call `memory(action="create_task", title="...", plan_id="...")` using the `plan_id` returned from `capture_plan` if the MCP returns it.
- Dedupe by plan name if the same plan appears under different paths (e.g. duplicate entries for the same file).
- **Verify:** Call `session(action="list_plans")`; require at least one plan. If verification fails, re-run backfill and re-check.

**Progressive mode:** If ContextStream MCP is available but `project`, `session`, or `memory` tools are not exposed, try disabling progressive mode (e.g. set `CONTEXTSTREAM_PROGRESSIVE_MODE=false` in the MCP env or use a session with full toolset). Optionally call `help(action="tools")` to confirm the required tools exist before running these steps.

## 3. Populate the Diagrams tab

The **Diagrams** tab at [https://contextstream.io/dashboard/diagrams](https://contextstream.io/dashboard/diagrams) is populated by **diagram entities**, not by project indexing or `create_doc`.

- Use `memory(action="create_diagram", title="...", content="<mermaid or diagram spec>", diagram_type="...")` to create diagrams. **content** should be the diagram specification only (e.g. raw Mermaid, without fence markers). **diagram_type** must be one of: `flowchart`, `sequence`, `class`, `er`, `gantt`, `mindmap`, `pie`, `other`.
- **Verify:** Call `memory(action="list_diagrams")` and refresh the ContextStream web app **Dashboard → Diagrams** to confirm diagrams appear.

To reuse diagrams from the repo, extract Mermaid from markdown (e.g. from `docs/SESSION-DATA-AVAILABILITY.md` or `docs/INTERFACE.md`) and pass that string as `content` with the appropriate `diagram_type` (e.g. `flowchart` or `sequence`).

## 4. Ongoing: keep Plans in sync

When using the ContextStream MCP, `.cursor/skills/contextstream/SKILL.md` and `.cursor/rules/plan-rebase-on-code-changes.mdc` describe syncing revised plans via `session(action="capture_plan", ...)` and `memory(action="create_task", ...)` where applicable.

## Optional: Capture implementation scrutiny (when MCP available)

After populating Plans and Documents, in the same session you can capture the ContextStream implementation scrutiny (gaps G1–G6 and solutions S1–S6) so future `context()` or `get_lessons` surfaces it: run `session(action="capture", event_type="decision", title="ContextStream population implementation scrutiny", content="<summary of gaps and solutions>")` and optionally `session(action="capture_lesson", ...)` with prevention "Ensure ContextStream MCP is enabled and in the session list before running population; verify with project(index_status) and session(list_plans)."

## Project merge: "plugin" → "simhub-plugin"

The ContextStream project named **"plugin"** was merged into **simhub-plugin** (this repo). Migration runbook: [CONTEXTSTREAM-MIGRATION.md](CONTEXTSTREAM-MIGRATION.md). **Deleting the "plugin" project** after migration is done manually in the [ContextStream web app](https://contextstream.io) (project/workspace settings); the MCP does not expose a delete-project action.

## Reference

Full tool reference and one-time checklist: `.cursor/skills/contextstream/SKILL.md` (section “Visibility in ContextStream web platform” and “One-time: Populate Plans and Documents”).
