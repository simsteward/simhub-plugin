# Active Context

## Current Focus

**Phase:** MVP (Part 1 -- Incident Clipping Tool)  
**Now:** SCAFFOLD-Plugin-Foundation (plugin shell, iRacing SDK connection, SimHub property layer, settings stub)

Full priority queue in `docs/product/priorities.md`.

## Active Decisions

- **Product vision reset (2026-02-13).** Sim Steward is an incident clipping tool, not an AI analyzer. Core loop: detect incident -> overlay notification -> replay jump -> OBS records clip -> save. PRD v3.0 with full spec set in `docs/product/specs/` and tech plans in `docs/tech/plans/`.
- **No backend.** Everything runs locally. Cloudflare Worker, R2, Workers AI -- all removed.
- **No monetization.** Free tool. Get users first. No Whop, no Statsig gates, no Pro tier.
- **No AI in scope.** AI analysis is explicitly deferred to a future phase after the clipping tool has real users (PRD Section 8).
- **OBS is the recording backbone.** Plugin controls OBS via WebSocket 5.x protocol. Biggest technical risk: WebSocket client in .NET 4.8.
- **Two-part roadmap.** Part 1: Detect + Clip + Save (single camera). Part 2: Automated multi-camera clipping (camera control + replay loops + video stitching).
- Memory Bank is primary context; supersedes other rules when in conflict.
- Subagent orchestration via `delegation.mdc` (auto-routing to specialists).

## Three Spikes Needed

1. **OBS WebSocket connectivity** -- Can .NET 4.8 maintain stable WebSocket to OBS? Blocks FR-005/006. (see FR-005-006-007-OBS-Integration story)
2. **Video stitching approach** -- FFmpeg CLI? OBS scene switching? Two separate files? Blocks FR-013. (see FR-013-Video-Stitching story)
3. **iRacing camera enumeration** -- Reliably discover camera groups/IDs at runtime. Blocks FR-010/011. (see FR-010-011-Camera-Control story)

## What Was Removed

All of the following are **gone, not deferred**: telemetry buffer/CSV pipeline, Cloudflare Worker + R2 + Workers AI, Whop licensing, Statsig feature gates, Free/Pro monetization tiers, web platform (4 layers), driver reputation system, video pipeline (vision LLM), operational cost models, performance timing docs.
