# Local observability (Grafana / Loki)

Quick start for plugin logs in local Grafana, plus optional **file-tail + gateway** topology. Canonical schema and events: **docs/GRAFANA-LOGGING.md**.

---

## Quick start: plugin logs into Grafana

1. **Create Loki storage path** — Default in `observability/local/docker-compose.yml` is `S:\sim-steward-grafana-storage\`. Create it or set `GRAFANA_STORAGE_PATH` in `observability/local/.env.observability.local`.

2. **Start the stack** (repo root):

   ```powershell
   npm run obs:up
   ```

   Or copy `observability/local/.env.observability.example` → `.env.observability.local`, set passwords/tokens, then `npm run obs:up:env`. Check: `npm run obs:ps`.

3. **Configure the plugin** — SimHub does not load `.env` by default. Recommended: `.\scripts\run-simhub-local-observability.ps1` (sets `SIMSTEWARD_LOKI_URL=http://localhost:3100`, `SIMSTEWARD_LOG_ENV=local`). Or set those in Windows user env and restart SimHub. See `.env.example` “Local Loki” block.

4. **Grafana** — http://localhost:3000 → Explore → Loki → `{app="sim-steward", env="local"}`.

5. **Generate traffic** — Use SimHub + web dashboard; confirm logs in **Explore** with `{app="sim-steward", env="local"}` (no repo-provisioned Grafana dashboards until you add JSON under `observability/local/grafana/provisioning/dashboards/`).

**Storage override:** Set `GRAFANA_STORAGE_PATH` in `.env.observability.local`; compose uses `${GRAFANA_STORAGE_PATH:-S:/sim-steward-grafana-storage}`.

**Terminal tail:** `npm run obs:poll` or `.\scripts\poll-loki.ps1`.

---

## Housekeeping: wipe dashboards’ data (local)

To **clear Loki chunks/WAL** and optional Alloy/Grafana bind-mount state **without** changing compose, `loki-config.yml`, datasource provisioning, `LOKI_PUSH_TOKEN`, or `SIMSTEWARD_LOKI_*`:

1. From repo root, run **`npm run obs:wipe -- -Force`** (always clears the `loki` subdirectory under `GRAFANA_STORAGE_PATH`).
2. Optional flags: **`-Alloy`** (reset file-tail positions), **`-Grafana`** (wipes `grafana.db`; re-run `scripts/grafana-bootstrap.ps1` if you use `GRAFANA_API_TOKEN`), **`-SampleLogs`** (clears `observability/local/sample-logs/*` files), or **`-All`** for all of the above.

Equivalent: `.\scripts\obs-wipe-local-data.ps1 -Force` (same switches).

**Grafana Cloud** (delete dashboards and old log lines without rotating Loki credentials): see **docs/GRAFANA-LOGGING.md** § Housekeeping (Grafana Cloud).

---

## File-tail + gateway topology (Alloy → gateway → Loki)

For **file-based** push with a token-protected write gateway (not plugin HTTP). Plugin → `plugin-structured.jsonl` → Alloy → `loki-gateway` → Loki. **Direct plugin push** to Grafana Cloud/local Loki is documented in **docs/GRAFANA-LOGGING.md**.

| Service | URL |
|---------|-----|
| Grafana | http://localhost:3000 |
| Loki (internal) | http://localhost:3100 |
| loki-gateway (push) | http://localhost:3500 |

Files under `observability/local/`. Security: `LOKI_PUSH_TOKEN` required for `POST /loki/api/v1/push`; gateway denies other routes.

**Setup:** Copy `observability/local/.env.observability.example` → `.env.observability.local`, set `LOKI_PUSH_TOKEN`, then:

`docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml up -d`

Alloy tails `observability/local/sample-logs/*.log` (or mount plugin data — set `SIMSTEWARD_DATA_PATH` so Alloy sees `PluginsData\SimSteward\plugin-structured.jsonl`). Push: `Authorization: Bearer <token>` to gateway.

**Validate:** Grafana datasource `loki_local`; LogQL `{app="sim-steward",env="local"}`. MCP: `list_datasources`, `query_loki_logs`.

**Troubleshooting:** Alloy logs for auth; token format `Bearer <token>`; plugin logs require correct `SIMSTEWARD_DATA_PATH` mount (see **docs/TROUBLESHOOTING.md** §8).

---

## See also

- **docs/GRAFANA-LOGGING.md** — Labels, events, LogQL, housekeeping.
- **docs/observability-scaling.md** — Many users / large grids.
- **docs/observability-testing.md** — Harness and Explore validation.
