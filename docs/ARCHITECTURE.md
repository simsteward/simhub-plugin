# Sim Steward — Architecture & Data Structures

Diagrams covering C# data structures, WebSocket message contracts, data API schema, and runtime communication flows.

---

## C# Plugin — Core Data Structures

Classes that drive the WebSocket state broadcast and structured logging.

```mermaid
classDiagram
  class PluginSnapshot {
    +string PluginMode
    +double CurrentSessionTime
    +string CurrentSessionTimeFormatted
    +int Lap
    +int Frame
    +int FrameEnd
    +int ReplaySessionCount
    +int ReplaySessionNum
    +string ReplaySessionName
    +PluginDiagnostics Diagnostics
  }
  class PluginDiagnostics {
    +bool IrsdkStarted
    +bool IrsdkConnected
    +bool WsRunning
    +int WsPort
    +int WsClients
    +bool SteamRunning
    +bool SimHubHttpListening
    +string DashboardPing
  }
  class LogEntry {
    +string Level
    +string Message
    +string Timestamp
    +string Component
    +string Event
    +Dictionary~string,object~ Fields
    +string SessionId
    +string SessionSeq
    +string Domain
    +int ReplayFrame
    +string IncidentId
  }
  class SessionLogging {
    +string NotInSession$ = "not in session"
    +int LapUnknown$ = -1
    +AppendRoutingAndDestination(fields)$
  }
  PluginSnapshot --> PluginDiagnostics : Diagnostics
  note for PluginSnapshot "Broadcast via WS at ~5 Hz\nSerialized by BuildStateJson()"
  note for LogEntry "Written to plugin-structured.jsonl\nStreamed to dashboard via WS logEvents"
  note for SessionLogging "Static helpers — all action and\niracing logs call AppendRoutingAndDestination"
```

---

## WebSocket Message Contract

All messages exchanged between plugin and dashboard over port 19847.

```mermaid
classDiagram
  direction LR

  class StateMessage {
    +type = "state"
    +string pluginMode
    +double currentSessionTime
    +string currentSessionTimeFormatted
    +int lap
    +int frame
    +int frameEnd
    +int replaySessionCount
    +int replaySessionNum
    +string replaySessionName
    +PluginDiagnostics diagnostics
  }
  class LogEventsMessage {
    +type = "logEvents"
    +LogEntry[] entries
  }
  class ActionResultMessage {
    +type = "actionResult"
    +string action
    +bool success
    +string result
    +string error
  }
  class PongMessage {
    +type = "pong"
  }
  class DashboardCommand {
    +string action
    +string arg
  }
  class DashboardLogPayload {
    +action = "log"
    +event = "dashboard_ui_event"
    +string element_id
    +string event_type
    +string message
    +string value
  }

  note for StateMessage "Plugin → Dashboard\n~5 Hz (200 ms throttle)"
  note for LogEventsMessage "Plugin → Dashboard\non every structured log write"
  note for ActionResultMessage "Plugin → Dashboard\nresponse to every command"
  note for DashboardCommand "Dashboard → Plugin\nall replay/seek/capture actions"
  note for DashboardLogPayload "Dashboard → Plugin\nUI click logging (action = log)"
```

---

## Observability Egress (Security & CORS)

**CRITICAL RULE:** The SimHub Dashboard (client-side JS) must **NEVER** make direct HTTP/API requests to external observability platforms (e.g., Grafana Loki, Cloudflare).

*   **Why?**
    1.  **Security:** Doing so would require embedding sensitive API tokens (like `SIMSTEWARD_LOKI_TOKEN`) directly into the client-side JavaScript, where anyone could extract them.
    2.  **CORS:** Browsers will block cross-origin requests from the local SimHub web server (`localhost:8888`) to external domains unless complex and insecure CORS policies are configured on the destination server.
*   **The Solution:** The dashboard must route all observability intents (like capturing an incident) through the WebSocket to the C# Plugin. The C# Plugin acts as a secure backend, utilizing `PluginLogger` to batch and execute the HTTPS POST requests to `SIMSTEWARD_LOKI_URL` from a trusted server environment.

---

## Data API Schema

Cloudflare Worker + D1 (mirrors local Flask + SQLite). Applied from `worker/schema.sql`.

```mermaid
erDiagram
  DRIVERS {
    int user_id PK
    text user_name
    text first_seen_at
    text last_seen_at
  }
  SESSIONS {
    int sub_session_id PK
    int session_id
    int series_id
    text track_name
    text session_type
    text captured_at
  }
  INCIDENTS {
    text id PK
    int sub_session_id FK
    int session_num
    int user_id FK
    int car_idx
    real session_time
    int replay_frame_num_end
    int delta
    text type
    text cause
    int other_user_id
    text source
    text processed_at
    int fingerprint_version
  }
  INCIDENT_CAPTURES {
    text id PK
    text incident_id FK
    int pov_user_id
    int pov_car_idx
    text camera_type
    int frame_start
    int frame_end
    text clip_r2_path
    text telemetry_json
    text telemetry_r2_path
    text subscription_tier
    text captured_at
  }
  DRIVERS ||--o{ INCIDENTS : "user_id"
  SESSIONS ||--o{ INCIDENTS : "sub_session_id"
  INCIDENTS ||--o{ INCIDENT_CAPTURES : "incident_id"
```

---

## Action Dispatch — Sequence

How a dashboard button press travels through the stack and returns a result.

```mermaid
sequenceDiagram
  participant D as Dashboard (JS)
  participant WS as DashboardBridge (Fleck)
  participant P as SimStewardPlugin
  participant IR as iRacing SDK

  D->>WS: { action, arg }
  WS->>WS: Authenticate token
  WS->>P: DispatchAction(action, arg, correlationId)
  P->>P: Log action_dispatched
  P->>P: MergeSessionAndRoutingFields()

  alt replay_speed
    P->>IR: ReplaySetPlaySpeed(multiplier, slowMotion)
  else replay_seek (prev/next)
    P->>IR: ReplaySearch(PrevIncident | NextIncident)
  else replay_jump (start/end)
    P->>IR: ReplaySearch(ToStart | ToEnd)
  else seek_to_incident
    P->>IR: ReplaySetPlayPosition(Begin, frame)
  else replay_session (prev/next)
    P->>IR: ReplaySearch(PrevSession | NextSession)
  else unknown action
    P-->>P: return not_supported
  end

  IR-->>P: (iRacing acts asynchronously)
  P->>P: Log action_result (success/error + duration_ms)
  P-->>WS: (success, result, error)
  WS-->>D: { type:"actionResult", action, success, result?, error? }
```

---

## Incident Detection — Sequence

How iRacing incidents flow from SDK shared memory to the dashboard leaderboard and Loki.

```mermaid
sequenceDiagram
  participant IR as iRacing shared memory
  participant T as IncidentTracker
  participant P as SimStewardPlugin
  participant JSONL as plugin-structured.jsonl
  participant Loki as Grafana Loki
  participant D as Dashboard (JS)

  loop DataUpdate() ~60 Hz
    IR->>P: CurDriverIncidentCount per car (YAML / telemetry)
    P->>T: OnSessionInfoUpdate() / tick
    T->>T: Compare delta vs baseline per car
    alt delta > 0 detected
      T->>P: incident_detected callback
      P->>P: Enrich with session context (MergeSessionAndRoutingFields)
      P->>JSONL: Write incident_detected NDJSON line
      JSONL-->>Loki: Plugin batches HTTPS POST to SIMSTEWARD_LOKI_URL (async)
      P->>D: Broadcast updated incidents[] via WebSocket
      D->>D: Re-render leaderboard + filter chips
    end
  end
```

---

## Session Context Fields

Fields injected into every `action_dispatched`, `action_result`, and `iracing_incident` log via `MergeSessionAndRoutingFields()`. All fall back to `"not in session"` when iRacing is not connected.

```mermaid
classDiagram
  class SessionContext {
    +string subsession_id
    +string parent_session_id
    +string session_num
    +string track_display_name
    +int lap
    +string log_env
    +string loki_push_target
  }
  class IncidentFingerprint {
    +string unique_user_id
    +string driver_name
    +string subsession_id
    +string session_num
    +string track_display_name
    +string session_time
    +string lap
    +int replay_frame
    +int replay_frame_end
    +string camera_group
  }
  note for SessionContext "Merged into every action and iracing log.\nSource: _logCtxSubsession, _logCtxParent,\n_logCtxSessionNum, _logCtxTrack, _logCtxLap"
  note for IncidentFingerprint "Combined: parent_session_id + subsession_id\n+ session_num + unique_user_id + replay_frame\nuniquely identify an incident across sessions"
```

---

## ContextStream KB links

| Spec | Doc ID |
|------|--------|
| Sim Steward — Data Routing (OTel / Loki / Prometheus) | `cbae1c33-c778-4e9a-9a8d-6b3e3c8c368b` |
| Troubleshooting | `88274879-cd2d-4d86-9766-c86b88f95cfe` |
| Observability — Scaling | `99bd9e71-2b08-4eea-b2d4-f7bb22b38af0` |
| Sim Steward — User Flows | `3eb2ceb5-f859-417b-a7e4-8dde05493d55` |
| Sim Steward — User Features (PM) | `c5157521-3681-4432-9c44-a49d8ee3a955` |
