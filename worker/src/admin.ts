// Admin routes — Cloudflare Access-gated in production (same dev-bypass as
// /approve for local testing), then narrowed further to an explicit email
// allowlist (ADMIN_EMAILS).
import type { Env } from "./env";
import { json, errorJson, html, readBody, wantsHtml } from "./http";
import { resolveAccessEmail } from "./access-verify";
import { newId, randomToken, timingSafeEqualStr } from "./crypto";

// Whitelisted subscription field values. Anything else is rejected outright
// rather than written through to the DB.
const VALID_STATUS = new Set(["active", "canceled", "past_due"]);
const VALID_TIER = new Set(["free", "pro"]);

const ADMIN_CSRF_COOKIE = "ss_admin_csrf";

// True when `email` appears in the ADMIN_EMAILS allowlist (case-insensitive,
// whitespace-trimmed). An unset/empty allowlist grants no one admin.
function isAdminEmail(email: string, env: Env): boolean {
  const needle = email.trim().toLowerCase();
  if (!needle) return false;
  return (env.ADMIN_EMAILS ?? "")
    .split(",")
    .map((e) => e.trim().toLowerCase())
    .filter((e) => e.length > 0)
    .includes(needle);
}

// Guard: require an Access-verified email that is ALSO on the admin allowlist.
// Returns the email or an error Response. Being Access-authenticated is not by
// itself an admin grant — any employee/tenant behind the same Access app would
// otherwise get full control of users and subscriptions.
async function requireAccess(request: Request, env: Env): Promise<string | Response> {
  const email = await resolveAccessEmail(request, env);
  if (!email) return errorJson("access_required", 401);
  if (!isAdminEmail(email, env)) return errorJson("admin_required", 403);
  return email;
}

// CSRF gate for the admin HTML form POSTs. Mirrors /approve: a double-submit
// token that must be present in BOTH a SameSite=Strict HttpOnly cookie and a
// hidden form field, plus a same-origin check. A cross-site page can neither
// read the cookie nor cause the browser to send it, so it cannot forge an
// admin mutation against a victim's live Access session.
// Returns an error Response when the request must be rejected, else null.
function verifyAdminFormCsrf(request: Request, formCsrf: string): Response | null {
  const url = new URL(request.url);
  const origin = request.headers.get("Origin");
  if (origin && origin !== url.origin) return errorJson("forbidden_origin", 403);

  const cookieCsrf =
    new RegExp(`(?:^|;\\s*)${ADMIN_CSRF_COOKIE}=([^;]+)`).exec(
      request.headers.get("Cookie") ?? "",
    )?.[1] ?? "";
  if (!formCsrf || !cookieCsrf || !timingSafeEqualStr(formCsrf, cookieCsrf)) {
    return errorJson("invalid_csrf", 403);
  }
  return null;
}

// Applies the right CSRF defense for the request's content type.
//
//  - form-urlencoded (the admin HTML forms) → double-submit token + Origin.
//  - application/json (API callers)         → the content type IS the defense:
//    an HTML form can only send form-urlencoded, text/plain or multipart, so a
//    cross-site page cannot produce application/json without a CORS preflight,
//    which this Worker never answers. Non-JSON, non-form bodies are therefore
//    rejected outright — text/plain in particular is CORS-safelisted and would
//    otherwise be a forgeable path around the token check.
function guardCsrf(request: Request, formCsrf: string): Response | null {
  const contentType = request.headers.get("Content-Type") ?? "";
  if (contentType.includes("application/x-www-form-urlencoded")) {
    return verifyAdminFormCsrf(request, formCsrf);
  }
  if (!contentType.includes("application/json")) {
    return errorJson("unsupported_content_type", 415);
  }
  return null;
}

// POST /admin/users — {email, display_name?}
export async function createUser(request: Request, env: Env): Promise<Response> {
  const gate = await requireAccess(request, env);
  if (gate instanceof Response) return gate;
  const actingEmail = gate;

  const body = await readBody<{ email?: string; display_name?: string; csrf?: string }>(
    request,
  );
  if (!body) return errorJson("invalid_json", 400);

  const csrfError = guardCsrf(request, String(body.csrf ?? ""));
  if (csrfError) return csrfError;

  if (!body.email) return errorJson("missing_email", 400);

  const now = new Date().toISOString();
  const user_id = newId();
  await env.DB.prepare(
    `INSERT INTO users (user_id, email, display_name, created_at, last_seen_at)
     VALUES (?,?,?,?,NULL) ON CONFLICT(email) DO UPDATE SET
       display_name=excluded.display_name`,
  )
    .bind(user_id, body.email, body.display_name ?? null, now)
    .run();

  const row = await env.DB.prepare(`SELECT user_id FROM users WHERE email = ?`)
    .bind(body.email)
    .first<{ user_id: string }>();
  const resolvedId = row?.user_id ?? user_id;

  await env.DB.prepare(
    `INSERT INTO subscriptions (user_id, tier, status, updated_at)
     VALUES (?, 'free', 'active', ?) ON CONFLICT(user_id) DO NOTHING`,
  )
    .bind(resolvedId, now)
    .run();

  console.log("admin_action", {
    action: "create_user",
    acting_email: actingEmail,
    target_email: body.email,
    target_user_id: resolvedId,
  });

  if (wantsHtml(request)) return Response.redirect(new URL("/admin", request.url).toString(), 303);
  return json({ ok: true, user_id: resolvedId });
}

// PATCH /admin/subscriptions/{user_id} — {tier?, status?, current_period_end?}
export async function patchSubscription(
  request: Request,
  env: Env,
  userId: string,
): Promise<Response> {
  const gate = await requireAccess(request, env);
  if (gate instanceof Response) return gate;
  const actingEmail = gate;

  const body = await readBody<{
    tier?: string;
    status?: string;
    current_period_end?: string;
    csrf?: string;
  }>(request);
  if (!body) return errorJson("invalid_json", 400);

  const csrfError = guardCsrf(request, String(body.csrf ?? ""));
  if (csrfError) return csrfError;

  // Validate before touching the DB — the upsert would otherwise persist any
  // arbitrary string as a tier/status and quietly break the entitlement checks
  // that compare against these exact values.
  const tier = body.tier === "" ? undefined : body.tier;
  const status = body.status === "" ? undefined : body.status;
  if (status !== undefined && !VALID_STATUS.has(status)) return errorJson("invalid_status", 400);
  if (tier !== undefined && !VALID_TIER.has(tier)) return errorJson("invalid_tier", 400);

  // The subscriptions upsert would happily create a row for a user_id that does
  // not exist (a typo'd path segment), leaving an orphan. Require the user.
  const user = await env.DB.prepare(`SELECT user_id FROM users WHERE user_id = ?`)
    .bind(userId)
    .first<{ user_id: string }>();
  if (!user) return errorJson("user_not_found", 404);

  const now = new Date().toISOString();
  // Upsert so a subscription row is created if missing.
  await env.DB.prepare(
    `INSERT INTO subscriptions (user_id, tier, status, current_period_end, updated_at)
     VALUES (?, COALESCE(?, 'free'), COALESCE(?, 'active'), ?, ?)
     ON CONFLICT(user_id) DO UPDATE SET
       tier=COALESCE(excluded.tier, subscriptions.tier),
       status=COALESCE(excluded.status, subscriptions.status),
       current_period_end=COALESCE(excluded.current_period_end, subscriptions.current_period_end),
       updated_at=excluded.updated_at`,
  )
    .bind(userId, tier ?? null, status ?? null, body.current_period_end ?? null, now)
    .run();

  const row = await env.DB.prepare(
    `SELECT user_id, tier, status, current_period_end, updated_at
     FROM subscriptions WHERE user_id = ?`,
  )
    .bind(userId)
    .first();

  console.log("admin_action", {
    action: "patch_subscription",
    acting_email: actingEmail,
    target_user_id: userId,
    tier: tier ?? null,
    status: status ?? null,
  });

  if (wantsHtml(request)) return Response.redirect(new URL("/admin", request.url).toString(), 303);
  return json({ ok: true, subscription: row });
}

// GET /admin — minimal HTML admin page.
export async function adminPage(request: Request, env: Env): Promise<Response> {
  const gate = await requireAccess(request, env);
  if (gate instanceof Response) {
    const status = gate.status;
    return html(
      `<!doctype html><meta charset="utf-8"><title>Admin</title>
<h1>${status === 403 ? "Not authorized" : "Not authenticated"}</h1>
<p>${
        status === 403
          ? "This account is not on the admin allowlist."
          : "Cloudflare Access authentication is required."
      }</p>`,
      status,
    );
  }

  const { results } = await env.DB.prepare(
    `SELECT u.user_id, u.email, u.display_name,
            s.tier, s.status, s.current_period_end
     FROM users u LEFT JOIN subscriptions s ON s.user_id = u.user_id
     ORDER BY u.created_at DESC LIMIT 200`,
  ).all<{
    user_id: string;
    email: string;
    display_name: string | null;
    tier: string | null;
    status: string | null;
    current_period_end: string | null;
  }>();

  // One CSRF token per page render, planted in a SameSite=Strict cookie scoped
  // to /admin (which also covers /admin/users and /admin/subscriptions/*) and
  // echoed into every mutating form below.
  const csrf = randomToken(16);

  const rows = (results ?? [])
    .map(
      (u) => `<tr>
  <td><code>${esc(u.user_id)}</code></td>
  <td>${esc(u.email ?? "")}</td>
  <td>${esc(u.display_name ?? "")}</td>
  <td>${esc(u.tier ?? "")}</td>
  <td>${esc(u.status ?? "")}</td>
  <td>${esc(u.current_period_end ?? "")}</td>
  <td>
    <form method="POST" action="/admin/subscriptions/${encodeURIComponent(u.user_id)}?_html=1">
      <input type="hidden" name="csrf" value="${esc(csrf)}">
      <input name="tier" placeholder="tier" value="${esc(u.tier ?? "")}" size="8">
      <select name="status">
        <option${u.status === "active" ? " selected" : ""}>active</option>
        <option${u.status === "canceled" ? " selected" : ""}>canceled</option>
        <option${u.status === "past_due" ? " selected" : ""}>past_due</option>
      </select>
      <button type="submit">Update</button>
    </form>
  </td>
</tr>`,
    )
    .join("\n");

  return html(
    `<!doctype html><meta charset="utf-8"><title>Sim Steward Admin</title>
<style>body{font-family:system-ui,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}
td,th{border:1px solid #ccc;padding:.4rem .6rem;text-align:left;font-size:14px}
form{display:flex;gap:.3rem;margin:0}</style>
<h1>Users &amp; subscriptions</h1>
<table>
  <thead><tr><th>user_id</th><th>email</th><th>name</th><th>tier</th><th>status</th><th>period end</th><th>edit</th></tr></thead>
  <tbody>${rows}</tbody>
</table>
<hr><h2>Create user</h2>
<form method="POST" action="/admin/users?_html=1">
  <input type="hidden" name="csrf" value="${esc(csrf)}">
  <input name="email" placeholder="email" required>
  <input name="display_name" placeholder="display name">
  <button type="submit">Create</button>
</form>`,
    200,
    {
      "Set-Cookie": `${ADMIN_CSRF_COOKIE}=${csrf}; Path=/admin; HttpOnly; Secure; SameSite=Strict; Max-Age=3600`,
    },
  );
}

function esc(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}
