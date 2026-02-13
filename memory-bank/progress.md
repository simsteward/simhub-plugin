# Progress

## What Works

- Project structure (docs, plugin, .cursor)
- PRD v3.0 (incident clipping tool) -- comprehensive master PRD with spec index, dependency graph, data model, spike summary
- 13 PRD specs in `docs/product/specs/` (Part 1 + Part 2, traceable to FR-IDs)
- 6 tech plans in `docs/tech/plans/` (scaffold, OBS spike, Dash Studio overlay, camera enumeration, recording orchestrator, video stitching)
- 10 user stories written (Part 1 + Part 2)
- Priorities tracker with new story queue
- Memory Bank + subagent orchestration (8 agents, delegation rule)
- iRacing SDK investigation -- `docs/tech/sdk-investigation.md`

## What's Left

### Part 1 -- MVP (Detect + Clip + Save)

- [ ] SCAFFOLD-Plugin-Foundation (plugin shell, iRacing SDK, settings stub)
- [ ] FR-001-002 Incident Detection (auto + manual mark)
- [ ] FR-008 Plugin Settings (OBS URL, hotkeys, offsets)
- [ ] FR-005-006-007 OBS Integration (WebSocket connect, start/stop recording, save prompt)
- [ ] FR-004 Replay Control (jump to incident timestamp)
- [ ] FR-003 In-Game Overlay (incident notification)
- [ ] FR-009 Incident Log (session incident list with replay-jump)

### Part 2 -- Multi-Camera Clipping (Backlog)

- [ ] FR-010-011 Camera Control
- [ ] FR-012-014-015 Multi-Camera Clipping
- [ ] FR-013 Video Stitching

## Known Issues / Spikes

| Spike | Risk | Blocks |
|-------|------|--------|
| OBS WebSocket from .NET 4.8 | High -- biggest architectural risk | FR-005, FR-006, FR-007 |
| Video stitching approach | Medium -- multiple options, none proven | FR-013 |
| iRacing camera enumeration | Medium -- SDK session info YAML parsing | FR-010, FR-011 |
| Replay timing accuracy | Low -- adjustable offset mitigates | FR-004 |

## Done (Recent)

| Item | Date |
|------|------|
| **PRD/spec/tech plan rollout:** PRD v3.0 rewrite; 13 PRD specs (Part 1 + Part 2); 6 tech plans; Phase 1 compliance review; delegation + product-owner/simhub-developer guidance updated | 2026-02-13 |
| **Product vision reset:** purged all old docs (PRD v1.2, 8 old stories, plans, cost/perf docs, API design), wrote PRD v2.0 + 10 new stories, updated priorities | 2026-02-13 |
| SimHub developer agent expanded with dashboard/overlay/Dash Studio expertise | 2026-02-11 |
| Orchestration optimization (tiered reading, dedup, token efficiency) | 2026-02-11 |
| Product-owner agent + stories directory | 2026-02-11 |
| Subagent orchestration (delegation rule + 8 agents) | 2026-02-11 |

Full history in `docs/product/priorities.md`.
