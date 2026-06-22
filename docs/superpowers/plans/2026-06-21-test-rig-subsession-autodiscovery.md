# Test Rig Subsession Auto-Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `scripts/test-rig/run.js` auto-detect the iRacing subsession from the loaded replay over the WebSocket, so `--subsession` becomes an optional safety assertion instead of a required, error-prone argument.

**Architecture:** The plugin emits a new `session_hello` WS message (on client connect, and re-broadcast when the loaded subsession changes) carrying the current `SubSessionID`. `run.js` reads it after connecting and resolves the effective subsession: auto-detect when the flag is omitted, abort on mismatch when the flag is supplied, fail fast when no replay is loaded.

**Tech Stack:** Node.js (built-in `node:test`, built-in `WebSocket`); C# / .NET Framework 4.8 (Fleck WebSocket, Newtonsoft.Json, xUnit).

**Spec:** `docs/superpowers/specs/2026-06-21-test-rig-subsession-autodiscovery-design.md`
**Contract:** `docs/RULES-TestRig-Contract.md`

## Global Constraints

- Plugin targets **.NET Framework 4.8**. Dashboard/orchestrator JS is ES6+ on Node (real Node, not Jint).
- WebSocket server is **Fleck**, bound via existing `DashboardBridge`. Do NOT use `HttpListener`.
- `run.js` relies on Node's **built-in `WebSocket`** (Node 22+; dev machine has v25.6.1).
- Subsession source of truth is `_irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID` (`0`/unknown ⇒ `null` on the wire).
- `session_hello` fields are exactly: `type`, `sub_session_id` (int|null), `sim_mode` (string|null), `plugin_mode` (string). Do NOT add `sub_session_id` to `replay_state_tick`.
- Logging rules: wrap meaningful `catch` blocks with `SentrySdk.CaptureException` where the existing code does; no new logs inside the 60 Hz `DataUpdate` hot path except the once-per-change hello broadcast.
- Full deploy gate (`deploy.ps1`): build 0 errors, `dotnet test` green, `tests/*.ps1` green. Retry-once-then-stop on failure.

---

### Task 1: `resolveSubsession` + optional `--subsession` (orchestrator pure logic)

**Files:**
- Modify: `scripts/test-rig/run.js` (`parseArgs` validation; add `resolveSubsession`; extend `module.exports`)
- Test: `scripts/test-rig/run.test.js`

**Interfaces:**
- Produces: `resolveSubsession({ flagValue, helloValue }) → { ok: true, subsession: number, source: 'hello'|'flag' } | { ok: false, error: 'no_replay_loaded' } | { ok: false, error: 'subsession_mismatch', flag: number, loaded: number }`
- Produces: `parseArgs(argv).subsession` is now `number | null` (null when `--subsession` omitted); a supplied-but-invalid value still throws `invalid:--subsession`.

- [ ] **Step 1: Write the failing tests**

Replace the existing `parseArgs: rejects missing --subsession` test (lines 30-33 of `run.test.js`) with the optional-flag test, update the non-numeric test's expected message, and add a new `resolveSubsession` block. Add `resolveSubsession` to the require destructure at the top of the file.

In the require block at top of `run.test.js`, add `resolveSubsession`:

```js
const {
  parseArgs,
  evaluateAssertions,
  indexFilePath,
  SCENARIOS,
  aggregateByImpactClass,
  validateSmokeIndex,
  buildSmokeReport,
  resolveSubsession,
} = require('./run.js');
```

Replace the test at lines 30-33 with:

```js
test('parseArgs: allows missing --subsession (auto-detect)', () => {
  const r = parseArgs(['--scenario', 'sweep']);
  assert.equal(r.subsession, null);
  assert.equal(r.scenario, 'sweep');
});
```

Update the non-numeric test (lines 35-38) expected message:

```js
test('parseArgs: rejects non-numeric subsession when supplied', () => {
  assert.throws(() => parseArgs(['--subsession', 'abc', '--scenario', 'sweep']),
    /invalid:--subsession/);
});
```

Append a new section after the `parseArgs` tests (before `// ── evaluateAssertions`):

```js
// ── resolveSubsession ───────────────────────────────────────────────────────

test('resolveSubsession: flag omitted, hello present → use hello', () => {
  const r = resolveSubsession({ flagValue: null, helloValue: 12345678 });
  assert.deepEqual(r, { ok: true, subsession: 12345678, source: 'hello' });
});

test('resolveSubsession: flag matches hello → proceed', () => {
  const r = resolveSubsession({ flagValue: 12345678, helloValue: 12345678 });
  assert.deepEqual(r, { ok: true, subsession: 12345678, source: 'flag' });
});

test('resolveSubsession: flag mismatches hello → abort', () => {
  const r = resolveSubsession({ flagValue: 999, helloValue: 12345678 });
  assert.equal(r.ok, false);
  assert.equal(r.error, 'subsession_mismatch');
  assert.equal(r.flag, 999);
  assert.equal(r.loaded, 12345678);
});

test('resolveSubsession: no hello → no_replay_loaded', () => {
  assert.deepEqual(resolveSubsession({ flagValue: null, helloValue: null }),
    { ok: false, error: 'no_replay_loaded' });
  assert.deepEqual(resolveSubsession({ flagValue: 12345678, helloValue: 0 }),
    { ok: false, error: 'no_replay_loaded' });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test scripts/test-rig/run.test.js`
Expected: FAIL — `resolveSubsession` is `undefined` (TypeError / not-a-function), and the new `parseArgs` test fails because the missing flag still throws.

- [ ] **Step 3: Make `--subsession` optional in `parseArgs`**

In `scripts/test-rig/run.js`, replace the validation block (currently lines 92-94):

```js
  if (!Number.isFinite(args.subsession) || args.subsession <= 0) {
    throw new Error('missing_or_invalid:--subsession');
  }
```

with:

```js
  // --subsession is optional: omitted → auto-detect from session_hello.
  // Supplied → must be a positive int (becomes a mismatch guardrail).
  if (args.subsession !== null &&
      (!Number.isFinite(args.subsession) || args.subsession <= 0)) {
    throw new Error('invalid:--subsession');
  }
```

- [ ] **Step 4: Add `resolveSubsession`**

In `scripts/test-rig/run.js`, add this function just above the `// Main` section banner (before `async function main`):

```js
// ────────────────────────────────────────────────────────────────────────────
//  Subsession resolution (pure — exported for tests)
// ────────────────────────────────────────────────────────────────────────────

// flagValue: number|null (from --subsession), helloValue: number|null (from session_hello).
function resolveSubsession({ flagValue, helloValue }) {
  const hello = Number.isFinite(helloValue) && helloValue > 0 ? helloValue : null;
  const flag  = Number.isFinite(flagValue)  && flagValue  > 0 ? flagValue  : null;
  if (hello === null) return { ok: false, error: 'no_replay_loaded' };
  if (flag === null)  return { ok: true, subsession: hello, source: 'hello' };
  if (flag === hello) return { ok: true, subsession: hello, source: 'flag' };
  return { ok: false, error: 'subsession_mismatch', flag, loaded: hello };
}
```

Add `resolveSubsession` to `module.exports` (the object at the bottom of the file), e.g. after `indexFilePath,`:

```js
  indexFilePath,
  resolveSubsession,
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `node --test scripts/test-rig/run.test.js`
Expected: PASS — all tests green (count increases by the 4 new `resolveSubsession` tests).

- [ ] **Step 6: Commit**

```bash
git add scripts/test-rig/run.js scripts/test-rig/run.test.js
git commit -m "feat(test-rig): resolveSubsession + optional --subsession"
```

---

### Task 2: Wire `session_hello` into the orchestrator runtime

**Files:**
- Modify: `scripts/test-rig/run.js` (`WsClient` constructor + message handler; `main()` resolution step)

**Interfaces:**
- Consumes: `resolveSubsession` (Task 1), `ws.waitFor(predicate, opts)` and `ws.lastHello` (added here).
- Produces: after this task, `main()` sets `args.subsession` to the resolved id before anchoring, or exits non-zero with `subsession_mismatch` / `no_replay_loaded`.

This task is runtime glue against a live WS; the pure resolution it depends on is already unit-tested in Task 1. Verification is the existing unit suite still passing plus `--help` smoke.

- [ ] **Step 1: Capture `session_hello` in `WsClient`**

In `scripts/test-rig/run.js`, in the `WsClient` constructor, add a `lastHello` field next to `lastTick` (currently around line 239):

```js
    this.lastTick   = null;     // last replay_state_tick observed
    this.lastHello  = null;     // last session_hello observed
```

In the `WsClient` `message` event handler, after the `replay_state_tick` capture line (currently `if (parsed.type === 'replay_state_tick') this.lastTick = parsed;`), add:

```js
        if (parsed.type === 'session_hello') this.lastHello = parsed;
```

- [ ] **Step 2: Add the resolution step in `main()`**

In `scripts/test-rig/run.js`, in `main()`, immediately after the anchor-precondition connect and BEFORE the `phase = 'anchor'` block (i.e. right after `await ws.connect({ timeoutMs: 30000 });`), insert:

```js
    phase = 'session_hello';
    const helloIsReady = m =>
      m && m.type === 'session_hello' &&
      Number.isFinite(m.sub_session_id) && m.sub_session_id > 0;
    let helloSubId = null;
    if (helloIsReady(ws.lastHello)) {
      helloSubId = ws.lastHello.sub_session_id;
    } else {
      try {
        const hello = await ws.waitFor(helloIsReady,
          { timeoutMs: ANCHOR_TIMEOUT_MS, label: 'session_hello' });
        helloSubId = hello.sub_session_id;
      } catch { helloSubId = null; }
    }

    const resolved = resolveSubsession({ flagValue: args.subsession, helloValue: helloSubId });
    if (!resolved.ok) {
      const message = resolved.error === 'subsession_mismatch'
        ? `subsession_mismatch: --subsession=${resolved.flag} but loaded replay=${resolved.loaded}`
        : 'no_replay_loaded: open iRacing and load a replay first';
      throw new Error(message);
    }
    args.subsession = resolved.subsession;
    log(`[run] subsession=${args.subsession} (source=${resolved.source})`, artifacts);
```

(The existing `throw` is already caught by `main()`'s `try/catch`, which logs `test_rig_run_failed`, writes `run.json`, and returns exit code 1 — no extra wiring needed.)

- [ ] **Step 3: Verify unit suite still passes**

Run: `node --test scripts/test-rig/run.test.js`
Expected: PASS — no regressions (this task adds no new unit tests; the pure logic is covered by Task 1).

- [ ] **Step 4: Smoke the CLI parses**

Run: `node scripts/test-rig/run.js --help`
Expected: usage text prints, exit 0 (confirms no syntax error introduced).

- [ ] **Step 5: Commit**

```bash
git add scripts/test-rig/run.js
git commit -m "feat(test-rig): auto-resolve subsession from session_hello in run.js"
```

---

### Task 3: `SessionHello.BuildJson` (plugin pure payload builder)

**Files:**
- Create: `src/SimSteward.Plugin/SessionHello.cs`
- Test: `src/SimSteward.Plugin.Tests/SessionHelloTests.cs`

**Interfaces:**
- Produces: `public static string SimSteward.Plugin.SessionHello.BuildJson(int? subSessionId, string simMode, string pluginMode)` — returns the `session_hello` JSON string; `sub_session_id` is `null` when `subSessionId` is null or `<= 0`; `sim_mode` is `null` when empty; `plugin_mode` defaults to `"Unknown"` when empty.

- [ ] **Step 1: Write the failing test**

Create `src/SimSteward.Plugin.Tests/SessionHelloTests.cs`:

```csharp
using Newtonsoft.Json.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SessionHelloTests
    {
        [Fact]
        public void BuildJson_WithSession_EmitsNumericSubId()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(12345678, "replay", "Replay"));
            Assert.Equal("session_hello", (string)jo["type"]);
            Assert.Equal(12345678, (int)jo["sub_session_id"]);
            Assert.Equal("replay", (string)jo["sim_mode"]);
            Assert.Equal("Replay", (string)jo["plugin_mode"]);
        }

        [Fact]
        public void BuildJson_ZeroSubId_EmitsNullSubIdAndNullSimMode()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(0, "", ""));
            Assert.Equal(JTokenType.Null, jo["sub_session_id"].Type);
            Assert.Equal(JTokenType.Null, jo["sim_mode"].Type);
            Assert.Equal("Unknown", (string)jo["plugin_mode"]);
        }

        [Fact]
        public void BuildJson_NullSubId_EmitsNull()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(null, "replay", "Replay"));
            Assert.Equal(JTokenType.Null, jo["sub_session_id"].Type);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter FullyQualifiedName~SessionHelloTests`
Expected: FAIL to compile — `SessionHello` does not exist.

- [ ] **Step 3: Create the implementation**

Create `src/SimSteward.Plugin/SessionHello.cs`:

```csharp
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Builds the <c>session_hello</c> WS payload for test-rig subsession
    /// auto-discovery. See docs/RULES-TestRig-Contract.md and
    /// docs/superpowers/specs/2026-06-21-test-rig-subsession-autodiscovery-design.md.
    /// </summary>
    public static class SessionHello
    {
        public static string BuildJson(int? subSessionId, string simMode, string pluginMode)
        {
            int? sub = (subSessionId.HasValue && subSessionId.Value > 0) ? subSessionId : null;
            return JsonConvert.SerializeObject(new
            {
                type           = "session_hello",
                sub_session_id = sub,
                sim_mode       = string.IsNullOrEmpty(simMode) ? null : simMode,
                plugin_mode    = string.IsNullOrEmpty(pluginMode) ? "Unknown" : pluginMode,
            });
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter FullyQualifiedName~SessionHelloTests`
Expected: PASS — 3 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/SessionHello.cs src/SimSteward.Plugin.Tests/SessionHelloTests.cs
git commit -m "feat(plugin): SessionHello.BuildJson payload builder + tests"
```

---

### Task 4: `DashboardBridge` — send hello on connect + `BroadcastHello`

**Files:**
- Modify: `src/SimSteward.Plugin/DashboardBridge.cs`

**Interfaces:**
- Consumes: nothing new (callback supplied by Task 5).
- Produces: new constructor parameter `Func<string> getHelloForNewClient = null` (trailing, optional — preserves existing positional call sites); new public method `void BroadcastHello(string json)`; on connect, each client receives the hello after state + log-tail.

- [ ] **Step 1: Add the constructor parameter + field**

In `src/SimSteward.Plugin/DashboardBridge.cs`, add a field next to the other callback fields (after `private readonly Func<string> _getLogTailForNewClient;` around line 17):

```csharp
        private readonly Func<string> _getHelloForNewClient;
```

Add a trailing optional parameter to the constructor signature (after `Action onLastClientDisconnected = null`):

```csharp
            Action onLastClientDisconnected = null,
            Func<string> getHelloForNewClient = null)
```

In the constructor body, assign it (next to `_getLogTailForNewClient = ...`):

```csharp
            _getHelloForNewClient = getHelloForNewClient ?? (() => null);
```

- [ ] **Step 2: Send the hello in `OnOpen`**

In `DashboardBridge.Start`, inside `socket.OnOpen`, after the existing log-tail `try { ... }` block (the one ending around line 92, just before the closing `};` of `OnOpen`), add:

```csharp
                        try
                        {
                            var helloJson = _getHelloForNewClient?.Invoke();
                            if (!string.IsNullOrEmpty(helloJson))
                                socket.Send(helloJson);
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex);
                            _logger?.Warn($"DashboardBridge: getHelloForNewClient failed: {ex.Message}");
                        }
```

- [ ] **Step 3: Add `BroadcastHello`**

In `src/SimSteward.Plugin/DashboardBridge.cs`, add this method after `BroadcastState` (around line 178):

```csharp
        public void BroadcastHello(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            List<IWebSocketConnection> snapshot;
            lock (_clientLock)
            {
                snapshot = new List<IWebSocketConnection>(_clients);
            }
            foreach (var client in snapshot)
            {
                try { client.Send(json); }
                catch (Exception ex) { _onSendError?.Invoke(ex, "hello"); }
            }
        }
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/SimSteward.Plugin/SimSteward.Plugin.csproj`
Expected: Build succeeded, 0 errors. (Existing call site still compiles — the new parameter is optional.)

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/DashboardBridge.cs
git commit -m "feat(plugin): DashboardBridge sends session_hello on connect + BroadcastHello"
```

---

### Task 5: `SimStewardPlugin` — supply hello callback + broadcast on subsession change

**Files:**
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.cs` (bridge construction ~line 1576; `DataUpdate` broadcast region ~line 1811; add a field + a helper method)

**Interfaces:**
- Consumes: `SessionHello.BuildJson` (Task 3), `DashboardBridge` ctor param + `BroadcastHello` (Task 4).
- Produces: clients receive a fresh `session_hello` on connect and whenever `SubSessionID` changes (including session load/unload). One broadcast per change — never per-tick.

- [ ] **Step 1: Add the change-tracking field**

In `src/SimSteward.Plugin/SimStewardPlugin.cs`, near the other test-rig/session fields (e.g. just after `private volatile string _logCtxSubsession = SessionLogging.NotInSession;` at line 86), add:

```csharp
        private int _lastHelloSubId = int.MinValue;  // forces a hello broadcast on first DataUpdate
```

- [ ] **Step 2: Add the hello-builder helper**

In `src/SimSteward.Plugin/SimStewardPlugin.cs`, add this private method (place it near `GetStateForNewClient` / `GetLogTailForNewClient`):

```csharp
        private string GetSessionHelloForNewClient()
        {
            int sub = 0;
            string simMode = "";
            try
            {
                sub = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                simMode = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            }
            catch { }
            return SessionHello.BuildJson(sub, simMode, _pluginMode);
        }
```

- [ ] **Step 3: Pass the callback into the bridge**

In `src/SimSteward.Plugin/SimStewardPlugin.cs`, in the `_bridge = new DashboardBridge(...)` call (starts line 1576), add the new named argument to the end of the argument list — change the `onLastClientDisconnected` lambda's closing to include it:

```csharp
                onLastClientDisconnected: () =>
                {
                    _replayIndexCancelRequested = true;
                    _replayIndexRecordModeEnabled = false;
                },
                getHelloForNewClient: GetSessionHelloForNewClient);
```

- [ ] **Step 4: Broadcast on subsession change**

In `src/SimSteward.Plugin/SimStewardPlugin.cs`, in `DataUpdate`, just after `if (_bridge == null) return;` (line 1811) and before the `_broadcastNoClientsPending` block, add:

```csharp
            int helloSubId = 0;
            try { helloSubId = _irsdk?.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0; } catch { }
            if (helloSubId != _lastHelloSubId)
            {
                _lastHelloSubId = helloSubId;
                try { _bridge.BroadcastHello(GetSessionHelloForNewClient()); }
                catch (Exception ex) { try { SentrySdk.CaptureException(ex); } catch { } }
            }
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build src/SimSteward.Plugin/SimSteward.Plugin.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Run the full C# test suite**

Run: `dotnet test`
Expected: PASS — all tests green (including `SessionHelloTests`).

- [ ] **Step 7: Commit**

```bash
git add src/SimSteward.Plugin/SimStewardPlugin.cs
git commit -m "feat(plugin): broadcast session_hello on connect + subsession change"
```

---

### Task 6: Document `session_hello` in the WS contract

**Files:**
- Modify: `docs/RULES-TestRig-Contract.md`

- [ ] **Step 1: Add the `session_hello` subsection**

In `docs/RULES-TestRig-Contract.md`, under the `## WS pushes (plugin → dashboard)` heading, add a new subsection immediately after the intro paragraph (before `### replay_state_tick`):

````markdown
### `session_hello`

Sent to each client right after it connects, and re-broadcast to all clients whenever the loaded subsession changes (so a client that connected before the replay finished loading still learns the id). One broadcast per change — never per tick. Built by `SessionHello.BuildJson`.

```json
{
  "type": "session_hello",
  "sub_session_id": 12345678,
  "sim_mode": "replay",
  "plugin_mode": "Replay"
}
```

- `sub_session_id` is `null` when no session is loaded (`SubSessionID == 0` / IRSDK absent).
- `sim_mode` is `null` when unknown; `plugin_mode` is `"Replay"` or `"Unknown"`.
- The test rig (`scripts/test-rig/run.js`) reads this to auto-detect `--subsession`: flag omitted → use this id; flag supplied and mismatched → abort; no non-null id within 30 s → fail (`no_replay_loaded`). `sub_session_id` is intentionally **not** on `replay_state_tick`.
````

- [ ] **Step 2: Commit**

```bash
git add docs/RULES-TestRig-Contract.md
git commit -m "docs(contract): document session_hello WS message"
```

---

### Task 7: End-to-end verification (the original goal — run the harness)

**Files:** none (verification only)

**Precondition:** iRacing open with a replay loaded and IRSDK ready; SimHub running the freshly-deployed plugin.

- [ ] **Step 1: Deploy the plugin through the full gate**

Run: `pwsh -NoProfile -File deploy.ps1`
Expected: build 0 errors, `dotnet test` green, `tests/*.ps1` green, SimHub auto-restarted. (Retry once on failure, then hard stop.)

- [ ] **Step 2: Run the harness WITHOUT `--subsession` (auto-detect)**

Run: `node scripts/test-rig/run.js --scenario smoke`
Expected: console shows `[run] subsession=<id> (source=hello)`; the scenario runs; `run.json` written under `logs/test-rig/<UTC>/` with the correct subsession and `ok: true` (assuming the replay is healthy).

- [ ] **Step 3: Verify the mismatch guardrail**

Run: `node scripts/test-rig/run.js --scenario smoke --subsession 1`
Expected: exits non-zero in phase `session_hello` with `subsession_mismatch: --subsession=1 but loaded replay=<id>`; `run.json` records the failure. (`1` will not match the loaded replay.)

- [ ] **Step 4: (Optional) Verify fail-fast with no replay**

With iRacing closed / no replay loaded, run: `node scripts/test-rig/run.js --scenario smoke`
Expected: after ~30 s, exits non-zero with `no_replay_loaded: open iRacing and load a replay first`.

- [ ] **Step 5: Final commit (if any cleanup) / done**

No code changes expected here. If verification surfaced a fix, commit it referencing the task it belongs to.

---

## Self-Review

**Spec coverage:**
- `session_hello` message (shape, on-connect, on-change, null semantics) → Tasks 3, 4, 5, 6. ✓
- Carrier is a dedicated message, not on `replay_state_tick` → enforced in Tasks 5/6 (no tick change). ✓
- `--subsession` optional + auto-detect → Tasks 1, 2. ✓
- Abort on mismatch / fail-fast on no replay (30 s) → `resolveSubsession` (Task 1) + `main()` wait (Task 2) + verified (Task 7). ✓
- Contract doc update → Task 6. ✓
- Tests for `resolveSubsession` (4 branches) + `parseArgs` optional → Task 1. ✓
- Out-of-scope items (no dashboard HTML, no extra fields, no new flags) → respected throughout. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code and exact commands. ✓

**Type consistency:** `resolveSubsession` return shape is identical in Task 1's tests, definition, and Task 2's consumer (`{ ok, subsession, source }` / `{ ok, error, flag, loaded }`). `SessionHello.BuildJson(int?, string, string)` signature matches its test (Task 3) and its single caller `GetSessionHelloForNewClient` (Task 5). `BroadcastHello(string)` / `getHelloForNewClient` names match between Tasks 4 and 5. ✓
