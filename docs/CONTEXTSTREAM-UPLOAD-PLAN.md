# ContextStream Knowledge Base Upload

Paste this into Cursor. It will read each doc file and create a ContextStream doc for each one.

---

Read each file listed below and call `mcp__contextstream__memory(action="create_doc", ...)` for it. Use the full file content as the `content` field. Do all 15 docs. Do not skip any.

## Docs to create

| File | title | doc_type |
|------|-------|----------|
| `docs/ARCHITECTURE.md` | `Sim Steward — Architecture and Data Structures` | `spec` |
| `docs/PRODUCT-FLOW.md` | `Sim Steward — Product Flow` | `spec` |
| `docs/USER-FLOWS.md` | `Sim Steward — User Flows` | `spec` |
| `docs/USER-FEATURES-PM.md` | `Sim Steward — User Features (PM)` | `spec` |
| `docs/GRAFANA-LOGGING.md` | `Grafana Loki Structured Logging` | `spec` |
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

- `docs/ARCHITECTURE.md` contains class, ER, and sequence diagrams — create it first.
- `docs/PRODUCT-FLOW.md`, `docs/USER-FLOWS.md`, and `docs/USER-FEATURES-PM.md` have mermaid flowcharts embedded as fenced code blocks — include them as-is.
- Do NOT include: `docs/README.md` (index only), `CLAUDE.md`, `.claude/CLAUDE.md`, node_modules files, or `.cursor/skills/` files.
- If a doc already exists in ContextStream with the same title, overwrite/update it.
