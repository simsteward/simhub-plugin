# Priority Tracker

> Single source of truth for work priorities. **Phase: MVP (Incident Clipping Tool)** -- Detect incidents, jump replay, clip via OBS, save locally.

**Last updated:** 2026-02-13

---

## Now (In Progress)

| Item | Owner | Notes |
|------|-------|-------|
| SCAFFOLD-Plugin-Foundation | | Plugin shell, iRacing SDK connection, SimHub property layer, settings stub |

---

## Next (Queued)

| Priority | Item | FR-IDs | Depends On | Notes |
|----------|------|--------|------------|-------|
| 1 | FR-001-002-Incident-Detection | FR-001, FR-002 | SCAFFOLD | Core detection loop -- everything depends on this |
| 2 | FR-008-Plugin-Settings | FR-008 | SCAFFOLD | Settings tab; unblocks Camera Control later |
| 3 | FR-005-006-007-OBS-Integration | FR-005, FR-006, FR-007 | SCAFFOLD | **OBS spike first** -- biggest architectural risk |
| 4 | FR-004-Replay-Control | FR-004 | FR-001-002 | Replay jump on incident; feeds into overlay and clipping |
| 5 | FR-009-Incident-Log | FR-009 | FR-001-002, FR-004 | Session log with replay-jump links (desktop tab) |
| 6 | FR-003-In-Game-Overlay | FR-003 | FR-001-002, FR-004, FR-005-006-007 | Replay overlay: incident list + jump + record. Live toast only while racing. |

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
| Product pivot: old docs purged, new PRD + 10 stories written (incident clipping tool) | 2026-02-13 |
