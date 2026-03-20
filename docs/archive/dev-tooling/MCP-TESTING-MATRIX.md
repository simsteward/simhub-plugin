# MCP-Based Testing Features — Test Matrix and Outcome Analysis

This document defines the test matrix for all testing features that can be driven or validated via MCP (SimSteward MCP and Grafana/Loki MCP). It documents expected outcomes, reality/gaps, and prerequisites.

## Prerequisites

| Prerequisite | Required for | Notes |
|--------------|---------------|--------|
| **Plugin running** | SimSteward: health, diagnostics, action | SimHub with SimSteward plugin loaded. If MCP backend does not talk to plugin, health/diagnostics may be stub or "unknown". |
| **Loki stack up** | SimSteward Loki tools, Docker MCP Loki validation | Local: `observability/local/docker-compose.yml`. Loki at `http://localhost:3100`, Grafana at `http://localhost:3000`. |
| **Grafana URL configured for MCP** | user-MCP_DOCKER (list_datasources, list_loki_*, query_loki_*) | Ensure the Grafana/Loki MCP (e.g. user-MCP_DOCKER) has Grafana base URL and auth (if needed) configured. |
| **Datasource UID** | Docker MCP: list_loki_label_*, query_loki_logs, query_loki_stats | Local stack uses UID `loki_local` (see `observability/local/grafana/provisioning/datasources/loki.yml`). |
| **iRacing + replay (optional)** | Replay workflow: sessions, seek, snapshot with replay metadata | For full replay test cases; health/diagnostics work without replay but sessions array may be empty. |

---

## Scope: two MCP servers

1. **user-SimSteward** — Plugin/session state and optional Loki: `simsteward_health`, `simsteward_diagnostics`, `simsteward_action`, `simsteward_loki_query`, `simsteward_loki_labels`, `simsteward_loki_correlate`, `simsteward_ws_status`, `simsteward_config`, etc.
2. **user-MCP_DOCKER** (Grafana/Loki MCP) — `list_datasources`, `list_loki_label_names`, `list_loki_label_values`, `query_loki_logs`, `query_loki_stats`. Require Grafana URL and datasource UID.

---

## 1. SimSteward MCP — Plugin and session state

| Test | Action | Expected outcome | Reality / gaps |
|------|--------|------------------|----------------|
| **Health** | Call `simsteward_health` | JSON/markdown with plugin mode, iRacing status, WebSocket server, session state, incident baseline, admin flag. | Depends on SimSteward MCP backend. If backend reads from a **running plugin** (e.g. HTTP or shared state), outcome is live. If backend is a stub or reads only config, health may be static or "unknown". Resource `simsteward://health` — fetch semantics (live vs cached) are implementation-defined. |
| **Diagnostics** | Call `simsteward_diagnostics` | simMode, sessionState, SessionInfoUpdate counter, results availability, sessions array (sessionNum, sessionType, sessionName). | Same as health: depends on whether MCP talks to the running plugin. If no plugin: empty sessions or error. REPLAY-WORKFLOW-TEST-CHECKLIST expects sessions when replay + SessionInfo; MCP must reflect same. |
| **Action** | Call `simsteward_action` with e.g. `action=RecordSessionSnapshot`, `arg=...` | success: true, result/error; plugin performs action (e.g. appends to session-discovery.jsonl). | Requires plugin running and MCP backend forwarding to plugin (WebSocket or HTTP). If plugin not running or port wrong, expect failure or timeout. |
| **Replay actions** | `simsteward_action` for ReplaySeekSessionStart(1), ReplaySetSpeed(16), etc. | success when replay loaded and args valid; failure with clear error when not replay or invalid N. | Same as action: needs live plugin + iRacing replay. |

**Document:** Which SimSteward MCP tools require a running plugin; expected response shape for "plugin not running" vs "replay not loaded". Optional: MCP smoke checklist — call simsteward_health → expect either live data or explicit "plugin unreachable" (no silent stub that looks like success).

---

## 2. SimSteward MCP — Loki (simsteward_loki_query, simsteward_loki_labels, simsteward_loki_correlate)

| Test | Action | Expected outcome | Reality / gaps |
|------|--------|------------------|----------------|
| **Labels** | Call `simsteward_loki_labels` (no label) then `simsteward_loki_labels(label="app")` | List of label names; then list of values for `app` (e.g. sim-steward). | SimSteward MCP must be configured with Loki URL (and auth if Grafana Cloud). If unconfigured or Loki down: empty list or error. No "datasource UID" in args — backend picks one config. |
| **Query** | Call `simsteward_loki_query` with `query={app="sim-steward",env="local"}` (or test_tag filter). | Parsed log entries (event, action, correlation_id, etc.) within time window. | Same: depends on MCP Loki config. Local stack (e.g. localhost:3100) vs Cloud changes URL/token. |
| **Correlate** | Call `simsteward_loki_correlate` with correlationId or sessionId or event. | Log lines for that correlation/session/event. | Same as query; convenience wrapper over LogQL. |

**Document:** Where SimSteward MCP gets its Loki URL/datasource (env, config file, or Cursor MCP env). Align with **docs/observability-testing.md** and **docs/observability-local.md**. After run_grafana_tests.ps1 harness run, simsteward_loki_query with `testing="true" | test_tag="grafana-harness"` should return the same events AssertLokiQueries asserts — use for optional "MCP assertion" path.

---

## 3. Docker/Grafana MCP (user-MCP_DOCKER) — Loki validation

| Test | Action | Expected outcome | Reality / gaps |
|------|--------|------------------|----------------|
| **Datasources** | Call `list_datasources` with `type="loki"`. | List including Loki datasource; UID `loki_local` (local stack). | MCP must be pointed at Grafana (base URL + auth if needed). If Grafana not running or wrong URL: empty or error. |
| **Label names** | Call `list_loki_label_names` with `datasourceUid="loki_local"` (or UID from list_datasources). | Names: e.g. app, env, component, level. | Requires datasource UID. If Loki has no data in range, label list may be empty or minimal. |
| **Label values** | Call `list_loki_label_values` for label `app`, datasource UID as above. | Values including `sim-steward`. | Time range and data presence matter. |
| **Query logs** | Call `query_loki_logs` with `datasourceUid="loki_local"`, `logql: {app="sim-steward",env="local"}`. | List of log lines (and optionally parsed JSON). | Requires **datasourceUid** and **logql**. Time params: startRfc3339/endRfc3339 or defaults; limit default 10 (may need increase for assertions). |
| **Query stats** | Call `query_loki_stats` with same selector. | Streams/chunks/entries/bytes counts. | Useful to confirm data exists before query_loki_logs. |

**Document:** Local stack uses datasource UID `loki_local`. After run_grafana_tests.ps1 (or manual harness + stack), Docker MCP query_loki_logs with logql including `testing="true"` and test_tag can replicate AssertLokiQueries checks (count action_result, incident_detected, session_digest, required fields).

---

## 4. Replay workflow tests — MCP vs ReplayWorkflowTest.ps1

| Test | Script (today) | MCP equivalent | Expected outcome | Reality |
|------|----------------|----------------|------------------|--------|
| Detect (state shape) | WebSocket connect, receive state, assert pluginMode + sessionDiagnostics | simsteward_health + simsteward_diagnostics | Same shape: pluginMode in {Replay, Live, Unknown}, sessionDiagnostics present, sessions[].sessionNum/sessionType/sessionName when replay. | MCP returns same data only if backend reads from running plugin; script uses raw WebSocket to plugin. MCP equivalent valid when SimSteward MCP is wired to the same plugin. |
| Snapshot file shape | Read session-discovery.jsonl, parse last line, assert type/trigger/playerCarIdx/sessionDiagnostics/replayFrameNum, optional replayMetadata | simsteward_action(RecordSessionSnapshot) then read file (or future MCP resource for snapshot). | One new line; structure as in checklist. | MCP can trigger the snapshot; **reading** the file still requires filesystem or a SimSteward resource that exposes "last snapshot". Full "test via MCP only" may need a resource like simsteward://snapshot or last-snapshot. |

**Document:** Replay workflow tests can be driven via SimSteward MCP (health, diagnostics, action) when the plugin is running; snapshot file structure check still requires reading session-discovery.jsonl unless a snapshot resource is exposed.

---

## 5. Observability tests — MCP vs run_grafana_tests.ps1

| Test | Current flow | MCP option | Expected outcome | Reality |
|------|--------------|------------|------------------|--------|
| Harness emits logs | run_grafana_tests.ps1 runs harness (dotnet) to Loki | No change (harness is dotnet). | Logs with testing=true, test_tag=grafana-harness. | Unchanged. |
| Assertions | AssertLokiQueries (dotnet) queries Loki HTTP API | Use simsteward_loki_query or Docker MCP query_loki_logs with same LogQL; agent or script asserts counts and fields. | ≥2 action_result, ≥1 incident_detected, ≥1 session_digest; action_result has correlation_id, success, action. | MCP path works if (1) Loki URL/datasource is configured for the MCP, (2) time range includes harness run. Retry/backoff (AssertLokiQueries uses 30s) can be done by agent loop or script. |

**Document:** After running the harness, call simsteward_loki_query (or Docker MCP query_loki_logs) with `{app="sim-steward"} | json | testing = "true" | test_tag = "grafana-harness"` and assert event counts and required fields per **docs/observability-testing.md**. See [assert_via_mcp.md](../../../tests/observability/assert_via_mcp.md) for step-by-step procedure.

---

## 6. Summary: test matrix (concise)

| Feature | MCP server | Tool(s) | Prerequisite | Expected | Reality / fix |
|---------|------------|--------|--------------|----------|---------------|
| Plugin health | SimSteward | simsteward_health | Plugin running (if live) | Mode, WS, session, baseline | Document "plugin required"; optional explicit unreachable response |
| Session diagnostics | SimSteward | simsteward_diagnostics | Plugin + optional replay | sessions[], simMode, etc. | Same |
| Replay/capture actions | SimSteward | simsteward_action | Plugin + iRacing/replay | success/error | Document |
| Loki labels | SimSteward | simsteward_loki_labels | Loki URL in MCP config | Label names/values | Document config source |
| Loki query/correlate | SimSteward | simsteward_loki_query, simsteward_loki_correlate | Same | Log lines | Same |
| Loki datasource discovery | Docker/Grafana | list_datasources(type=loki) | Grafana URL | UID e.g. loki_local | Document Grafana URL + UID |
| Loki labels (Grafana) | Docker/Grafana | list_loki_label_names/values | Grafana + Loki + datasource UID | app, env, etc. | Document UID |
| Loki query (Grafana) | Docker/Grafana | query_loki_logs | Same | Log lines | Document; optional MCP assertion procedure |
| Replay workflow | SimSteward | health, diagnostics, action | Plugin (+ replay for sessions) | Same as ReplayWorkflowTest.ps1 | Snapshot read: file or new resource |

---

## References

- [docs/replay-workflow.md](../../replay-workflow.md) — replay test cases and MCP path
- [docs/observability-testing.md](../../observability-testing.md) — harness, AssertLokiQueries, asserting via MCP
- [docs/observability-local.md](../../observability-local.md) — local stack and Grafana/Loki MCP validation
- [tests/observability/assert_via_mcp.md](../tests/observability/assert_via_mcp.md) — step-by-step MCP assertion procedure
