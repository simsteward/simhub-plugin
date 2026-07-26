# Review Findings — Live Incident Point Resolution & Escalation (2026-07-19)

**Purpose:** adversarial review findings for the incident-points/escalation implementation shipped 2026-07-19
(see `docs/IRACING-DATA-AVAILABILITY.md` Group 5 for the empirical Branch-B confirmation that motivated it).
This is the input for a follow-up planning session — each item is meant to be independently actionable.
Two review rounds ran against the live working tree (all tests green at review time); every item below was
verified against actual code unless marked PLAUSIBLE-UNVERIFIED.

**Implementation under review:**
- `src/SimSteward.Plugin/IncidentCauseMapping.cs` (new) — source-driven cause classification, points-override.
- `src/SimSteward.Plugin/IncidentSeverityCorrelator.cs` (new) — per-car quick-succession merge window (~6s),
  max-points-wins escalation per iRacing's official rule, dirt 4x→2x cap.
- `src/SimSteward.Plugin/ReplayIncidentYamlDiff.cs` — non-{1,2,4} deltas now `Math.Min(4, delta)` + `IsAggregateDelta`.
- `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs` — correlator wiring, escalation board-amend,
  `live_incident_escalated` event, YAML verification probe, dirt-session baseline flag.
- `src/SimSteward.Plugin/PluginState.cs` — `LiveIncidentBoardEntry.PointsResolved`.
- `src/SimSteward.Dashboard/index.html` — `pending` points badge, `flagged` cause tag, Replay Index pointer note.

---

## Round 1 findings (complete, 17-minute pass)

### BUG 1 — Unsynchronized cross-thread access to `_liveIncidentBoardEntries`
- **Anchors:** `SimStewardPlugin.cs` `GetIncidentsForNewClient`, `DashboardBridge.cs` `socket.OnOpen`,
  vs. `SimStewardPlugin.LiveIncidentDetection.cs` (Clear/Add/Find + `BroadcastLiveIncidentBoard`).
- The telemetry thread mutates and serializes the raw `List` while a Fleck WS `OnOpen` thread serializes the
  same list on every client connect. No lock on either side.
- **Scenario:** dashboard refresh during an incident → `InvalidOperationException: Collection was modified`
  (swallowed in `DashboardBridge`, new client silently gets no incident history) or torn/malformed JSON.
- **Fix direction:** dedicated lock guarding all board reads/writes (both threads). Trivially triggered in
  real use — highest priority.

### BUG 2 — Points-override cause vs. cause-rank disagree; spurious no-op escalations
- **Anchor:** `IncidentSeverityCorrelator.cs` `Correlate` (points-override vs. `_pendingBestCauseRank`).
- `IsEscalation` is driven by cause-rank changes, but the emitted cause uses the points-override whenever
  points are resolved — the two disagree.
- **Scenario:** player off-tracks, `player_incident_count` resolves points=1 (cause `off-track`). Seconds
  later a `repair_flag` sample (cause rank 4, no points) arrives → rank rises 1→4 → `IsEscalation=true`,
  but points-override forces the cause back to `off-track` → logs/broadcasts an escalation reading
  `1x off-track → 1x off-track`. Spurious event + full board rebroadcast, zero visible change.
- **Fix direction:** single severity authority — either rank derives from resolved points when present, or
  escalation fires only on the *emitted* (points, cause) pair changing.

### RISK 3 — Sliding-from-last window merges genuinely distinct incidents forever
- **Anchor:** `IncidentSeverityCorrelator.cs` — window slides from the LAST sample per car.
- **Scenario:** chicane-heavy track, driver runs wide every ~5s: every off slides the window, all collapse
  into ONE incident, and (being same-cause/no-points) the 2nd–Nth offs produce no board row and no log —
  silently invisible. iRacing's "quick succession" rule targets one continuous loss-of-control sequence,
  not distinct offs.
- **Fix direction:** fixed-from-first window (bounds the merge while still absorbing escalation catch-up),
  or a max-total-window cap on top of the slide.

### RISK 4 — "flagged" outranks "off-track", replacing a specific cause with a vaguer one
- **Anchor:** `IncidentSeverityCorrelator.cs` cause ranks (`off-track=1 < flagged=2 < spin=3 < contact=4`).
- Observed live (car 12): off-track then `furled_flag` → cause overwritten `off-track` → `flagged`. "Flagged"
  is a status/consequence, not a physical severity tier — this is an information regression.
- **Fix direction:** keep the most-specific physical cause; surface flag status as a separate attribute
  (e.g. a `flags` field on the board entry), not a competing cause.

### RISK 5 — `points=3` aggregate renders unstyled, unfilterable; 3 is not a valid tier
- **Anchors:** `ReplayIncidentYamlDiff.cs` (`Math.Min(4, delta)`), `index.html` points-badge CSS
  (`.s1/.s2/.s4` only) and filter chips (`1|2|4` only).
- A YAML delta of 3 (replay path — bypasses the correlator) resolves points=3: uncolored badge, visible only
  under the "All" chip. Per the "highest tier" rule, delta 3 = 1+2 → should snap to **2**, not stay 3.
- **Fix direction:** snap non-{1,2,4} aggregates to the nearest valid tier at the diff layer
  (3→2, 5/6/7+→4), keep `IsAggregateDelta` for auditability.

### UX 6 — Pending-points incidents vanish under every point-value filter chip
- **Anchor:** `index.html` `getFilteredIncidents` (`String(i.points) === incFilter`); server sends
  `Points=0, PointsResolved=false` for unresolved.
- Under Branch B, almost every other-car live incident is `pending` (points=0) → the 1×/2×/4× chips show
  almost nothing during a live session, silently hiding the dominant category.
- **Fix direction:** add a "pending" chip; make numeric chips exclude unresolved entries explicitly.

### IMPROVEMENT 7 — Verification probe logs INFO every 5s forever; its question is answered
- **Anchor:** `SimStewardPlugin.LiveIncidentDetection.cs` `PollLiveYamlIncidentsForVerificationLocked`.
- Branch B is confirmed (84 polls / 60min / 25 real incidents / 0 deltas). ~720 INFO lines/hr per live
  session is standing volume cost against CLAUDE.md's logging rules.
- **Fix direction:** gate on `_logger.IsDebugMode` (keep as cheap insurance against a future iRacing change)
  or remove outright.

### IMPROVEMENT 8 — Unbounded board growth + full-array rebroadcast per change
- **Anchor:** `SimStewardPlugin.LiveIncidentDetection.cs` — board only cleared at session boundary; every
  change re-serializes and broadcasts the whole array to every client → O(n²) bytes over a wreck-heavy race.
  `_livePendingIncidentFingerprintByCar` never pruned except on reset (bounded by car count — minor).
- **Fix direction:** rolling cap (e.g. last N entries) and/or delta-broadcast protocol.

### TEST-GAP 9
- No coverage for: (a) points-override-vs-rank no-op escalation (BUG 2); (b) merge-forever with repeated
  sub-6s offs (RISK 3); (c) points=3 snapping (RISK 5); (d) any concurrency assertion on the board list.
- Add correlator case: off-track(no pts) → points resolve to 1 → contact-flag(no pts, rank 4) — assert the
  emitted cause and whether `IsEscalation` should fire at all.

### Verified NOT bugs (round 1)
- **Fingerprint recompute mismatch:** does not occur — fp computed once at new-incident time, stored in the
  per-car dict AND as `entry.Id`; escalation looks it up, never recomputes from the merged sample.
- **`PointsResolved` true→false regression on escalation:** cannot happen — the correlator's `Math.Max`
  retains the running points max, so a later null-points sample still carries the resolved value.

### Round 1 top-3
1. Threading race on the incident board (BUG 1).
2. Points-vs-rank reconciliation (BUG 2 + RISK 4) — one severity authority.
3. Pending incidents unreachable by filters + points=3 snapping (UX 6 + RISK 5).

---

## Round 2 findings (early-terminated pass; focus areas below)

Round 2 was directed at: (1) the dirt-session baseline flaw — `_liveRaceIsDirtSession` sampled once at
baseline from the material under the player's car, which is likely meaningless when loading in while in the
garage (the common case), silently disabling the dirt cap for the whole session; (2) replay-vs-live
asymmetry's downstream consumers (misfire evaluator, TR-023 validation, walk features); (3) whether the
escalation audit trail alone suffices to reconstruct an incident's final state from Loki; (4) probe
parse-flicker (ok→fail→ok) phantom/missed deltas; (5) re-verification of round 1's "not a bug" claims.

The pass was wound down early at the user's request, but all five focus areas were covered before the
cutoff. Delta findings (new items only — round-1 items unchanged unless noted):

### BUG 10 — Dirt-cap is effectively dead on real dirt sessions (garage-time baseline latch)
- **Anchor:** `SimStewardPlugin.LiveIncidentDetection.cs` — `_liveRaceIsDirtSession` computed ONLY inside
  the `needReset` block, never recomputed after.
- The flag reads `CarIdxTrackSurfaceMaterial[playerCarIdx]` at baseline time — which fires on
  `first_run_live`/session boundaries, exactly when the player is sitting in the garage/pit stall
  (`CarIdxTrackSurface` = InPitStall or NotInWorld). Pit concrete / not-in-world does not report the
  racing-groove dirt materials (7/8), so `IsDirtRacingSurface(...)` returns false. There is also no
  on-track guard on the read.
- **Scenario:** load into a genuine dirt-oval race → baseline captured in the pit stall →
  `_liveRaceIsDirtSession=false` latches for the entire session → the 4x→2x dirt cap in
  `IncidentSeverityCorrelator.ApplyDirtCap` never applies. The feature Step 2.5 was built for silently
  never fires in its intended case.
- **Fix direction:** don't latch at baseline. Either (a) re-evaluate on the first tick where the player's
  `CarIdxTrackSurface == OnTrack`, or (b) latch true if ANY car shows material 7/8 during the session
  (dirt is a track property, not a per-car one) — a cheap OR-scan of the material scratch array each
  detection tick would be correct and self-healing.

### VERIFIED CLEAN — Replay-path asymmetry does NOT break misfire / TR-023 / validation counts
- `ReplayControlActions.EvaluateMisfire` operates only on the replay-index store built by the FF-sweep
  path; the live correlator writes only to `_liveIncidentBoardEntries` and log events. Two disjoint data
  stores — no cross-comparison, no off-by-N. Retracted as a correctness concern.
- **Residual (UX only):** the live "Incidents" tab shows ONE merged row where the "Replay Index" tab shows
  2–3 rows (track_surface + player_incident_count + yaml_incident_delta) for the same physical incident.
  A user eyeballing counts across tabs sees a mismatch — worth an explicit "these tabs count differently"
  note alongside the existing honesty note.

### IMPROVEMENT 11 — Escalation final-state row lacks location/telemetry; audit requires a fingerprint join
- **Anchor:** escalation `escFields` in `SimStewardPlugin.LiveIncidentDetection.cs` carries only
  fingerprint, car_idx, driver, from/to points+cause, detection_source, session_time, car_lap. The rich
  context (`track_location`, `lap_dist_pct`, `surface_material`, per-car telemetry, player-only block)
  lives only on the original `live_incident_detection` row — which holds the PRE-escalation points/cause.
- The final merged state IS reconstructable from Loki (both rows share `fingerprint`, and the join target
  always exists since `IsEscalation` requires a prior `isNew` row), but no single row holds the final
  state WITH location. For off-track→contact, the only row with `track_location` says "off-track"/pending;
  the row saying "contact"/4x has no location.
- **Fix direction:** add `track_location` + `lap_dist_pct` to the escalation row so it is self-contained.

### VERIFIED CLEAN — Probe parse-flicker (ok→fail→ok and session-change)
- On a failed-parse tick, `Diff` is never called and the previous good snapshot is retained — the next
  good poll diffs against the last GOOD snapshot, no phantom deltas. Session change resets the probe
  baseline to null before the probe runs, and a null baseline emits nothing. Minor residual (PLAUSIBLE,
  log-only, harmless): if `TryParseOfficialIncidentsByCarIdx` picks a different `sessUsed` across polls
  and the new session's counts are higher, a phantom positive delta could LOG — but it never feeds the
  board, so no functional impact.

### RE-VERIFIED — Round-1 "NOT BUGS" both still hold
- **Fingerprint stability:** confirmed — fp computed once from the first sample, stored in the per-car
  dict AND as `entry.Id`, matched by stored-string identity, never recomputed from the merged sample.
- **PointsResolved regression:** confirmed impossible — the correlator's `Math.Max` on points means once
  resolved (≥1) it stays resolved for the car's window; the dirt cap (4→2) preserves `HasValue`.

### Round 2 top fix
**BUG 10 (dirt baseline)** is the one new actionable correctness item: as written, the dirt 4x→2x cap
never activates on a real dirt oval. Move dirt detection off the garage-time baseline latch to an
on-track / any-car material scan.

---

## Combined priority order for the follow-up planning session

1. **BUG 1** — threading race on `_liveIncidentBoardEntries` (throws/corrupts on client connect during an incident).
2. **BUG 2 + RISK 4** — one severity authority for points vs. cause-rank (spurious no-op escalations; `flagged` label regression).
3. **BUG 10** — dirt-cap garage-baseline latch (feature dead in its intended case).
4. **UX 6 + RISK 5** — "pending" filter chip; snap aggregate deltas to valid tiers (3→2).
5. **RISK 3** — bound the merge window (fixed-from-first or max-total cap).
6. **IMPROVEMENT 7** — probe to DEBUG or removed.
7. **IMPROVEMENT 11** — self-contained escalation rows (location on the final-state row).
8. **IMPROVEMENT 8** — board growth cap / delta broadcast.
9. **TEST-GAP 9** — cover items 2, 3, 5, and board concurrency.

---

## GAP 12 — No spin / loss-of-control detector exists (2026-07-25)

- **What's missing:** none of the current detection sources (`repair_flag`, `furled_flag`, `black_flag`,
  `disqualify`, `player_incident_count`, `track_surface`, `yaml_incident_delta`, `fast_repair` — see
  `ReplayIncidentIndexDetection.cs`) directly detect a spin. `IncidentCauseMapping.CauseSpin` ("spin") only
  ever gets assigned as a side effect of a *resolved* points value of exactly 2 — which in practice means
  only the player's own live `PlayerCarMyIncidentCount` delta, since no other car's points resolve live
  (Group 1, `docs/IRACING-DATA-AVAILABILITY.md`). For every other car, a spin is invisible unless it happens
  to also trip off-track, a flag, or a fast repair.
- **Confirmed no prior art in CrewChief:** GitHub code search across the full `mrbelowski/CrewChiefV4` repo
  found zero occurrences of `YawRate` anywhere, and `Yaw` only as an unused raw struct field in
  `iRacingData.cs`. `DamageReporting.cs` and `iRacingSpotter.cs`/`NoisyCartesianCoordinateSpotter.cs` were
  read directly — no yaw-rate, angular-velocity, or heading-change logic anywhere. `docs/IRACING-CROSSWALK.md`
  previously miscited `DamageReporting.cs` for this and has been corrected.
- **Confirmed no SDK-level event either:** iRacing's `SessionFlags` bitfield (all 32 bits enumerated in
  `docs/IRACING-CROSSWALK.md` Appendix A) has no spin/loss-of-control bit. There is no dedicated "car X
  spun" signal anywhere in the SDK, live or replay, for any car.
- **What a from-scratch heuristic could use (any car, no admin):** `CarIdxLapDistPct` (position, 60Hz),
  `CarIdxRPM` (direct), `CarIdxGear` (0/neutral during an incident is already flagged as a loose hint in
  `IRACING-CROSSWALK.md`'s Section 2 notes, never acted on). Speed has no direct per-car field — it would
  need to be derived by differentiating `CarIdxLapDistPct × track length` over time, same derivation noted
  as a general SDK limitation in `docs/IRACING-DATA-AVAILABILITY.md` Group 2. A real yaw signal
  (`Yaw`/`YawRate`) would be needed for a confident spin read and is player-only — no `CarIdxYaw` array
  exists — so any heuristic built from position/speed/RPM/gear would be an approximation, not a confirmed
  spin detection, exactly like the existing off-track/contact detectors already are for point values.
- **Status:** not scheduled — flagged for a future planning pass, not part of the priority order above
  (items 1-9 are fixes to the shipped feature; this is a net-new detector that doesn't exist yet).
10. **UX residual** — "these tabs count differently" note (live merged vs. replay unmerged).
