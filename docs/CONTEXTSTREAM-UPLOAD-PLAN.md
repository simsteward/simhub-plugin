# ContextStream Knowledge Base Upload

Paste this into Cursor. It will read each doc file and create a ContextStream doc for each one.

---

Read each file listed below and call `mcp__contextstream__memory(action="create_doc", ...)` for it. Use the full file content as the `content` field. Do all **16** docs. Do not skip any.

## Docs to create

| File | title | doc_type |
|------|-------|----------|
| `docs/ARCHITECTURE.md` | `Sim Steward — Architecture and Data Structures` | `spec` |
| `docs/PRODUCT-FLOW.md` | `Sim Steward — Product Flow` | `spec` |
| `docs/USER-FLOWS.md` | `Sim Steward — User Flows` | `spec` |
| `docs/USER-FEATURES-PM.md` | `Sim Steward — User Features (PM)` | `spec` |
| `docs/GRAFANA-LOGGING.md` | `Grafana Loki Structured Logging` | `spec` |
| `docs/DATA-ROUTING-OBSERVABILITY.md` | `Sim Steward — Data Routing (OTel / Loki / Prometheus)` | `spec` |
| `docs/IRACING-TELEMETRY.md` | `iRacing Telemetry — SDK Variable Reference` | `spec` |
| `docs/RULES-ActionCoverage.md` | `Action Coverage — 100% Log Rule` | `spec` |
| `docs/DATA-API-DEPLOY.md` | `Data API — Local vs Production` | `spec` |
| `docs/observability-local.md` | `Observability — Local Stack` | `spec` |
| `docs/observability-scaling.md` | `Observability — Scaling` | `spec` |
| `docs/observability-testing.md` | `Observability — Testing` | `spec` |
| `docs/TROUBLESHOOTING.md` | `Troubleshooting` | `spec` |
| `docs/REDEPLOY-CONCEPT.md` | `Redeploy Concept` | `spec` |
| `docs/RULES-MinimalOutput.md` | `Rules — Minimal Output` | `spec` |
| `README.md` | `Sim Steward Plugin — README` | `general` |

## Call format for each

```
mcp__contextstream__memory(action="create_doc", title="<title>", content="<full file content>", doc_type="<doc_type>")
```

## Notes

- After you change observability or architecture docs in this repo, **re-sync ContextStream**: use `memory(action="get_doc", doc_id="…")` or title query to find the doc, then `memory(action="update_doc", doc_id="…", content="…")` with the current file body so CS matches the repo (no separate per-user log shipper; Loki push is in-process per `SIMSTEWARD_LOKI_URL` where documented).
- **Large specs:** `docs/GRAFANA-LOGGING.md` (~30k+ chars) may exceed a single Composer MCP payload. From repo root, build a JSON file for the MCP **memory** tool: `node -e "const fs=require('fs'); const o={action:'update_doc',doc_id:'58a20aaf-bdde-4318-88f7-1ec8ec44377b',content:fs.readFileSync('docs/GRAFANA-LOGGING.md','utf8')}; fs.writeFileSync('grafana-args.json', JSON.stringify(o));"` — open `grafana-args.json`, copy the object into the tool arguments (or paste `action` / `doc_id` / `content` manually). Do not commit `grafana-args.json`. KB doc `58a20aaf-…` may hold a short summary between full mirrors; duplicate Loki docs (e.g. “Loki logging schema and event taxonomy”) need `get_doc` for their UUIDs.
- `docs/ARCHITECTURE.md` contains class, ER, and sequence diagrams — create it first.
- `docs/PRODUCT-FLOW.md`, `docs/USER-FLOWS.md`, and `docs/USER-FEATURES-PM.md` have mermaid flowcharts embedded as fenced code blocks — include them as-is.
- Do NOT include: `docs/README.md` (index only), `CLAUDE.md`, `.claude/CLAUDE.md`, node_modules files, or `.cursor/skills/` files.
- If a doc already exists in ContextStream with the same title, overwrite/update it.
