# Local observability (Grafana / Loki / Prometheus)

Quick start for plugin logs in local Grafana/Loki, **optional OTLP metrics** (OpenTelemetry Collector → Prometheus), and the **loki-gateway** push endpoint. Canonical log schema and events: **docs/GRAFANA-LOGGING.md**. Routing rationale (Loki vs metrics): **docs/DATA-ROUTING-OBSERVABILITY.md**.

---

## Canonical path: metrics (local)

**Chosen topology:** SimHub plugin → **OTLP** (gRPC default on port **4317**, or HTTP/protobuf on **4318**) → **OpenTelemetry Collector** (`otel-collector` service) → **Prometheus text** on **:8889** → **Prometheus** scrapes the collector → **Grafana** datasource `prometheus_local` (PromQL).

- **Why not `/metrics` inside the plugin:** SimHub targets .NET Framework 4.8; exposing a pull endpoint without **HttpListener** (admin/port issues) or a separate process is awkward. OTLP to a localhost collector matches **docs/DATA-ROUTING-OBSERVABILITY.md** and keeps a single happy path for local dev.
- **Grafana → Prometheus URL:** use the Docker service name **`http://prometheus:9090`** in provisioning (not `localhost`), because Grafana runs inside the compose network.
- **Plugin → collector URL:** use **`http://127.0.0.1:4317`** (or **4318** with `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`) from the Windows host so SimHub resolves IPv4 reliably.

---

## Quick start: plugin logs into Grafana

1. **Create Loki storage path** — Default in `observability/local/docker-compose.yml` is `S:\sim-steward-grafana-storage\`. Create it or set `GRAFANA_STORAGE_PATH` in `observability/local/.env.observability.local`.

2. **Start the stack** (repo root):

   ```powershell
   npm run obs:up
   ```

   Or copy `observability/local/.env.observability.example` → `.env.observability.local`, set passwords/tokens, then `npm run obs:up:env`. Check: `npm run obs:ps`.

3. **Configure the plugin** — SimHub does not load `.env` by default. Recommended: `.\scripts\run-simhub-local-observability.ps1` (sets `SIMSTEWARD_LOKI_URL=http://localhost:3100`, `SIMSTEWARD_LOG_ENV=local`, and OTLP for metrics — see script). Or set those in Windows user env and restart SimHub. See `.env.example` “Local Loki” and “OTLP / Prometheus (local metrics)” blocks.

4. **Grafana** — http://localhost:3000 → Explore → Loki → `{app="sim-steward", env="local"}`. Provisioned dashboard **Sim Steward — Deploy health** (`simsteward-deploy-health`) correlates `deploy.ps1` markers (`event=deploy_marker`) with plugin bring-up and errors — set `SIMSTEWARD_LOKI_URL` before deploy so markers appear.

5. **Metrics (optional)** — With the stack up, set **`OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317`** (or use `SIMSTEWARD_OTLP_ENDPOINT`) before starting SimHub. After the plugin loads, Explore → **Prometheus Local** → e.g. `simsteward_process_cpu_percent` or `up{job="otel-collector"}`. Smoke: `npm run obs:poll:prometheus` or `.\scripts\poll-prometheus.ps1`.

6. **Generate traffic** — Use SimHub + web dashboard; confirm logs in **Explore** with `{app="sim-steward", env="local"}` (no repo-provisioned Grafana dashboards until you add JSON under `observability/local/grafana/provisioning/dashboards/`).

**Storage override:** Set `GRAFANA_STORAGE_PATH` in `.env.observability.local`; compose uses `${GRAFANA_STORAGE_PATH:-S:/sim-steward-grafana-storage}`.

**Terminal tail:** `npm run obs:poll` (direct Loki :3100) or `npm run obs:poll:grafana` / `.\scripts\poll-loki.ps1 -ViaGrafana` using **GRAFANA_API_TOKEN** (or admin user/password) in repo `.env` — same path Grafana Explore uses (`loki_local` datasource). **Prometheus:** `npm run obs:poll:prometheus` / `.\scripts\poll-prometheus.ps1`.

---

## Housekeeping: wipe dashboards’ data (local)

To **clear Loki chunks/WAL**, optional **Prometheus TSDB**, and optional Grafana bind-mount state **without** changing compose, `loki-config.yml`, datasource provisioning, `LOKI_PUSH_TOKEN`, or `SIMSTEWARD_LOKI_*`:

1. From repo root, run **`npm run obs:wipe -- -Force`** (clears the `loki` and **`prometheus`** subdirectories under `GRAFANA_STORAGE_PATH`).
2. Optional flags: **`-Grafana`** (wipes `grafana.db`; re-run `scripts/grafana-bootstrap.ps1` if you use `GRAFANA_API_TOKEN`), **`-SampleLogs`** (clears `observability/local/sample-logs/*` files), or **`-All`** for both.

Equivalent: `.\scripts\obs-wipe-local-data.ps1 -Force` (same switches).

**Grafana Cloud** (delete dashboards and old log lines without rotating Loki credentials): see **docs/GRAFANA-LOGGING.md** § Housekeeping (Grafana Cloud).

---

## Loki gateway (token-protected push)

The repo stack includes **Grafana**, **Loki**, and **loki-gateway** (nginx). The plugin writes **`plugin-structured.jsonl`** and **POSTs** batches to **`SIMSTEWARD_LOKI_URL`** (no separate log agent). For this compose, set that URL to `http://localhost:3100` (Loki) or `http://localhost:3500` (gateway) and use `Authorization: Bearer <LOKI_PUSH_TOKEN>` when using the gateway — see **docs/GRAFANA-LOGGING.md**.

| Service | URL |
|---------|-----|
| Grafana | http://localhost:3000 |
| Loki (query / direct API) | http://localhost:3100 |
| loki-gateway (push) | http://localhost:3500 |
| OpenTelemetry Collector (OTLP gRPC) | `http://127.0.0.1:4317` (host → container) |
| OpenTelemetry Collector (OTLP HTTP) | `http://127.0.0.1:4318` |
| Collector Prometheus exporter (host curl / debug) | `http://127.0.0.1:18889/metrics` (mapped from container **8889**; Prometheus scrapes `otel-collector:8889` inside compose) |
| Prometheus (UI / API) | http://localhost:9090 |
| Collector health_check | http://127.0.0.1:13133 |

Files under `observability/local/`. Security: `LOKI_PUSH_TOKEN` required for `POST /loki/api/v1/push` on the gateway; gateway denies other routes.

**Setup:** Copy `observability/local/.env.observability.example` → `.env.observability.local`, set `LOKI_PUSH_TOKEN`, then:

`docker compose --env-file .env.observability.local -f observability/local/docker-compose.yml up -d`

**Validate:** Grafana datasource `loki_local`; LogQL `{app="sim-steward",env="local"}` once the plugin is pushing to Loki (or your configured `SIMSTEWARD_LOKI_URL`). MCP: `list_datasources`, `query_loki_logs`.

**Troubleshooting:** Token format `Bearer <token>`; ensure `plugin-structured.jsonl` is actually ingested (see **docs/TROUBLESHOOTING.md** §8).

### Port collisions (Docker bind errors)

The stack publishes these **host** ports together; any other process (or second compose project) using the same port will prevent `docker compose up`:

| Host port | Service |
|-----------|---------|
| 3000 | Grafana |
| 3100 | Loki |
| 3500 | loki-gateway |
| 4317, 4318 | OpenTelemetry Collector (OTLP) |
| 8080 | data-api |
| 9090 | Prometheus |
| 13133 | Collector `health_check` |
| 18889 | Collector Prometheus exporter (host; container listens on 8889) |

**SimHub** (separate from Docker) commonly uses **8888** (HTTP) and **19847** (Sim Steward WebSocket default). Those can collide with unrelated tools, not usually with this compose file.

**Audit script:** from repo root run `pwsh -NoProfile -File scripts/check-obs-ports.ps1` to see what is already listening on these ports (and owning process name).

**Typical conflicts:** **3000** (other Grafana, React dev server), **8080** (many dev backends), **9090** (another Prometheus), **4317/4318** (another OTel collector or agent). **8889:** On some setups **SimHub (`SimHubWPF.exe`)** also listens on **8889** alongside **8888** — that blocks mapping collector **8889** to the host, which is why compose publishes **`18889:8889`** (Prometheus still scrapes `otel-collector:8889` inside Docker).

### Metrics / OTLP troubleshooting

- **`up{job="otel-collector"} == 0`** — Prometheus cannot reach the collector on `otel-collector:8889` (compose network). Confirm `otel-collector` is running: `npm run obs:ps`.
- **No `simsteward_*` series** — OTLP is off until **`OTEL_EXPORTER_OTLP_ENDPOINT`** or **`SIMSTEWARD_OTLP_ENDPOINT`** is set **before** SimHub starts. Use **`http://127.0.0.1:4317`** for gRPC; for port **4318** set **`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`**.
- **Connection refused on 4317** — Collector not started or ports not published; run `npm run obs:up` from repo root.
- **Grafana Prometheus query errors** — Datasource must be **`http://prometheus:9090`** (container DNS), not `localhost:9090`.
- **Loki remains authoritative** for `host_resource_sample` until you rely on Prom-only SLOs; metrics duplicate CPU/working set at OTLP export cadence.

---

## See also

- **docs/GRAFANA-LOGGING.md** — Labels, events, LogQL, housekeeping.
- **docs/observability-scaling.md** — Many users / large grids.
- **docs/observability-testing.md** — Harness and Explore validation.
