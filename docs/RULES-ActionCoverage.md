# Action coverage — 100% structured log rule

Canonical reference for *every user-facing interaction must be logged with the correct `domain` and fields*. Grafana/Loki taxonomy: [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md).

## Goals

1. **Coverage** — No new button, dashboard handler, `DispatchAction` branch, or iRacing callback without a matching structured log.
2. **Schema** — Action and iRacing logs carry session context; incident logs carry driver/uniqueness fields (see [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) § Schema reference).

## Log category taxonomy (`domain`)

| `domain`    | When to use |
|-------------|-------------|
| `lifecycle` | Plugin start/stop, SDK connect/disconnect |
| `action`    | Button or command from dashboard → plugin (`DispatchAction`) |
| `ui`        | Dashboard-only interactions that do not cross the WS bridge |
| `iracing`   | iRacing SDK: session change, mode change, incident, replay seek |
| `system`    | Dependency checks, ping, WS client connect/disconnect |

## Required field sets

### Session context (all `action` + `iracing` logs)

Include each field, or fall back to `"not in session"` (`SessionLogging.NotInSession`):

| Field                 | Meaning |
|-----------------------|---------|
| `subsession_id`       | iRacing `WeekendInfo.SubSessionID` (globally unique split) |
| `parent_session_id`   | Broader event id (`WeekendInfo.SessionID`) |
| `session_num`         | Phase: practice / qualify / race (`SessionNum`) |
| `track_display_name`  | Track name from `WeekendInfo` |

Action and `dashboard_ui_event` paths merge these via `MergeSessionAndRoutingFields()` in `SimStewardPlugin`.

### Incident uniqueness (`iracing_incident` rule / `incident_detected` emission)

| Field           | Meaning |
|-----------------|---------|
| `unique_user_id`| iRacing CustID for the driver |
| `camera_view`   | Camera / view context (see Grafana doc for `cam_car_idx` / `camera_group`) |
| `start_frame`   | Replay frame at incident start |
| `end_frame`     | Replay frame at incident end |
| `session_time`  | `SessionTime` at detection |

**Canonical event name in this rule:** `iracing_incident`. **Current JSONL `event`:** `incident_detected` (tracker) — align names when code is updated; until then, LogQL uses `incident_detected`.

### Driver identity (per-driver events)

`subsession_id` + `unique_user_id` + `display_name` (or `driver_name` in emitted logs) uniquely identifies a driver in a server instance.

## The rule (summary)

### C# plugin — actions (`domain="action"`)

- Every `DispatchAction` branch MUST log `action_dispatched` (before) and `action_result` (after).
- Required: `action`, `arg`, `correlation_id`, success/error, plus session context fields (via `MergeSessionAndRoutingFields()`).

### Dashboard (JS → WS)

- Every button click MUST send structured log payload with `event:"dashboard_ui_event"`, `element_id`, `event_type:"click"`, `message`.
- UI-only: `event_type:"ui_interaction"`, `domain:"ui"`.

### iRacing (`domain="iracing"`)

- Session start/end, mode change, replay seek, and incident as specified in [CLAUDE.md](../CLAUDE.md) / [.cursorrules](../.cursorrules) Action Coverage section.

## PR checklist

- [ ] New dashboard button → `dashboard_ui_event` log
- [ ] New `DispatchAction` branch → `action_dispatched` + `action_result`
- [ ] New iRacing SDK handler → structured log, `domain="iracing"`
- [ ] Incident logging → full uniqueness signature (`unique_user_id`, start/end frame, camera / view)

## Code touchpoints

| Area | Location |
|------|----------|
| Session merge for actions / dashboard logs | [SimStewardPlugin.cs](../src/SimSteward.Plugin/SimStewardPlugin.cs) — `MergeSessionAndRoutingFields`, `OnDashboardStructuredLog` |
| Structured log API | [PluginLogger.cs](../src/SimSteward.Plugin/PluginLogger.cs) — `Structured()` |
| `dashboard_ui_event` bridge | [DashboardBridge.cs](../src/SimSteward.Plugin/DashboardBridge.cs) |
| Session fallback constant | [SessionLogging.cs](../src/SimSteward.Plugin/SessionLogging.cs) — `NotInSession` |

## Pending verification (from prior logging plan)

- Confirm every `DashboardBridge` action path that logs actions applies session merge (spot-check `MergeSessionAndRoutingFields` on all branches).
- When incident pipeline is extended, ensure optional fields in [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) match this rule.
