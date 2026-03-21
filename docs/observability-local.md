# Local observability (Grafana / Loki)

Quick start for plugin logs in local Grafana/Loki and the **loki-gateway** push endpoint. Canonical schema and events: **docs/GRAFANA-LOGGING.md**.

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

To **clear Loki chunks/WAL** and optional Grafana bind-mount state **without** changing compose, `loki-config.yml`, datasource provisioning, `LOKI_PUSH_TOKEN`, or `SIMSTEWARD_LOKI_*`:

1. From repo root, run **`npm run obs:wipe -- -Force`** (always clears the `loki` subdirectory under `GRAFANA_STORAGE_PATH`).
2. Optional flags: **`-Grafana`** (wipes `grafana.db`; re-run `scripts/grafana-bootstrap.ps1` if you use `GRAFANA_API_TOKEN`), **`-SampleLogs`** (clears `observability/local/sample-logs/*` files), or **`-All`** for both.

Equivalent: `.\scripts\obs-wipe-local-data.ps1 -Force` (same switches).

**Grafana Cloud** (delete dashboards and old log lines without rotating Loki credentials): see **docs/GRAFANA-LOGGING.md** § Housekeeping (Grafana Cloud).

---

## Loki gateway (token-protected push)

The repo stack includes **Grafana**, **Loki**, and **loki-gateway** (nginx). The plugin still writes only to **`plugin-structured.jsonl`**; **nothing in this compose tails that file automatically**. You must run your own ingestion (e.g. ship NDJSON lines to Loki, Grafana Cloud agent, or `POST` to the gateway with `Authorization: Bearer <LOKI_PUSH_TOKEN>`). **Direct plugin HTTP** to Grafana Cloud/local Loki is documented in **docs/GRAFANA-LOGGING.md**.

| Service | URL |
|---------|-----|
| Grafana | http://localhost:3000 |
| Loki (query / direct API) | http://localhost:3100 |
| loki-gateway (push) | http://localhost:3500 |

Files under `observability/local/`. Security: `LOKI_PUSH_TOKEN` required for `POST /loki/api/v1/push` on the gateway; gateway denies other routes.

**Setup:** Copy `observability/local/.env.observability.example` → `.env.observability.local`, set `LOKI_PUSH_TOKEN`, then:

`docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml up -d`

**Validate:** Grafana datasource `loki_local`; LogQL `{app="sim-steward",env="local"}` once your shipper is pushing. MCP: `list_datasources`, `query_loki_logs`.

**Troubleshooting:** Token format `Bearer <token>`; ensure `plugin-structured.jsonl` is actually ingested (see **docs/TROUBLESHOOTING.md** §8).

---

## See also

- **docs/GRAFANA-LOGGING.md** — Labels, events, LogQL, housekeeping.
- **docs/observability-scaling.md** — Many users / large grids.
- **docs/observability-testing.md** — Harness and Explore validation.
