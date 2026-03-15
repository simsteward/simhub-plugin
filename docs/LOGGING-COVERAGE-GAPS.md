# SimHub Logging — 100% Coverage Gap Analysis

**Objective:** 100% log coverage per project rules (SimHub.mdc, GRAFANA-LOGGING.md).  
**Method:** Coordinator evaluated code against mandatory coverage rules; local LLM (Ollama) was not available in session, so analysis was done from the same criteria.

## Coverage rules (mandatory)

- **Every action handler:** `action_dispatched` on entry, `action_result` on exit with `success`, `duration_ms`, `correlation_id`.
- **Every WebSocket message type in DashboardBridge:** `action_received` on entry with `correlation_id`.
- **Every iRacing broadcast/SDK call path:** log dispatch and outcome.
- **Every incident detection path:** `incident_detected` with full identifying fields.
- **Every lifecycle hook:** named lifecycle event.

---

## Gaps identified

### 1. [bridge] WebSocket message types without `action_received`

- **`ping`** — Handled in `HandleMessage`; sends `pong` and returns. No `action_received` (and no `DispatchAction`). For strict 100% coverage, either log a minimal `action_received` for `ping` (e.g. with correlation_id) or document that `ping` is intentionally excluded as non-action traffic.
- **`log`** — Handled in `HandleMessage`; may emit `dashboard_ui_event` via `_onStructuredLog`, but the **message type itself** does not log `action_received`. So the "log" WebSocket message type is not covered by the "every WebSocket message type must log action_received" rule.

### 2. [bridge] Error responses not structured-logged

- **`invalid_json`** — When `JObject.Parse(msg)` throws, `SendResponse(socket, "error", "invalid_json")` is sent and the method returns. No `_logger.Structured()` call. Gap: no structured event (e.g. `ws_message_error` or `action_received` with `action: "invalid_json"`) for audit/debugging.
- **`missing_action`** — When `action` is null or empty, `SendResponse(socket, "error", "missing_action")` and return. No structured log. Same gap.

### 3. [bridge] Optional: `action_result` for non-DispatchAction paths

- **`ping`** and **`log`** never call `_dispatchAction`, so they never get `action_dispatched`/`action_result`. If the rule is "every message type that can be considered an action," then either:
  - Add a single `action_received` (+ optional `action_result`) for `ping` and `log` (with correlation_id), or
  - Explicitly document that `ping` and `log` are non-actions and out of scope for action_dispatched/action_result.

### 4. [simhub-plugin] Stub action handlers

- **ToggleIntentionalCapture, SetReplayCaptureSpeed, SetSecondsBefore/After, SetCaptureDriver1/2, SetCaptureCamera1/2, SetAutoRotateAndCapture, ToggleAutoRotateAndCapture, SetAutoRotateDwellSeconds** — All go through `DispatchAction`, so they already get `action_dispatched` and `action_result`. **No gap** for coverage; they are covered.

### 5. [tracker] Incident and lifecycle

- **incident_detected** — Single path via `AddIncident` → `EmitStructured("incident_detected", ...)` with full fields. **No gap.**
- **baseline_established, session_reset, seek_backward_detected** — Emitted via `LogStructured` → plugin `_logger.Emit`. **No gap.**
- **yaml_update, tracker_status** — Emitted via `EmitStructured` (DEBUG / INFO). **No gap.**

### 6. [simhub-plugin] Lifecycle

- Lifecycle events in Init/End and SDK callbacks (logging_ready, plugin_started, bridge_starting, plugin_ready, irsdk_started, iracing_connected, iracing_disconnected, plugin_stopped, etc.) are present. **No gap.**

### 7. [simhub-plugin] iRacing broadcast / SDK

- Replay actions (ReplayPlayPause, ReplaySetSpeed, NextIncident, PrevIncident, etc.) are logged as `replay_control` with mode/speed/search_mode. **No gap** for those paths.
- No separate "SDK call outcome" log for every broadcast (e.g. RpySrch_NextIncident) — only the high-level `replay_control` event. If the rule is "every iRacing broadcast or SDK call path: log the dispatch event and its outcome," then a **possible gap** is: no explicit outcome log when a broadcast fails or returns an error (e.g. SDK not ready). Current behavior: `action_result` carries success/error from the handler; the handler may return an error string. So "outcome" is reflected in `action_result`. Only if we require a **dedicated** SDK-outcome event would this be a gap.

---

## Summary table

| # | Component   | Gap                                                                 | Severity (for 100%) |
|---|-------------|---------------------------------------------------------------------|----------------------|
| 1 | bridge      | `action_received` not logged for WebSocket message types `ping`, `log` | Medium (strict rule)  |
| 2 | bridge      | No structured log for `invalid_json` / `missing_action` error responses | Medium                |
| 3 | bridge      | No `action_result` for `ping` / `log` (optional if treated as non-actions) | Low                   |
| 4 | simhub-plugin | Optional: explicit SDK/broadcast outcome event when a replay SDK call fails | Low                   |

---

## Recommended next steps

1. **Bridge:** Add a single structured log for error responses: e.g. `ws_message_error` or `action_received` with `action: "error"`, `error: "invalid_json"` or `"missing_action"`, and optional `client_ip`. Use one event type and put the reason in fields.
2. **Bridge:** Decide policy for `ping` and `log`:
   - **Option A:** Log `action_received` for both (with correlation_id) for full message-type coverage; optionally skip `action_result` for `ping` to avoid noise.
   - **Option B:** Document in GRAFANA-LOGGING.md or SimHub.mdc that `ping` and `log` are excluded from the "every WebSocket message type" rule (non-actions).
3. **Plugin:** If desired, add a WARN/INFO log when a replay SDK call (e.g. RpySrch_NextIncident) fails or is skipped (e.g. SDK not ready), so "outcome" is visible beyond `action_result`.

---

*Generated from coordinator analysis; local LLM (Ollama) was not available in session.*
