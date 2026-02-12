# Priority Tracker

> Single source of truth for work priorities. **Phase: Alpha** – Prove the core loop (Telemetry → AI Ruling).

**Last updated:** 2025-02-11

---

## Now (In Progress)

| Item | Owner | Notes |
|------|-------|-------|
| Scaffold SimHub plugin + telemetry buffer | | FR-A-001 to FR-A-005 |

---

## Next (Queued)

| Priority | Item | Depends On |
|---------|------|------------|
| 1 | Cloudflare Worker: receive POST, archive to R2 | Plugin sends data |
| 2 | Cloudflare Worker: Workers AI + Steward prompt | R2 archiving |
| 3 | SimHub UI: Main tab + incident list | Plugin + Worker response |
| 4 | SimHub UI: In-game overlay + Mark button | Main tab |
| 5 | Replay jumping (irsdk_BroadcastReplaySearch) | UI + iRacing SDK |

---

## Backlog

- Visual grading (🔴🟡☠️) – FR-A-014
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
| Project structure + PRD + priorities | 2025-02-11 |
