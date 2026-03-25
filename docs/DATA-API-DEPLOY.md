# Data API: local vs production

The plugin can POST session summaries (sessions, drivers, incidents) to a **Data API** after capture. You choose one endpoint via the plugin settings **Data API** field.

| Environment | Endpoint | Backend |
|-------------|----------|---------|
| **Local dev** | `http://localhost:8080` | Local Flask + SQLite (`observability/local/data-api/`) |
| **Production** | Your Cloudflare Worker URL | Cloudflare Worker + D1 |

Same contract both sides: `POST /session-complete` with `SessionSummary` JSON. Deterministic incident IDs make retries and backfill safe.

---

## Local (Flask + SQLite)

1. Run the data-api container (or `python app.py` from `observability/local/data-api/`).
2. In the plugin settings, set **Data API** to `http://localhost:8080`.
3. After session capture, the plugin POSTs to `http://localhost:8080/session-complete`.

---

## Production (Cloudflare Worker + D1)

### Deploy steps

1. **Prereqs:** Node.js, [Wrangler](https://developers.cloudflare.com/workers/wrangler/install/) (`pnpm add -g wrangler` or `pnpx wrangler`), Cloudflare account.

2. **Create the D1 database**
   ```bash
   cd worker
   npx wrangler d1 create simsteward-db
   ```
   Copy the printed `database_id`.

3. **Wire the database into the Worker**  
   Edit `worker/wrangler.toml` and set `database_id` under `[[d1_databases]]` to the value from step 2 (replace `REPLACE_AFTER_D1_CREATE`).

4. **Apply the schema**
   ```bash
   npx wrangler d1 execute simsteward-db --remote --file=./schema.sql
   ```
   For local D1 (e.g. tests): use `--local` instead of `--remote`.

5. **Deploy the Worker**
   ```bash
   npx wrangler deploy
   ```
   Note the deployed URL (e.g. `https://simsteward-data.<account>.workers.dev`).

6. **Configure the plugin**  
   In SimHub → Sim Steward settings, set **Data API** to that Worker URL (no trailing slash). The plugin will POST to `{url}/session-complete` after each session capture.

### Worker routes

- `GET /health` — returns `{"status":"ok"}`.
- `POST /session-complete` — body: `SessionSummary` JSON (camelCase); same as local data-api. Returns `200` with `{"ok":true,"sub_session_id":<id>}` or `400`/`500` with `{"error":"..."}`.

### Schema

Schema lives in `worker/schema.sql` (drivers, sessions, incidents, incident_captures). It matches the local data-api and the deterministic data point plan; incidents include optional `fingerprint_version` (1 = deterministic).
