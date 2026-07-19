// Worker bindings + config, mirrors wrangler.toml.
export interface Env {
  DB: D1Database;
  INCIDENT_INDEX_BUCKET: R2Bucket;
  DEVICE_CODES: KVNamespace;

  // Secret (set via `wrangler secret put JWT_HMAC_SECRET`).
  JWT_HMAC_SECRET: string;

  // Non-secret config (wrangler.toml [vars]).
  ACCESS_TEAM_DOMAIN: string;
  ACCESS_AUD: string;

  // Comma-separated allowlist of Access-verified emails permitted to use the
  // admin routes (/admin, /admin/users, /admin/subscriptions/*). Access
  // authentication alone is NOT sufficient — the resolved email must appear
  // here (case-insensitive). Empty/unset means no one is an admin.
  ADMIN_EMAILS?: string;

  // Dev/test only — bypasses real Cloudflare Access verification. MUST be unset
  // in production. Presence of any truthy value enables the bypass, but ONLY
  // when ENVIRONMENT is also "development" (see access-verify.ts) — this is a
  // deliberate two-key gate so a lone stray ACCESS_DEV_BYPASS in a misconfigured
  // deploy can never silently disable Access verification.
  ACCESS_DEV_BYPASS?: string;

  // Deployment environment marker. MUST NEVER be "development" in production
  // wrangler.toml — it is the second half of the ACCESS_DEV_BYPASS gate above.
  // Unset (production) or any value other than "development" disables the bypass.
  ENVIRONMENT?: string;
}
