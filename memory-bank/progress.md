# Progress

## What Works

- Project structure (docs, plugin, .cursor)
- Plugin scaffold implementation started in `plugin/`:
	- `SimSteward.csproj` (.NET 4.8, SimHub refs, local SDK link resolution)
	- `src/SimStewardPlugin.cs` (`IPlugin` + `IDataPlugin` shell, telemetry reads, connection tracking, throttled logs)
	- `src/Settings/SettingsControl.xaml` placeholder settings tab
	- `scripts/create-simhub-sdk-links.ps1` and `scripts/deploy-plugin.ps1`
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

## Open Implementation Fixes (Current)

- [ ] Runtime verification in SimHub host (manual): build + deploy + plugin load + telemetry log confirmation (parked – SCAFFOLD closed for scheduling).

## Plugin Workflow Gates

- [x] SimHub agent implementation complete
- [x] SimHub agent self-review complete
- [x] Coding agent single execution pass complete

## Done (Recent)

| Item | Date |
|------|------|
| Heartbeat UI semantics updated: faint idle heart, success beat opacity pulse, broken-heart on telemetry error, and automatic return to normal heart on recovery; deploy run succeeded | 2026-02-14 |
| OTLP auth/push fix: exporter now auto-detects OTLP endpoint path and sends OTLP JSON (`resourceLogs`/`scopeLogs`/`logRecords`) instead of Loki payload for `/otlp/v1/logs`; connection wording updated to "Connect telemetry"; missing-credentials message made endpoint-agnostic; deploy run succeeded | 2026-02-14 |
| Monitoring scrutiny fixes: Loki flush on shutdown (LokiExporter.Dispose), disk-log init-only docs, schema doc accuracy, Grafana dashboard (description, 30s refresh, heartbeat panel sum+$__auto, "How to get data" text panel); scratch scrutiny docs removed; deploy run | 2026-02-14 |
| Activity sparkline: log lines written + errors + network bytes; 5s window, 5 Hz refresh, linear Y, three polylines (Log lines / Errors / Network bytes); TelemetryManager + LokiExporter counters; deploy run | 2026-02-14 |
| Scaffold review-fix pass completed: SimHub SDK API compatibility fixed, deploy script output-path handling fixed, SDK link script now hard-link-first fallback, stale agent rule paths corrected, dual-agent re-review passed | 2026-02-13 |
| Plugin scaffold code + local SDK links/deploy script added; fixed SimHub PluginsData deploy path policy baked into agent/rules; dual-agent review completed with follow-up fixes identified | 2026-02-13 |
| **PRD/spec/tech plan rollout:** PRD v3.0 rewrite; 13 PRD specs (Part 1 + Part 2); 6 tech plans; Phase 1 compliance review; delegation + product-owner/simhub-developer guidance updated | 2026-02-13 |
| **Product vision reset:** purged all old docs (PRD v1.2, 8 old stories, plans, cost/perf docs, API design), wrote PRD v2.0 + 10 new stories, updated priorities | 2026-02-13 |
| SimHub developer agent expanded with dashboard/overlay/Dash Studio expertise | 2026-02-11 |
| Orchestration optimization (tiered reading, dedup, token efficiency) | 2026-02-11 |
| Product-owner agent + stories directory | 2026-02-11 |
| Subagent orchestration (delegation rule + 8 agents) | 2026-02-11 |

Full history in `docs/product/priorities.md`.
