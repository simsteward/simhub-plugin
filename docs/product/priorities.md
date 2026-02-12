# Priority Tracker

> Single source of truth for work priorities. **Phase: Alpha (SimHub-First)** -- Plugin: Telemetry -> POST -> UI. Worker deferred to separate repo.

**Last updated:** 2026-02-12

---

## Now (In Progress)

| Item | Owner | Notes |
|------|-------|-------|
| SCAFFOLD-SimHub-Plugin | | Plugin shell, iRacing SDK, placeholder tab |

---

## Next (Queued)

| Priority | Item | Depends On |
|---------|------|-------------|
| 1 | FR-A-001-002-Telemetry-Buffer | SCAFFOLD |
| 2 | FR-A-003-Incident-Detection | Buffer |
| 3 | FR-A-004-005-Telemetry-Serialization | Detection |
| 4 | FR-A-006-HTTPS-POST | Serialization |
| 5 | FR-A-012-014-Main-Tab-Incident-List | Detection (build dep), POST (runtime dep) |
| 6 | FR-A-013-In-Game-Overlay | Detection, Main Tab (verdict mapping) |
| 7 | FR-A-015-Replay-Jumping | Main Tab (spike first) |

---

## Backlog

- Cloudflare Worker (FR-A-007 to FR-A-011) -- separate private repo
- Beta: Whop integration
- Beta: Product website

---

## Blocked

| Item | Blocker |
|------|---------|
| | |

---

## Done (Recent)

| Item | Completed |
|------|-----------|
| Stories rewritten (product-owner scrutiny, merged FR-A-014 into FR-A-012) | 2026-02-12 |
| Product plan (SimHub-first), stories, API contract, PRD compliance | 2026-02-11 |
| Project structure + PRD + priorities | 2025-02-11 |
