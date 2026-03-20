# ContextStream "plugin" → "simhub-plugin" migration

This runbook migrates all plans, tasks, docs, diagrams, and todos from the ContextStream project **"plugin"** into the **simhub-plugin** project (this repo), then documents removal of the "plugin" project.

## Prerequisite: get the "plugin" project ID

The migration requires the **project_id** (UUID) of the "plugin" project in ContextStream. The MCP in this workspace does not expose a "list projects" action.

**How to get the plugin project_id:**

1. Open the [ContextStream web app](https://contextstream.io).
2. Switch to or select the project named **"plugin"**.
3. Open project settings or the URL — the project ID is the UUID (e.g. in the URL path or project details).
4. Copy that UUID; you will pass it as `plugin_project_id` in the steps below.

## Where to run the migration

- Use a **Cursor session** with this repo open (`c:\Users\winth\dev\sim-steward\plugin`) so the default ContextStream project is **simhub-plugin**.
- Ensure **ContextStream MCP** is enabled (Settings → MCP).
- Run `init(folder_path="c:\\Users\\winth\\dev\\sim-steward\\plugin")` so the session is bound to simhub-plugin.

## Migration steps (run in order)

Replace `PLUGIN_PROJECT_ID` with the UUID from the prerequisite.

### 1. Plans and tasks

- List source plans:  
  `session(action="list_plans", project_id="PLUGIN_PROJECT_ID")`
- For each plan:
  - Get full plan:  
    `session(action="get_plan", plan_id="<id>", project_id="PLUGIN_PROJECT_ID", include_tasks=True)`
  - Create in target (no project_id = simhub-plugin):  
    `session(action="capture_plan", title="<title>", steps=[...])`  
    Use the `plan_id` returned in the response for the next step.
  - For each task that belonged to that plan:  
    `memory(action="create_task", title="<title>", plan_id="<new_plan_id>", ...)`  
    with the new plan_id from `capture_plan`.

### 2. Docs

- List:  
  `memory(action="list_docs", project_id="PLUGIN_PROJECT_ID")`
- For each doc:
  - Get:  
    `memory(action="get_doc", doc_id="<id>", project_id="PLUGIN_PROJECT_ID")`
  - Create in target:  
    `memory(action="create_doc", title="<title>", content="<content>", doc_type="<spec|general>")`

### 3. Diagrams

- List:  
  `memory(action="list_diagrams", project_id="PLUGIN_PROJECT_ID")`
- For each:
  - Get:  
    `memory(action="get_diagram", diagram_id="<id>", project_id="PLUGIN_PROJECT_ID")`
  - Create in target:  
    `memory(action="create_diagram", title="<title>", content="<mermaid or diagram spec>", diagram_type="<flowchart|sequence|class|er|gantt|mindmap|pie|other>")`

### 4. Todos

- List:  
  `memory(action="list_todos", project_id="PLUGIN_PROJECT_ID")`
- For each:  
  `memory(action="create_todo", title="<title>", ...)` with the same title/priority/status as needed.

### 5. Events (optional)

- List:  
  `memory(action="list_events", project_id="PLUGIN_PROJECT_ID")`
- For events you want to keep, re-create with:  
  `session(action="capture", event_type="<type>", title="<title>", content="<content>")`

## Verification

In the same session (no project_id so simhub-plugin):

- `session(action="list_plans")`
- `memory(action="list_tasks")`
- `memory(action="list_docs")`
- `memory(action="list_diagrams")`
- `memory(action="list_todos")`

Confirm counts and content match what was migrated. Refresh the ContextStream web app and check the **simhub-plugin** project.

## Delete the "plugin" project (manual)

The ContextStream MCP does not expose a "delete project" action. **You must remove the "plugin" project in the ContextStream web app:**

1. Open [ContextStream](https://contextstream.io).
2. Select the **"plugin"** project.
3. Open project or workspace settings and delete/remove the "plugin" project.

After migration and verification, deleting the "plugin" project avoids duplicate data and keeps a single project (**simhub-plugin**) for this repo.

## Reference

- Plan: ContextStream "plugin" → "simhub-plugin" migration (`.cursor/plans/` or CreatePlan).
- Config: [.contextstream/config.json](../.contextstream/config.json). Set `CONTEXTSTREAM_PROJECT_ID` in `.env` (see `.env.example`).
