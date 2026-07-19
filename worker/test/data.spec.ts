import { describe, it, expect, beforeEach } from "vitest";
import { incidentsPush, putIncidentIndex, getIncidentIndex, getSession } from "../src/data";
import { signAccessToken } from "../src/jwt";
import { testEnv, applySchema, jsonRequest } from "./helpers";

const SUB = 555001;

async function bearer(userId = "user-x", tier = "free"): Promise<string> {
  const { token } = await signAccessToken(testEnv.JWT_HMAC_SECRET, {
    user_id: userId,
    tier,
    app_id: "test-app",
  });
  return token;
}

async function seedActiveUser(userId = "user-x"): Promise<void> {
  const now = new Date().toISOString();
  await testEnv.DB.prepare(
    `INSERT INTO users (user_id, email, created_at) VALUES (?,?,?)`,
  )
    .bind(userId, `${userId}@example.com`, now)
    .run();
  await testEnv.DB.prepare(
    `INSERT INTO subscriptions (user_id, tier, status, updated_at) VALUES (?, 'free', 'active', ?)`,
  )
    .bind(userId, now)
    .run();
}

function authedJson(url: string, method: string, body: unknown, token: string): Request {
  return jsonRequest(url, method, body, { Authorization: `Bearer ${token}` });
}

async function incidentRow(userId: string): Promise<any> {
  return testEnv.DB.prepare(`SELECT * FROM incidents WHERE id = ?`).bind("inc-1").first();
}

beforeEach(async () => {
  await applySchema();
  await seedActiveUser();
});

describe("data-plane auth gating", () => {
  it("rejects requests without a bearer token", async () => {
    const res = await incidentsPush(
      jsonRequest("https://w/incidents/push", "POST", { incidents: [] }),
      testEnv,
    );
    expect(res.status).toBe(401);
  });

  it("rejects when the subscription is inactive", async () => {
    await testEnv.DB.prepare(
      `UPDATE subscriptions SET status = 'canceled' WHERE user_id = 'user-x'`,
    ).run();
    const token = await bearer();
    const res = await incidentsPush(
      authedJson("https://w/incidents/push", "POST", { incidents: [] }, token),
      testEnv,
    );
    expect(res.status).toBe(403);
  });
});

describe("rank-gated incident upsert", () => {
  it("replay (rank 2) overwrites live (rank 1); live cannot overwrite replay", async () => {
    const token = await bearer();

    // Live insert.
    await incidentsPush(
      authedJson(
        "https://w/incidents/push",
        "POST",
        { incidents: [{ id: "inc-1", sub_session_id: SUB, cause: "live-cause", source: "live" }] },
        token,
      ),
      testEnv,
    );
    let row = await incidentRow("user-x");
    expect(row.source).toBe("live");
    expect(row.source_rank).toBe(1);
    expect(row.cause).toBe("live-cause");

    // Replay reconciliation overwrites.
    await incidentsPush(
      authedJson(
        "https://w/incidents/push",
        "POST",
        {
          incidents: [
            { id: "inc-1", sub_session_id: SUB, cause: "replay-cause", source: "replay_reconciled" },
          ],
        },
        token,
      ),
      testEnv,
    );
    row = await incidentRow("user-x");
    expect(row.source).toBe("replay_reconciled");
    expect(row.source_rank).toBe(2);
    expect(row.cause).toBe("replay-cause");

    // A later live push must NOT overwrite the replay-reconciled row.
    await incidentsPush(
      authedJson(
        "https://w/incidents/push",
        "POST",
        { incidents: [{ id: "inc-1", sub_session_id: SUB, cause: "late-live", source: "live" }] },
        token,
      ),
      testEnv,
    );
    row = await incidentRow("user-x");
    expect(row.source).toBe("replay_reconciled");
    expect(row.cause).toBe("replay-cause");
  });
});

describe("batched writes (subrequest-cap regression)", () => {
  it("pushes 60+ incidents in one request and persists every row", async () => {
    const token = await bearer();
    const incidents = Array.from({ length: 75 }, (_, i) => ({
      id: `bulk-${i}`,
      sub_session_id: SUB,
      car_idx: i % 40,
      cause: `cause-${i}`,
      source: "live",
    }));

    const res = await incidentsPush(
      authedJson("https://w/incidents/push", "POST", { incidents }, token),
      testEnv,
    );
    expect(res.status).toBe(200);
    expect(((await res.json()) as any).incidents_upserted).toBe(75);

    const count = await testEnv.DB.prepare(
      `SELECT COUNT(*) AS n FROM incidents WHERE owner_user_id = 'user-x' AND id LIKE 'bulk-%'`,
    ).first<{ n: number }>();
    expect(count?.n).toBe(75);
  });

  it("stores a session-complete payload with many drivers and incidents in one batch", async () => {
    const { sessionComplete } = await import("../src/data");
    const token = await bearer();
    const drivers = Array.from({ length: 40 }, (_, i) => ({
      user_id: 900000 + i,
      user_name: `Driver ${i}`,
    }));
    const incidents = Array.from({ length: 64 }, (_, i) => ({
      id: `sc-${i}`,
      sub_session_id: SUB,
      car_idx: i % 40,
      source: "live",
    }));

    const res = await sessionComplete(
      authedJson(
        "https://w/session-complete",
        "POST",
        { session: { sub_session_id: SUB, track_name: "Spa" }, drivers, incidents },
        token,
      ),
      testEnv,
    );
    expect(res.status).toBe(200);
    expect(((await res.json()) as any).incidents_upserted).toBe(64);

    const drv = await testEnv.DB.prepare(`SELECT COUNT(*) AS n FROM DRIVERS`).first<{ n: number }>();
    expect(drv?.n).toBe(40);
    const inc = await testEnv.DB.prepare(
      `SELECT COUNT(*) AS n FROM incidents WHERE id LIKE 'sc-%'`,
    ).first<{ n: number }>();
    expect(inc?.n).toBe(64);
  });

  it("PUT /incident-index writes a large index (incidents + blob row) in one batch", async () => {
    const token = await bearer();
    const index = {
      SubSessionId: SUB,
      TotalRaceIncidents: 80,
      Incidents: Array.from({ length: 80 }, (_, i) => ({
        id: `big-${i}`,
        sub_session_id: SUB,
        car_idx: i % 40,
        source: "replay_reconciled",
      })),
    };

    const res = await putIncidentIndex(
      authedJson(`https://w/incident-index/${SUB}`, "PUT", index, token),
      testEnv,
      SUB,
    );
    expect(res.status).toBe(200);

    const inc = await testEnv.DB.prepare(
      `SELECT COUNT(*) AS n FROM incidents WHERE id LIKE 'big-%'`,
    ).first<{ n: number }>();
    expect(inc?.n).toBe(80);
    const blob = await testEnv.DB.prepare(
      `SELECT incident_count FROM incident_index_blobs WHERE owner_user_id = 'user-x' AND sub_session_id = ?`,
    )
      .bind(SUB)
      .first<{ incident_count: number }>();
    expect(blob?.incident_count).toBe(80);
  });
});

describe("incident index R2 round-trip", () => {
  it("PUT stores verbatim to R2 + upserts D1 bookkeeping; GET returns it", async () => {
    const token = await bearer();
    const index = {
      SubSessionId: SUB,
      IndexBuildTimeMs: 1234,
      TotalRaceIncidents: 2,
      IncidentCountByCarIdx: { "0": 1, "3": 1 },
      Incidents: [
        { id: "inc-a", sub_session_id: SUB, car_idx: 0, source: "replay_reconciled" },
        { id: "inc-b", sub_session_id: SUB, car_idx: 3, source: "replay_reconciled" },
      ],
      Sessions: [],
      Validation: { ok: true },
      OutputPath: "C:/whatever.json",
    };

    const putRes = await putIncidentIndex(
      authedJson(`https://w/incident-index/${SUB}`, "PUT", index, token),
      testEnv,
      SUB,
    );
    expect(putRes.status).toBe(200);
    const putBody = (await putRes.json()) as any;
    expect(putBody.incident_count).toBe(2);

    // Bookkeeping row exists, owner-scoped.
    const blob = await testEnv.DB.prepare(
      `SELECT * FROM incident_index_blobs WHERE owner_user_id = ? AND sub_session_id = ?`,
    )
      .bind("user-x", SUB)
      .first<any>();
    expect(blob.incident_count).toBe(2);
    expect(blob.r2_key).toBe(`incident-index/v1/user-x/${SUB}.json`);

    // Incidents upserted at rank 2.
    const incA = await testEnv.DB.prepare(`SELECT source_rank FROM incidents WHERE id = 'inc-a'`).first<any>();
    expect(incA.source_rank).toBe(2);

    // GET returns the verbatim JSON.
    const getRes = await getIncidentIndex(
      new Request(`https://w/incident-index/${SUB}`, {
        headers: { Authorization: `Bearer ${token}` },
      }),
      testEnv,
      SUB,
    );
    expect(getRes.status).toBe(200);
    const roundTrip = (await getRes.json()) as any;
    expect(roundTrip.SubSessionId).toBe(SUB);
    expect(roundTrip.Incidents.length).toBe(2);
  });

  it("GET returns 404 for a missing index", async () => {
    const token = await bearer();
    const res = await getIncidentIndex(
      new Request(`https://w/incident-index/999999`, {
        headers: { Authorization: `Bearer ${token}` },
      }),
      testEnv,
      999999,
    );
    expect(res.status).toBe(404);
  });

  it("GET response carries X-Content-Type-Options: nosniff", async () => {
    const token = await bearer();
    await putIncidentIndex(
      authedJson(`https://w/incident-index/${SUB}`, "PUT", { SubSessionId: SUB, Incidents: [] }, token),
      testEnv,
      SUB,
    );
    const res = await getIncidentIndex(
      new Request(`https://w/incident-index/${SUB}`, { headers: { Authorization: `Bearer ${token}` } }),
      testEnv,
      SUB,
    );
    expect(res.headers.get("X-Content-Type-Options")).toBe("nosniff");
  });

  it("PUT rejects a body over the 4 MB cap with 413", async () => {
    const token = await bearer();
    // A single huge Incidents array — well past 4 MB once serialized.
    const bigIncidents = Array.from({ length: 120_000 }, (_, i) => ({
      id: `padding-incident-${i}-${"x".repeat(20)}`,
      sub_session_id: SUB,
    }));
    const oversized = JSON.stringify({ SubSessionId: SUB, Incidents: bigIncidents });
    expect(oversized.length).toBeGreaterThan(4 * 1024 * 1024);

    const res = await putIncidentIndex(
      new Request(`https://w/incident-index/${SUB}`, {
        method: "PUT",
        headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json" },
        body: oversized,
      }),
      testEnv,
      SUB,
    );
    expect(res.status).toBe(413);
    expect(((await res.json()) as any).error).toBe("payload_too_large");
  });
});

describe("requireActiveUser — tokens_valid_after (H2)", () => {
  it("rejects an access token minted before a reuse-detection revocation cutoff", async () => {
    const token = await bearer("user-x");

    // Simulate auth.ts's reuse-detection path setting the cutoff to "now",
    // strictly after the token above was minted.
    await new Promise((r) => setTimeout(r, 5));
    await testEnv.DB.prepare(`UPDATE users SET tokens_valid_after = ? WHERE user_id = 'user-x'`)
      .bind(new Date().toISOString())
      .run();

    const res = await getSession(
      new Request(`https://w/session/${SUB}`, { headers: { Authorization: `Bearer ${token}` } }),
      testEnv,
      SUB,
    );
    expect(res.status).toBe(401);
    expect(((await res.json()) as any).error).toBe("token_revoked");
  });

  it("accepts an access token minted after the revocation cutoff", async () => {
    await testEnv.DB.prepare(`UPDATE users SET tokens_valid_after = ? WHERE user_id = 'user-x'`)
      .bind(new Date().toISOString())
      .run();
    // iat has 1-second resolution; wait past a full second boundary so the
    // minted token's iat is unambiguously later than the cutoff regardless of
    // where within its own second the cutoff timestamp landed.
    await new Promise((r) => setTimeout(r, 1100));
    const token = await bearer("user-x"); // iat is now after the cutoff

    const res = await getSession(
      new Request(`https://w/session/${SUB}`, { headers: { Authorization: `Bearer ${token}` } }),
      testEnv,
      SUB,
    );
    expect(res.status).not.toBe(401);
  });

  it("accepts an access token when tokens_valid_after is unset", async () => {
    const token = await bearer("user-x");
    const res = await getSession(
      new Request(`https://w/session/${SUB}`, { headers: { Authorization: `Bearer ${token}` } }),
      testEnv,
      SUB,
    );
    expect(res.status).not.toBe(401);
  });
});

describe("tenant isolation (IDOR prevention)", () => {
  it("one user cannot read another user's incident index for the same subsession", async () => {
    await seedActiveUser("user-y");
    const tokenX = await bearer("user-x");
    const tokenY = await bearer("user-y");

    const index = { SubSessionId: SUB, TotalRaceIncidents: 1, Incidents: [{ id: "inc-x", sub_session_id: SUB }] };
    const putRes = await putIncidentIndex(
      authedJson(`https://w/incident-index/${SUB}`, "PUT", index, tokenX),
      testEnv,
      SUB,
    );
    expect(putRes.status).toBe(200);

    // user-y asking for the SAME subsession sees nothing — not user-x's data.
    const asY = await getIncidentIndex(
      new Request(`https://w/incident-index/${SUB}`, {
        headers: { Authorization: `Bearer ${tokenY}` },
      }),
      testEnv,
      SUB,
    );
    expect(asY.status).toBe(404);
  });

  it("one user's index PUT cannot clobber another user's blob or incidents", async () => {
    await seedActiveUser("user-y");
    const tokenX = await bearer("user-x");
    const tokenY = await bearer("user-y");

    await putIncidentIndex(
      authedJson(
        `https://w/incident-index/${SUB}`,
        "PUT",
        { SubSessionId: SUB, Incidents: [{ id: "inc-shared", sub_session_id: SUB, cause: "x-cause", source: "replay_reconciled" }] },
        tokenX,
      ),
      testEnv,
      SUB,
    );
    await putIncidentIndex(
      authedJson(
        `https://w/incident-index/${SUB}`,
        "PUT",
        { SubSessionId: SUB, Incidents: [{ id: "inc-shared", sub_session_id: SUB, cause: "y-cause", source: "replay_reconciled" }] },
        tokenY,
      ),
      testEnv,
      SUB,
    );

    // Same fingerprint id, same subsession — but two fully separate rows.
    const xRow = await testEnv.DB.prepare(
      `SELECT cause FROM incidents WHERE owner_user_id = 'user-x' AND id = 'inc-shared'`,
    ).first<any>();
    const yRow = await testEnv.DB.prepare(
      `SELECT cause FROM incidents WHERE owner_user_id = 'user-y' AND id = 'inc-shared'`,
    ).first<any>();
    expect(xRow.cause).toBe("x-cause");
    expect(yRow.cause).toBe("y-cause");

    // user-x still reads their own blob back, untouched by user-y's PUT.
    const asX = await getIncidentIndex(
      new Request(`https://w/incident-index/${SUB}`, {
        headers: { Authorization: `Bearer ${tokenX}` },
      }),
      testEnv,
      SUB,
    );
    const body = (await asX.json()) as any;
    expect(body.Incidents[0].cause).toBe("x-cause");
  });

  it("getSession is owner-scoped", async () => {
    await seedActiveUser("user-y");
    const tokenX = await bearer("user-x");
    const tokenY = await bearer("user-y");

    const { sessionComplete } = await import("../src/data");
    await sessionComplete(
      authedJson(
        "https://w/session-complete",
        "POST",
        { session: { sub_session_id: SUB, track_name: "Okayama" }, incidents: [] },
        tokenX,
      ),
      testEnv,
    );

    const asY = await getSession(
      new Request(`https://w/session/${SUB}`, {
        headers: { Authorization: `Bearer ${tokenY}` },
      }),
      testEnv,
      SUB,
    );
    expect(asY.status).toBe(404);

    const asX = await getSession(
      new Request(`https://w/session/${SUB}`, {
        headers: { Authorization: `Bearer ${tokenX}` },
      }),
      testEnv,
      SUB,
    );
    expect(asX.status).toBe(200);
  });
});
