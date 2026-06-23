# Test-Rig Sweep Harness

A reusable, parameterized harness for running and **autonomously verifying** replay incident-index sweeps. It replaces human attention with multiple independent on-wire signals: the monitor's exit code is the verdict (`0` = verified full-coverage completion; non-zero = stall / speed-loss / cap-hit / truncated coverage / timeout), the captured `logEvents` are the audit trail, and coverage + speed are asserted rather than assumed — so a run can be trusted when nobody is watching the sim.

## The unattended recipe

```sh
# 1. (optional) deploy the build under test
#    production 16x:   ./deploy.ps1
#    experiment speed: edit DefaultFastForwardPlaySpeed; SIMSTEWARD_SKIP_TESTS=1 ./deploy.ps1

# 2. wait for IRSDK/replay to reconnect after the restart
node scripts/test-rig/harness/ready.mjs

# 3. start the verification engine FIRST (holds the WS open, captures everything)
node scripts/test-rig/harness/monitor.mjs  --sub 85380877 --expect-speed 16 &
node scripts/test-rig/harness/capture.mjs  --label 16x &

# 4. trigger (pick one)
node scripts/test-rig/harness/drive-dashboard.mjs --expect-speed 16   # full-stack via dashboard
#   or
node scripts/test-rig/run.js --subsession 85380877 --scenario sweep    # lean via WS

# 5. monitor exits 0 on a verified full-coverage completion (prints per-session totals + audit),
#    non-zero on stall / speed-loss / cap-hit / truncated coverage. capture.mjs leaves the
#    full telemetry JSONL under logs/test-rig/.

# 6. compare preserved runs
node scripts/test-rig/harness/scorecard.mjs index_1x.json index_2x.json index_16x.json --labels 1x,2x,16x
```

Preserve each run's index between sweeps (`monitor.mjs --preserve <path>`, or copy
`%LOCALAPPDATA%\SimSteward\replay-incident-index\<sub>.json` aside) so the scorecard can diff them.

## Scripts

| Script | Role |
|---|---|
| `ready.mjs` | Readiness probe — exit 0 once a `replay_state_tick` arrives, exit 1 on timeout. |
| `monitor.mjs` | Verification engine — holds the WS open, logs the 4-signal heartbeat, raises on stall/speed-loss, detects completion, parses + summarizes the index. **Exit code = verdict.** |
| `capture.mjs` | Telemetry recorder — every inbound WS message to JSONL incl. `logEvents`; flags cap-hit / speed-loss / completion audit. |
| `drive-dashboard.mjs` | Playwright driver — full-stack trigger via the real dashboard UI (needs a local Playwright install). |
| `scorecard.mjs` | Offline cross-run diff — per-index totals + fingerprint overlap + fallback `(carIdx, sessionNum, ~1 s)` match rate. |

Each `.mjs` is an ES module using Node 22+'s built-in global `WebSocket` (no npm deps;
`drive-dashboard.mjs` is the sole exception — it needs Playwright). Run any with `--help`.

## Full manual

See [`../../../docs/TEST-RIG-HARNESS.md`](../../../docs/TEST-RIG-HARNESS.md) for the operating
manual: what "verified" means, the four signals, the critical gotchas (hold-the-connection,
the 90k sample cap, IRSDK settle, inverted frame numbering), and the two ways to trigger a sweep.
