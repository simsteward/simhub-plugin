import { describe, it, expect, beforeEach } from "vitest";
import { tokenExchange, deviceStart, devicePoll, approve } from "../src/auth";
import { verifyAccessToken } from "../src/jwt";
import { randomToken, sha256Hex } from "../src/crypto";
import { testEnv, applySchema, seedApp, seedUserWithToken, jsonRequest } from "./helpers";

const APP_TOKEN = "raw-app-token";

beforeEach(async () => {
  await applySchema();
  await seedApp("test-app", APP_TOKEN);
});

async function currentTokenHashes(userId: string) {
  const { results } = await testEnv.DB.prepare(
    `SELECT token_hash, revoked_at FROM user_tokens WHERE user_id = ? ORDER BY created_at`,
  )
    .bind(userId)
    .all<{ token_hash: string; revoked_at: string | null }>();
  return results ?? [];
}

describe("POST /auth/token — issuance + rotation", () => {
  it("exchanges a valid user_token: rotates it, revokes the old, issues a JWT", async () => {
    const userId = await seedUserWithToken("a@example.com", "user-token-1");

    const res = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "user-token-1",
      }),
      testEnv,
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as any;

    // A fresh, different user_token is returned.
    expect(body.user_token).toBeTruthy();
    expect(body.user_token).not.toBe("user-token-1");
    expect(body.expires_in_sec).toBe(900);

    // The access_token is a valid JWT for this user.
    const claims = await verifyAccessToken(testEnv.JWT_HMAC_SECRET, body.access_token);
    expect(claims?.sub).toBe(userId);
    expect(claims?.tier).toBe("free");
    expect(claims?.app_id).toBe("test-app");

    // Old token is revoked; a new unrevoked row exists.
    const rows = await currentTokenHashes(userId);
    expect(rows.length).toBe(2);
    const revoked = rows.filter((r) => r.revoked_at !== null);
    const active = rows.filter((r) => r.revoked_at === null);
    expect(revoked.length).toBe(1);
    expect(active.length).toBe(1);
  });

  it("rejects an unknown user_token with 401", async () => {
    const res = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "never-issued",
      }),
      testEnv,
    );
    expect(res.status).toBe(401);
    expect(((await res.json()) as any).error).toBe("invalid_user_token");
  });

  it("rejects an invalid app_token with 401", async () => {
    await seedUserWithToken("b@example.com", "user-token-b");
    const res = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: "wrong-app-token",
        user_token: "user-token-b",
      }),
      testEnv,
    );
    expect(res.status).toBe(401);
    expect(((await res.json()) as any).error).toBe("invalid_app_token");
  });

  it("rejects an inactive subscription with 403", async () => {
    await seedUserWithToken("c@example.com", "user-token-c", "pro", "canceled");
    const res = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "user-token-c",
      }),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("subscription_inactive");
  });
});

describe("POST /auth/token — reuse detection", () => {
  it("presenting an already-revoked token mass-revokes and returns 401", async () => {
    const userId = await seedUserWithToken("d@example.com", "user-token-d");

    // First exchange succeeds and rotates.
    const first = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "user-token-d",
      }),
      testEnv,
    );
    expect(first.status).toBe(200);
    const firstBody = (await first.json()) as any;
    const rotated = firstBody.user_token as string;

    // Re-presenting the ORIGINAL (now revoked) token is a reuse/theft signal.
    const reuse = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "user-token-d",
      }),
      testEnv,
    );
    expect(reuse.status).toBe(401);
    expect(((await reuse.json()) as any).error).toBe("token_reuse_detected");

    // Every token for the user is now revoked — including the freshly rotated one.
    const rows = await currentTokenHashes(userId);
    expect(rows.every((r) => r.revoked_at !== null)).toBe(true);

    // The rotated token no longer works either.
    const afterMassRevoke = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: rotated,
      }),
      testEnv,
    );
    expect(afterMassRevoke.status).toBe(401);
    expect(((await afterMassRevoke.json()) as any).error).toBe("token_reuse_detected");
  });

  it("reuse-detection is scoped to the compromised lineage — a second device's token still works (H1)", async () => {
    const userId = await seedUserWithToken("lineage-a@example.com", "lineage-a-token");
    // A second, independent lineage for the SAME user — a real second device
    // paired via its own root mint, not a hand-crafted row.
    const now = new Date().toISOString();
    const lineageBTokenId = "lineage-b-root";
    const lineageBHash = await sha256Hex("lineage-b-token");
    await testEnv.DB.prepare(
      `INSERT INTO user_tokens (token_id, user_id, token_hash, lineage_id, created_at) VALUES (?,?,?,?,?)`,
    )
      .bind(lineageBTokenId, userId, lineageBHash, lineageBTokenId, now)
      .run();

    // Rotate lineage A once, then replay the original (revoked) token — trips reuse-detection.
    const firstExchange = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "lineage-a-token",
      }),
      testEnv,
    );
    expect(firstExchange.status).toBe(200);
    const reuse = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "lineage-a-token",
      }),
      testEnv,
    );
    expect(reuse.status).toBe(401);
    expect(((await reuse.json()) as any).error).toBe("token_reuse_detected");

    // Lineage B was never touched — its token still exchanges successfully.
    // (If lineage A's revocation had swept in lineage B, this token's row would
    // already show revoked_at set, and tokenExchange would answer 401
    // token_reuse_detected instead of minting a fresh access token.)
    const lineageBExchange = await tokenExchange(
      jsonRequest("https://w/auth/token", "POST", {
        app_token: APP_TOKEN,
        user_token: "lineage-b-token",
      }),
      testEnv,
    );
    expect(lineageBExchange.status).toBe(200);
  });
});

describe("POST /auth/device/poll — hardening", () => {
  const DEV_EMAIL = "poller@example.com";
  const devEnv = { ...testEnv, ACCESS_DEV_BYPASS: "1" } as typeof testEnv;

  // Runs a full start → approve cycle and returns the device_code ready to poll.
  async function approvedDeviceCode(): Promise<string> {
    const startRes = await deviceStart(
      jsonRequest("https://w/auth/device/start", "POST", { app_token: APP_TOKEN }),
      testEnv,
    );
    const { device_code, user_code } = (await startRes.json()) as any;

    const params = new URLSearchParams({ user_code, csrf: "tok" });
    const approveRes = await approve(
      new Request("https://w/approve", {
        method: "POST",
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
          "X-Dev-Access-Email": DEV_EMAIL,
          Cookie: "ss_approve_csrf=tok",
          Origin: "https://w",
        },
        body: params.toString(),
      }),
      devEnv,
    );
    expect(approveRes.status).toBe(200);
    return device_code;
  }

  it("rejects a poll with no app_token", async () => {
    const device_code = await approvedDeviceCode();
    const res = await devicePoll(
      jsonRequest("https://w/auth/device/poll", "POST", { device_code }),
      testEnv,
    );
    expect(res.status).toBe(400);
    expect(((await res.json()) as any).error).toBe("missing_app_token");
  });

  it("rejects a poll with an unknown app_token", async () => {
    const device_code = await approvedDeviceCode();
    const res = await devicePoll(
      jsonRequest("https://w/auth/device/poll", "POST", {
        device_code,
        app_token: "not-a-real-app-token",
      }),
      testEnv,
    );
    expect(res.status).toBe(401);
    expect(((await res.json()) as any).error).toBe("invalid_app_token");
  });

  it("rejects a poll from a different app than the one that started the pairing", async () => {
    const device_code = await approvedDeviceCode();
    const otherToken = await seedApp("other-app", "other-app-token");
    const res = await devicePoll(
      jsonRequest("https://w/auth/device/poll", "POST", {
        device_code,
        app_token: otherToken,
      }),
      testEnv,
    );
    expect(res.status).toBe(403);
    expect(((await res.json()) as any).error).toBe("app_mismatch");
  });

  it("mints exactly one user_token for an approved code, then reports expired", async () => {
    const device_code = await approvedDeviceCode();
    const poll = () =>
      devicePoll(
        jsonRequest("https://w/auth/device/poll", "POST", { device_code, app_token: APP_TOKEN }),
        testEnv,
      );

    const first = (await (await poll()).json()) as any;
    expect(first.status).toBe("approved");
    expect(first.user_token).toBeTruthy();

    // KV state is gone → replaying the code yields nothing.
    const second = (await (await poll()).json()) as any;
    expect(second.status).toBe("expired");
    expect(second.user_token).toBeUndefined();
  });

  it("concurrent polls mint a single token — the loser gets expired, not a dud token", async () => {
    const device_code = await approvedDeviceCode();
    const poll = () =>
      devicePoll(
        jsonRequest("https://w/auth/device/poll", "POST", { device_code, app_token: APP_TOKEN }),
        testEnv,
      );

    const [a, b] = await Promise.all([poll(), poll()]);
    const bodies = [(await a.json()) as any, (await b.json()) as any];
    const approved = bodies.filter((x) => x.status === "approved");
    const expired = bodies.filter((x) => x.status === "expired");

    expect(approved.length).toBe(1);
    expect(expired.length).toBe(1);
    expect(expired[0].user_token).toBeUndefined();

    // Exactly one token row exists for the pairing — no double mint.
    const rows = await testEnv.DB.prepare(
      `SELECT COUNT(*) AS n FROM user_tokens WHERE user_id = (SELECT user_id FROM users WHERE email = ?)`,
    )
      .bind(DEV_EMAIL)
      .first<{ n: number }>();
    expect(rows?.n).toBe(1);
  });
});

describe("randomToken entropy", () => {
  it("encodes the full bit-stream — 16 bytes yields 22 base64url chars (128 bits)", () => {
    const t = randomToken(16);
    expect(t.length).toBe(22);
    expect(t).toMatch(/^[A-Za-z0-9_-]+$/);
  });

  it("produces distinct tokens across many draws", () => {
    const seen = new Set(Array.from({ length: 500 }, () => randomToken(16)));
    expect(seen.size).toBe(500);
  });
});

describe("GET/POST /approve — CSRF protection", () => {
  const DEV_EMAIL = "approver@example.com";
  const devEnv = { ...testEnv, ACCESS_DEV_BYPASS: "1" } as typeof testEnv;

  async function startPairing(): Promise<string> {
    const res = await deviceStart(
      jsonRequest("https://w/auth/device/start", "POST", { app_token: APP_TOKEN }),
      testEnv,
    );
    return ((await res.json()) as any).user_code as string;
  }

  function approvePost(
    userCode: string,
    opts: { csrf?: string; cookie?: string; origin?: string } = {},
  ): Request {
    const params = new URLSearchParams({ user_code: userCode });
    if (opts.csrf !== undefined) params.set("csrf", opts.csrf);
    const headers: Record<string, string> = {
      "Content-Type": "application/x-www-form-urlencoded",
      "X-Dev-Access-Email": DEV_EMAIL,
    };
    if (opts.cookie) headers["Cookie"] = opts.cookie;
    if (opts.origin) headers["Origin"] = opts.origin;
    return new Request("https://w/approve", { method: "POST", headers, body: params.toString() });
  }

  it("GET confirm page sets a SameSite=Strict CSRF cookie matching the hidden field", async () => {
    const userCode = await startPairing();
    const res = await approve(
      new Request(`https://w/approve?user_code=${userCode}`, {
        headers: { "X-Dev-Access-Email": DEV_EMAIL },
      }),
      devEnv,
    );
    expect(res.status).toBe(200);
    const setCookie = res.headers.get("Set-Cookie") ?? "";
    expect(setCookie).toContain("ss_approve_csrf=");
    expect(setCookie).toContain("SameSite=Strict");
    expect(setCookie).toContain("HttpOnly");
    const cookieToken = /ss_approve_csrf=([^;]+)/.exec(setCookie)![1];
    const body = await res.text();
    expect(body).toContain(`name="csrf" value="${cookieToken}"`);
  });

  it("rejects a POST without a CSRF token/cookie pair (forged cross-site form)", async () => {
    const userCode = await startPairing();
    const res = await approve(approvePost(userCode), devEnv);
    expect(res.status).toBe(403);
  });

  it("rejects a POST whose form token does not match the cookie", async () => {
    const userCode = await startPairing();
    const res = await approve(
      approvePost(userCode, { csrf: "attacker-guess", cookie: "ss_approve_csrf=real-value" }),
      devEnv,
    );
    expect(res.status).toBe(403);
  });

  it("rejects a cross-origin POST even with a valid token pair", async () => {
    const userCode = await startPairing();
    const res = await approve(
      approvePost(userCode, {
        csrf: "tok",
        cookie: "ss_approve_csrf=tok",
        origin: "https://evil.example",
      }),
      devEnv,
    );
    expect(res.status).toBe(403);
  });

  it("approves with a matching token pair and same-origin POST", async () => {
    const userCode = await startPairing();
    const res = await approve(
      approvePost(userCode, { csrf: "tok", cookie: "ss_approve_csrf=tok", origin: "https://w" }),
      devEnv,
    );
    expect(res.status).toBe(200);
    expect(await res.text()).toContain("Device approved");
  });
});
