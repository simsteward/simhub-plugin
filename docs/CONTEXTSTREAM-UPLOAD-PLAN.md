# ContextStream — repo sync (simhub-plugin)

**Canonical sync path:** use the **ContextStream MCP server** project sync — not custom HTTP clients, not shell scripts that call the ContextStream API directly, and not hand-built JSON files to paste into tools.

## How to sync after doc or code changes

1. In Cursor (ContextStream MCP enabled, same auth the server already uses), call:
   - **`project(action="index")`** — uses local ingest when the workspace folder is linked; otherwise triggers project indexing on the service, **or**
   - **`project(action="ingest_local", path="<absolute path to this repo root>")`** — when you need an explicit folder path (optionally `force: true` for a full re-ingest).
2. Confirm status with **`project(action="index_status")`** (and **`project(action="index_history")`** if you need an audit trail).
3. **Log the sync in ContextStream** (so the team sees what happened): **`session(action="capture", event_type="operation", title="ContextStream project index", content="…")`** — include branch, short reason (e.g. “observability docs updated”), and optional commit SHA.

**Do not:** drive sync via one-off REST calls, `node -e` payloads, committed `*-args.json` files, or automation that spawns `contextstream-mcp.exe` with a separate credential context (often 401). CLI ingest in **`docs/TROUBLESHOOTING.md`** remains a **human troubleshooting** fallback when MCP or env is broken — not the default workflow.

## Curated Memory docs (optional)

The **search/index** pipeline is the primary way assistants see repo files. Separate **`memory(action="create_doc" | "update_doc")`** entries are only for intentionally duplicated “spec” articles in Memory; if you use them, do it **through the MCP `memory` tool in Cursor**, not via external API wrappers. Large files (e.g. `docs/GRAFANA-LOGGING.md`) are handled by the **service ingest path** above — do not invent parallel “manual full-body upload” scripts.

## Coverage checklist (verify after index)

These paths should be represented in the indexed project (titles are historical Memory-doc names; index is source of truth for search):

| File | Reference title | doc_type (if mirroring Memory) |
|------|-----------------|--------------------------------|
| `docs/ARCHITECTURE.md` | Sim Steward — Architecture and Data Structures | `spec` |
| `docs/PRODUCT-FLOW.md` | Sim Steward — Product Flow | `spec` |
| `docs/USER-FLOWS.md` | Sim Steward — User Flows | `spec` |
| `docs/USER-FEATURES-PM.md` | Sim Steward — User Features (PM) | `spec` |
| `docs/GRAFANA-LOGGING.md` | Grafana Loki Structured Logging | `spec` |
| `docs/DATA-ROUTING-OBSERVABILITY.md` | Sim Steward — Data Routing (OTel / Loki / Prometheus) | `spec` |
| `docs/IRACING-TELEMETRY.md` | iRacing Telemetry — SDK Variable Reference | `spec` |
| `docs/RULES-ActionCoverage.md` | Action Coverage — 100% Log Rule | `spec` |
| `docs/DATA-API-DEPLOY.md` | Data API — Local vs Production | `spec` |
| `docs/observability-local.md` | Observability — Local Stack | `spec` |
| `docs/observability-scaling.md` | Observability — Scaling | `spec` |
| `docs/observability-testing.md` | Observability — Testing | `spec` |
| `docs/TROUBLESHOOTING.md` | Troubleshooting | `spec` |
| `docs/REDEPLOY-CONCEPT.md` | Redeploy Concept | `spec` |
| `docs/RULES-MinimalOutput.md` | Rules — Minimal Output | `spec` |
| `README.md` | Sim Steward Plugin — README | `general` |

## Exclusions

Do NOT rely on index content for: `docs/README.md` (index only), `CLAUDE.md`, `.claude/CLAUDE.md`, `node_modules`, `.cursor/skills/` (personal/editor guidance, not product docs).
