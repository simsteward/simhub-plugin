# Observability testing and dashboard validation

Grafana/Loki **test harness** (CI/local) plus **manual dashboard validation** in Explore.

---

## Test harness (emit + assert)

### Overview

1. Emit structured events (`action_result`, `incident_detected`, `session_digest`) with `testing="true"` and `test_tag` (e.g. `grafana-harness`).
2. Assert via Loki HTTP API or MCP that expected fields exist.

### Full test (local stack + harness)

```powershell
.\tests\observability\run_grafana_tests.ps1
```

Requires Docker, .NET SDK, storage path (default `S:\sim-steward-grafana-storage` or set `GRAFANA_STORAGE_PATH`).

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

After harness run, query e.g. `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`. Expect ≥2 `action_result`, ≥1 `incident_detected`, ≥1 `session_digest` with required fields. See **tests/observability/assert_via_mcp.md**.

### LogQL: test vs production

- Test only: `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"`
- Production only: `{app="sim-steward"} | json | testing != "true"`

### CI

`run_grafana_tests.ps1` is not in `deploy.ps1` by default; add to pipeline if desired.

### Troubleshooting (harness)

| Issue | Action |
|-------|--------|
| Loki not ready | Check port 3100; extend ready wait in script. |
| No lines | Verify `SIMSTEWARD_LOKI_URL` / recent harness run / `TEST_TAG`. |
| Volume errors | Create host paths or adjust compose. |
| Assertion timeout | Re-run harness then assert immediately; Loki indexing delay. |

---

## Dashboard validation (e.g. last 7 days)

Run in **Grafana → Explore → Loki**, range **Last 7 days**.

### Event distribution

```logql
sum by (event) (count_over_time({app="sim-steward"} | json [$__range]))
```

If grouping fails, use `{app="sim-steward"} | json` and group in UI.

### Label check

Query `{app="sim-steward"}` — expect `env`, `component`, `level`. Cloud: match datasource UID to provisioned dashboards (`loki_local` or variable `DS_LOKI`).

### Component breakdown (optional)

```logql
sum by (component) (count_over_time({app="sim-steward"} [$__range]))
```

Expect `simhub-plugin`, `bridge`, `tracker` (and optionally `dashboard`).

### Outcome

Confirm events exist and dashboards in **docs/GRAFANA-LOGGING.md** (provisioned list) show data.

---

## References

- **docs/GRAFANA-LOGGING.md** — Taxonomy, dashboards, LogQL.
- **observability/local/** — Docker Compose and provisioning.
