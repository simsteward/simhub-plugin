# Cloudflare-Backed Storage for Incidents, Incident Index & Metadata

**Date:** 2026-07-19  
**Status:** Approved — implementation in progress on `feature/cloudflare-incident-storage`. Supersedes Component 2 of the 2026-05-03 spec.  
**Environment:** Dev

## Context

Today, incidents and the "replay incident index" (`ReplayIncidentIndexFileRoot`) live only as local JSON files under `%LocalAppData%\SimSteward\replay-incident-index\{subSessionId}.json` — no durable off-machine copy, no cross-session querying, nothing survives a wiped machine. A prior design (`docs/superpowers/specs/2026-05-03-cloud-only-observability-design.md`, `docs/DATA-API-DEPLOY.md`) sketched a Cloudflare Worker + D1 `/session-complete` endpoint but was explicitly deferred and never built (no `worker/` directory exists). This plan supersedes and extends that deferred design: incidents, the incident index, and session metadata get written to Cloudflare (D1 + R2) for durable storage and later retrieval by the plugin/dashboard, gated behind a real JWT-based auth system (app token + user token + short-lived access token, with refresh rotation and subscription validation) — because this is meant to be multi-tenant from day one, not just a backup of one dev machine's data.

Decisions locked in during design (do not re-litigate):
- **Storage shape:** Hybrid — D1 for queryable relational rows, R2 for the full-fidelity incident-index JSON blob.
- **Write triggers:** Both live (async, as incidents are detected) and replay-reconciled (when a replay index build finalizes), deduplicated via a deterministic fingerprint.
- **Retrieval:** The plugin/dashboard needs a read path (e.g. "load incident index from cloud" when no local cache exists) — not write-only.
- **Auth:** JWT-based, multi-tenant from day one. App token + user token → access token. Full hosted login/approval web page (not a manual bootstrap). Subscription validation is real (Worker-enforced), but subscription *data* is admin-managed via a small built tool, not Stripe.
- **Multi-car reconciliation:** In scope now (see §5 — turned out much smaller than initially feared).

---

## 1. Cloudflare-side architecture

New top-level directory `worker/` (does not exist today):
- `worker/wrangler.toml` — bindings: D1 (`simsteward-db`), R2 (`simsteward-incident-index`), KV (`simsteward-device-codes`), secret `JWT_HMAC_SECRET`.
- `worker/schema.sql` — D1 schema (§2).
- `worker/src/index.ts` — router entry.
- `worker/src/auth.ts` — device/start, device/poll, token exchange+rotation.
- `worker/src/jwt.ts` — HS256 sign/verify (use `@tsndr/cloudflare-worker-jwt`, a ~2KB zero-dep package built for Workers' `SubtleCrypto` — don't hand-roll base64url/signature-comparison edge cases on a security-critical path).
- `worker/src/data.ts` — session-complete, incidents/push, incident-index GET/PUT, session GET.
- `worker/src/admin.ts` — admin API for user/subscription management (§4).
- `worker/src/access-verify.ts` — verifies Cloudflare Access JWTs via JWKS (§4).
- `worker/test/*.spec.ts` — Vitest via `@cloudflare/vitest-pool-workers`.

### Route list

**Data plane (require `Authorization: Bearer <access_token>`, our own JWT):**
- `POST /session-complete` — supersedes the deferred spec's static-bearer version, now JWT-gated.
- `POST /incidents/push` — array of live-detected incident rows, each keyed by the v2 fingerprint.
- `PUT /incident-index/{subSessionId}` — full `ReplayIncidentIndexFileRoot` JSON → R2 blob + per-row D1 upserts.
- `GET /incident-index/{subSessionId}` — returns the R2 blob verbatim (full fidelity, including the `Validation` block).
- `GET /session/{subSessionId}` — lightweight D1-only summary.
- `GET /health`.

**App auth (public; app_token only until a user_token exists):**
- `POST /auth/device/start` → `{device_code, user_code, verification_uri, interval_sec, expires_in_sec}`.
- `POST /auth/device/poll` → `{status: pending|approved|denied|expired, user_token?}` (returned once).
- `POST /auth/token` — `{app_token, user_token}` → `{access_token, user_token (rotated), expires_in_sec:900}`. Same endpoint serves first exchange and every refresh.

**Human-facing, gated by Cloudflare Access (§4), not our JWT:**
- `GET /approve` — device-pairing approval page.
- `/admin/*` — subscription/user management UI + API.

### R2 key layout

`incident-index/v1/{subSessionId}.json` — mirrors the local path convention in `ReplayIncidentIndexOutputPaths.GetFilePathForSubSession`. Plain overwrite-by-key (last-reconciled-write-wins); merging happens at the D1 row level, not the blob level.

---

## 2. D1 schema

Extends the deferred spec's tables (`DRIVERS`, `SESSIONS`, `INCIDENTS`, `INCIDENT_CAPTURES`) — add `source`/`source_rank` (1=live, 2=replay_reconciled) to `INCIDENTS`, and `index_source`/`index_updated_at` to `SESSIONS`. `fingerprint_version` set to `2` from day one (no legacy v1 cloud data exists yet).

New tables (none of this exists anywhere today):

```sql
CREATE TABLE apps (
  app_id TEXT PRIMARY KEY, token_hash TEXT UNIQUE, version_label TEXT,
  revoked_at TEXT, created_at TEXT NOT NULL
);
CREATE TABLE users (
  user_id TEXT PRIMARY KEY, email TEXT UNIQUE, display_name TEXT,
  created_at TEXT NOT NULL, last_seen_at TEXT
);
CREATE TABLE subscriptions (
  user_id TEXT PRIMARY KEY REFERENCES users(user_id),
  tier TEXT NOT NULL DEFAULT 'free',
  status TEXT NOT NULL DEFAULT 'active',   -- active | past_due | canceled
  current_period_end TEXT, updated_at TEXT NOT NULL
);
CREATE TABLE user_tokens (
  token_id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(user_id),
  token_hash TEXT NOT NULL,                -- sha256(raw opaque token); never store raw
  rotated_from TEXT REFERENCES user_tokens(token_id),
  created_at TEXT NOT NULL, revoked_at TEXT, last_used_at TEXT, device_label TEXT
);
CREATE INDEX idx_user_tokens_hash ON user_tokens(token_hash);

CREATE TABLE incident_index_blobs (
  sub_session_id INTEGER PRIMARY KEY, r2_key TEXT NOT NULL, content_sha256 TEXT NOT NULL,
  incident_count INTEGER NOT NULL, index_build_time_ms INTEGER, updated_at TEXT NOT NULL
);
```

`device_codes` pairing state is **not** in D1 — short-lived (≤10 min TTL), so Workers KV with native `expirationTtl` avoids a cleanup cron. Keys: `device_codes:{device_code}`, `user_codes:{user_code}→device_code`.

JWT claims: `{sub: user_id, tier, app_id, jti, iat, exp}`, `exp = now + 900s`, signed with `JWT_HMAC_SECRET` (`wrangler secret put`).

**Firm invariant:** the Worker never recomputes a fingerprint — the C# plugin computes it once and sends it as the row PK. No cross-language hashing-drift risk.

---

## 3. Dedup / idempotency (the crux)

`ReplayIncidentIndexFingerprint.ComputeHexV2` (`src/SimSteward.Plugin/ReplayIncidentIndexFingerprint.cs:95-141`) already exists, is tested, and is exactly what's needed — it quantizes `sessionTimeMs` onto a fixed 500ms grid so the same physical incident gets the same fingerprint whether detected live (fast tick cadence) or during a 16x replay fast-forward sweep. It's currently marked "PROTOTYPE — not wired in"; both production call sites (`ReplayIncidentIndexDocumentBuilder.Build`, `ReplayIncidentIndexDocumentModel.cs:161-166`, and `LogLiveIncidentDetectionsLocked`, `SimStewardPlugin.LiveIncidentDetection.cs:163-164`) still call `ComputeHexV1` (raw timestamp, cadence-sensitive — unusable for cross-cadence dedup).

**Change:** wire v2 in *additively* — add a `CloudFingerprint` field alongside the existing `Fingerprint` (v1) on `ReplayIncidentIndexIncidentRow`, computed at both call sites. `Fingerprint` (v1) stays untouched for existing local-file/dashboard consumers. `CloudFingerprint` (v2) is what travels to Cloudflare and becomes `incidents.id`.

**D1 upsert** — rank-gated so a replay-reconciled write always wins over live, never regresses, and repeated identical writes are no-ops:

```sql
INSERT INTO incidents (id, sub_session_id, session_num, user_id, car_idx, session_time,
                        replay_frame_num_end, delta, type, cause, other_user_id, source,
                        source_rank, processed_at, fingerprint_version)
VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,2)
ON CONFLICT(id) DO UPDATE SET
  session_num=excluded.session_num, cause=excluded.cause,
  replay_frame_num_end=excluded.replay_frame_num_end, source=excluded.source,
  source_rank=excluded.source_rank, processed_at=excluded.processed_at
WHERE excluded.source_rank >= incidents.source_rank;
```

SQLite/D1's `WHERE` clause on `DO UPDATE` makes "never let a stale live-retry clobber an already-reconciled row" work without a read-then-write race.

**Local durable outbox** — new `src/SimSteward.Plugin/CloudOutbox.cs`, persisted at `%LocalAppData%\SimSteward\cloud-outbox\pending.ndjson`. Write-ahead: every push (live incident or index finalize) is appended *synchronously before* the fire-and-forget HTTP attempt, removed only after a confirmed 2xx (so a crash mid-push never loses data). Reuses the atomic temp+`File.Replace` primitive already in `ReplayIncidentIndexOutputPaths.cs:49-61` — worth promoting to a small shared `AtomicFile` helper now that it has two callers. A drain loop hooks into `DataUpdate()` on a throttled tick counter (same pattern as `_captureQueueDrainTickCounter` in `SimStewardPlugin.cs:114-115`), capped exponential backoff (5s→15s→60s→5min ceiling). Because upserts are idempotent by fingerprint, at-least-once delivery from retries is always safe.

**Known accepted imprecision:** the fixed 500ms bucket can (a) merge two genuinely distinct same-car incidents within 500ms into one fingerprint, or (b) fail to merge one incident whose samples straddle a bucket boundary — both already documented in `ReplayIncidentIndexFingerprintV2Tests.cs`. Using the existing tested prototype is the pragmatic choice; a tolerance-window clustering scheme is the natural v3 if this proves too coarse in practice.

---

## 4. Auth: two separate systems, deliberately

**A. Human login (device-approval page, admin tool) → Cloudflare Access, not custom code.** Building a password/session/email system from scratch is exactly the kind of custom security surface the goal's "no security risks" asks us to avoid. Cloudflare Access (Zero Trust, free tier covers a handful of seats) fronts `/approve` and `/admin/*` with its built-in **email One-Time-PIN** identity provider — zero custom auth code, just an allowlist of emails in the Access policy. Use a custom domain with per-path self-hosted Access applications (not the one-click Workers.dev toggle, which protects the whole route) so `/auth/device/poll` and the other API routes stay outside Access entirely and keep using our own JWT scheme. The Worker verifies the Access JWT itself (`Cf-Access-Jwt-Assertion` header) against Cloudflare's JWKS (`https://<team-domain>.cloudflareaccess.com/cdn-cgi/access/certs`), checking `iss`/`aud` — never trust the `Cf-Access-Authenticated-User-Email` header alone (spoofable without JWT verification). `worker/src/access-verify.ts` owns this.

**B. Plugin ↔ Worker (device pairing, refresh, data-plane calls) → our own JWT system.** This can't reuse Access — it's a headless desktop app polling an API, not a browser session.

1. `POST /auth/device/start {app_token}` → `{device_code, user_code, verification_uri, interval_sec, expires_in_sec}`.
2. Plugin shows `user_code` via a new WS push `{type:"cloudPairing", userCode, verificationUri, expiresInSec}` and a structured log line.
3. User visits `verification_uri` (`/approve`, behind Cloudflare Access — logs in via OTP if not already), enters `user_code`, approves. Worker flips the KV entry to `status:"approved"`, resolves `user_id`.
4. Plugin polls `/auth/device/poll` on `interval_sec`. On `approved`, gets `user_token` (returned exactly once; KV entry deleted immediately after — replay-proof).
5. Plugin DPAPI-encrypts and persists `user_token` (`CurrentUser` scope — ties to the Windows account; re-pairing needed if SimHub ever runs under a different account, an accepted tradeoff).
6. **Exchange/refresh** (`/auth/token`): validates `app_token` not revoked, looks up `sha256(user_token)`; on success revokes the old `user_tokens` row, inserts a new one (`rotated_from`), mints a new opaque `user_token` + a fresh 15-min `access_token`. Plugin persists the rotated `user_token`, keeps `access_token` in memory only.
7. Plugin refreshes proactively (~60s before `exp`) before any data-plane call.
8. **Reuse detection:** presenting a `user_token` whose hash maps to an already-revoked row means a stale/stolen copy was replayed — Worker revokes *every* token for that user, returns `401 token_reuse_detected`. Plugin clears its token store, drops to "unpaired," logs `ERROR` + `SentrySdk.CaptureException` (this is a real security signal worth a Grafana/Sentry alert).
9. **Subscription gating:** every data-plane route checks `subscriptions.status=='active'` at exchange time and embeds `tier` in the JWT (15-min staleness window is acceptable). Inactive → distinct `402`/`403` so the plugin can surface "subscription inactive" rather than a generic failure.

**App token caveat (explicitly accepted, not re-litigated):** a .NET Framework DLL decompiles trivially — the embedded app token is NOT a real secret. Its job is build/version identification (so a compromised release can be revoked via `apps.revoked_at`), never the sole access-control boundary. The `user_token` is the actual boundary, hence rotation-on-every-use above.

**Admin tooling (§ user's explicit ask — "not manual, I expect you to do this"):** rather than hand-typed `wrangler d1 execute` SQL, build `worker/src/admin.ts` — Access-gated routes (`POST /admin/users`, `PATCH /admin/subscriptions/{user_id}`) plus a minimal Access-gated HTML form page. This gives real, repeatable subscription management without a Stripe integration.

**Bootstrap (chicken-and-egg fix):** there's no `users` row for anyone on day one, so `/approve` and `/admin` can't require a pre-existing DB user before granting access. Both routes get-or-create a `users` row keyed by the Access-verified email on first hit (new row defaults to `tier='free', status='active'` — see tier-semantics note below) — Cloudflare Access (the email allowlist) is what actually gates who can reach these pages at all; the DB row is just bookkeeping, not a second gate.

**Access policy scoping (simplified for v1):** one Cloudflare Access application/policy covers both `/approve` and `/admin` for now — today there's exactly one real human (you) needing both. Splitting into two separately-scoped Access apps (a broad "any customer" allowlist for `/approve`, a narrow "admin only" allowlist for `/admin`) is a config-only change in Cloudflare's dashboard whenever a second, non-admin user needs to pair a device — no code change required, so don't build that separation now.

**Tier semantics (explicit open assumption, not blocking):** pricing tiers/entitlements aren't defined yet. Default every new user to `tier='free', status='active'` so nothing is blocked by default; the Worker still enforces `status=='active'` (a real check, just permissive today) so the gating code path is exercised and ready the moment tiers/pricing are decided. Revisit what `free` vs. paid actually restricts once that's defined — don't invent restrictions now.

**Rate limiting (brute-force mitigation, ties to "no security risks"):** `device_code` is a long random token (128-bit) — the actual bearer of trust for polling — while `user_code` is short/human-typable but only usable by someone who can already pass Cloudflare Access login, so guessing it alone isn't sufficient. Still, add a simple KV-backed attempt counter (e.g. max 10 polls/minute per `device_code`, max 5 `/auth/token` exchanges/minute per `user_token` hash) in `worker/src/auth.ts` — cheap insurance, not a new subsystem.

**Local dev config:** add a local `wrangler dev` URL as the dev-time value of `SIMSTEWARD_CLOUD_API_URL` (mirrors the existing `SIMSTEWARD_LOG_ENV` local/production split) so the plugin can be exercised against a local Worker+D1(`--local`)+R2 before touching production.

---

## 5. Multi-car incident reconciliation (smaller than it first looked)

Investigation corrected the initial assumption. The "focus list" hardcoded to `[playerCarIdx]` (`SimStewardPlugin.ReplayIncidentIndexBuild.cs:235-236`, resolver at `:1026-1035`) only gates **camera lock** (`SnapCameraToPrimaryFocusLocked`, `:1042-1078`) and the player-only enrichment/baseline pieces. The actual incident detection loop (flags/surface/fast-repair, `ReplayIncidentIndexDetector.cs:100-213`), the YAML incident-count diff (`ReplayIncidentYamlDiff.cs`, already documented as "authoritative per-driver-per-event source for other cars"), and the discrepancy/validation builder (`ReplayIncidentIndexValidationComparer.cs:24-87`) **already run field-wide across all ~64 car slots** — this is what feeds `IncidentCountByCarIdx` and the `Incidents` list today, not a player-only filter.

The genuinely player/camera-limited pieces are: `_replayIndexBaselinePlayerCarMyIncidentCount` (scalar baseline for the SDK's `PlayerCarMyIncidentCount`, which the SDK itself only reports for whichever car the replay **camera** is locked to — a hard SDK limitation, not a code choice) and `AddPlayerOnlyIncidentContext` (`SimStewardPlugin.LiveIncidentDetection.cs:287-299` — throttle/brake/rpm/gear/steer/tire-compound/surface-material enrichment, likewise camera-locked-car-only per the SDK).

**Scoped change:** loosen the focus-list resolver so all cars present in the session (from `SessionInfo` driver list) are included for incident-row purposes, keeping camera lock as a separate, still-single-car concept used only for the enrichment extras. Other drivers' cloud incident rows get full core fields (car_idx, session_time, delta/points, type/cause, source, lap) at `source_rank=2` same as the player — just without the camera-locked throttle/brake/rpm-style enrichment, which isn't required for D1/R2 storage or the dedup upsert. Full per-car enrichment parity would require N sequential camera-lock passes (one per car) — impractical at typical grid sizes (20-60 cars) and explicitly out of scope; note it as a possible future refinement, not a blocker.

**Also correct:** the earlier plan draft said replay fast-forwards at 32x — the actual default is **16x** (`SimStewardPlugin.ReplayIncidentIndexBuild.cs:15`).

---

## 6. Plugin-side C# components

New files (partial-class convention matching `SimStewardPlugin.ReplayIncidentIndexBuild.cs`):

- `CloudTokenStore.cs` — DPAPI read/write/clear (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`), path `%LocalAppData%\SimSteward\cloud-auth\user-token.bin`; also owns the in-memory access token + expiry.
- `CloudAuthClient.cs` — static long-lived `HttpClient` (3-5s timeout, matching `LokiPushClient.cs:19-22`), device/start, device/poll, token exchange. Never throws — caught, logged, `SentrySdk.CaptureException` where warranted.
- `CloudDataClient.cs` — `POST /incidents/push`, `PUT`/`GET /incident-index/{id}`, `GET /session/{id}`. Pushes fire-and-forget (`Task.Run`, mirroring `LokiPushClient.Push`); GETs awaited off the 60Hz `DataUpdate` thread.
- `CloudOutbox.cs` — durable NDJSON outbox (§3).
- `SimStewardPlugin.CloudSync.cs` — glue: `DataUpdate()` hook for throttled outbox drain + proactive token refresh; hooks `LogLiveIncidentDetectionsLocked` (enqueue each sample, `source="live"`) and `FinalizeReplayIndexBuildLocked` (enqueue index push after the existing local atomic write succeeds, `source="replay_reconciled"`); new `DispatchAction` branches (`cloud_pair_start`, `cloud_pair_cancel`, `cloud_incident_index_fetch`) each with `action_dispatched`/`action_result` structured logs matching the existing branches at `SimStewardPlugin.cs:776-821`.
- Loosen the focus-list resolver per §5 in `SimStewardPlugin.ReplayIncidentIndexBuild.cs`.

New env vars (`.env.example`, existing `SIMSTEWARD_*` convention): `SIMSTEWARD_CLOUD_API_URL`, `SIMSTEWARD_CLOUD_APP_TOKEN`.

---

## 7. Dashboard-side additions

Follows the existing `{action, arg}` / `{type:"..."}` WS pattern (`index.html:789-797`, `replay-incident-index.html:247-248`) — the dashboard never calls the Worker directly, per `docs/ARCHITECTURE.md:134-141`'s CRITICAL RULE.

New commands (dashboard→plugin): `cloud_pair_start`, `cloud_pair_cancel`, `cloud_incident_index_fetch` (arg=subSessionId, shown as a "Load from Cloud" button when local cache is missing).
New pushes (plugin→dashboard): `cloudPairing` `{userCode, verificationUri, expiresInSec}`; `cloudIncidentIndex` `{subSessionId, root, source:"cloud"}` (plugin also persists it locally via `ReplayIncidentIndexOutputPaths.WriteJsonAtomic` so it behaves like a locally-built index afterward); `cloudSyncStatus` `{paired, lastPushUtc, outboxPendingCount}` (never exposes tokens).
Every new button emits the standard `{action:"log", event:"dashboard_ui_event", ...}` click log first, per `docs/RULES-ActionCoverage.md`.

---

## 8. Test plan

**Unit (`dotnet test`):** a new test tying live and replay `CloudFingerprint` computation together (same car/source/points/timestamp-within-bucket → identical fingerprint); `CloudOutboxTests` (durability across simulated restart, atomic rewrite after partial ack); `CloudTokenStoreTests` (DPAPI round-trip, clear-on-reuse-detected); `CloudAuthClientTests` (refresh/rotation state machine against a fake HTTP layer — check `ReplayIncidentIndexLokiIntegrationTests.cs` for this repo's existing pattern for testing network classes).

**Worker (Vitest, separate gate from `dotnet test`):** extract `chooseWinningRow()` (the rank-based conflict decision) as a pure function and unit test directly; token rotation/reuse-detection against local D1; Access JWT verification against a test JWKS.

**Manual/integration checklist:** `wrangler dev` + local D1/R2/KV smoke test of the full pairing flow; `wrangler d1 execute --remote --file=worker/schema.sql` against a scratch DB before production; kill-network-mid-session test (confirm outbox drains on reconnect); token-reuse test (confirm forced re-pair + Sentry event); subscription-gating test (flip a test user to `canceled` via the new admin tool, confirm the dashboard surfaces "subscription inactive").

---

## 9. Phased rollout

- **Phase 0:** Create the isolated worktree/branch before touching anything else.
- **Phase A:** `worker/` scaffold, D1+R2+KV provisioning, Cloudflare Access setup (team domain, OTP policy on `/approve` + `/admin`), `/auth/device/*` + `/auth/token` + `/health` + admin API/page. Vitest suite + manual `wrangler dev` smoke test.
- **Phase B:** Data-plane routes + rank-based upsert + R2 wiring. Plugin write-path: fingerprint v2 wired additively, `CloudTokenStore`/`CloudAuthClient`/`CloudDataClient`/`CloudOutbox`, hooks into live detection + replay finalize, pairing `DispatchAction` branches, multi-car focus-list loosening (§5). Write-only — soak before building retrieval.
- **Phase C:** Read-path (`GET /incident-index/{id}`, `GET /session/{id}`) + `cloud_incident_index_fetch` + dashboard "Load from Cloud" button, pairing UI, cloud status badge. Full manual checklist end-to-end.

---

## Verification

- `dotnet test` (0 failures) covering the new fingerprint/outbox/token-store/auth-client unit tests.
- `worker/` Vitest suite green; `wrangler dev` local smoke test of device pairing (start→approve via a local Access bypass or a stubbed header→poll→token exchange→rotation) and of a live incident push + replay-index PUT round-tripping into local D1/R2 (`--local`).
- Manual end-to-end: run SimHub, pair via the real `/approve` page (Cloudflare Access OTP), generate a live incident, confirm it lands in D1 (`wrangler d1 execute --remote`); run a replay index build for the same subsession, confirm the reconciled row wins the upsert (rank 2) without duplicating; use the admin tool to set a test user's subscription to `canceled` and confirm the plugin surfaces "subscription inactive."
- `deploy.ps1` still passes build (0 errors) + `dotnet test` + `tests/*.ps1` unchanged — the Worker deploys separately via its own script, not folded into `deploy.ps1`.

## Security hardening addendum (2026-07-19, post-review)

Automated security review of the initial implementation surfaced two HIGH findings; both are fixed and regression-tested in `worker/test/`:

1. **Tenant isolation / IDOR.** iRacing subsession IDs are global, so keying data by `sub_session_id` alone let any authenticated user read or overwrite any other user's rows/blobs. Fix: all tenant data is namespaced by `owner_user_id` (the JWT `sub` claim — never a body field). `INCIDENTS` PK is now `(owner_user_id, id)`, `SESSIONS` and `incident_index_blobs` PKs are `(owner_user_id, sub_session_id)`, and the R2 key layout is `incident-index/v1/{owner_user_id}/{subSessionId}.json` — cross-tenant access is structurally impossible. Two users who race in the same subsession each keep fully independent copies. (`incidents.user_id` remains the iRacing driver ID of the car involved — a distinct concept from the owning tenant.)
2. **CSRF on `/approve`.** The approval POST previously required only an Access session, so a cross-site page could auto-submit an approval for a victim's browser. Fix: double-submit CSRF token (random per-approval value in a `SameSite=Strict; HttpOnly; Secure` cookie AND a hidden form field, compared timing-safely) plus an `Origin` header check. Same-site enforcement means an attacker page can neither read nor cause the browser to send the cookie.

### Critical files

- `src/SimSteward.Plugin/ReplayIncidentIndexFingerprint.cs` (wire v2)
- `src/SimSteward.Plugin/ReplayIncidentIndexDocumentModel.cs`, `SimStewardPlugin.LiveIncidentDetection.cs` (both fingerprint call sites)
- `src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs` (focus-list loosening, finalize hook)
- `src/SimSteward.Plugin/LokiPushClient.cs`, `ReplayIncidentIndexOutputPaths.cs` (patterns to mirror/reuse)
- `worker/schema.sql`, `worker/src/*.ts` (new)
- `src/SimSteward.Dashboard/index.html` (new WS handlers — `replay-incident-index.html` was deleted and merged into this file's "Replay Index" tab by an unrelated main-branch commit; see addendum #2)

## Security hardening addendum #2 (2026-07-19, later same session — Phases 0–2 of the follow-up hardening plan)

**Context: main diverged out from under this branch.** Between this design landing and this addendum, `origin/main` merged a large, unrelated commit (live incident points/escalation engine + dashboard consolidation) that **deleted `replay-incident-index.html`**, folding its rendering logic into `index.html`'s new "Replay Index" tab. This branch had independently modified that same file (a "Load from cloud…" fetch button, plus its own XSS-escaping pass). Phase 0 of the follow-up plan reconciled this: fast-forwarded onto `origin/main`, resolved the resulting delete/modify conflict by porting the cloud-fetch button and its WS handler into the new consolidated tab, and confirmed the branch's XSS-escaping concern was already independently satisfied by main's own `escapeHtmlForCaptured()` helper (used ~55x across the same render paths) — no second escape helper was needed. Along the way, `dotnet build`/`dotnet test` surfaced and fixed real defects introduced by an earlier, spend-limit-interrupted fixer pass: four C# compile errors in `SimStewardPlugin.CloudSync.cs` against the actual (correct) client API shapes, two stale outbox tests asserting a since-corrected synchronous-persist contract, and a genuine production gap where `CloudOutbox.PersistPendingIfDirty()` was documented as the drain's job but never actually called anywhere — meaning outbox entries lived in memory only and a plugin restart would have silently dropped everything pending.

Phase 1b completed the orphaned CloudSync fixer work: rotated user-token persistence on refresh was fixed (the previous code set the in-memory access token but never persisted the rotated `user_token`, which would have permanently locked an install out on its second refresh); `cloudPairing`/`cloudSyncStatus` WS broadcasts were wired end-to-end (dashboard connect/hello, pairing start/approve/deny/expire, and after each successful outbox drain); a WARN log was added for corrupt outbox lines on load (the counter existed, nothing consumed it). Two other items the original plan listed as still-open — `action_dispatched`/`action_result` logging on the cloud DispatchAction branches, and moving outbox persistence off the 60Hz thread — turned out already correctly implemented on inspection; re-verify claims like this against the code before trusting old plan text, rather than redoing work that's already done. A sixth item (a "subSessionId=0 guard" on live-incident cloud pushes) was **not** implemented as literally scoped: `CloudSyncBridge.OnIncidentDetected`, the intended integration point, is never called from anywhere — the live incident detector that landed via the Phase 0 rebase and this branch's cloud outbox evolved independently and have never been wired together. Whether v1 cloud sync should push live incidents at all (vs. staying replay-index-only, which is what the current D1 schema's column naming matches) is an open product decision, not resolved here.

Phase 2 closed the plan's remaining named security gaps — all seven were confirmed genuinely unimplemented (unlike several Phase 1 items) and fixed with regression tests in `worker/test/`:

3. **C1 — `ACCESS_DEV_BYPASS` fail-open.** The bypass in `access-verify.ts` was honored on its own; a lone stray `ACCESS_DEV_BYPASS` in a misconfigured deploy would have silently disabled real Access verification. Fix: a two-key gate — the bypass now also requires `env.ENVIRONMENT === "development"`, and logs a loud `console.error` when only one of the two is set. Neither var may ever appear in `wrangler.toml`.
4. **H1 — token-theft blast radius.** Reuse-detection in `tokenExchange` revoked every token the user owned, including other, uncompromised devices. Fix: `user_tokens.lineage_id` (the root `token_id` of a device's rotation chain, set at `devicePoll` mint and propagated on every rotation) scopes revocation to `WHERE user_id AND lineage_id` — pairing a second device now survives a first device's token being stolen and replayed.
5. **H2 — 15-minute post-theft window.** Revoking the rotating user token did nothing about an access-token JWT the attacker may have already minted from it; that JWT stayed valid for its full 15-minute TTL. Fix: `users.tokens_valid_after`, set alongside the lineage revocation; `requireActiveUser` now joins it into the same subscription-status query and rejects any JWT whose `iat` predates the cutoff (`401 token_revoked`).
6. **M1 — blind pairing approval.** The `/approve` confirmation page showed only the raw user code, with nothing to help a human notice a code that isn't theirs. Fix: the page now shows the requesting app id and the pairing's start time.
7. **M2 — unbounded index upload.** `PUT /incident-index/{id}` had no size limit. Fix: a 4 MB cap (real index files run 10s–100s of KB) enforced via a `Content-Length` fast-path check plus a definitive post-read byte-length check (a missing or lying `Content-Length` can't bypass it) → `413 payload_too_large`.
8. **L1 — MIME sniffing.** `GET /incident-index/{id}` now sends `X-Content-Type-Options: nosniff`.
9. **L4 — log hygiene.** The top-level catch in `index.ts` now truncates the logged error string to 200 characters (no code path in this Worker embeds secrets in an exception message, but unbounded stack traces are still unwanted log volume).

**Deliberately deferred, not part of Phase 2:** a data-fidelity gap noticed during Phase 0 — main's `ReplayIncidentYamlDiff.IsAggregateDelta` audit flag (capped-delta incidents) has no corresponding column in `worker/schema.sql`'s `INCIDENTS` table, so a cloud-synced row silently loses that signal. Not a security finding; left as an open call on whether to add the column before treating the schema as final.

All schema changes (`lineage_id`, `tokens_valid_after`) are additive to a schema that has never been deployed anywhere — no migration was needed. `dotnet build`/`dotnet test` (0 errors, 290/291 — the one skip is a Loki integration test needing a live harness) and worker `npm test`/`npm run typecheck` (67/67, clean) are green after Phases 0–2.
