# Observability testing and Explore validation

Grafana/Loki **test harness** (CI/local) plus **manual LogQL validation** in Explore.

---

## Test harness (emit + assert)

### Overview

1. Emit structured events (`action_result`, `incident_detected`, `session_digest`) with `testing="true"` and `test_tag` (e.g. `grafana-harness`).
2. Assert via Loki HTTP API or MCP that expected fields exist.

### Harness only (Loki already running)

```powershell
$env:SIMSTEWARD_LOKI_URL = "http://localhost:3100"
$env:SIMSTEWARD_LOG_ENV = "local"
$env:TEST_TAG = "grafana-harness"
dotnet run --project harness\SimSteward.GrafanaTestHarness\SimSteward.GrafanaTestHarness.csproj -- --count 3
```

### Assertion tool only

```powershell
$env:LOKI_QUERY_URL = "http://localhost:3100"
$env:TEST_TAG = "grafana-harness"
dotnet run --project tests\observability\AssertLokiQueries\AssertLokiQueries.csproj
```

### MCP assertions

After harness run, query e.g. `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`. Expect ≥2 `action_result`, ≥1 `incident_detected`, ≥1 `session_digest` with required fields.

### LogQL: test vs production

- Test only: `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`
- Production only: `{app="sim-steward"} | json | testing != "true"`

### CI

Observability harness is not in `deploy.ps1` by default; add harness + Loki stack steps to the pipeline if desired.

### Troubleshooting (harness)

| Issue | Action |
|-------|--------|
| Loki not ready | Check port 3100; extend ready wait in script. |
| No lines | Verify `SIMSTEWARD_LOKI_URL` / recent harness run / `TEST_TAG`. |
| Volume errors | Create host paths or adjust compose. |
| Assertion timeout | Re-run harness then assert immediately; Loki indexing delay. |

---

## Explore validation (e.g. last 7 days)

Run in **Grafana → Explore → Loki**, range **Last 7 days**.

### Event distribution

```logql
sum by (event) (count_over_time({app="sim-steward"} | json [$__range]))
```

If grouping fails, use `{app="sim-steward"} | json` and group in UI.

### Label check

Query `{app="sim-steward"}` — expect `env`, `component`, `level`. Cloud: use your Loki datasource (local provisioning uses UID `loki_local`).

### Component breakdown (optional)

```logql
sum by (component) (count_over_time({app="sim-steward"} [$__range]))
```

Expect `simhub-plugin`, `bridge`, `tracker` (and optionally `dashboard`).

### Outcome

Confirm expected events exist and panels/queries in **docs/GRAFANA-LOGGING.md** (LogQL reference) return data.

---

## References

- **docs/GRAFANA-LOGGING.md** — Taxonomy, LogQL, housekeeping.
- **observability/local/** — Docker Compose and provisioning.
