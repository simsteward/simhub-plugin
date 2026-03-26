# ContextStream Support Case: Web UI Does Not Display Artifacts

**Date:** 2026-03-25
**Account:** billing@simsteward.com (Pro plan)
**Workspace:** sim-steward (f5c5b873-acfb-47ec-b93b-4acabfa78a8b)
**MCP Server Version:** 0.1.78
**Client:** Claude Code (claude)

---

## Summary

Artifacts created via the MCP server (docs, tasks, plans, todos, diagrams, events, memory nodes, decisions) are fully accessible via MCP tools and the REST API, but are **invisible in the ContextStream web dashboard** at contextstream.io. Only the **Skills** page displays data.

---

## Bug 1: Dashboard pages never fetch artifact data

**Severity:** High — the entire web UI is non-functional for artifact management

**Steps to Reproduce:**
1. Log in to contextstream.io
2. Navigate to any dashboard page: /dashboard/docs, /dashboard/todos, /dashboard/plans, /dashboard/diagrams, /dashboard/notes, /dashboard/lessons, /dashboard/preferences, /dashboard/insights
3. Observe: page scaffolding renders (background layout components load) but no artifact data appears

**Expected:** Pages display artifacts that exist (e.g., 9 docs, 10 tasks, 6 plans, 6 todos, 3 diagrams)
**Actual:** All pages render empty — zero API calls to fetch artifact data are fired by the browser

**Evidence (from HAR capture, 225 requests analyzed):**

The browser makes auth/billing/workspace API calls successfully:
```
200  GET /api/v1/auth/me
200  GET /api/v1/credits/balance
200  GET /api/v1/workspaces
200  GET /api/v1/workspaces/{id}/integrations
200  GET /api/v1/reminders/active
200  GET /api/v1/notifications
```

But **never calls any artifact endpoint**:
```
NEVER CALLED  /api/v1/docs      ← returns 9 docs via curl
NEVER CALLED  /api/v1/tasks     ← returns 10 tasks via curl
NEVER CALLED  /api/v1/plans     ← returns 9 plans via curl
NEVER CALLED  /api/v1/diagrams  ← returns 5 diagrams via curl
NEVER CALLED  /api/v1/todos     ← returns 6 todos via curl
NEVER CALLED  /api/v1/skills    ← returns skills via curl (only type that works in UI)
```

**RSC response pattern (identical for ALL dashboard pages):**
1. First RSC payload: ~400 bytes — layout component reference (e.g., `DocsBackground`, `TodosBackground`)
2. Second RSC payload: ~1500 bytes — `ClientPageRoot` + JS chunk imports
3. **No third RSC with data payload** — the data-fetching step never happens

**Root cause:** The Next.js client components render scaffolding but the data-fetching logic is not wired to the REST API. This is a frontend implementation gap, not a data/auth/CORS issue.

---

## Bug 2: Several REST API endpoints return 404

**Severity:** Medium — MCP server can access this data but the public REST API cannot

These endpoints return 404 even with valid authentication and workspace_id:
```
404  GET /api/v1/events
404  GET /api/v1/nodes
404  GET /api/v1/decisions
404  GET /api/v1/memory
404  GET /api/v1/sessions
```

Meanwhile the MCP server accesses this same data successfully:
```
mcp__contextstream__memory(action="list_events")     → 10 events found
mcp__contextstream__memory(action="list_nodes")       → 20 memory nodes found
mcp__contextstream__memory(action="decisions")        → 7 decisions found
```

This suggests these artifact types are only available via the MCP stdio binary's internal/private API and have no public REST endpoints yet.

---

## Bug 3: Notes on tasks/plans query param requirements

These endpoints require `workspace_id` as a query parameter:
```
400  GET /api/v1/tasks                                    ← missing param
200  GET /api/v1/tasks?workspace_id=f5c5b873-...          ← works
400  GET /api/v1/plans                                    ← missing param
200  GET /api/v1/plans?workspace_id=f5c5b873-...          ← works
```

If the web UI ever does wire up data fetching, it will need to include `workspace_id` in the requests for tasks and plans.

---

## Cross-Layer Verification Matrix

| Layer | Method | Docs | Tasks | Plans | Todos | Diagrams | Events | Nodes | Decisions | Skills |
|-------|--------|------|-------|-------|-------|----------|--------|-------|-----------|--------|
| MCP (stdio) | `mcp__contextstream__memory` | 9 | 10 | 6 | 6 | 3 | 10 | 20 | 7 | Yes |
| REST API (curl) | `GET /api/v1/{type}` | 9 | 22 | 9 | 6 | 5 | 404 | 404 | 404 | Yes |
| Web UI (browser) | Dashboard pages | Empty | Empty | Empty | Empty | Empty | Empty | Empty | Empty | **Works** |

---

## Environment Details

- CORS: Wide open (`access-control-allow-origin: *`) — not a browser issue
- Auth: Valid session — `/api/v1/auth/me` returns user profile
- No workspace/project scoping mismatch — identical results regardless of query path
- HAR file available on request (13.7MB, 225 requests captured during debugging session)

---

## Requested Resolution

1. **Wire up dashboard pages** to call existing REST API endpoints for docs, tasks, plans, todos, diagrams
2. **Add missing REST endpoints** for events, nodes, decisions (currently only accessible via MCP stdio)
3. **Ensure workspace_id** is passed in requests for tasks/plans endpoints

## Workaround

Currently using MCP tools as the primary interface for all artifact management. This works but means the web dashboard provides no visibility into workspace data.
