# Spike: OBS WebSocket Connectivity from .NET 4.8

**FR-IDs:** FR-005, FR-006, FR-007
**Risk:** High — #1 architectural risk (PRD Section 7, Constraint #1)
**Status:** Not started
**Created:** 2026-02-13

---

## Question to Answer

Can a .NET Framework 4.8 SimHub plugin maintain a stable WebSocket connection to OBS Studio's obs-websocket 5.x server, complete the auth handshake, and reliably start/stop recording over the duration of a typical iRacing session (1-3 hours)?

If not, what's the simplest fallback that keeps OBS integration viable?

---

## Why This Is the #1 Risk

Every recording feature depends on this connection. FR-005 (OBS connection), FR-006 (start/stop recording), and FR-007 (clip save prompt) are all blocked if WebSocket from .NET 4.8 to OBS doesn't work. There is no alternative recording path in the architecture — OBS is the recording backbone.

The risk is specific to .NET Framework 4.8, which SimHub requires. Modern .NET (6+) has robust WebSocket support; .NET 4.8 has known issues (see Candidate Libraries below).

---

## Candidate Libraries

### Option A: websocket-sharp (Recommended starting point)

- **NuGet:** `WebSocketSharp` 1.0.3-rc11 (prerelease, but widely used)
- **Targets:** .NET Framework 3.5+. Compatible with 4.8.
- **License:** MIT
- **Threading:** Event-driven (`OnMessage`, `OnOpen`, `OnClose`, `OnError` callbacks). Runs receive loop on a background thread.
- **Pros:** Purpose-built WebSocket client/server. Handles framing, ping/pong, close handshake. Large user base. Simple API.
- **Cons:** Prerelease on NuGet (no stable 1.0). Repo maintenance is sporadic — last significant commit activity varies. Must verify it handles OBS's WebSocket behavior correctly.
- **Spike priority:** Test first.

### Option B: WebSocket4Net

- **NuGet:** `WebSocket4Net` 0.15.2
- **Targets:** .NET Framework 2.0+ and .NET Standard 1.3. Compatible with 4.8.
- **License:** Apache 2.0
- **Threading:** Event-driven, similar callback model.
- **Pros:** Actively maintained (kerryjiang/WebSocket4Net). Broad .NET version support. Apache license.
- **Cons:** Less common in the SimHub ecosystem. API may be slightly more verbose.
- **Spike priority:** Test second if websocket-sharp fails.

### Option C: System.Net.WebSockets.ClientWebSocket (Built-in)

- **Availability:** .NET Framework 4.5+ (built-in, no NuGet needed).
- **License:** N/A (framework built-in).
- **Threading:** Async/await (`ConnectAsync`, `SendAsync`, `ReceiveAsync`).
- **Known issues on .NET 4.8:**
  - **WebSocketException after 90+ seconds** — SslStream disposal bug on .NET 4.7-4.7.2 connections. May affect 4.8 (needs testing with `ws://` and `wss://`).
  - **Invalid Connection header** — rejects `Upgrade,Keep-Alive` header values that other clients accept.
  - These issues do NOT reproduce on .NET Core/.NET 6+.
- **Pros:** No third-party dependency. Async/await fits C# patterns.
- **Cons:** The known long-connection bugs are exactly the failure mode we're worried about. OBS connections last 1-3 hours.
- **Spike priority:** Test third, only if options A and B both fail.

### Option D: Raw TcpClient (Last resort)

Manual WebSocket framing over `System.Net.Sockets.TcpClient`. Full control, but high implementation cost and high bug surface. Only consider if all three options above fail.

### Library Comparison Summary

| Criteria | websocket-sharp | WebSocket4Net | ClientWebSocket | Raw TcpClient |
|----------|----------------|---------------|-----------------|---------------|
| .NET 4.8 compat | Yes | Yes | Yes (w/ bugs) | Yes |
| NuGet available | Yes (prerelease) | Yes (stable) | Built-in | N/A |
| Long-connection stability | Unknown (test) | Unknown (test) | Known issues | Manual |
| Threading model | Event callbacks | Event callbacks | Async/await | Manual |
| Implementation effort | Low | Low | Medium | High |
| License | MIT | Apache 2.0 | N/A | N/A |

---

## obs-websocket 5.x Protocol Summary

Default port: `4455`. Protocol: `ws://localhost:4455` (unencrypted local connection).

### Connection Flow

```
Client                          OBS Server
  |---- WebSocket connect --------->|
  |<--- OpCode 0: Hello -----------|  (contains auth challenge if password set)
  |---- OpCode 1: Identify ------->|  (contains auth response + event subscriptions)
  |<--- OpCode 2: Identified ------|  (connection ready)
  |                                 |
  |---- OpCode 6: Request -------->|  (e.g., StartRecord)
  |<--- OpCode 7: Response --------|  (success/failure + data)
```

### Authentication (SHA-256 challenge-response)

If OBS has a password configured, the Hello message includes `challenge` and `salt` strings. The client must:

1. Concatenate `password + salt`
2. SHA-256 hash → base64 encode → produces `base64Secret`
3. Concatenate `base64Secret + challenge`
4. SHA-256 hash → base64 encode → produces auth string for Identify

.NET 4.8 has `System.Security.Cryptography.SHA256` and `Convert.ToBase64String` — no dependency concerns here.

### Key Requests for Sim Steward

| Request | Purpose | Response Data |
|---------|---------|---------------|
| `StartRecord` | Begin OBS recording | None (status only) |
| `StopRecord` | Stop OBS recording | `outputPath` — the saved file path |
| `GetRecordStatus` | Poll recording state | `outputActive`, `outputPaused`, `outputTimecode`, `outputBytes` |
| `GetVersion` | Verify OBS/protocol version | `obsVersion`, `obsWebSocketVersion`, `availableRequests` |

### Event Subscriptions

Subscribe to `Outputs` events (bitmask `64`) to receive `RecordStateChanged` events instead of polling `GetRecordStatus`. The `eventSubscriptions` field in Identify controls this.

---

## Spike Test Plan

### What to Build

A minimal .NET Framework 4.8 **console application** (not a full SimHub plugin yet). Keep it isolated to test WebSocket behavior without SimHub's runtime complicating results.

### Test Sequence

#### Test 1: Connect + Auth

1. Connect to `ws://localhost:4455`
2. Receive Hello (OpCode 0), parse JSON
3. If auth required: compute SHA-256 response
4. Send Identify (OpCode 1) with auth string
5. Receive Identified (OpCode 2)
6. **Pass:** Connection established, auth succeeds. **Fail:** Exception, timeout, or auth rejection.

#### Test 2: Start/Stop Recording

1. After successful connection, send `StartRecord` request (OpCode 6)
2. Wait 10 seconds
3. Send `StopRecord` request (OpCode 6)
4. Parse response for `outputPath`
5. Verify the file exists on disk at that path
6. **Pass:** File created with non-zero size. **Fail:** Request error, no file, or file path missing from response.

#### Test 3: Long-Running Stability (Critical)

1. Connect and authenticate
2. Keep connection open for **30 minutes minimum** (target 60 min)
3. Every 60 seconds, send `GetRecordStatus` request as a heartbeat/keepalive
4. Log: response time, any errors, connection drops
5. If connection drops, log the error and attempt reconnection
6. **Pass:** Zero unrecoverable disconnects over 30 min. **Fail:** Connection dies and cannot be reestablished, or frequent drops (>2 per 30 min).

#### Test 4: OBS Restart Recovery

1. Establish connection
2. Kill/close OBS while connected
3. Detect disconnection (how quickly? what error?)
4. Restart OBS
5. Attempt reconnection with exponential backoff (1s, 2s, 4s, 8s, cap at 30s)
6. **Pass:** Reconnection succeeds within 60s of OBS restart. **Fail:** Cannot detect disconnect, or reconnect fails.

#### Test 5: SimHub Integration (If Tests 1-4 pass)

1. Move the working code into a minimal SimHub plugin (IPlugin implementation)
2. Run the WebSocket client alongside SimHub's `DataUpdate` loop
3. Verify: no thread contention, no UI freezes, no exceptions during SimHub property updates
4. Start/stop a recording while SimHub is actively processing telemetry
5. **Pass:** Plugin loads, connects, records, no SimHub instability. **Fail:** Deadlocks, UI freezes, or SimHub errors.

### What to Measure

| Metric | Target | Method |
|--------|--------|--------|
| Auth handshake time | < 500ms | Stopwatch around connect-to-identified |
| Request round-trip | < 200ms | Stopwatch per request/response pair |
| Connection uptime | 30+ min without drop | Timer + heartbeat log |
| Reconnect time | < 60s after OBS restart | Timer from disconnect to re-identified |
| Memory growth | < 10MB over 30 min | Process memory snapshot every 5 min |
| SimHub UI thread blocking | 0 occurrences | Observe SimHub responsiveness during test |

---

## Success Criteria

### GREEN: Proceed with implementation

- websocket-sharp or WebSocket4Net connects, authenticates, and maintains a stable connection for 30+ minutes on .NET 4.8
- Start/Stop recording works reliably, `outputPath` returned on stop
- Reconnection after OBS restart works within 60 seconds
- No thread safety issues when running inside SimHub

### YELLOW: Proceed with mitigations

- Connection works but drops occasionally (1-2 times per hour) — add robust reconnection with backoff
- Minor threading issues — solvable with `SynchronizationContext` or lock-based marshaling
- One library fails but another succeeds — document which and why

### RED: Fallback required

- All three libraries fail to maintain stable connections on .NET 4.8
- Auth handshake cannot be completed (SHA-256 or WebSocket framing issues)
- SimHub integration causes deadlocks or crashes

---

## Fallback Options (If RED)

### Fallback A: Out-of-Process Bridge (Preferred fallback)

Spawn a small .NET 6+ console app (`sim-steward-obs-bridge.exe`) that handles the WebSocket connection to OBS. The SimHub plugin communicates with the bridge via **named pipes** (`System.IO.Pipes`, fully supported in .NET 4.8).

```
SimHub Plugin (.NET 4.8) --[named pipe]--> Bridge (.NET 6+) --[WebSocket]--> OBS
```

- **Pros:** Sidesteps all .NET 4.8 WebSocket issues. Bridge can use the mature `System.Net.WebSockets.ClientWebSocket` from .NET 6+.
- **Cons:** Extra process to manage (start, monitor, restart). Users must have .NET 6+ runtime installed (or ship self-contained). More moving parts.
- **Complexity:** Medium. Named pipes are simple. The bridge is < 200 lines.

### Fallback B: HTTP Polling via obs-websocket's REST-like Mode

obs-websocket 5.x is WebSocket-only (no REST API). This fallback is **not viable**.

### Fallback C: OBS CLI / Hotkey Simulation

Instead of WebSocket, use OBS's command-line flags (`--startrecording`, `--stoprecording`) or simulate hotkeys via `SendKeys`/`SendInput`.

- **Pros:** Zero WebSocket dependency.
- **Cons:** No feedback (can't get `outputPath`, can't confirm recording started/stopped, can't detect OBS state). Fragile. Loses FR-007 (clip save prompt) entirely.
- **Viability:** Last resort. Significant feature degradation.

---

## Key Risks to Watch During Spike

| Risk | Why It Matters | What to Look For |
|------|---------------|-----------------|
| **Thread safety** | WebSocket callbacks fire on background threads. SimHub's `DataUpdate` and UI run on specific threads. Cross-thread property updates can deadlock or crash. | Test property writes from WebSocket callback thread. Use `Invoke`/`BeginInvoke` or `SynchronizationContext` if needed. |
| **Memory leaks** | Long WebSocket connections can leak if buffers aren't recycled or event handlers pile up. 1-3 hour sessions are the norm. | Monitor process memory every 5 min during Test 3. Watch for growing byte arrays or event handler counts. |
| **Auth failure handling** | OBS closes the connection with code `4009` on bad auth. The plugin must surface this clearly, not just show "disconnected." | Deliberately send wrong password. Verify error is distinguishable from network failure. |
| **JSON parsing** | obs-websocket uses JSON messages. Need a .NET 4.8-compatible JSON library. SimHub bundles `Newtonsoft.Json` — confirm it's accessible to plugins. | Use `Newtonsoft.Json` (already in SimHub's runtime). If not accessible, `System.Text.Json` is NOT available on .NET 4.8 — would need NuGet package. |
| **Firewall / localhost** | Some security software blocks WebSocket on localhost. OBS binds to `127.0.0.1:4455` by default. | Test on a clean Windows install with default firewall. Note if any configuration is needed. |
| **OBS version variance** | obs-websocket 5.x is built into OBS 28+. Older OBS versions use 4.x (incompatible protocol). | Check OBS version via `GetVersion` request. Fail fast with clear error if < 28. |

---

## Spike Output

When complete, update this document with:

1. **Library chosen** and version tested
2. **Test results** for each of the 5 tests (pass/fail + notes)
3. **Metrics table** with actual measurements
4. **Verdict:** GREEN / YELLOW / RED
5. **Recommendations** for the implementation phase (connection manager design, threading strategy, error handling patterns)
6. **Code location** of the spike test app (for reference, not production use)
