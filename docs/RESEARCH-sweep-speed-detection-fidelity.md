# Replay Incident-Index — Sweep-Speed Detection Fidelity Study

**Date:** 2026-06-22 · **Replay:** subsession `85380877` (Winton Motor Raceway — National, 3 sessions, 137,969 frames) · **Plugin build:** `1.0.1+69e2bf3` (main, post-merge detector) · **Status:** complete; production restored to 16×.

First end-to-end measurement of how the fast-forward **sweep speed** affects what the incident-index build detects. We swept the same replay on the same detector at **1×, 2×, and 16×**, with full-coverage verification, and diffed the results down to the incident-fingerprint level.

---

## TL;DR

1. **The default 16× sweep under-detects by ~5.5%** vs a full-density 1× sweep — **154 vs 163 incidents**, with **8 of the 9 missed in the Race** (the densest session). All missed are off-track (`track_surface`) events that 16× samples over.
2. **1× and 2× are effectively identical** (Race *exactly* 113 in both; totals 163 vs 162). 2× buys full-density accuracy at ~1/8 the wall-clock of 1×.
3. **STRUCTURAL ISSUE — incident fingerprints are not stable across sweep speeds.** The fingerprint hashes the raw sampled `sessionTimeMs`, which shifts with cadence, so the *same physical incident* gets a *different* fingerprint at each speed (16×∩1× overlap = **13/163**). This breaks cross-speed dedup and risks jump-to-incident misfires. → see [Finding 2](#finding-2-fingerprints-are-not-stable-across-sweep-speeds-structural) and the prototype fix.
4. **There is no external ground truth** for this replay: iRacing exposes no official incident count (`total_expected = 0`), so "truth" here is the densest full sweep (1×), not the sim's tally.

---

## The scorecard

Same detector, full coverage, dashboard-triggered, every run verified on the wire.

| Session | **1× (truth)** | 2× | 16× (production default) |
|---|---:|---:|---:|
| Practice (free) | 5 | 5 | 5 |
| Qualify (partial) | 45 | 44 | 44 |
| **Race (full)** | **113** | **113** | **105** |
| **Total** | **163** | **162** | **154** |
| Wall-clock (full sweep) | ~38 min | ~19 min | ~2.5 min |
| `detectionSource` | track_surface 160 / player_incident_count 3 | 161 / 1 | (track_surface dominant) |
| Build audit | detected 163, expected 0, drivers_over 27 | 162 / 0 / 27 | 154 / 0 / 27 |

---

## Finding 1 — 16× under-detects, mildly and where density is highest

- 16× misses **~9 incidents (5.5%)** vs the 1× source of truth; **8 are in the Race**, all `track_surface` (off-track) events. Practice and Qualify (sparse sessions) are stable across all speeds.
- The mechanism: detection density scales with sweep speed. At 16× the plugin's 60 Hz `DataUpdate` samples roughly 1 frame in 16 (~267 ms of session-time between samples), so brief off-track excursions that begin and end between two samples are stepped over. In the busy Race those brief excursions are common; in Practice/Qualify they are rare.
- **1× ≈ 2×**: Race is *exactly* 113 in both, total differs by 1 (a single Qualify incident). Detection saturates by 2× — going slower than 2× buys nothing here, only time.

**Implication:** the production 16× index is ~95% complete on this replay. If completeness in dense sessions matters, **2× is the sweet spot** (full-density accuracy, 1/8 the time of 1×, and well under the sample cap — see below).

---

## Finding 2 — Fingerprints are NOT stable across sweep speeds (STRUCTURAL)

This is the most important result and was not previously known.

### The defect

The incident fingerprint (`ReplayIncidentIndexFingerprint.cs`, TR-020 v1) is:

```
SHA-256( "v1|{subSessionId}|{carIdx}|{sessionTimeMs}|{detectionSource}|{points}" )
```

`sessionTimeMs` is the **raw session time at the sample where the incident was recorded**. Because the sweep samples at a cadence that depends on speed (~16 ms apart at 1×, ~267 ms at 16×), the *same physical incident* is recorded at a *different* `sessionTimeMs` at each speed — and therefore hashes to a **different fingerprint**.

### The measurement

Fingerprint-set overlap against the 1× source of truth (163 incidents):

| Build | fingerprints shared with 1× | extra (not in 1×) |
|---|---:|---:|
| 2× (162) | **92 / 163** | 70 |
| 16× (154) | **13 / 163** | 141 |

Matching the *same* incidents by `(carIdx, sessionNum, ~1 s sessionTime)` instead recovers **~134/154** for 16× — confirming they are largely the **same physical events, re-fingerprinted**, not genuinely different detections. The fingerprint, not the detection, is the unstable part.

### Why it matters

- **Cross-speed dedup is broken.** Re-indexing the same replay at a different speed produces an almost entirely new fingerprint set; the system cannot recognize "this is the same incident I already have."
- **Jump-to-incident misfire risk.** The production index is built at 16×, but live replay aggregation runs at 1× (normal playback). Jump-to-incident / misfire detection matches index fingerprints against live detection; with non-overlapping fingerprints, it leans entirely on the ±500 ms session-time tolerance, and any exact-fingerprint check will not match.
- **Any persistence keyed on fingerprint** (captures, annotations, cross-references) is fragile to a re-index at a different speed.

### Prototype fix

A v2 fingerprint that **quantizes `sessionTimeMs`** before hashing, so the same incident lands in one time bucket regardless of cadence:

```
SHA-256( "v2|{sub}|{carIdx}|{round(sessionTimeMs / Q) * Q}|{source}|{points}" )    // Q ≈ 500 ms
```

Implemented additively (v1 unchanged, not wired into the build) in `ReplayIncidentIndexFingerprint.cs` with tests in `ReplayIncidentIndexFingerprintV2Tests.cs`.

**Quantum selection / caveat:** `Q` must be ≥ the coarsest sample spacing (~267 ms at 16×) so a single incident merges across speeds; 500 ms gives margin. The tradeoff: too large risks merging two *distinct* incidents for the same car; too small fails to merge the same incident across speeds. **Fixed-grid quantization still splits two near-identical times that straddle a bucket boundary** — so the production-grade fix may instead need **tolerance-window matching** (match within ±Q at query time) or **event-onset time** (fingerprint the first frame the car went off-track, not the sample frame) rather than grid quantization. The prototype demonstrates the direction and documents this limitation in its boundary test.

---

## The 90,000-sample cap (and why 1× nearly fooled us)

`SimStewardPlugin.ReplayIncidentIndexBuild.cs:849` hard-stops the FF build at **90,000 telemetry samples**:

```csharp
if (!frameAtEnd && _replayIndexFfTelemetrySampleCount <= 90000) return;
... if (_replayIndexFfTelemetrySampleCount > 90000) { /* WARN replay_index_ff_sample_cap_hit, force completion */ }
```

- At 16× a full replay needs ~8.6k samples; at 2×, ~69k — **both under the cap.**
- At 1× the build samples ~1 per frame, so it hits 90k at frame ~90,000 = **65% of this replay** and **force-stops there**. The first 1× run reported **104** incidents (Race truncated to 54) — an artifact of the cap, **not** a real 1× result.
- The cap is **appropriate for production** (at 16× it covers ~6.7 h of replay). It only bites slow diagnostic sweeps. The real 1× source of truth required temporarily raising it to 200k (reverted after).

**This was caught only because we captured `logEvents`** and saw `replay_index_ff_sample_cap_hit`. Had we trusted the index count alone, "1× = 104" would have become a wrong conclusion (and falsely implied non-monotonic detection). See methodology below.

---

## No external ground truth

The build's completion audit reports `total_expected = 0`, `coverage_pct = null`, `drivers_over_detected = 27` at **every** speed. iRacing exposes **no official incident count** for this replay (consistent with the `replay_state_tick` YAML aggregates being `null` — no final session results). Therefore:

- "Truth" in this study = the **densest full sweep (1×)**, not the sim's official tally.
- `drivers_over_detected = 27` is purely an artifact of comparing against an expected of 0; it does **not** indicate 27 false positives.
- A future accuracy study against *official* counts needs a replay with finalized results loaded.

---

## Methodology — the conditions that made this testing succeed

The earlier ad-hoc attempts failed or produced unverifiable results; the run that worked combined several deliberate practices. Documented here because the harness, not just the numbers, is the reusable asset.

1. **Drive the replay through the real dashboard controls, not a back-channel.** Each sweep was started by clicking the actual dashboard buttons — `test-rig.html` `tr-jump-start` + `tr-play-pause` to reset to frame 0, then `replay-incident-index.html` `btn-start` to trigger the build — driven by a headless **Playwright** agent. This exercises the production control path (dashboard → WebSocket → plugin), which is what a user actually does. (`run.js`'s direct WS sends bypass the dashboard and were *not* used here.)

2. **Verify every claim with multiple independent signals — never one.** A persistent monitor logged a 4-signal heartbeat every 30 s:
   - sweep **frame** advancing (and at the rate implied by the speed: 120 fps at 2×, 60 fps at 1×, ~960 fps at 16×),
   - **samples** accumulating,
   - **`telemetry_play_speed`** read *back from the iRacing SDK* (`ReplayPlaySpeed`) — the sim's own report, not the plugin's request,
   - **requested == actual** speed (catches "speed lost").

   The frame advancing at *exactly* speed×60 fps is what distinguished a genuinely-running replay from a plugin counter spinning over a stuck sim. The monitor **raised an exception** on a >90 s frame stall or a speed mismatch.

3. **Hold the WebSocket open for the whole sweep.** The plugin cancels an in-progress build when the *last* dashboard client disconnects (`onLastClientDisconnected → _replayIndexCancelRequested`). The monitor stayed connected end-to-end so the build could not be cancelled by the Playwright agent closing its tab.

4. **Capture the full telemetry stream, including `logEvents`.** A second client recorded **every** inbound WS message to JSONL — the complete progress-tick trajectory *and* the structured log events. This is what surfaced the `replay_index_ff_sample_cap_hit` (the 1× truncation) and the `replay_index_build_completion_audit` (coverage stats). **Capturing logs, not just the final artifact, is what prevented a wrong conclusion.**

5. **Confirm full coverage explicitly.** Each run was checked for `max_frame ≈ frame_end` (≥ 99.9%) and **zero** `sample_cap_hit` events before its numbers were trusted. The truncated 1× (max frame 89,856 / 137,969) was rejected on this check.

6. **Control the detector version.** An early "16× = 154" baseline was on the *pre-merge* detector and was discarded; all three reported speeds were swept on the identical `69e2bf3` build so the only variable is sweep speed.

7. **Preserve per-speed artifacts for offline diff.** Each speed's `index.json` was copied aside (`scratchpad/index_{1xfull,2x,16x}_85380877.json`) before the next run overwrote `…/SimSteward/replay-incident-index/85380877.json`, enabling the fingerprint-level set diff after the fact.

8. **Tooling prerequisite.** The Playwright MCP browser tools register only at session start; a Claude Code session restart was required before the dashboard could be driven programmatically.

**Ethos: log and verify, no guessing.** Every "it's running / it's done / it found N" was backed by on-wire telemetry, and the one time the data contradicted an assumption (1× finding *fewer*, not more), the contradiction was traced to a concrete cause (the cap) rather than rationalized.

---

## Recommendations

1. **Fix fingerprint stability** (Finding 2) — decide between grid-quantization (prototype shipped) vs tolerance-window matching vs event-onset time. This is the highest-value follow-up; it affects dedup and jump-to-incident reliability today.
2. **Consider 2× (or denser) as the default sweep speed** if Race completeness matters — it recovers the ~8 missed Race incidents at 1/8 the cost of 1×, and stays well under the 90k cap.
3. **Make the 90k cap scale with replay length / speed**, or at least surface the truncation to the dashboard, so a slow sweep of a long replay does not silently produce a partial index.
4. **For an accuracy study against official counts**, re-run on a replay with finalized iRacing results (non-null `total_expected`).

---

## Reproduction

1. Deploy the desired sweep speed: set `ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed` (production 16); for a full 1× also raise the 90k cap in `SimStewardPlugin.ReplayIncidentIndexBuild.cs:849,853`. Deploy with `SIMSTEWARD_SKIP_TESTS=1` for non-16 builds (the `DefaultFastForwardPlaySpeed_Is16` test pins 16).
2. Load the replay; from the dashboard, reset to start (`tr-jump-start` + pause) and trigger `btn-start`.
3. Monitor on the WebSocket: assert `telemetry_play_speed == requested`, frame advancing, and on completion `max_frame ≈ frame_end` with no `sample_cap_hit`.
4. Preserve `%LOCALAPPDATA%\SimSteward\replay-incident-index\<sub>.json` before the next run; diff incident sets across speeds by fingerprint and by `(carIdx, sessionNum, ~1 s sessionTime)`.
