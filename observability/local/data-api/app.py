"""
SimSteward Data API — local SQLite ingest for session summaries and incidents.
Accepts POST /session-complete with SessionSummary JSON; UPSERTs into drivers, sessions, incidents.
Schema matches the deterministic data point plan (Phase 2 local dev mirror).
"""
import json
import os
import sqlite3
from datetime import datetime
from pathlib import Path

from flask import Flask, request, jsonify

app = Flask(__name__)
DB_PATH = os.environ.get("SIMSTEWARD_DB_PATH", "/data/simsteward.db")


def get_db():
    Path(DB_PATH).parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_schema(conn):
    conn.executescript("""
        CREATE TABLE IF NOT EXISTS drivers (
            user_id INTEGER PRIMARY KEY,
            user_name TEXT,
            first_seen_at TEXT,
            last_seen_at TEXT
        );

        CREATE TABLE IF NOT EXISTS sessions (
            sub_session_id INTEGER PRIMARY KEY,
            session_id INTEGER,
            series_id INTEGER,
            track_name TEXT,
            session_type TEXT,
            captured_at TEXT
        );

        CREATE TABLE IF NOT EXISTS incidents (
            id TEXT PRIMARY KEY,
            sub_session_id INTEGER,
            session_num INTEGER,
            user_id INTEGER,
            car_idx INTEGER,
            session_time REAL,
            replay_frame_num_end INTEGER,
            delta INTEGER,
            type TEXT,
            cause TEXT,
            other_user_id INTEGER,
            source TEXT,
            processed_at TEXT,
            FOREIGN KEY (sub_session_id) REFERENCES sessions(sub_session_id),
            FOREIGN KEY (user_id) REFERENCES drivers(user_id)
        );

        CREATE TABLE IF NOT EXISTS incident_captures (
            id TEXT PRIMARY KEY,
            incident_id TEXT NOT NULL,
            pov_user_id INTEGER,
            pov_car_idx INTEGER,
            camera_type TEXT,
            frame_start INTEGER,
            frame_end INTEGER,
            clip_r2_path TEXT,
            telemetry_json TEXT,
            telemetry_r2_path TEXT,
            subscription_tier TEXT,
            captured_at TEXT,
            FOREIGN KEY (incident_id) REFERENCES incidents(id)
        );

        CREATE INDEX IF NOT EXISTS idx_incidents_user ON incidents(user_id);
        CREATE INDEX IF NOT EXISTS idx_incidents_session ON incidents(sub_session_id);
        CREATE INDEX IF NOT EXISTS idx_captures_incident ON incident_captures(incident_id);
        CREATE INDEX IF NOT EXISTS idx_captures_pov_user ON incident_captures(pov_user_id);
    """)
    conn.commit()


@app.route("/health", methods=["GET"])
def health():
    return jsonify({"status": "ok"})


@app.route("/session-complete", methods=["POST"])
def session_complete():
    if not request.is_json:
        return jsonify({"error": "Content-Type must be application/json"}), 400
    try:
        body = request.get_json()
    except Exception as e:
        return jsonify({"error": f"Invalid JSON: {e}"}), 400

    captured_at = body.get("capturedAt") or datetime.utcnow().isoformat() + "Z"
    sub_session_id = body.get("subSessionID")
    if sub_session_id is None:
        return jsonify({"error": "subSessionID required"}), 400

    conn = get_db()
    try:
        init_schema(conn)

        # Upsert session
        conn.execute(
            """INSERT INTO sessions (sub_session_id, session_id, series_id, track_name, session_type, captured_at)
               VALUES (?, ?, ?, ?, ?, ?)
               ON CONFLICT(sub_session_id) DO UPDATE SET
                 session_id=excluded.session_id, series_id=excluded.series_id, track_name=excluded.track_name,
                 session_type=excluded.session_type, captured_at=excluded.captured_at""",
            (
                sub_session_id,
                body.get("sessionID") or body.get("sessionId"),
                body.get("seriesID") or body.get("seriesId"),
                body.get("trackName") or "",
                body.get("sessionType") or "",
                captured_at,
            ),
        )

        # Upsert drivers from results
        for r in body.get("results") or []:
            user_id = r.get("userID", 0)
            user_name = (r.get("driverName") or "").strip() or None
            conn.execute(
                """INSERT INTO drivers (user_id, user_name, first_seen_at, last_seen_at)
                   VALUES (?, ?, ?, ?)
                   ON CONFLICT(user_id) DO UPDATE SET
                     user_name=excluded.user_name,
                     last_seen_at=excluded.last_seen_at""",
                (user_id, user_name, captured_at, captured_at),
            )

        # Upsert incidents from incidentFeed
        for inc in body.get("incidentFeed") or []:
            inc_id = inc.get("id")
            if not inc_id:
                continue
            conn.execute(
                """INSERT INTO incidents (
                     id, sub_session_id, session_num, user_id, car_idx, session_time,
                     replay_frame_num_end, delta, type, cause, other_user_id, source, processed_at
                   ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)
                   ON CONFLICT(id) DO UPDATE SET
                     sub_session_id=excluded.sub_session_id, session_num=excluded.session_num,
                     user_id=excluded.user_id, session_time=excluded.session_time,
                     replay_frame_num_end=excluded.replay_frame_num_end, delta=excluded.delta,
                     type=excluded.type, cause=excluded.cause, source=excluded.source""",
                (
                    inc_id,
                    inc.get("subSessionId", sub_session_id),
                    body.get("sessionNum"),
                    inc.get("userId", 0),
                    inc.get("carIdx", 0),
                    inc.get("sessionTime", 0),
                    inc.get("replayFrameNum", 0),
                    inc.get("delta", 0),
                    inc.get("type") or "",
                    inc.get("cause"),
                    inc.get("otherUserId"),  # optional; IncidentEvent has otherCarIdx not otherUserId
                    inc.get("source") or "yaml",
                ),
            )

        conn.commit()
        return jsonify({"ok": True, "sub_session_id": sub_session_id})
    except Exception as e:
        conn.rollback()
        return jsonify({"error": str(e)}), 500
    finally:
        conn.close()


if __name__ == "__main__":
    with get_db() as c:
        init_schema(c)
    app.run(host="0.0.0.0", port=8080, debug=False)
