// Device-pairing + token-exchange flow.
import type { Env } from "./env";
import { json, errorJson, html, readJson, readBody, isRateLimited } from "./http";
import { sha256Hex, randomToken, humanCode, newId, timingSafeEqualStr } from "./crypto";
import { signAccessToken, ACCESS_TOKEN_TTL_SEC } from "./jwt";
import { resolveAccessEmail } from "./access-verify";

const DEVICE_CODE_TTL_SEC = 600; // 10 minutes
const POLL_INTERVAL_SEC = 5;

interface DeviceCodeState {
  status: "pending" | "approved";
  user_code: string;
  appId: string;
  startedAt: string;
  user_id?: string;
}

interface AppRow {
  app_id: string;
  token_hash: string;
  revoked_at: string | null;
}

const deviceKey = (deviceCode: string) => `device_codes:${deviceCode}`;
const userCodeKey = (userCode: string) => `user_codes:${userCode}`;

// Look up an app by its raw token; null if missing or revoked.
async function resolveApp(env: Env, appToken: string): Promise<AppRow | null> {
  const hash = await sha256Hex(appToken);
  const row = await env.DB.prepare(
    "SELECT app_id, token_hash, revoked_at FROM apps WHERE token_hash = ?",
  )
    .bind(hash)
    .first<AppRow>();
  if (!row || row.revoked_at) return null;
  return row;
}

// POST /auth/device/start — body {app_token}
export async function deviceStart(request: Request, env: Env): Promise<Response> {
  // Per-IP limit: every call mints two KV entries with a 10-minute TTL, so an
  // unthrottled caller can flood the namespace (and the user_code space) for
  // free. The app_token is shared by every install, so it cannot be the key.
  const clientIp = request.headers.get("CF-Connecting-IP") ?? "unknown";
  if (await isRateLimited(env, `device_start:${clientIp}`, 10)) {
    return errorJson("rate_limited", 429);
  }

  const body = await readJson<{ app_token?: string }>(request);
  if (!body?.app_token) return errorJson("missing_app_token", 400);

  const app = await resolveApp(env, body.app_token);
  if (!app) return errorJson("invalid_app_token", 401);

  const device_code = randomToken(16);
  const user_code = humanCode(8);

  const state: DeviceCodeState = {
    status: "pending",
    user_code,
    appId: app.app_id,
    startedAt: new Date().toISOString(),
  };
  await env.DEVICE_CODES.put(deviceKey(device_code), JSON.stringify(state), {
    expirationTtl: DEVICE_CODE_TTL_SEC,
  });
  await env.DEVICE_CODES.put(userCodeKey(user_code), JSON.stringify({ device_code }), {
    expirationTtl: DEVICE_CODE_TTL_SEC,
  });

  const origin = new URL(request.url).origin;
  return json({
    device_code,
    user_code,
    verification_uri: `${origin}/approve`,
    interval_sec: POLL_INTERVAL_SEC,
    expires_in_sec: DEVICE_CODE_TTL_SEC,
  });
}

// POST /auth/device/poll — body {device_code, app_token}
export async function devicePoll(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ device_code?: string; app_token?: string }>(request);
  if (!body?.device_code) return errorJson("missing_device_code", 400);
  if (!body?.app_token) return errorJson("missing_app_token", 400);

  if (await isRateLimited(env, `poll:${body.device_code}`, 10)) {
    return errorJson("rate_limited", 429);
  }

  // Polling must present the same registered (unrevoked) app credential the
  // pairing was started with — otherwise a leaked device_code alone is enough
  // to collect the minted user_token.
  const app = await resolveApp(env, body.app_token);
  if (!app) return errorJson("invalid_app_token", 401);

  const raw = await env.DEVICE_CODES.get(deviceKey(body.device_code));
  if (!raw) return json({ status: "expired" });

  const state = JSON.parse(raw) as DeviceCodeState;
  if (state.appId !== app.app_id) return errorJson("app_mismatch", 403);

  if (state.status === "pending") return json({ status: "pending" });

  // Approved — mint a fresh single-use user_token and delete the pairing state.
  if (!state.user_id) return errorJson("approved_without_user", 500);

  const user_token = randomToken(16);
  const token_hash = await sha256Hex(user_token);
  const now = new Date().toISOString();

  // Concurrency guard. The KV read + delete below is not atomic, so two polls
  // racing on the same approved device_code could each mint a valid token.
  // Deriving token_id deterministically from the device_code makes them collide
  // on the primary key: ON CONFLICT DO NOTHING lets exactly one insert land.
  const token_id = (await sha256Hex(`poll:${body.device_code}`)).slice(0, 32);
  // This mint is the root of a new rotation chain — lineage_id = its own token_id.
  const insert = await env.DB.prepare(
    `INSERT INTO user_tokens (token_id, user_id, token_hash, rotated_from, lineage_id, created_at)
     VALUES (?, ?, ?, NULL, ?, ?) ON CONFLICT(token_id) DO NOTHING`,
  )
    .bind(token_id, state.user_id, token_hash, token_id, now)
    .run();

  // Zero rows changed → a concurrent poll already claimed this device_code and
  // received the only token that will ever be minted for it. This caller lost
  // the race, so report the code as spent rather than handing back a token
  // whose hash was never stored.
  if (!insert.meta?.changes) return json({ status: "expired" });

  // Single-use: delete both KV entries so the code can't be replayed.
  await env.DEVICE_CODES.delete(deviceKey(body.device_code));
  await env.DEVICE_CODES.delete(userCodeKey(state.user_code));

  return json({ status: "approved", user_token });
}

// POST /auth/token — body {app_token, user_token}
export async function tokenExchange(request: Request, env: Env): Promise<Response> {
  const body = await readJson<{ app_token?: string; user_token?: string }>(request);
  if (!body?.app_token || !body?.user_token) return errorJson("missing_credentials", 400);

  const app = await resolveApp(env, body.app_token);
  if (!app) return errorJson("invalid_app_token", 401);

  const token_hash = await sha256Hex(body.user_token);
  if (await isRateLimited(env, `token:${token_hash}`, 5)) {
    return errorJson("rate_limited", 429);
  }

  const existing = await env.DB.prepare(
    `SELECT token_id, user_id, lineage_id, revoked_at FROM user_tokens WHERE token_hash = ?`,
  )
    .bind(token_hash)
    .first<{ token_id: string; user_id: string; lineage_id: string; revoked_at: string | null }>();

  if (!existing) return errorJson("invalid_user_token", 401);

  // Reuse of an already-rotated token → theft/replay signal. Revoke only this
  // device's rotation chain (lineage_id) — a different device's token for the
  // same user is a different lineage and stays unaffected. Also cut off any
  // access-token JWT already minted from the compromised chain: tokens_valid_after
  // closes the window between detection and that token's natural 15-min expiry.
  if (existing.revoked_at) {
    const now = new Date().toISOString();
    await env.DB.batch([
      env.DB.prepare(
        `UPDATE user_tokens SET revoked_at = ? WHERE user_id = ? AND lineage_id = ? AND revoked_at IS NULL`,
      ).bind(now, existing.user_id, existing.lineage_id),
      env.DB.prepare(`UPDATE users SET tokens_valid_after = ? WHERE user_id = ?`).bind(
        now,
        existing.user_id,
      ),
    ]);
    return errorJson("token_reuse_detected", 401);
  }

  // Subscription gate — reject before minting if not active.
  const sub = await env.DB.prepare(
    `SELECT tier, status FROM subscriptions WHERE user_id = ?`,
  )
    .bind(existing.user_id)
    .first<{ tier: string; status: string }>();
  const tier = sub?.tier ?? "free";
  const status = sub?.status ?? "active";
  if (status !== "active") return errorJson("subscription_inactive", 403);

  // Rotate: revoke the presented token, mint + insert a replacement.
  const now = new Date().toISOString();
  const rotated_user_token = randomToken(16);
  const rotated_hash = await sha256Hex(rotated_user_token);
  await env.DB.prepare(
    `UPDATE user_tokens SET revoked_at = ?, last_used_at = ? WHERE token_id = ?`,
  )
    .bind(now, now, existing.token_id)
    .run();
  await env.DB.prepare(
    `INSERT INTO user_tokens (token_id, user_id, token_hash, rotated_from, lineage_id, created_at)
     VALUES (?, ?, ?, ?, ?, ?)`,
  )
    .bind(newId(), existing.user_id, rotated_hash, existing.token_id, existing.lineage_id, now)
    .run();

  const { token: access_token } = await signAccessToken(env.JWT_HMAC_SECRET, {
    user_id: existing.user_id,
    tier,
    app_id: app.app_id,
  });

  return json({
    access_token,
    user_token: rotated_user_token,
    expires_in_sec: ACCESS_TOKEN_TTL_SEC,
  });
}

// Get-or-create the user + subscription rows for an Access-verified email.
async function getOrCreateUser(env: Env, email: string): Promise<string> {
  const now = new Date().toISOString();
  const existing = await env.DB.prepare(`SELECT user_id FROM users WHERE email = ?`)
    .bind(email)
    .first<{ user_id: string }>();
  if (existing) return existing.user_id;

  const user_id = newId();
  // ON CONFLICT guards the race where two approvals land for the same email.
  await env.DB.prepare(
    `INSERT INTO users (user_id, email, display_name, created_at, last_seen_at)
     VALUES (?, ?, NULL, ?, ?) ON CONFLICT(email) DO NOTHING`,
  )
    .bind(user_id, email, now, now)
    .run();
  const row = await env.DB.prepare(`SELECT user_id FROM users WHERE email = ?`)
    .bind(email)
    .first<{ user_id: string }>();
  const resolvedId = row?.user_id ?? user_id;

  await env.DB.prepare(
    `INSERT INTO subscriptions (user_id, tier, status, updated_at)
     VALUES (?, 'free', 'active', ?) ON CONFLICT(user_id) DO NOTHING`,
  )
    .bind(resolvedId, now)
    .run();
  return resolvedId;
}

// GET/POST /approve — human-facing approval page (Access-gated in production).
export async function approve(request: Request, env: Env): Promise<Response> {
  const email = await resolveAccessEmail(request, env);
  if (!email) {
    return html(`<!doctype html><meta charset="utf-8"><title>Approve device</title>
<h1>Not authenticated</h1><p>Cloudflare Access authentication is required.</p>`, 401);
  }

  const url = new URL(request.url);

  if (request.method === "GET") {
    const user_code = (url.searchParams.get("user_code") ?? "").trim().toUpperCase();
    if (!user_code) {
      return html(`<!doctype html><meta charset="utf-8"><title>Approve device</title>
<h1>Approve a device</h1>
<form method="GET" action="/approve">
  <label>Enter the code shown in SimHub:
    <input name="user_code" autofocus required></label>
  <button type="submit">Continue</button>
</form>`);
    }
    // CSRF double-submit: a per-approval random token goes into BOTH a
    // SameSite=Strict HttpOnly cookie and a hidden form field; the POST must
    // present a matching pair. A cross-site attacker page can neither read the
    // cookie nor get the browser to send it (SameSite=Strict), so it cannot
    // forge an approval for a victim's Access session.
    const csrf = randomToken(16);

    // Surface which app requested this pairing and when, so a human staring at
    // an unfamiliar code (someone else's, or a stale one from a prior session)
    // has enough context to notice it isn't theirs, rather than approving blind.
    let contextHtml = "";
    const reverseForContext = await env.DEVICE_CODES.get(userCodeKey(user_code));
    if (reverseForContext) {
      const { device_code: dcForContext } = JSON.parse(reverseForContext) as { device_code: string };
      const rawForContext = await env.DEVICE_CODES.get(deviceKey(dcForContext));
      if (rawForContext) {
        const ctxState = JSON.parse(rawForContext) as DeviceCodeState;
        contextHtml = `<p class="pairing-context">Requested by app <strong>${escapeHtml(ctxState.appId)}</strong> at <strong>${escapeHtml(ctxState.startedAt)}</strong></p>`;
      }
    }

    return html(
      `<!doctype html><meta charset="utf-8"><title>Approve device</title>
<h1>Approve device</h1>
<p>Signed in as <strong>${escapeHtml(email)}</strong></p>
<p>Approve pairing for code <strong>${escapeHtml(user_code)}</strong>?</p>
${contextHtml}
<form method="POST" action="/approve">
  <input type="hidden" name="user_code" value="${escapeHtml(user_code)}">
  <input type="hidden" name="csrf" value="${escapeHtml(csrf)}">
  <button type="submit">Approve</button>
</form>`,
      200,
      {
        "Set-Cookie": `ss_approve_csrf=${csrf}; Path=/approve; HttpOnly; Secure; SameSite=Strict; Max-Age=600`,
      },
    );
  }

  // POST — perform the approval. Verify Origin + CSRF token before touching
  // any pairing state.
  const origin = request.headers.get("Origin");
  if (origin && origin !== url.origin) {
    return html(`<h1>Forbidden</h1><p>Cross-origin approval rejected.</p>`, 403);
  }

  const form = await readBody<{ user_code?: string; csrf?: string }>(request);
  const user_code = (form?.user_code ?? "").trim().toUpperCase();
  const formCsrf = form?.csrf ?? "";
  const cookieCsrf =
    /(?:^|;\s*)ss_approve_csrf=([^;]+)/.exec(request.headers.get("Cookie") ?? "")?.[1] ?? "";
  if (!formCsrf || !cookieCsrf || !timingSafeEqualStr(formCsrf, cookieCsrf)) {
    return html(`<h1>Forbidden</h1><p>Invalid or missing CSRF token. Reload the approval page and try again.</p>`, 403);
  }

  if (!user_code) return html(`<h1>Missing code</h1>`, 400);

  const reverse = await env.DEVICE_CODES.get(userCodeKey(user_code));
  if (!reverse) {
    return html(`<!doctype html><meta charset="utf-8"><title>Approve device</title>
<h1>Code not found or expired</h1>`, 404);
  }
  const { device_code } = JSON.parse(reverse) as { device_code: string };
  const raw = await env.DEVICE_CODES.get(deviceKey(device_code));
  if (!raw) {
    return html(`<!doctype html><meta charset="utf-8"><title>Approve device</title>
<h1>Code not found or expired</h1>`, 404);
  }

  const user_id = await getOrCreateUser(env, email);
  const state = JSON.parse(raw) as DeviceCodeState;
  state.status = "approved";
  state.user_id = user_id;

  // Keep the same TTL window (approx — recompute remaining is not worth it here;
  // re-issue the full window, still bounded by the original expiry semantics).
  await env.DEVICE_CODES.put(deviceKey(device_code), JSON.stringify(state), {
    expirationTtl: DEVICE_CODE_TTL_SEC,
  });

  return html(`<!doctype html><meta charset="utf-8"><title>Device approved</title>
<h1>Device approved</h1>
<p>You can return to SimHub — pairing will complete shortly.</p>`);
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
