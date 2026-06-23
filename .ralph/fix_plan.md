# fix_plan — Incident Engine (greenfield), prioritized. One narrow task per loop.

Mark `[x]` when done + tested + committed. Split anything bigger than one iteration. Acceptance criterion on every line.
Source of truth = `docs/superpowers/specs/incident-engine/`. All items are OFFLINE-testable (no iRacing).

## Track 0 — Harden the gate + harness FIRST (trust before features)
- [ ] T0.1 Confirm `.ralph/gate.sh` runs GREEN end-to-end. *Accept: exit 0, no SimHub/iRacing touched.*
- [ ] T0.2 Run the gate ≥10× and remove any flake/non-determinism found (clock/order/env/path/network). *Accept: 10/10 green.*
- [ ] T0.3 Author `docs/superpowers/specs/incident-engine/00-overview.md` + `01-telemetry-sample-contract.md`. *Accept: specs define the engine boundary + the `TelemetrySample` DTO fields.*
- [ ] T0.4 Implement `TelemetrySample` DTO (pure, immutable) per spec 01, with tests. *Accept: round-trips its fields; deterministic test.*
- [ ] T0.5 Add a deterministic offline test layer for the test-rig harness logic — `scorecard.mjs` diff + `capture.mjs` BOM-strip — driven by fixtures (`scratchpad/index_*`, `logs/test-rig/telemetry_*.jsonl`). *Accept: node tests cover the verdict/diff paths.*

## Track 1 — Incident identity (prerequisite for index + jump reliability)
- [ ] T1.1 Spec `04-incident-identity.md` (quantized time + tolerance-window; why — cross-speed stability). *Accept: spec states the rule + the tolerance.*
- [ ] T1.2 Implement `IncidentFingerprint` in the engine (lift the v2/quantized prototype logic). *Accept: same incident at 1×/16× sample times → same id; distinct incidents differ; deterministic tests.*

## Track 2 — Detectors (one per loop; synthetic + golden fixtures)
- [ ] T2.1 Spec `02-detectors.md` + implement off-track detector (track-surface) with rumble-strip-material false-positive filter. *Accept: off-track on surface change; suppressed on rumble material; tested.*
- [ ] T2.2 car-contact detector. *Accept: rising-edge on contact signal; tested.*
- [ ] T2.3 fast-repair rising-edge detector. *Accept: emits once per increment of fastRepairsUsed; tested.*
- [ ] T2.4 black-flag + disqualify detectors (flag-bit rising edges). *Accept: one incident per rising edge; tested.*
- [ ] T2.5 player-incident-count delta detector. *Accept: emits on count delta; tested.*

## Track 3 — Index build + Jump engine
- [ ] T3.1 Spec `03-index-build.md` + implement index folder (sessions, impact class, per-car counts, incident rows). *Accept: builds a `ReplayIncidentIndex` from a sample stream; tested.*
- [ ] T3.2 Spec `05-jump-engine.md` + `NextIncident(index, currentSessionTimeMs, direction)`. *Accept: returns the correct next/prev incident or none; tested incl. boundaries.*
- [ ] T3.3 Spec `06-misfire.md` + `EvaluateMisfire(expected, landed, toleranceMs)`. *Accept: misfire iff outside ±tolerance OR fingerprint mismatch; tested.*

## Deferred — SUPERVISED, live, NOT the autonomous loop
- One-time golden raw-`TelemetrySample` capture of `subses85380877` → `tests/fixtures/` (makes end-to-end replayable offline forever; diff engine output vs study indexes 1×=163/2×=162/16×=154 via `scorecard.mjs`).
- Plugin adapter (IRSDK→`TelemetrySample`) + routing the real build/jump through the engine.
- Live 1×/16× sweep validation. `deploy.ps1`. financial-dashboard PR. OTel cloud-token region fix.
