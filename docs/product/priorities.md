# Priority Tracker

> Single source of truth for work priorities. **Phase: MVP (Incident Clipping Tool)** -- Detect incidents, jump replay, clip via OBS, save locally.

**Focus:** Replay overlay (FR-003) over the replay window is the next user-facing goal. Grafana work is compartmentalized and de-prioritized; **SimHub plugin telemetry (heartbeat, Activity sparkline, Connect telemetry) is sufficient monitoring for now.**

**Last updated:** 2026-02-14

---

## Now (In Progress)

| Item | Owner | Notes |
|------|-------|-------|
| FR-001-002-Incident-Detection | | Core detection loop; unblocks overlay, replay, OBS integration |

---

## Next (Queued)

| Priority | Item | FR-IDs | Depends On | Notes |
|----------|------|--------|------------|-------|
| 1 | FR-008-Plugin-Settings | FR-008 | SCAFFOLD | Settings tab; unblocks Camera Control later |
| 2 | FR-005-006-007-OBS-Integration | FR-005, FR-006, FR-007 | SCAFFOLD | **OBS spike first** -- biggest architectural risk |
| 3 | FR-004-Replay-Control | FR-004 | FR-001-002 | Replay jump on incident; feeds into overlay and clipping |
| 4 | FR-003a-Replay-Overlay | FR-003 | FR-001-002, FR-004, FR-005-006-007 | Replay overlay: incident list, jump, record, OBS status. |
| 5 | FR-003b-Live-Toast | FR-003 | FR-001-002 | Minimal live-racing toast on incident; auto-dismiss. |
| 6 | FR-009-Incident-Log | FR-009 | FR-001-002, FR-004 | Session log with replay-jump links (desktop tab) |

---

## Deferred (not now)

| Item | Notes |
|------|-------|
| **Grafana** (Loki setup guide, dashboard improvements, MCP, further telemetry) | Compartmentalized. SimHub plugin (heartbeat, Activity sparkline, Connect telemetry, optional Loki push) = sufficient monitoring for MVP. Revisit post–replay overlay / user feedback. |

---

## Backlog (Part 2 -- Multi-Camera Clipping)

| Priority | Item | FR-IDs | Depends On |
|----------|------|--------|------------|
| 7 | FR-010-011-Camera-Control | FR-010, FR-011 | SCAFFOLD, FR-008 |
| 8 | FR-012-014-015-Multi-Camera-Clipping | FR-012, FR-014, FR-015 | FR-004, FR-005-006-007, FR-010-011 |
| 9 | FR-013-Video-Stitching | FR-013 | FR-012-014-015 |

---

## Blocked

| Item | Blocker |
|------|---------|
| _(none)_ | |

---

## Done (Recent)

| Item | Completed |
|------|-----------|
| SCAFFOLD-Plugin-Foundation closed for scheduling (plugin shell/settings/scripts; dual review PASS; manual SimHub runtime validation pending or completed) | 2026-02-14 |
| Product pivot: old docs purged, new PRD + 10 stories written (incident clipping tool) | 2026-02-13 |
