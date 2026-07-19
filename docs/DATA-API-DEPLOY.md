# Data API: deploy & run

The **Data API** is a Cloudflare Worker that gives incidents, the replay incident index, and session metadata durable off-machine storage and a read-back path. Storage is **hybrid**: Cloudflare **D1** holds queryable relational rows, **R2** holds the full-fidelity incident-index JSON blob, and **KV** holds short-lived device-pairing state. Auth is real JWT (not a static bearer) — see [Auth model](#auth-model). Full design: `docs/superpowers/specs/2026-07-19-cloudflare-incident-storage-design.md`.

> **Supersedes the old contract.** Earlier revisions of this doc described a never-built `/session-complete`-only Worker with a single static `Authorization: Bearer <token>` secret, plus a local Flask + SQLite alternative. Both are gone. The Worker now speaks the route list below under JWT auth, and local development uses `wrangler dev --local` (Miniflare), not Flask.

---

## Auth model

Two deliberately separate systems (see design spec §4):

- **Plugin ↔ Worker (data plane) — our own JWT.** The plugin pairs once via device-approval, then holds a rotating opaque `user_token` (DPAPI-encrypted on disk) which it exchanges at `/auth/token` for a short-lived (15 min) `access_token`. Every data-plane call carries `Authorization: Bearer <access_token>`. The embedded **app token** is only build/version identification (a .NET DLL decompiles trivially — it is *not* the access boundary); the rotating `user_token` is the real boundary.
- **Human pages (`/approve`, `/admin/*`) — Cloudflare Access.** These are fronted by Cloudflare Access (Zero Trust) with its built-in email One-Time-PIN identity provider — an email allowlist, zero custom auth code. The Worker verifies the `Cf-Access-Jwt-Assertion` JWT against Cloudflare's JWKS; it never trusts the `Cf-Access-Authenticated-User-Email` header alone. The API routes stay *outside* Access and keep using our JWT.

---

## Routes

**Data plane** (require `Authorization: Bearer <access_token>`):

| Route | Purpose |
|-------|---------|
| `POST /session-complete` | Upsert session + drivers + incidents into D1 (JWT-gated successor to the old static-bearer version). |
| `POST /incidents/push` | Array of live-detected incident rows, each keyed by the v2 fingerprint. |
| `PUT /incident-index/{subSessionId}` | Full `ReplayIncidentIndexFileRoot` JSON → R2 blob + per-row D1 upserts. |
| `GET /incident-index/{subSessionId}` | Returns the R2 blob verbatim (full fidelity, incl. the `Validation` block). |
| `GET /session/{subSessionId}` | Lightweight D1-only summary. |
| `GET /health` | `{"status":"ok"}`. |

**App auth** (public; `app_token` only until a `user_token` exists):

| Route | Purpose |
|-------|---------|
| `POST /auth/device/start` | → `{device_code, user_code, verification_uri, interval_sec, expires_in_sec}`. |
| `POST /auth/device/poll` | → `{status: pending\|approved\|denied\|expired, user_token?}` (token returned once). |
| `POST /auth/token` | `{app_token, user_token}` → `{access_token, user_token (rotated), expires_in_sec:900}`. Serves first exchange and every refresh. |

**Human-facing** (behind Cloudflare Access, not our JWT):

| Route | Purpose |
|-------|---------|
| `GET /approve` | Device-pairing approval page. |
| `/admin/*` | Subscription/user management UI + API (`POST /admin/users`, `PATCH /admin/subscriptions/{user_id}`). |

Incident row PKs are the deterministic v2 fingerprint, and the D1 upsert is rank-gated (replay-reconciled `source_rank=2` wins over live `source_rank=1`, never regresses), so retries, backfill, and at-least-once delivery from the plugin's outbox are all safe.

---

## Deploy (production)

Initial provisioning is **manual and human-run** — this dev environment deliberately holds no Cloudflare credentials. Use the runbook script `worker/deploy-worker.ps1`, which checks prerequisites and walks the ordered steps below (pass `-DryRun` to print the commands without executing). After the one-time setup in step 5 below, subsequent deploys happen automatically on merge to `main` via `.github/workflows/worker-ci-cd.yml` — the manual `wrangler deploy` in step 5 is still useful for the very first deploy and for ad-hoc pushes, but isn't required for every change once CI/CD is wired up.

1. **Prereqs:** Node.js and Wrangler (`npx wrangler` is fine). Log in once: `wrangler login`. The script aborts with a clear message if `wrangler whoami` shows you are not authenticated.

2. **Provision resources** (each prints an ID to paste into `worker/wrangler.toml`):
   ```bash
   npx wrangler d1 create simsteward-db                 # → database_id
   npx wrangler r2 bucket create simsteward-incident-index
   npx wrangler kv namespace create simsteward-device-codes   # → id
   ```

3. **Apply the schema** to D1:
   ```bash
   npx wrangler d1 execute simsteward-db --remote --file=./schema.sql
   ```

4. **Set the JWT signing secret** (interactive — the script prints the instruction rather than blocking):
   ```bash
   npx wrangler secret put JWT_HMAC_SECRET
   ```

5. **Deploy:**
   ```bash
   npx wrangler deploy
   ```
   Note the deployed hostname.

6. **Cloudflare Access** (dashboard, one-time): create a self-hosted Access application on a custom domain covering `/approve` and `/admin/*`, with an **email OTP** policy allowlisting your address. Keep it per-path so the API routes stay outside Access. Fill `ACCESS_TEAM_DOMAIN` and `ACCESS_AUD` in `worker/wrangler.toml`, then `npx wrangler deploy` again.

7. **Point the plugin at it:** in the plugin's `.env`, set `SIMSTEWARD_CLOUD_API_URL` to the Worker hostname (no trailing slash) and `SIMSTEWARD_CLOUD_APP_TOKEN` to the build's app token. Pair a device via the `/approve` page on first run.

8. **One-time: enable automated deploy-on-merge.** Create a *scoped* Cloudflare API token (Workers Scripts:Edit, D1:Edit, R2:Edit, KV:Edit — not a global API key) at dash.cloudflare.com → My Profile → API Tokens, and add it as the `CLOUDFLARE_API_TOKEN` secret on the GitHub repo (Settings → Secrets and variables → Actions). From then on, a push to `main` touching `worker/**` runs the Worker's test suite and — only if it passes — deploys via `cloudflare/wrangler-action`. This automates step 5 only; steps 2–4 and 6 (provisioning, schema, secrets, Access) are one-time setup this workflow does not repeat, and the real (non-placeholder) resource IDs from step 2 must already be committed in `worker/wrangler.toml`.

`worker/deploy-worker.ps1` echoes this checklist after a deploy.

---

## Local development (`wrangler dev --local`)

The old local Flask + SQLite `data-api` container is **superseded** — do not run it. Local development uses Miniflare, which gives you local D1, R2, and KV with no cloud calls:

```bash
cd worker
npx wrangler dev --local
```

Apply the schema to the local D1 with `--local` instead of `--remote`:

```bash
npx wrangler d1 execute simsteward-db --local --file=./schema.sql
```

Set the plugin's `SIMSTEWARD_CLOUD_API_URL` to the `wrangler dev` URL (mirrors the existing `SIMSTEWARD_LOG_ENV` local/production split) to exercise the full pairing + push flow against the local Worker before touching production. Worker unit tests run under Vitest (`@cloudflare/vitest-pool-workers`), a separate gate from `dotnet test`.
