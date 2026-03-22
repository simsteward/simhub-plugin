# Sim Steward documentation — start here

**Agents:** Read only the tier that matches the task. Do not glob all of `docs/` unless asked.

Editing files outside the **SimHub rule doc allowlist** does not attach the full SimHub plugin rule pack (see `.cursor/rules/SimHub.mdc` globs).

---

## Tier A — canonical (most plugin/dashboard work)

| Doc | Use when |
|-----|----------|
| [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) | Loki labels, events, LogQL, housekeeping |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Runtime issues, logs, deploy |

---

## Tier A+ — architecture (data structures + flows)

| Doc | Use when |
|-----|----------|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Class diagrams (PluginSnapshot, LogEntry, WS messages), ER diagram (data API), sequence diagrams (action dispatch, incident pipeline) |

---

## Tier B — topic hubs (open when needed)

| Doc | Use when |
|-----|----------|
| [USER-FEATURES-PM.md](USER-FEATURES-PM.md) | PM-style user features (12 flows), connections, vision vs shipped vs [PRODUCT-FLOW.md](PRODUCT-FLOW.md) |
| [USER-FLOWS.md](USER-FLOWS.md) | Step-by-step user journeys through today's UI (mermaid diagrams); PM issues and flow gaps |
| [observability-local.md](observability-local.md) | Local Grafana/Loki stack, npm scripts, loki-gateway |
| [observability-scaling.md](observability-scaling.md) | Many users, large grids, Loki cardinality |
| [DATA-ROUTING-OBSERVABILITY.md](DATA-ROUTING-OBSERVABILITY.md) | OTel vs Loki vs Prometheus, ~1k-user sizing, car telemetry taxonomy |
| [observability-testing.md](observability-testing.md) | Harness, AssertLokiQueries, Explore validation |
| [DATA-API-DEPLOY.md](DATA-API-DEPLOY.md) | Data API env (local vs prod) |
| [REDEPLOY-CONCEPT.md](REDEPLOY-CONCEPT.md) | Redeploy mental model |

---

## Not for code agents

- **docs/marketing/** — Human-facing pages; excluded from default search via `.cursorignore`.

---

## Elsewhere

- **docs/RULES-MinimalOutput.md** — Referenced from `.cursor/rules/00_MinimalOutput.mdc` (global).
- **Observability code:** `observability/local/`, `tests/observability/`, `harness/`.
