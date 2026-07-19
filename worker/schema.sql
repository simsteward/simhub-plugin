-- Sim Steward Cloudflare D1 schema.
-- Apply locally:  npx wrangler d1 execute simsteward-db --local  --file=./schema.sql
-- Apply remote:   npx wrangler d1 execute simsteward-db --remote --file=./schema.sql

-- ---------------------------------------------------------------------------
-- Domain tables (drivers / sessions / incidents) — recreated from the
-- previously-deferred data-api design. Incident IDs are the client-computed
-- v2 fingerprint; the server never recomputes them.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS DRIVERS (
  user_id       INTEGER PRIMARY KEY,
  user_name     TEXT,
  first_seen_at TEXT,
  last_seen_at  TEXT
);

-- All tenant data is namespaced by owner_user_id (the authenticated cloud user
-- from the JWT `sub` claim — NOT the same thing as the iRacing driver user_id).
-- iRacing subsession ids are global across all iRacing customers, so two cloud
-- users can legitimately store data for the same sub_session_id; composite keys
-- keep their rows fully isolated (IDOR prevention).
CREATE TABLE IF NOT EXISTS SESSIONS (
  owner_user_id     TEXT NOT NULL,
  sub_session_id    INTEGER NOT NULL,
  session_id        INTEGER,
  series_id         INTEGER,
  track_name        TEXT,
  session_type      TEXT,
  captured_at       TEXT,
  index_source      TEXT,
  index_updated_at  TEXT,
  PRIMARY KEY (owner_user_id, sub_session_id)
);

CREATE TABLE IF NOT EXISTS INCIDENTS (
  owner_user_id          TEXT NOT NULL,
  id                     TEXT NOT NULL,
  sub_session_id         INTEGER,
  session_num            INTEGER,
  user_id                INTEGER,   -- iRacing driver id of the car involved, not the tenant
  car_idx                INTEGER,
  session_time           REAL,
  replay_frame_num_end   INTEGER,
  delta                  INTEGER,
  type                   TEXT,
  cause                  TEXT,
  other_user_id          INTEGER,
  source                 TEXT,
  source_rank            INTEGER,
  processed_at           TEXT,
  fingerprint_version    INTEGER,
  PRIMARY KEY (owner_user_id, id)
);

CREATE INDEX IF NOT EXISTS idx_incidents_owner_sub ON INCIDENTS(owner_user_id, sub_session_id);

-- Reserved stub — not actively used in this phase.
CREATE TABLE IF NOT EXISTS INCIDENT_CAPTURES (
  id           TEXT PRIMARY KEY,
  incident_id  TEXT,
  pov_user_id  INTEGER,
  created_at   TEXT
);

-- ---------------------------------------------------------------------------
-- Auth / identity / billing tables.
-- ---------------------------------------------------------------------------

-- Registered plugin apps. app_token (raw) is presented by the plugin; we store
-- only its sha256 hash. A non-null revoked_at disables the app.
CREATE TABLE IF NOT EXISTS apps (
  app_id        TEXT PRIMARY KEY,
  token_hash    TEXT UNIQUE,
  version_label TEXT,
  revoked_at    TEXT,
  created_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
  user_id      TEXT PRIMARY KEY,
  email        TEXT UNIQUE,
  display_name TEXT,
  created_at   TEXT NOT NULL,
  last_seen_at TEXT,
  -- Set to now() when a rotated-token reuse is detected for this user. Any
  -- access-token JWT with iat before this timestamp is rejected even if it
  -- hasn't expired yet — closes the window between theft detection and the
  -- stolen access token's natural 15-minute expiry (requireActiveUser).
  tokens_valid_after TEXT
);

CREATE TABLE IF NOT EXISTS subscriptions (
  user_id            TEXT PRIMARY KEY REFERENCES users(user_id),
  tier               TEXT NOT NULL DEFAULT 'free',
  status             TEXT NOT NULL DEFAULT 'active',
  current_period_end TEXT,
  updated_at         TEXT NOT NULL
);

-- Opaque rotating user tokens. Only the sha256 hash is stored. On each token
-- exchange the current row is revoked and a new one inserted (rotated_from
-- chains the lineage); presenting an already-revoked hash is a reuse signal.
CREATE TABLE IF NOT EXISTS user_tokens (
  token_id     TEXT PRIMARY KEY,
  user_id      TEXT NOT NULL REFERENCES users(user_id),
  token_hash   TEXT NOT NULL,
  rotated_from TEXT REFERENCES user_tokens(token_id),
  -- Root token_id of this device's rotation chain (set once at devicePoll mint,
  -- propagated unchanged on every rotation). Reuse-detection revokes only the
  -- rows sharing the compromised lineage_id, not every token the user owns —
  -- pairing a second device stays unaffected when a different device's token
  -- is stolen and replayed.
  lineage_id   TEXT NOT NULL,
  created_at   TEXT NOT NULL,
  revoked_at   TEXT,
  last_used_at TEXT,
  device_label TEXT
);

CREATE INDEX IF NOT EXISTS idx_user_tokens_lineage ON user_tokens(user_id, lineage_id);

CREATE INDEX IF NOT EXISTS idx_user_tokens_hash ON user_tokens(token_hash);

-- Bookkeeping for the verbatim incident-index blobs stored in R2. Owner-scoped:
-- the R2 key embeds the owner (incident-index/v1/{owner}/{sub}.json) so one
-- tenant can never read or overwrite another's blob.
CREATE TABLE IF NOT EXISTS incident_index_blobs (
  owner_user_id       TEXT NOT NULL,
  sub_session_id      INTEGER NOT NULL,
  r2_key              TEXT NOT NULL,
  content_sha256      TEXT NOT NULL,
  incident_count      INTEGER NOT NULL,
  index_build_time_ms INTEGER,
  updated_at          TEXT NOT NULL,
  PRIMARY KEY (owner_user_id, sub_session_id)
);
