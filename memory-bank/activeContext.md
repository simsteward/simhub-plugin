# Active Context

## Current Focus

**Phase:** MVP (Part 1 -- Incident Clipping Tool)  
**Now:** FR-001-002-Incident-Detection (core detection loop; unblocks overlay, replay, OBS integration)

SCAFFOLD was closed for scheduling per [priority-product-sync](docs/product/priority-product-sync.md). Next work is the core detection loop. Full priority queue in `docs/product/priorities.md`.

**Recent (2026-02-14):** Priority-product-sync executed: SCAFFOLD moved to Done, FR-001-002 promoted to Now; memory-bank updated. Grafana remains deferred; replay overlay (FR-003) is the user-facing goal, reached by doing dependencies in order (detection first).

## Active Decisions

- **Product vision reset (2026-02-13).** Sim Steward is an incident clipping tool, not an AI analyzer. Core loop: detect incident -> overlay notification -> replay jump -> OBS records clip -> save. PRD v3.0 with full spec set in `docs/product/specs/` and tech plans in `docs/tech/plans/`.
- **No backend.** Everything runs locally. Cloudflare Worker, R2, Workers AI -- all removed.
- **No monetization.** Free tool. Get users first. No Whop, no Statsig gates, no Pro tier.
- **No AI in scope.** AI analysis is explicitly deferred to a future phase after the clipping tool has real users (PRD Section 8).
- **OBS is the recording backbone.** Plugin controls OBS via WebSocket 5.x protocol. Biggest technical risk: WebSocket client in .NET 4.8.
- **Two-part roadmap.** Part 1: Detect + Clip + Save (single camera). Part 2: Automated multi-camera clipping (camera control + replay loops + video stitching).
- Memory Bank is primary context; supersedes other rules when in conflict.
- Subagent orchestration via `delegation.mdc` (auto-routing to specialists).
- Process guidance captured: direct execution is allowed, but plugin implementation requires simhub-developer review + code-reviewer review, then self-documentation in memory-bank.

## Three Spikes Needed

1. **OBS WebSocket connectivity** -- Can .NET 4.8 maintain stable WebSocket to OBS? Blocks FR-005/006. (see FR-005-006-007-OBS-Integration story)
2. **Video stitching approach** -- FFmpeg CLI? OBS scene switching? Two separate files? Blocks FR-013. (see FR-013-Video-Stitching story)
3. **iRacing camera enumeration** -- Reliably discover camera groups/IDs at runtime. Blocks FR-010/011. (see FR-010-011-Camera-Control story)

## What Was Removed

All of the following are **gone, not deferred**: telemetry buffer/CSV pipeline, Cloudflare Worker + R2 + Workers AI, Whop licensing, Statsig feature gates, Free/Pro monetization tiers, web platform (4 layers), driver reputation system, video pipeline (vision LLM), operational cost models, performance timing docs.
