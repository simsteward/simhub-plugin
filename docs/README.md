# Sim Steward documentation — start here

**Agents:** Read only the tier that matches the task. Do not glob all of `docs/` unless asked.

Editing files outside the **SimHub rule doc allowlist** does not attach the full SimHub plugin rule pack (see `.cursor/rules/SimHub.mdc` globs).

---

## Tier A — canonical (most plugin/dashboard work)

| Doc | Use when |
|-----|----------|
| [INTERFACE.md](INTERFACE.md) | WebSocket contract, message types, actions |
| [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) | Loki labels, events, LogQL, dashboards |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Runtime issues, logs, deploy |
| [STATE_AND_ROADMAP.md](STATE_AND_ROADMAP.md) | Roadmap, current state, tech debt |

[STATUS.md](STATUS.md) redirects here + [archive/project-status-archived.md](archive/project-status-archived.md).

---

## Tier B — topic hubs (open when needed)

| Doc | Use when |
|-----|----------|
| [observability-local.md](observability-local.md) | Local Grafana/Loki stack, npm scripts, Alloy gateway |
| [observability-scaling.md](observability-scaling.md) | Many users, large grids, Loki cardinality |
| [observability-testing.md](observability-testing.md) | Harness, AssertLokiQueries, Explore validation |
| [replay-workflow.md](replay-workflow.md) | MCP replay capture, checklist, datapoints |
| [reference/README.md](reference/README.md) | Session timing, YAML/results, SDK research |
| [plans/](plans/) | Future: log/event stream UI plans |
| [DATA-API-DEPLOY.md](DATA-API-DEPLOY.md) | Data API env (local vs prod) |
| [REDEPLOY-CONCEPT.md](REDEPLOY-CONCEPT.md) | Redeploy mental model |

**Stubs:** Short filenames like `LOCAL-OBSERVABILITY-QUICKSTART.md` → one-line pointers to Tier B merged docs.

---

## Archive / maintainer (skip unless task says so)

| Path | Contents |
|------|----------|
| [archive/README.md](archive/README.md) | Historical and one-off analysis |
| [archive/dev-tooling/](archive/dev-tooling/) | Cursor, ContextStream, Ollama/MCP host |
| [archive/project-status-archived.md](archive/project-status-archived.md) | Old STATUS narrative |

---

## Not for code agents

- **docs/marketing/** — Human-facing pages; excluded from default search via `.cursorignore`.

---

## Elsewhere

- **docs/RULES-MinimalOutput.md** — Referenced from `.cursor/rules/00_MinimalOutput.mdc` (global).
- **Observability code:** `observability/local/`, `tests/observability/`, `harness/`.
