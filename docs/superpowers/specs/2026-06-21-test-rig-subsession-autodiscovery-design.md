# Test Rig — Subsession Auto-Discovery (design)

**Date:** 2026-06-21
**Status:** Approved (brainstorm), pending implementation plan
**Branch:** feat/test-rig

## Problem

`scripts/test-rig/run.js` requires `--subsession <id>` on the command line. That id
locates the index file at `%LOCALAPPDATA%\SimSteward\replay-incident-index\<id>.json`
and tells the rig which replay it is exercising. The subsession is **already knowable**
from the live session (the plugin reads `WeekendInfo.SubSessionID` from IRSDK at many
call sites), so passing it by hand is redundant and a foot-gun: a wrong id silently
builds or polls the wrong index path with no signal that the run is meaningless.

Goal: **run a scenario without typing the subsession** — the orchestrator auto-detects
it from whatever replay is loaded, and a manually-passed id becomes a safety assertion
rather than a source of error.

## Decisions (locked during brainstorm)

- **Source of truth = the plugin, over the WebSocket** (not the dashboard HTML; `run.js`
  talks to the WS directly and never drives a browser).
- **Carrier = a dedicated `session_hello` message** sent on connect, not a field on
  `replay_state_tick`. Identity is its own message; per-tick state stays per-tick.
- **`--subsession` precedence:** auto-detect by default; an explicitly-passed id is
  compared to the hello and **aborts on mismatch**.
- **No replay loaded:** wait up to the existing anchor timeout (30s) for a hello carrying
  a real subsession, then **fail fast** ("load a replay first").

## Design

### Plugin (C#)

New WS message, plugin → dashboard:

```json
{ "type": "session_hello",
  "sub_session_id": 12345678,
  "sim_mode": "replay",
  "plugin_mode": "Replay" }
```

- `sub_session_id` is `null` when no session is loaded / id is unknown (`SubSessionID`
  is `0` or IRSDK data is absent).
- Sent to a client **right after it connects**, in `DashboardBridge.OnOpen`, alongside
  the existing `getStateForNewClient` / `getLogTailForNewClient` sends.
- **Re-broadcast to all clients whenever the loaded subsession changes**, so a client
  that connected before the replay finished loading still receives the real id. One
  broadcast on change — never per-tick.

Mechanics:

- `DashboardBridge` gains a `Func<string> getHelloForNewClient` constructor callback
  (mirrors `getStateForNewClient`) invoked in `OnOpen`, plus a public
  `BroadcastHello(string json)` used for the on-change re-send.
- The plugin builds the payload from `_irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID`
  (same source as every other call site); `0`/unknown serializes as `null`.
- The on-change re-send is wired into the existing session/subsession transition point
  (the plugin already tracks `_logCtxSubsession`).
- `sub_session_id` is **not** added to `replay_state_tick`.

### Orchestrator (`scripts/test-rig/run.js`)

- `--subsession` becomes **optional**. `parseArgs` no longer rejects its absence; only
  `--scenario` remains required.
- After WS connect and before anchoring, wait for a `session_hello` whose
  `sub_session_id` is non-null, using `ANCHOR_TIMEOUT_MS` (30s).
- Resolution via a pure, unit-testable helper `resolveSubsession({ flagValue, helloValue })`:

  | flag | hello | outcome |
  |---|---|---|
  | omitted | present | use hello's id |
  | present | matches | use it, proceed |
  | present | mismatches | **abort** — `subsession_mismatch (flag=… loaded=…)`, exit non-zero |
  | any | absent after timeout | **fail fast** — `no_replay_loaded`, exit non-zero |

- The resolved id flows into `indexFilePath()` exactly as today.

### Contract doc

Add a `session_hello` subsection to `docs/RULES-TestRig-Contract.md` under
"WS pushes (plugin → dashboard)": the shape above, when it is sent (on connect + on
subsession change), and the `null` semantics.

### Tests (`scripts/test-rig/run.test.js`)

- `resolveSubsession` — one case per branch in the table (use / match / mismatch-abort /
  timeout-fail).
- `parseArgs` — accepts a missing `--subsession`; still rejects a missing/invalid
  `--scenario`.
- Pure functions; no live WS required, consistent with the existing unit-test style.

## Out of scope (YAGNI)

- No dashboard HTML/JS changes.
- No new CLI flags beyond making `--subsession` optional.
- No extra `session_hello` fields beyond `sub_session_id`, `sim_mode`, `plugin_mode`.
- `sub_session_id` is not added to `replay_state_tick`.
