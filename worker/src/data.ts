// Data-plane routes. All (except /health) require our own access-token JWT and
// re-check that the subscription is still active.
import type { Env } from "./env";
import { json, errorJson, readJson } from "./http";
import { verifyAccessToken, bearerFrom, type AppJwtClaims } from "./jwt";
import { sha256Hex } from "./crypto";
import { rankForSource } from "./upsert";

// Incident row as sent on the wire (camelCase for index, but /incidents/push
// uses snake_case per the plugin's payload). We accept both spellings defensively.
interface IncidentInput {
  id: string;
  sub_session_id?: number;
  subSessionId?: number;
  session_num?: number;
  sessionNum?: number;
  user_id?: number;
  userId?: number;
  car_idx?: number;
  carIdx?: number;
  session_time?: number;
  sessionTime?: number;
  replay_frame_num_end?: number;
  replayFrameNumEnd?: number;
  delta?: number;
  type?: string;
  cause?: string;
  other_user_id?: number;
  otherUserId?: number;
  source?: string;
}

type AuthOk = { ok: true; claims: AppJwtClaims };
type AuthErr = { ok: false; response: Response };

// Verify the JWT, then re-check subscription status (the cheap security gate).
export async function requireActiveUser(
  request: Request,
  env: Env,
): Promise<AuthOk | AuthErr> {
  const token = bearerFrom(request);
  if (!token) return { ok: false, response: errorJson("missing_bearer_token", 401) };

  const claims = await verifyAccessToken(env.JWT_HMAC_SECRET, token);
  if (!claims) return { ok: false, response: errorJson("invalid_token", 401) };

  const row = await env.DB.prepare(
    `SELECT s.status AS status, u.tokens_valid_after AS tokens_valid_after
     FROM subscriptions s JOIN users u ON u.user_id = s.user_id
     WHERE s.user_id = ?`,
  )
    .bind(claims.sub)
    .first<{ status: string; tokens_valid_after: string | null }>();
  if (!row || row.status !== "active") {
    return { ok: false, response: errorJson("subscription_inactive", 403) };
  }
  // Reject any access token minted before a reuse-detection cutoff was set —
  // closes the window between stolen-token detection and this JWT's natural
  // 15-minute expiry (auth.ts tokenExchange sets tokens_valid_after).
  if (row.tokens_valid_after) {
    const cutoffMs = Date.parse(row.tokens_valid_after);
    if (!Number.isNaN(cutoffMs) && claims.iat * 1000 < cutoffMs) {
      return { ok: false, response: errorJson("token_revoked", 401) };
    }
  }
  return { ok: true, claims };
}

const num = (...vals: (number | undefined)[]): number | null => {
  for (const v of vals) if (v !== undefined && v !== null) return v;
  return null;
};

// Builds (but does not run) the rank-gated upsert for a single incident row,
// namespaced to the authenticated owner (JWT sub — never a body field, so one
// tenant can't write into another's rows even for the same globally-shared
// iRacing subsession). Mirrors chooseWinningRow(): the WHERE clause only
// overwrites when the incoming source_rank >= existing.
//
// Returns a bound statement so callers can submit every row in ONE env.DB.batch
// call. Running these sequentially burns one subrequest per row, and a full
// race index can exceed the Worker's per-invocation subrequest cap.
function buildIncidentUpsert(
  env: Env,
  ownerUserId: string,
  row: IncidentInput,
  defaultSource: string,
): D1PreparedStatement {
  const source = row.source ?? defaultSource;
  const source_rank = rankForSource(source);
  const now = new Date().toISOString();
  return env.DB.prepare(
    `INSERT INTO incidents (owner_user_id, id, sub_session_id, session_num, user_id, car_idx, session_time,
                            replay_frame_num_end, delta, type, cause, other_user_id, source,
                            source_rank, processed_at, fingerprint_version)
     VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,2)
     ON CONFLICT(owner_user_id, id) DO UPDATE SET
       session_num=excluded.session_num, cause=excluded.cause,
       replay_frame_num_end=excluded.replay_frame_num_end, source=excluded.source,
       source_rank=excluded.source_rank, processed_at=excluded.processed_at
     WHERE excluded.source_rank >= incidents.source_rank`,
  )
    .bind(
      ownerUserId,
      row.id,
      num(row.sub_session_id, row.subSessionId),
      num(row.session_num, row.sessionNum),
      num(row.user_id, row.userId),
      num(row.car_idx, row.carIdx),
      num(row.session_time, row.sessionTime),
      num(row.replay_frame_num_end, row.replayFrameNumEnd),
      num(row.delta),
      row.type ?? null,
      row.cause ?? null,
      num(row.other_user_id, row.otherUserId),
      source,
      source_rank,
      now,
    );
}

// POST /session-complete — SessionSummary JSON (sessions + drivers + incidents).
export async function sessionComplete(request: Request, env: Env): Promise<Response> {
  const auth = await requireActiveUser(request, env);
  if (!auth.ok) return auth.response;

  const body = await readJson<any>(request);
  if (!body) return errorJson("invalid_json", 400);

  const now = new Date().toISOString();
  const session = body.session ?? body.Session ?? {};
  const subSessionId = num(session.sub_session_id, session.subSessionId, body.subSessionId);
  if (subSessionId === null) return errorJson("missing_sub_session_id", 400);

  // One batch for the whole payload — session, then drivers, then incidents.
  // D1 runs a batch as a single transaction in array order, so this is both
  // atomic and a single subrequest regardless of how many rows arrive.
  const statements: D1PreparedStatement[] = [
    env.DB.prepare(
      `INSERT INTO SESSIONS (owner_user_id, sub_session_id, session_id, series_id, track_name, session_type,
                             captured_at, index_source, index_updated_at)
       VALUES (?,?,?,?,?,?,?,?,?)
       ON CONFLICT(owner_user_id, sub_session_id) DO UPDATE SET
         session_id=excluded.session_id, series_id=excluded.series_id,
         track_name=excluded.track_name, session_type=excluded.session_type,
         captured_at=excluded.captured_at`,
    ).bind(
      auth.claims.sub,
      subSessionId,
      num(session.session_id, session.sessionId),
      num(session.series_id, session.seriesId),
      session.track_name ?? session.trackName ?? null,
      session.session_type ?? session.sessionType ?? null,
      session.captured_at ?? session.capturedAt ?? now,
      session.index_source ?? session.indexSource ?? null,
      now,
    ),
  ];

  const drivers: any[] = body.drivers ?? body.Drivers ?? [];
  for (const d of drivers) {
    const userId = num(d.user_id, d.userId);
    if (userId === null) continue;
    statements.push(
      env.DB.prepare(
        `INSERT INTO DRIVERS (user_id, user_name, first_seen_at, last_seen_at)
         VALUES (?,?,?,?)
         ON CONFLICT(user_id) DO UPDATE SET
           user_name=excluded.user_name, last_seen_at=excluded.last_seen_at`,
      ).bind(userId, d.user_name ?? d.userName ?? null, now, now),
    );
  }

  const incidents: IncidentInput[] = body.incidents ?? body.Incidents ?? [];
  let upserted = 0;
  for (const inc of incidents) {
    if (!inc?.id) continue;
    statements.push(buildIncidentUpsert(env, auth.claims.sub, inc, "live"));
    upserted++;
  }

  await env.DB.batch(statements);

  return json({ ok: true, sub_session_id: subSessionId, incidents_upserted: upserted });
}

// POST /incidents/push — {incidents: [...]}
export async function incidentsPush(request: Request, env: Env): Promise<Response> {
  const auth = await requireActiveUser(request, env);
  if (!auth.ok) return auth.response;

  const body = await readJson<{ incidents?: IncidentInput[] }>(request);
  if (!body?.incidents || !Array.isArray(body.incidents)) {
    return errorJson("missing_incidents", 400);
  }

  // Single batch — a push of 60+ incidents must not become 60+ subrequests.
  const statements: D1PreparedStatement[] = [];
  for (const inc of body.incidents) {
    if (!inc?.id) continue;
    statements.push(buildIncidentUpsert(env, auth.claims.sub, inc, "live"));
  }
  if (statements.length > 0) await env.DB.batch(statements);

  return json({ ok: true, incidents_upserted: statements.length });
}

// Local index files run 10s-100s of KB; 4 MB is generous headroom while still
// bounding worst-case memory/storage from a single request.
const MAX_INCIDENT_INDEX_BYTES = 4 * 1024 * 1024;

// PUT /incident-index/{subSessionId} — store verbatim JSON to R2 + upsert D1.
export async function putIncidentIndex(
  request: Request,
  env: Env,
  subSessionId: number,
): Promise<Response> {
  const auth = await requireActiveUser(request, env);
  if (!auth.ok) return auth.response;

  // Fast-path reject via the declared Content-Length before buffering the body.
  const declaredLength = Number(request.headers.get("Content-Length"));
  if (Number.isFinite(declaredLength) && declaredLength > MAX_INCIDENT_INDEX_BYTES) {
    return errorJson("payload_too_large", 413);
  }

  const bodyBytes = await request.arrayBuffer();
  if (bodyBytes.byteLength === 0) return errorJson("empty_body", 400);
  // Definitive check — a missing/incorrect Content-Length must not bypass the cap.
  if (bodyBytes.byteLength > MAX_INCIDENT_INDEX_BYTES) return errorJson("payload_too_large", 413);

  // Parse a copy for D1 bookkeeping; R2 gets the verbatim bytes.
  let parsed: any;
  try {
    parsed = JSON.parse(new TextDecoder().decode(bodyBytes));
  } catch {
    return errorJson("invalid_json", 400);
  }

  // The R2 key embeds the authenticated owner — a caller can only ever write
  // (and later read) their own namespace, regardless of subSessionId.
  const r2_key = `incident-index/v1/${auth.claims.sub}/${subSessionId}.json`;

  const incidents: IncidentInput[] = parsed.Incidents ?? parsed.incidents ?? [];
  const content_sha256 = await sha256Hex(bodyBytes);
  const incident_count =
    parsed.TotalRaceIncidents ?? parsed.totalRaceIncidents ?? incidents.length;
  const index_build_time_ms = parsed.IndexBuildTimeMs ?? parsed.indexBuildTimeMs ?? null;
  const now = new Date().toISOString();

  // Every incident row plus the blob bookkeeping row in one batch — one
  // subrequest and one transaction for an index of any size.
  const statements: D1PreparedStatement[] = [];
  for (const inc of incidents) {
    if (!inc?.id) continue;
    statements.push(buildIncidentUpsert(env, auth.claims.sub, inc, "replay_reconciled"));
  }
  statements.push(
    env.DB.prepare(
      `INSERT INTO incident_index_blobs
         (owner_user_id, sub_session_id, r2_key, content_sha256, incident_count, index_build_time_ms, updated_at)
       VALUES (?,?,?,?,?,?,?)
       ON CONFLICT(owner_user_id, sub_session_id) DO UPDATE SET
         r2_key=excluded.r2_key, content_sha256=excluded.content_sha256,
         incident_count=excluded.incident_count, index_build_time_ms=excluded.index_build_time_ms,
         updated_at=excluded.updated_at`,
    ).bind(
      auth.claims.sub,
      subSessionId,
      r2_key,
      content_sha256,
      incident_count,
      index_build_time_ms,
      now,
    ),
  );

  // Retry semantics: D1 first, R2 second, because this PUT is fully idempotent
  // and the client (CloudOutbox) retries the whole request on any non-2xx.
  //   - D1 throws  → nothing written anywhere; the retry is a clean redo.
  //   - R2 throws  → D1 already committed, so we return 500 rather than a
  //     misleading 200. The retry re-runs the identical rank-gated upserts
  //     (no-ops at equal rank) and re-puts the identical bytes.
  // The transient inconsistency this leaves is a blob row whose R2 object is
  // missing; GET answers 404 for it, which is the same thing the client sees
  // for a never-uploaded index, and the retry repairs it.
  await env.DB.batch(statements);

  try {
    await env.INCIDENT_INDEX_BUCKET.put(r2_key, bodyBytes, {
      httpMetadata: { contentType: "application/json" },
    });
  } catch (err) {
    console.error("r2_put_failed_after_d1_commit", {
      r2_key,
      sub_session_id: subSessionId,
      error: String(err),
    });
    return errorJson("storage_error", 500);
  }

  return json({ ok: true, sub_session_id: subSessionId, r2_key, incident_count });
}

// GET /incident-index/{subSessionId} — return the R2 object verbatim.
export async function getIncidentIndex(
  request: Request,
  env: Env,
  subSessionId: number,
): Promise<Response> {
  const auth = await requireActiveUser(request, env);
  if (!auth.ok) return auth.response;

  // Owner-scoped key: reading another tenant's index is structurally impossible
  // (the caller's own JWT sub is baked into the key we look up).
  const r2_key = `incident-index/v1/${auth.claims.sub}/${subSessionId}.json`;
  const obj = await env.INCIDENT_INDEX_BUCKET.get(r2_key);
  if (!obj) return errorJson("not_found", 404);

  return new Response(obj.body, {
    headers: { "Content-Type": "application/json", "X-Content-Type-Options": "nosniff" },
  });
}

// GET /session/{subSessionId} — lightweight D1 summary.
export async function getSession(
  request: Request,
  env: Env,
  subSessionId: number,
): Promise<Response> {
  const auth = await requireActiveUser(request, env);
  if (!auth.ok) return auth.response;

  const session = await env.DB.prepare(
    `SELECT sub_session_id, session_id, series_id, track_name, session_type, captured_at
     FROM SESSIONS WHERE owner_user_id = ? AND sub_session_id = ?`,
  )
    .bind(auth.claims.sub, subSessionId)
    .first();
  if (!session) return errorJson("not_found", 404);

  const count = await env.DB.prepare(
    `SELECT COUNT(*) AS n FROM incidents WHERE owner_user_id = ? AND sub_session_id = ?`,
  )
    .bind(auth.claims.sub, subSessionId)
    .first<{ n: number }>();

  return json({ session, incident_count: count?.n ?? 0 });
}
