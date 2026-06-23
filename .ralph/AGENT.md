# AGENT.md — commands, recovery, and hard-won gotchas (keep current)

## The loop
- One iteration (HITL): `bash .ralph/ralph-once.sh [increment|plan|test-harden]`
- N capped iterations: `bash .ralph/ralph-loop.sh 15 [increment]`  (Sonnet default; `RALPH_MODEL=opus` for rare hard tasks)
- The gate (run before every commit): `bash .ralph/gate.sh`  → exit 0 == green.
- Branch: ALL work on `ralph/auto`. Never `main`. Per-iteration commit = rollback point.

## Hard rules
- The autonomous loop is **OFFLINE ONLY**. Never run `deploy.ps1`, never start/stop SimHub, never open a live sweep or the live WS, never touch iRacing.
- `SimSteward.IncidentEngine` is **PURE**: no IRSDKSharper / SimHub / Fleck references. Ever.
- No placeholders, no flaky tests, gate green (not skipped) to count an increment.

## iRacing safety + recovery (we CANNOT restart iRacing from the loop)
- The open replay is `subses85380877` (Winton National). A backup is at `tests/fixtures/replays/subses85380877.rpy` (1.37 GB, git-ignored).
- If SimHub/iRacing locks up or the replay closes: re-launch from Windows — `Start-Process "C:\Users\winth\OneDrive\Documents\iRacing\replay\subses85380877.rpy"` (file association reloads the replay). Do NOT kill iRacing.

## Recover the repo if a run goes off the rails
- `git reset --hard <last-good-commit>` (every iteration commits, so rollback is cheap).
- `git status` should always show `main` untouched and `tests/fixtures/replays/` + `.ralph/state/` git-ignored.

## Gotchas learned from the sweep study (build these correctly in the engine)
- **Inverted frames:** the *plugin's* `replayFrameNum`/`replayFrameNumEnd` are inverted vs the SDK — judge "start" by `session_time` (~100s), not raw frame. The engine works on `sessionTimeMs`, sidestepping this.
- **90k sample cap:** the plugin's FF build hard-stops at 90,000 samples (truncates a 1× sweep at ~65%). The engine's index build must be coverage-explicit (no silent truncation).
- **UTF-8 BOM:** the plugin writes index JSON with a BOM — strip a leading `﻿` before `JSON.parse`.
- **Fingerprint instability (the big one):** v1 fingerprint hashes raw `sessionTimeMs`, so the same incident gets a different id at 1× vs 16× (16×∩1× overlap = 13/163). The engine's identity MUST be sampling-rate-stable (quantize / tolerance-window). This is why Track 1 precedes index + jump.
- **Detection fidelity vs speed:** 16× under-detects ~5.5% vs 1× (race off-tracks). Study baselines for `subses85380877`: 1×=163, 2×=162, 16×=154 (P5/Q44-45/R105-113).

## Usage (Claude Pro — 5h-rolling + weekly)
- Sonnet workhorse; Opus only for spec/hard-debug/review on confirmed headroom.
- Bounded batches (~10–15); checkpoint cumulative tokens + wall-clock between batches; defer slow 1×/16× validation (pause the loop, zero token burn, during sweeps).
