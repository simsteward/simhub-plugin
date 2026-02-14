# Priority Steward × Product Owner: What We Should Do Next

**Date:** 2026-02-14  
**Purpose:** Align priorities (queue, sequencing) with product scope (stories, dependencies, risks) and produce a clear recommendation.

---

## Discussion

### Priority Steward

**Current state:** SCAFFOLD is still in **Now** with one open item: manual SimHub runtime validation (build + deploy + plugin load + telemetry confirmation). The **Next** queue is ordered 1→6: Incident Detection, Plugin Settings, OBS Integration (spike first), Replay Control, **Replay Overlay (FR-003)**, Incident Log. Grafana is in **Deferred**; the stated focus is the replay overlay over the replay window.

**Concerns:**
- Now has only one item, but that item isn’t “done” until runtime is verified. We shouldn’t promote from Next until SCAFFOLD is closed or we explicitly park the validation and accept the risk.
- FR-003 (overlay) is priority 5 and depends on FR-001-002, FR-004, and FR-005-006-007. So the *sequence* that gets us to overlay is: finish SCAFFOLD → 1 → (3 + 4 in some order) → 5. The queue order already reflects that; the only way to “do overlay next” in a meaningful way is to start the *dependencies* next (Incident Detection, then OBS spike + Replay Control).
- Recommendation: **Close or explicitly park SCAFFOLD**, then **promote FR-001-002-Incident-Detection to Now** as the single next thing. That unblocks everything downstream including the overlay.

### Product Owner

**Current state:** PRD Phase = MVP (Incident Clipping Tool). Core loop: detect incident → overlay notification → replay jump → OBS records clip → save. FR-003 story is **Ready** and has two surfaces: (1) replay-mode overlay (incident list, jump, record, OBS status) and (2) live-racing toast (minimal, auto-dismiss). Dependencies are correct: we need incidents (FR-001-002), replay jump (FR-004), and OBS start/stop (FR-005-006-007) before the overlay can do its job.

**Concerns:**
- The FR-003 story is large. We could split it: **FR-003a Replay Overlay** (Dash Studio overlay, incident list, jump, record, OBS status) and **FR-003b Live Toast** (minimal notification on incident during live racing). That would let us schedule and demo the overlay sooner and add the toast when detection is solid.
- **OBS spike** is the biggest technical risk. Doing it early (right after or in parallel with Incident Detection) de-risks the overlay and FR-008 (settings). So the product view aligns with “OBS spike first” in the queue.
- We shouldn’t start building the overlay UI in earnest until we have at least *some* incident data and a way to jump replay (even stubbed). So: **Incident Detection first**, then either Replay Control or OBS spike; then overlay.

**Suggestion:** If we want “overlay” to feel like the next big milestone, we can (a) keep the current Next order, and (b) optionally split FR-003 into FR-003a (replay overlay) and FR-003b (live toast) so the steward can schedule overlay as one chunk and toast as a quick follow-up.

### Joint view

- **Next actionable step:** Treat SCAFFOLD as done for scheduling purposes (with or without manual validation in the Done notes), and **promote FR-001-002-Incident-Detection to Now** so the next work is clearly “core detection loop.”
- **Path to overlay:** 1 → FR-001-002, 2 → FR-008 (settings), 3 → OBS spike + FR-005-006-007, 4 → FR-004 (replay control), 5 → FR-003 (overlay). No reorder needed; the queue already reflects this. The “focus on overlay” is a *goal*, not “do overlay before its deps.”
- **Optional refinement:** Split FR-003 into FR-003a (replay overlay) and FR-003b (live toast) in stories/specs and in the priority table, so we can mark overlay done and then add toast. Product owner can create the story split; priority steward can add two rows (FR-003a, FR-003b) in Next with appropriate ordering.
- **Risks to watch:** OBS WebSocket in .NET 4.8 (high); if the spike fails, we need a fallback before we lock in overlay recording controls.

---

## Recommendation for “What We Should Do Next”

| Step | Owner | Action |
|------|--------|--------|
| 1 | Priority steward | Move SCAFFOLD to Done (with note: “manual runtime validation pending or completed”); promote FR-001-002-Incident-Detection to Now. |
| 2 | Team | Start **FR-001-002-Incident-Detection** (core detection loop). Everything else—including the overlay—depends on this. |
| 3 | Product owner (optional) | If we want smaller deliverables, split FR-003 into FR-003a (replay overlay) and FR-003b (live toast); hand off to priority steward to update Next. |
| 4 | Priority steward | Keep Next order as-is (Incident Detection → Settings → OBS → Replay Control → Overlay → Incident Log). Revisit after OBS spike. |

**Bottom line:** The next thing we should do is **close SCAFFOLD and start FR-001-002-Incident-Detection**. The replay overlay (FR-003) stays the *target* milestone; we get there by doing its dependencies in order, starting with detection.
