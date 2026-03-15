-- SimSteward D1 schema (mirrors local data-api and deterministic data point plan).
-- Apply once: wrangler d1 execute simsteward-db --remote --file=./schema.sql
-- (Use --local for local dev.)

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
    fingerprint_version INTEGER DEFAULT 1,
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
