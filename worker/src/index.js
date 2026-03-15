/**
 * SimSteward Data API Worker (Phase 3).
 * POST /session-complete: accepts SessionSummary JSON, UPSERTs into D1.
 * Contract matches observability/local/data-api/app.py.
 */

function toIsoZ() {
  return new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
}

function jsonResponse(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function errResponse(message, status) {
  return jsonResponse({ error: message }, status);
}

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const method = request.method;

    if (url.pathname === '/health' && method === 'GET') {
      return jsonResponse({ status: 'ok' });
    }

    if (url.pathname === '/session-complete' && method === 'POST') {
      if (request.headers.get('Content-Type')?.toLowerCase().indexOf('application/json') === -1) {
        return errResponse('Content-Type must be application/json', 400);
      }
      let body;
      try {
        body = await request.json();
      } catch (e) {
        return errResponse('Invalid JSON', 400);
      }
      const subSessionId = body.subSessionID;
      if (subSessionId === undefined || subSessionId === null) {
        return errResponse('subSessionID required', 400);
      }

      const capturedAt = body.capturedAt || toIsoZ();
      const sessionId = body.sessionID ?? body.sessionId;
      const sessionNum = body.sessionNum;
      const seriesId = body.seriesID ?? body.seriesId;
      const trackName = body.trackName ?? '';
      const sessionType = body.sessionType ?? '';

      const db = env.DB;
      try {
        // 1. UPSERT session
        await db
          .prepare(
            `INSERT INTO sessions (sub_session_id, session_id, series_id, track_name, session_type, captured_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6)
             ON CONFLICT(sub_session_id) DO UPDATE SET
               session_id=excluded.session_id, series_id=excluded.series_id, track_name=excluded.track_name,
               session_type=excluded.session_type, captured_at=excluded.captured_at`
          )
          .bind(
            subSessionId,
            sessionId != null ? Number(sessionId) : null,
            seriesId != null ? Number(seriesId) : null,
            trackName,
            sessionType,
            capturedAt
          )
          .run();

        // 2. UPSERT drivers from results
        const results = body.results || [];
        if (results.length > 0) {
          const driverStmt = db.prepare(
            `INSERT INTO drivers (user_id, user_name, first_seen_at, last_seen_at)
             VALUES (?1, ?2, ?3, ?4)
             ON CONFLICT(user_id) DO UPDATE SET user_name=excluded.user_name, last_seen_at=excluded.last_seen_at`
          );
          const driverRuns = results.map((r) => {
            const uid = r.userID ?? 0;
            const name = (r.driverName || '').trim() || null;
            return driverStmt.bind(uid, name, capturedAt, capturedAt);
          });
          await db.batch(driverRuns);
        }

        // 3. UPSERT incidents from incidentFeed
        const incidentFeed = body.incidentFeed || [];
        const incidentsWithId = incidentFeed.filter((inc) => inc.id != null && inc.id !== '');
        if (incidentsWithId.length > 0) {
          const incidentStmt = db.prepare(
            `INSERT INTO incidents (
               id, sub_session_id, session_num, user_id, car_idx, session_time,
               replay_frame_num_end, delta, type, cause, other_user_id, source, processed_at, fingerprint_version
             ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, NULL, 1)
             ON CONFLICT(id) DO UPDATE SET
               sub_session_id=excluded.sub_session_id, session_num=excluded.session_num,
               user_id=excluded.user_id, session_time=excluded.session_time,
               replay_frame_num_end=excluded.replay_frame_num_end, delta=excluded.delta,
               type=excluded.type, cause=excluded.cause, source=excluded.source`
          );
          const incidentRuns = incidentsWithId.map((inc) =>
            incidentStmt.bind(
              inc.id,
              inc.subSessionId ?? subSessionId,
              sessionNum != null ? sessionNum : null,
              inc.userId ?? 0,
              inc.carIdx ?? 0,
              inc.sessionTime ?? 0,
              inc.replayFrameNum ?? 0,
              inc.delta ?? 0,
              inc.type || '',
              inc.cause ?? null,
              inc.otherUserId ?? null,
              inc.source || 'yaml'
            )
          );
          await db.batch(incidentRuns);
        }

        return jsonResponse({ ok: true, sub_session_id: subSessionId });
      } catch (e) {
        return errResponse('Database error', 500);
      }
    }

    return errResponse('Not Found', 404);
  },
};
