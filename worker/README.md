# Sim Steward Cloudflare Worker

Backend for the Sim Steward SimHub plugin: device-pairing auth, a rotating-token
data plane, rank-based incident dedup, and verbatim incident-index storage in R2.

Everything here is **fully testable locally** (Miniflare via
`@cloudflare/vitest-pool-workers` + `wrangler dev --local`). No Cloudflare account
is required to develop or run the test suite.

## Layout

| File | Purpose |
|------|---------|
| `src/index.ts` | Router entry (method + pathname dispatch). |
| `src/auth.ts` | Device-pairing + token-exchange flow (`/auth/*`, `/approve`). |
| `src/data.ts` | Data plane (`/session-complete`, `/incidents/push`, `/incident-index/*`, `/session/*`). |
| `src/admin.ts` | Access-gated user/subscription admin (`/admin/*`). |
| `src/jwt.ts` | Our own short-lived HS256 access-token JWTs. |
| `src/access-verify.ts` | Verifies Cloudflare Access JWTs against the team JWKS. |
| `src/upsert.ts` | Pure `chooseWinningRow()` rank logic (mirrored in the D1 upsert SQL). |
| `schema.sql` | D1 schema. |
| `wrangler.toml` | Bindings + non-secret vars (placeholder IDs). |

## Develop & test

```bash
npm install
npm test          # vitest (Miniflare) — 67 tests, no account needed
npm run typecheck # tsc --noEmit
npm run dev       # wrangler dev --local
```

`npm run dev` / `npm test` run entirely against local Miniflare — local D1, R2, and
KV are created on the fly. For a local D1 you want to inspect, apply the schema with:

```bash
npx wrangler d1 execute simsteward-db --local --file=./schema.sql
```

### Access-gated routes in local dev

`/approve` and `/admin/*` require a verified Cloudflare Access JWT in production.
Access can't be exercised locally, so an **env-gated dev bypass** accepts a
`X-Dev-Access-Email` header (or `?dev_email=` query param) instead. It requires
BOTH `ACCESS_DEV_BYPASS` AND `ENVIRONMENT=development` to be set — a two-key gate
so a lone stray `ACCESS_DEV_BYPASS` can never silently disable Access verification
(`vitest.config.ts` sets both for the test suite). For `wrangler dev --local`, pass
both explicitly: `wrangler dev --local --var ACCESS_DEV_BYPASS:1 --var ENVIRONMENT:development`.
Neither var may ever appear in the production `wrangler.toml`.

## Manual deploy — requires a real Cloudflare account (NOT done here)

The following steps provision real cloud resources and are **left as a manual
checklist for the account owner**. None of them are performed by this worker or
its test suite. They mirror the plan's §8 manual checklist and the placeholder
convention already documented in `../docs/DATA-API-DEPLOY.md`.

1. **Create resources** (each prints an id — paste it into `wrangler.toml`,
   replacing the `REPLACE_AFTER_*` placeholders):
   ```bash
   npx wrangler d1 create simsteward-db                       # → database_id
   npx wrangler r2 bucket create simsteward-incident-index
   npx wrangler kv namespace create simsteward-device-codes   # → id
   ```
2. **Apply the schema** to the remote D1:
   ```bash
   npx wrangler d1 execute simsteward-db --remote --file=./schema.sql
   ```
3. **Set the JWT signing secret** (never committed):
   ```bash
   npx wrangler secret put JWT_HMAC_SECRET
   ```
4. **Configure Cloudflare Access**: create an Access application in front of
   `/approve` and `/admin/*`, then set `ACCESS_TEAM_DOMAIN` and `ACCESS_AUD` in
   `wrangler.toml` `[vars]` to the team domain and the app's AUD tag. Do **not**
   set `ACCESS_DEV_BYPASS`.
5. **Seed at least one app row** in `apps` (the plugin's `app_token`; store only
   its sha256 hash).
6. **Deploy manually** (always available, and required for the very first deploy
   since the automated path below still needs the resources above to exist first):
   ```bash
   npx wrangler deploy
   ```
7. **One-time: enable automated deploy-on-merge** (`.github/workflows/worker-ci-cd.yml`).
   A push to `main` touching `worker/**` runs the test job, then — only after it
   passes — deploys via `cloudflare/wrangler-action`. This workflow deploys code
   only; it does **not** provision resources, so steps 1–5 above must already be
   done (and the real, non-placeholder resource IDs from step 1 committed into
   `wrangler.toml` — they're not secret, safe to commit). One-time setup:
   - Create a **scoped** Cloudflare API token (Workers Scripts:Edit, D1:Edit,
     R2:Edit, KV:Edit — NOT a global API key) at
     [dash.cloudflare.com → My Profile → API Tokens](https://dash.cloudflare.com/profile/api-tokens).
   - Add it as the `CLOUDFLARE_API_TOKEN` secret on the GitHub repo (Settings →
     Secrets and variables → Actions → New repository secret).
   - After that, merging to `main` deploys automatically; no further manual
     `wrangler deploy` is needed unless the automated path is unavailable.

## Route summary

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/health` | none | `{status:"ok"}` |
| POST | `/auth/device/start` | app_token | starts device pairing |
| POST | `/auth/device/poll` | app_token | polls; mints `user_token` once approved |
| POST | `/auth/token` | app_token + user_token | rotates `user_token`, mints access JWT |
| GET/POST | `/approve` | Cloudflare Access | human approval page |
| POST | `/session-complete` | access JWT | sessions/drivers/incidents upsert |
| POST | `/incidents/push` | access JWT | rank-gated incident upsert |
| PUT | `/incident-index/{id}` | access JWT | verbatim JSON → R2 + D1 upsert |
| GET | `/incident-index/{id}` | access JWT | verbatim R2 object |
| GET | `/session/{id}` | access JWT | D1 summary |
| GET | `/admin` | Cloudflare Access | users + subscription form |
| POST | `/admin/users` | Cloudflare Access | create user |
| PATCH/POST | `/admin/subscriptions/{user_id}` | Cloudflare Access | update subscription |
