import { describe, it, expect, beforeEach } from "vitest";
import { createUser, patchSubscription, adminPage } from "../src/admin";
import { testEnv, applySchema, jsonRequest } from "./helpers";

// Matches the ADMIN_EMAILS binding in vitest.config.ts. ADMIN is deliberately a
// different case than the binding to prove the comparison is case-insensitive.
const ADMIN = "admin@example.com";
const NON_ADMIN = "someone-else@example.com";

beforeEach(async () => {
  await applySchema();
});

function formRequest(
  url: string,
  fields: Record<string, string>,
  opts: { email?: string; cookie?: string; origin?: string } = {},
): Request {
  const headers: Record<string, string> = {
    "Content-Type": "application/x-www-form-urlencoded",
    "X-Dev-Access-Email": opts.email ?? ADMIN,
  };
  if (opts.cookie) headers["Cookie"] = opts.cookie;
  if (opts.origin) headers["Origin"] = opts.origin;
  return new Request(url, {
    method: "POST",
    headers,
    body: new URLSearchParams(fields).toString(),
  });
}

function apiRequest(
  url: string,
  method: string,
  body: unknown,
  email: string = ADMIN,
): Request {
  return jsonRequest(url, method, body, { "X-Dev-Access-Email": email });
}

async function seedUser(userId: string, email: string): Promise<void> {
  const now = new Date().toISOString();
  await testEnv.DB.prepare(`INSERT INTO users (user_id, email, created_at) VALUES (?,?,?)`)
    .bind(userId, email, now)
    .run();
}

describe("admin allowlist", () => {
  it("rejects an unauthenticated caller with 401", async () => {
    // No dev-access email header and no Access assertion → not authenticated.
    const res = await createUser(
      jsonRequest("https://w/admin/users", "POST", { email: "new@example.com" }),
      testEnv,
    );
    expect(res.status).toBe(401);
    expect(((await res.json()) as any).error).toBe("access_required");
  });

  it("rejects an Access-authenticated but non-allowlisted email with 403", async () => {
    const res = await createUser(
      apiRequest("https://w/admin/users", "POST", { email: "new@example.com" }, NON_ADMIN),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("admin_required");

    // Nothing was written.
    const row = await testEnv.DB.prepare(`SELECT user_id FROM users WHERE email = ?`)
      .bind("new@example.com")
      .first();
    expect(row).toBeNull();
  });

  it("matches the allowlist case-insensitively and ignores surrounding spaces", async () => {
    const res = await createUser(
      apiRequest("https://w/admin/users", "POST", { email: "cased@example.com" }, "  ADMIN@EXAMPLE.COM  "),
      testEnv,
    );
    expect(res.status).toBe(200);
  });

  it("denies everyone when ADMIN_EMAILS is unset", async () => {
    const noAdmins = { ...testEnv, ADMIN_EMAILS: undefined } as typeof testEnv;
    const res = await createUser(
      apiRequest("https://w/admin/users", "POST", { email: "new@example.com" }),
      noAdmins,
    );
    expect(res.status).toBe(403);
  });

  it("blocks a non-allowlisted email from the admin page", async () => {
    const res = await adminPage(
      new Request("https://w/admin", { headers: { "X-Dev-Access-Email": NON_ADMIN } }),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(await res.text()).toContain("not on the admin allowlist");
  });
});

describe("admin page CSRF token", () => {
  it("sets an ss_admin_csrf cookie and embeds the same token in both forms", async () => {
    await seedUser("u-1", "u1@example.com");
    const res = await adminPage(
      new Request("https://w/admin", { headers: { "X-Dev-Access-Email": ADMIN } }),
      testEnv,
    );
    expect(res.status).toBe(200);

    const setCookie = res.headers.get("Set-Cookie") ?? "";
    expect(setCookie).toContain("ss_admin_csrf=");
    expect(setCookie).toContain("Path=/admin");
    expect(setCookie).toContain("SameSite=Strict");
    expect(setCookie).toContain("HttpOnly");

    const token = /ss_admin_csrf=([^;]+)/.exec(setCookie)![1];
    const body = await res.text();
    // Both the create-user form and the per-row subscription form carry it.
    const hidden = body.match(new RegExp(`name="csrf" value="${token}"`, "g")) ?? [];
    expect(hidden.length).toBe(2);
    expect(body).toContain(`action="/admin/subscriptions/u-1?_html=1"`);
  });
});

describe("admin form POST CSRF enforcement", () => {
  it("rejects a form POST with no CSRF token at all", async () => {
    const res = await createUser(
      formRequest("https://w/admin/users?_html=1", { email: "a@example.com" }),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("invalid_csrf");
  });

  it("rejects a form POST whose token does not match the cookie", async () => {
    const res = await createUser(
      formRequest(
        "https://w/admin/users?_html=1",
        { email: "a@example.com", csrf: "attacker-guess" },
        { cookie: "ss_admin_csrf=real-value" },
      ),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("invalid_csrf");
  });

  it("rejects a cross-origin form POST even with a matching token pair", async () => {
    const res = await createUser(
      formRequest(
        "https://w/admin/users?_html=1",
        { email: "a@example.com", csrf: "tok" },
        { cookie: "ss_admin_csrf=tok", origin: "https://evil.example" },
      ),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("forbidden_origin");
  });

  it("accepts a same-origin form POST with a valid token pair from an allowlisted admin", async () => {
    const res = await createUser(
      formRequest(
        "https://w/admin/users?_html=1",
        { email: "created@example.com", csrf: "tok" },
        { cookie: "ss_admin_csrf=tok", origin: "https://w" },
      ),
      testEnv,
    );
    // _html=1 → 303 redirect back to /admin rather than a JSON body.
    expect(res.status).toBe(303);

    const row = await testEnv.DB.prepare(`SELECT user_id FROM users WHERE email = ?`)
      .bind("created@example.com")
      .first<{ user_id: string }>();
    expect(row?.user_id).toBeTruthy();
  });

  it("enforces the same CSRF pair on the per-row subscription form", async () => {
    await seedUser("u-sub", "sub@example.com");

    const noToken = await patchSubscription(
      formRequest("https://w/admin/subscriptions/u-sub?_html=1", { status: "canceled" }),
      testEnv,
      "u-sub",
    );
    expect(noToken.status).toBe(403);

    const ok = await patchSubscription(
      formRequest(
        "https://w/admin/subscriptions/u-sub?_html=1",
        { status: "canceled", tier: "pro", csrf: "tok" },
        { cookie: "ss_admin_csrf=tok", origin: "https://w" },
      ),
      testEnv,
      "u-sub",
    );
    expect(ok.status).toBe(303);

    const sub = await testEnv.DB.prepare(
      `SELECT tier, status FROM subscriptions WHERE user_id = 'u-sub'`,
    ).first<any>();
    expect(sub.status).toBe("canceled");
    expect(sub.tier).toBe("pro");
  });

  it("accepts a JSON PATCH from an allowlisted admin without a CSRF token", async () => {
    // application/json is itself the CSRF defense — a cross-site HTML form
    // cannot produce that content type without a CORS preflight.
    await seedUser("u-json", "json@example.com");
    const res = await patchSubscription(
      apiRequest("https://w/admin/subscriptions/u-json", "PATCH", { status: "past_due" }),
      testEnv,
      "u-json",
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as any;
    expect(body.subscription.status).toBe("past_due");
  });

  it("rejects a text/plain body (CORS-safelisted, so forgeable) with 415", async () => {
    await seedUser("u-plain", "plain@example.com");
    const res = await patchSubscription(
      new Request("https://w/admin/subscriptions/u-plain", {
        method: "PATCH",
        headers: { "Content-Type": "text/plain", "X-Dev-Access-Email": ADMIN },
        body: JSON.stringify({ status: "canceled" }),
      }),
      testEnv,
      "u-plain",
    );
    expect(res.status).toBe(415);
  });
});

describe("patchSubscription validation", () => {
  beforeEach(async () => {
    await seedUser("u-v", "v@example.com");
  });

  it("rejects an unknown status with 400 invalid_status", async () => {
    const res = await patchSubscription(
      apiRequest("https://w/admin/subscriptions/u-v", "PATCH", { status: "totally-made-up" }),
      testEnv,
      "u-v",
    );
    expect(res.status).toBe(400);
    expect(((await res.json()) as any).error).toBe("invalid_status");
  });

  it("rejects an unknown tier with 400 invalid_tier", async () => {
    const res = await patchSubscription(
      apiRequest("https://w/admin/subscriptions/u-v", "PATCH", { tier: "platinum" }),
      testEnv,
      "u-v",
    );
    expect(res.status).toBe(400);
    expect(((await res.json()) as any).error).toBe("invalid_tier");
  });

  it("accepts every whitelisted status and tier", async () => {
    for (const status of ["active", "canceled", "past_due"]) {
      const res = await patchSubscription(
        apiRequest("https://w/admin/subscriptions/u-v", "PATCH", { status }),
        testEnv,
        "u-v",
      );
      expect(res.status).toBe(200);
    }
    for (const tier of ["free", "pro"]) {
      const res = await patchSubscription(
        apiRequest("https://w/admin/subscriptions/u-v", "PATCH", { tier }),
        testEnv,
        "u-v",
      );
      expect(res.status).toBe(200);
    }
  });

  it("returns 404 for a user that does not exist and writes no orphan row", async () => {
    const res = await patchSubscription(
      apiRequest("https://w/admin/subscriptions/ghost", "PATCH", { status: "active" }),
      testEnv,
      "ghost",
    );
    expect(res.status).toBe(404);
    expect(((await res.json()) as any).error).toBe("user_not_found");

    const orphan = await testEnv.DB.prepare(
      `SELECT user_id FROM subscriptions WHERE user_id = 'ghost'`,
    ).first();
    expect(orphan).toBeNull();
  });
});
