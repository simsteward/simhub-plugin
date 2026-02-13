# Spec: OBS WebSocket Connection

**FR-IDs:** FR-005
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-005-006-007-OBS-Integration.md`

---

## Overview

The OBS connection is Sim Steward's transport layer to OBS Studio. It manages the WebSocket lifecycle — connect, authenticate, maintain, reconnect — so that higher-level features (FR-006 recording control, FR-007 clip management) can send commands without worrying about the connection itself.

This spec covers **only** the connection layer. Recording commands (`StartRecord`, `StopRecord`) and clip handling are scoped to a separate FR-006/007 spec.

The connection manager is the single point of contact between the plugin and OBS. It owns the socket, the auth handshake, the reconnection policy, and the connection state exposed to the rest of the plugin and to the SimHub UI.

Aligns with PRD Section 4 (FR-005) and Section 6 (Technical Architecture — "OBS WebSocket Client" box).

---

## Detailed Requirements

### R-OBS-01: Connection Lifecycle

The connection manager supports four operations:

| Operation | Trigger | Behavior |
|-----------|---------|----------|
| **Connect** | Plugin `Init` (if auto-connect enabled) or explicit user action | Open WebSocket to configured URL, perform auth handshake, transition to Connected. |
| **Disconnect** | Plugin `End`, user action, or settings URL changed | Send WebSocket close frame, dispose socket, transition to Disconnected. |
| **Reconnect** | Connection lost unexpectedly (OBS closed, network error) | Automatic reconnection with exponential backoff (R-OBS-05). |
| **Send Request** | Called by higher-level features (FR-006, FR-007) | Send an obs-websocket OpCode 6 request, return the OpCode 7 response. Throws if not Connected. |

**Auto-connect on Init:** The connection manager attempts to connect during `Init` if OBS settings are configured (non-empty URL). If OBS isn't reachable, it transitions to Error state and begins reconnection — it does not block plugin startup.

**Connect must be non-blocking.** The WebSocket handshake runs on a background thread. `Init` returns immediately; the connection state updates asynchronously.

### R-OBS-02: obs-websocket 5.x Authentication

The connection manager implements the obs-websocket 5.x challenge-response authentication handshake.

**Handshake sequence:**

1. Open WebSocket to configured URL (e.g., `ws://localhost:4455`)
2. Receive **OpCode 0 (Hello)** from OBS — contains `obsWebSocketVersion`, `rpcVersion`, and optionally `authentication` (with `challenge` and `salt`)
3. If `authentication` is present and a password is configured:
   - Compute: `base64(SHA-256(password + salt))` → `base64Secret`
   - Compute: `base64(SHA-256(base64Secret + challenge))` → `authResponse`
4. Send **OpCode 1 (Identify)** with `rpcVersion: 1`, `authResponse` (if auth required), and `eventSubscriptions` bitmask
5. Receive **OpCode 2 (Identified)** — connection is ready

**Auth edge cases:**

| Scenario | Behavior |
|----------|----------|
| OBS has no password, plugin has no password | Skip auth fields in Identify. Handshake succeeds. |
| OBS has no password, plugin has a password | OBS ignores the extra auth. Handshake succeeds. |
| OBS has a password, plugin has no password | OBS rejects Identify. Connection closed with code `4009`. Transition to Error with message "Authentication failed — check OBS password in settings." |
| OBS has a password, plugin has wrong password | Same as above — code `4009`, Error state. |

**Event subscriptions:** Subscribe to `Outputs` category (bitmask `64`) to receive `RecordStateChanged` events. This allows FR-006 to react to recording state changes without polling.

**Crypto:** Use `System.Security.Cryptography.SHA256` and `Convert.ToBase64String`. Both available in .NET 4.8 — no external dependency.

### R-OBS-03: Configuration from FR-008 Settings

The connection manager reads OBS configuration from the shared settings model (FR-008, R-SET-01):

| Setting | Model Field | Usage |
|---------|-------------|-------|
| WebSocket URL (includes port) | `ObsWebSocketUrl` | WebSocket endpoint. Default: `ws://localhost:4455` |
| Password | `ObsWebSocketPassword` | Auth handshake. Empty = no auth. |

**Settings changes at runtime:** Per FR-008 R-SET-07, settings take effect immediately. When the OBS URL or password changes, the connection manager must:

1. Disconnect the current connection (if any)
2. Reconnect using the new settings

The simplest approach: the settings UI calls a `Reconnect()` method on the connection manager after saving OBS settings changes.

### R-OBS-04: Connection State

The connection exposes its state as a SimHub plugin property so that the overlay (FR-003) and settings UI (FR-008) can display it.

**State machine:**

```
                         ┌───────────────────────────┐
                         │                           │
                         ▼                           │
  ┌──────────────┐   Connect()   ┌──────────────┐   │
  │ Disconnected │──────────────▶│  Connecting   │   │
  └──────┬───────┘               └──────┬───────┘   │
         ▲                              │            │
         │                     ┌────────┴────────┐   │
     Disconnect()         Identified         Error   │
     or max retries            │                │    │
         │                     ▼                ▼    │
         │              ┌──────────────┐  ┌─────────┐│
         │              │  Connected   │  │  Error   ││
         │              └──────┬───────┘  └────┬────┘│
         │                     │               │     │
         │              Connection lost   Auto-retry │
         │                     │               │     │
         │                     ▼               │     │
         │              ┌──────────────┐       │     │
         └──────────────│ Reconnecting │◀──────┘     │
                        └──────┬───────┘             │
                               │                     │
                          Success ───────────────────┘
```

**States:**

| State | Description | Property Value |
|-------|-------------|----------------|
| **Disconnected** | No connection. Initial state, or after explicit disconnect / max retries exhausted. | `"Disconnected"` |
| **Connecting** | WebSocket open + auth handshake in progress. | `"Connecting"` |
| **Connected** | Identified. Ready to send/receive requests. | `"Connected"` |
| **Reconnecting** | Connection lost. Attempting to re-establish with backoff. | `"Reconnecting"` |
| **Error** | A connection attempt failed. Includes a reason string. Transient — auto-transitions to Reconnecting (if retries remain) or Disconnected (if exhausted). | `"Error"` |

**Transitions:**

| From | To | Trigger |
|------|-----|---------|
| Disconnected | Connecting | `Connect()` called |
| Connecting | Connected | OpCode 2 (Identified) received |
| Connecting | Error | Timeout, auth failure, connection refused |
| Connected | Reconnecting | Socket closed unexpectedly, OBS exited |
| Reconnecting | Connecting | Backoff timer fires, retry attempt begins |
| Reconnecting | Disconnected | Max retries exhausted |
| Error | Reconnecting | Auto-retry (if retries remain) |
| Error | Disconnected | Max retries exhausted, or auth failure (no retry) |
| Any | Disconnected | `Disconnect()` called explicitly |

**SimHub properties:** Exposed as:
- `SimSteward.OBS.StatusText` (string) — human-readable state label (e.g., `"Connected"`, `"Disconnected"`, `"Reconnecting"`). Updated on every state transition. The overlay (FR-003a R-OVR-05) and settings UI bind to this property.
- `SimSteward.OBS.IsConnected` (bool) — `true` when state is `Connected`, `false` otherwise. Convenience property for overlay color binding.
- `SimSteward.OBS.ConnectionError` (string) — last error message (e.g., "Authentication failed", "Connection refused"). Cleared on successful connection.

### R-OBS-05: Reconnection Policy

When the connection drops unexpectedly, the connection manager automatically attempts to reconnect.

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Initial delay | 1 second | Quick first retry — OBS may have just hiccupped |
| Backoff multiplier | 2x | Exponential: 1s → 2s → 4s → 8s → 16s → 30s |
| Max delay | 30 seconds | Don't wait longer than 30s between attempts |
| Max retries | 10 | After 10 failures (~3.5 min total), give up |
| Give-up behavior | Transition to Disconnected, log warning | User must manually reconnect (via settings test button or plugin restart) |

**Auth failures do NOT trigger reconnection.** If the handshake fails with close code `4009` (authentication error), retrying with the same credentials is pointless. Transition directly to Error → Disconnected. The user must fix the password in settings.

**OBS version mismatch:** If `GetVersion` (sent after Identified) returns an obs-websocket version < 5.0, disconnect with error "OBS WebSocket 5.x required (found {version}). Update OBS to version 28 or later." No retry.

### R-OBS-06: Error States

The connection manager handles these error conditions explicitly:

| Condition | Detection | User-Facing Message | Retry? |
|-----------|-----------|---------------------|--------|
| OBS not running | Connection refused (`ECONNREFUSED`) | "Cannot connect to OBS. Is OBS Studio running?" | Yes (backoff) |
| OBS not installed | Same as not running — no way to distinguish from connection layer | Same message as above | Yes (backoff) — give up after max retries |
| Wrong password | WebSocket close code `4009` | "Authentication failed — check OBS password in settings." | No |
| OBS closed during session | Socket closed / error event | "OBS connection lost. Reconnecting..." | Yes (backoff) |
| Network error (e.g., firewall) | Connection timeout or refused | "Cannot connect to OBS at {url}. Check URL and firewall settings." | Yes (backoff) |
| OBS WebSocket < 5.x | `GetVersion` response check | "OBS WebSocket 5.x required. Update OBS to version 28 or later." | No |
| Invalid URL format | Pre-connect validation | Blocked at settings validation (FR-008 R-SET-02). Connection not attempted. | N/A |
| WebSocket handshake timeout | No OpCode 0 received within 10s | "OBS connection timed out." | Yes (backoff) |

**"OBS not installed" note:** The connection layer cannot detect whether OBS is installed — it only knows whether the WebSocket endpoint is reachable. The error message covers both "not installed" and "not running" with the same guidance: "Is OBS Studio running?"

### R-OBS-07: Thread Safety

WebSocket callbacks fire on background threads. SimHub's `DataUpdate` runs on its own thread. The WPF settings UI runs on the UI thread. The connection manager must handle all three safely.

**Rules:**

1. **State transitions** are protected by a lock. Only one thread may change state at a time.
2. **SimHub property updates** (`SetPropertyValue`) are called from state transition handlers. SimHub's property system is thread-safe for writes — no marshaling needed.
3. **WPF UI updates** (settings tab connection status) require `Dispatcher.Invoke` / `Dispatcher.BeginInvoke` if updated from a non-UI thread. The connection manager fires a C# `event` on state change; the settings UI subscribes and marshals to the UI thread.
4. **Request/response correlation:** The connection manager serializes outgoing requests by assigning a unique `requestId` to each OpCode 6 message and matching it against the `requestId` in the OpCode 7 response. A `TaskCompletionSource<T>` or callback dictionary keyed by `requestId` handles async responses. This allows multiple features to issue requests without blocking each other.
5. **No blocking the SimHub DataUpdate loop.** `DataUpdate` must never await a WebSocket operation. Connection state is read-only from `DataUpdate`; commands are fire-and-forget or dispatched to the connection manager's background thread.

---

## Technical Design Notes

### Connection Manager Class

A single `ObsConnectionManager` class owns the full connection lifecycle. Suggested structure:

```
plugin/Obs/
├── ObsConnectionManager.cs     # Connection lifecycle, state machine, reconnect
├── ObsConnectionState.cs       # Enum: Disconnected, Connecting, Connected, Reconnecting, Error
├── ObsAuthHelper.cs            # SHA-256 challenge-response computation
└── ObsMessageTypes.cs          # Strongly-typed models for OpCode 0/1/2/6/7 messages
```

**Key design points:**
- The connection manager is instantiated once during `Init` and disposed during `End`.
- It exposes `Connect()`, `Disconnect()`, `SendRequestAsync<TResponse>(string requestType, object requestData)`.
- State is exposed via a `State` property (enum) and a `StateChanged` event.
- It holds a reference to the plugin's settings model (FR-008) for URL/password.

### State Machine Implementation

A simple `switch`-on-enum in a `TransitionTo(newState)` method that validates transitions and fires the `StateChanged` event. No need for a full state machine library — there are only 5 states and ~10 transitions.

### Library Choice

**Pending spike results** (`docs/tech/plans/obs-websocket-spike.md`). The spike evaluates three candidates:

1. **websocket-sharp** (test first) — event-driven, .NET 3.5+, MIT
2. **WebSocket4Net** (test second) — event-driven, .NET 2.0+, Apache 2.0
3. **System.Net.WebSockets.ClientWebSocket** (test third) — built-in, known .NET 4.8 long-connection bugs

The connection manager should be designed with an internal `IWebSocketClient` interface so the concrete library can be swapped based on spike results without changing the state machine or auth logic.

### JSON Handling

obs-websocket messages are JSON. Use `Newtonsoft.Json` (bundled with SimHub) for serialization/deserialization. Define C# types for the message envelope and request/response payloads.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **SCAFFOLD-Plugin-Foundation** | Plugin lifecycle (`Init`, `End`), property registration (`AddProperty`, `SetPropertyValue`). |
| **FR-008 Plugin Settings** | OBS URL and password. Connection manager reads from the shared settings model. |
| **OBS Studio (external)** | Must be running with obs-websocket 5.x (built into OBS 28+). Plugin does not start or manage OBS. |
| **.NET 4.8 WebSocket library** | Spike-dependent. See obs-websocket-spike tech plan. |
| **Newtonsoft.Json** | JSON parsing. Bundled with SimHub — no extra NuGet needed. |
| **System.Security.Cryptography** | SHA-256 for auth handshake. Built into .NET 4.8. |

**Constraint: .NET Framework 4.8.** SimHub requires it. Modern `System.Net.WebSockets` improvements are unavailable. This is the #1 architectural risk (PRD Constraint #1). The spike must validate a working library before implementation begins.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Plugin connects to OBS via obs-websocket 5.x protocol | R-OBS-01, R-OBS-02 |
| Connection uses configurable URL/port/password (from FR-008 settings) | R-OBS-03 |
| Connection status is surfaced in the plugin UI | R-OBS-04 (properties `SimSteward.OBS.StatusText`, `SimSteward.OBS.IsConnected`) |
| Plugin handles OBS not running, connection loss, reconnection attempts | R-OBS-05, R-OBS-06 |

---

## Open Questions

| # | Question | Impact | Resolution Path |
|---|----------|--------|-----------------|
| 1 | Which WebSocket library works on .NET 4.8? | Determines implementation approach. If all fail, fallback to out-of-process bridge (spike Fallback A). | OBS WebSocket spike (`docs/tech/plans/obs-websocket-spike.md`) |
| 2 | Does `websocket-sharp` handle OBS's close-code semantics (e.g., `4009` for auth failure) correctly? | Affects error differentiation in R-OBS-06. | Validated during spike Test 1. |
| 3 | Thread interaction between WebSocket callbacks and SimHub's DataUpdate loop — any deadlock risk? | Affects R-OBS-07 threading design. | Validated during spike Test 5 (SimHub integration). |
