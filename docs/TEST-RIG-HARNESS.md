# Test-Rig Sweep Harness — Operating Manual

A reusable harness for running and **autonomously verifying** replay incident-index sweeps. Its purpose is to let development and testing run **without a human watching the sim** — the harness replaces human attention with multiple independent on-wire signals and raises an exception the moment reality diverges from expectation.

Companion docs: [RULES-TestRig-Contract.md](RULES-TestRig-Contract.md) (the WS contract) and [RESEARCH-sweep-speed-detection-fidelity.md](RESEARCH-sweep-speed-detection-fidelity.md) (the study this harness was built for).

> **Why this exists.** Earlier sweep tests were unverifiable ("is it even running?") and one nearly produced a wrong conclusion because a silent sample-cap truncated the sweep. The harness exists so that *every* "it's running / it finished / it found N" is backed by evidence, and so a run can be trusted when nobody is looking.

---

## What "verified" means here (the core idea)

A single signal is never trusted. The plugin's own progress counter can advance while the sim is stuck; a dashboard can say "building" while nothing happens. The harness cross-checks **four independent signals**, sampled over time:

| # | Signal | Source | What it proves |
|---|---|---|---|
| 1 | sweep **frame** advancing, at **speed × 60 fps** | `replay_sweep_progress_tick.frame` | the replay is genuinely moving (not a spinning counter) |
| 2 | **samples** accumulating | `replay_sweep_progress_tick.samples_so_far` | the detector is processing, not idling |
| 3 | **`telemetry_play_speed`** | iRacing SDK `ReplayPlaySpeed`, read back | the *sim itself* reports the replay playing at the requested speed |
| 4 | **requested == actual** speed | progress tick | no "speed lost" mid-sweep |

Frame advancing at *exactly* `speed × 60` fps is the decisive tell — a fake/stuck counter won't track wall-clock. The harness **raises** if frame stalls > 90 s or signals 3/4 disagree.

**Completion is also verified, not assumed:** a run's numbers are trusted only when `max_frame ≈ frame_end` (≥ 99.9%) **and** zero `replay_index_ff_sample_cap_hit` events. (A truncated sweep silently drops the tail sessions — see the cap note below.)

---

## Components

| Component | Role | Reusable script |
|---|---|---|
| **Readiness probe** | wait for IRSDK/replay after a SimHub restart (it takes 10–30 s to reconnect) | `scripts/test-rig/harness/ready.mjs` |
| **Sweep monitor** | connect, **hold the WS open**, log the 4-signal heartbeat, raise on stall/speed-loss, detect completion, parse + summarize the index | `scripts/test-rig/harness/monitor.mjs` |
| **Telemetry capture** | record **every** inbound WS message (full progress trajectory + `logEvents`) to JSONL — this is what surfaces the cap-hit and the completion audit | `scripts/test-rig/harness/capture.mjs` |
| **Dashboard driver** | drive the real dashboard UI (reset → trigger) via Playwright — exercises the production control path | `scripts/test-rig/harness/drive-dashboard.mjs` |
| **Scorecard** | diff preserved indexes across runs by fingerprint and by `(carIdx, sessionNum, ~1 s time)` | `scripts/test-rig/harness/scorecard.mjs` |

> These were proven ad-hoc during the 2026-06-22 study and then consolidated here. The originals lived in scratch; this manual is the durable form.

---

## Two ways to trigger a sweep

The verification engine (monitor + capture) is identical for both; only the trigger differs.

1. **Dashboard-driven (full-stack, exercises the real UI):** the driver clicks `tr-jump-start` + `tr-play-pause` (reset to frame 0, paused) on `test-rig.html`, then `ri-btn-start` on `index.html`'s Replay Index tab (`index.html#replayindex`; the standalone `replay-incident-index.html` page was merged into this tab). Use this when validating the dashboard path itself, or when a human may be watching.
2. **WS-driven (lean, best for unattended/CI):** `scripts/test-rig/run.js` sends `replay_jump:start` → `replay_pause` → `replay_incident_index_build:start` directly. Fewer moving parts (no browser), so more robust for unattended loops. **Note the verb:** `replay_incident_index_build` requires `arg:"start"` (also `cancel`/`finalize`); an empty arg returns `bad_arg`.

Either way, **the monitor must already be connected before the trigger fires** (see "Hold the connection" below).

---

## Critical gotchas (each one cost a real failure)

1. **Hold the WebSocket open for the whole sweep.** The plugin cancels an in-progress build when the *last* dashboard client disconnects (`onLastClientDisconnected → _replayIndexCancelRequested`). Start the monitor (which stays connected) **first**; then a Playwright driver may open and close its tab freely.
2. **The 90,000-sample cap.** `SimStewardPlugin.ReplayIncidentIndexBuild.cs:849` force-stops the build at 90k telemetry samples. Fine at 16× (covers ~6.7 h of replay) and 2× (~69k), but at **1× it truncates at ~65%** of a 138k-frame replay. For a full-density 1× sweep, temporarily raise the cap (e.g. → 200k) and **revert after**. Always check for the cap-hit event before trusting a slow-sweep result.
3. **IRSDK settle after deploy.** `deploy.ps1` restarts SimHub; IRSDK takes 10–30 s to reconnect. Run the readiness probe before triggering, or the dashboard shows "Waiting…" and clicks no-op.
4. **Inverted frame numbering.** The plugin's `frame`/`frame_end` are inverted vs the SDK (documented, deliberately not renamed). Do **not** judge "at the start" by a raw frame number — judge by `session_time` dropping to the session-start value (~100 s on the study replay) after a jump-to-start.
5. **Control the detector version.** When comparing sweeps, deploy the *same* plugin build for every run; a different commit changes detection, not just sampling.
6. **Experiment builds skip the gate.** Changing `DefaultFastForwardPlaySpeed` off 16 fails the `DefaultFastForwardPlaySpeed_Is16` unit test; deploy those with `SIMSTEWARD_SKIP_TESTS=1`, and restore + clean-deploy (full gate) when done.
7. **Preserve the index before the next run.** Each build overwrites `%LOCALAPPDATA%\SimSteward\replay-incident-index\<sub>.json`. Copy it aside per run for the scorecard diff.
8. **Playwright tools register at session start.** The browser MCP tools only appear after a Claude Code session restart; the standalone driver script avoids this by using a local Playwright install.

---

## Running a verified sweep (the unattended recipe)

```
# 1. (optional) deploy the build under test
#    production 16x:   ./deploy.ps1
#    experiment speed: edit DefaultFastForwardPlaySpeed; SIMSTEWARD_SKIP_TESTS=1 ./deploy.ps1

# 2. wait for IRSDK/replay
node scripts/test-rig/harness/ready.mjs

# 3. start the verification engine FIRST (holds the connection, captures everything)
node scripts/test-rig/harness/monitor.mjs  --sub 85380877 --expect-speed 16 &
node scripts/test-rig/harness/capture.mjs  --sub 85380877 &

# 4. trigger (pick one)
node scripts/test-rig/harness/drive-dashboard.mjs           # full-stack via dashboard
#   or
node scripts/test-rig/run.js --scenario sweep               # lean via WS

# 5. monitor exits 0 on a verified full-coverage completion (prints per-session totals + audit),
#    non-zero on stall / speed-loss / cap-hit / truncated coverage — the signal an unattended
#    caller checks. capture.mjs leaves the full telemetry JSONL + the preserved index.

# 6. compare runs
node scripts/test-rig/harness/scorecard.mjs index_1x.json index_2x.json index_16x.json
```

**What makes this unattended-safe:** steps 3–5 need no human eyes. The monitor's exit code is the verdict; the captured `logEvents` are the audit trail; coverage and speed are asserted, not assumed. A scheduled/automated run can deploy → sweep → assert → report and only escalate to a human on a non-zero exit.

---

## Toward fully automated development & testing

This harness is the building block for unattended dev loops:

- **Regression gate:** after any detector change, auto-sweep a fixture replay at 2× and assert the per-session counts (and, once stable, the fingerprints) against a committed baseline. Fail the build on drift.
- **Speed/accuracy matrix:** sweep 1×/2×/16× and emit the scorecard automatically (the study, re-runnable on demand).
- **Cross-speed identity check:** once the [v2 fingerprint](RESEARCH-sweep-speed-detection-fidelity.md#prototype-fix) lands, assert fingerprint overlap across speeds ≥ a threshold — a direct regression test for the stability fix.

The only step still needing a human is **loading the replay in iRacing** (the deleted `load-replay.ps1` automated this previously; restoring an equivalent would close the last gap to fully hands-off runs).

---

## Artifacts a run produces

- `logs/test-rig/<UTC>/` — `run.json`, `events.jsonl`, `index.json`, `console.log` (from `run.js`), or the monitor/capture logs.
- Preserved per-run index copies (for the scorecard).
- Full per-tick telemetry JSONL (progress trajectory + `logEvents` incl. completion audit + any cap-hit/speed-loss).
