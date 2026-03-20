# Sim Steward — State, Roadmap (Now / Next / Later), and Tech Debt

> Developer-curated. **STATUS.md** redirects here + archived narrative; **INTERFACE.md** is the WebSocket contract.

---

## Current state (as of 2026-03-02)

- **Plugin:** YAML incident detection from session YAML (`CurDriverIncidentCount`). Incidents carry `replayFrameNum` (from `ReplayFrameNum` at add time). Seek-backward does not clear incidents or reset metrics; dedup by `(CarIdx, ReplayFrameNum)` before add. Real-time `incidentEvents` push and throttled state; OBS-ready fields (`replayFrameNum`, `sessionId`) in place.
- **Dashboard:** Single-page HTML/JS. Replay controls: play/pause, speed row (including -1× rewind with probe), frame step, replay state legend. Incidents: two collapsible sections — **Live (at playhead)** and **Entire session**; frame-based visibility for Live; persisted expanded section in `localStorage`. Drivers table; diagnostics (Scanned/Captured); Select-incident-and-seek with toast on not-found.
- **Docs:** `INTERFACE.md` is source of truth for WebSocket contract. `STATUS.md` → this file + `archive/project-status-archived.md`. See **docs/README.md** for the full map.

---

## Now / Next / Later

### Now (immediate focus)

| Item | Notes |
|------|--------|
| **Runtime verification** | Deploy → iRacing replay → confirm incidents fire and Live/Entire/rewind behave as expected. |
| **Validate rewind in iRacing** | Manually confirm `ReplaySetSpeed("-1")` is supported; if not, dashboard already degrades (message + unsupported rewind button). |

### Next (short-term)

| Item | Notes |
|------|--------|
| **OBS integration** | Scene switching + clip recording from `incidentEvents`; use `replayFrameNum` to seek and `sessionId` for clip naming. Architecture TBD (e.g. obs-websocket 5.x). |
| **INTERFACE.md rewind note** | Optional one-liner that `ReplaySetSpeed("-1")` is rewind. |
| **Optional dashboard polish** | e.g. “Incidents in view: K / N” in diagnostics; per-incident frame on cards when diagnostics expanded; optional grouping by same frame (“3 incidents at 12:34”). |

### Later (backlog / not scheduled)

| Item | Notes |
|------|--------|
| **Log/event stream UI** | Per `docs/plans/PLAN-log-event-stream-SUMMARY.md`: live event stream panel (log entries + incident push + optional physics). |
| **Physics events in UI** | If physics signals are re-enabled or expanded, surface them in dashboard (stream and/or incident correlation). |
| **Session-change behavior** | Document or refine what happens to incidents and UI when replay session number changes (e.g. clear vs retain). |

---

## Tech debt (not on roadmap)

Things that should be done for clarity, consistency, or maintainability but are not feature roadmap items.

| # | Item | Where / why |
|---|------|-------------|
| 1 | **Replay frame naming vs iRacing SDK** | In plugin state, `replayFrameNum` = current playback position (from SDK `ReplayFrameNumEnd`), `replayFrameNumEnd` = last frame of session (from SDK `ReplayFrameNum`). Names are inverted relative to SDK. Consider aligning names or documenting the mapping in INTERFACE.md and in code comments. |
| 2 | **Unused / dead fields** | IncidentTracker (and possibly others) have compiler warnings for unused fields; remove or use. |
| 3 | **No automated tests** | No unit or integration tests in repo; add when touching critical paths (e.g. IncidentTracker dedup, state build). |
| 4 | **STATE_AND_ROADMAP.md** | Update “Current state” date when doing releases or major checkpoints. |
| 5 | **Single-file dashboard** | `index.html` is large; consider splitting (e.g. JS module, or logical sections) for maintainability when adding more features. |
| 6 | **Error handling and reconnection** | Dashboard reconnection and WebSocket error handling could be centralized and logged for easier debugging. |
| 7 | **Magic numbers** | e.g. seek-backward threshold (10 frames), throttle (~200 ms), rewind probe timeout (~1.5 s); consider named constants or config. |

---

## References

- **Status and open tasks:** `docs/STATUS.md` (redirect) + `docs/STATE_AND_ROADMAP.md`
- **WebSocket contract:** `docs/INTERFACE.md`
- **Log/event stream plan:** `docs/plans/PLAN-log-event-stream-SUMMARY.md`, `docs/plans/PLAN-log-event-stream.md`, `docs/plans/PLAN-event-stream-ui.md`
