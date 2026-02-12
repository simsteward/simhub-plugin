# Active Context

## Current Focus

**Phase:** Alpha  
**Now:** Scaffold SimHub plugin + telemetry buffer (FR-A-001 to FR-A-005)

## Next Steps

1. Cloudflare Worker: receive POST, archive to R2
2. Cloudflare Worker: Workers AI + Steward prompt
3. SimHub UI: Main tab + incident list
4. SimHub UI: In-game overlay + Mark button
5. Replay jumping (irsdk_BroadcastReplaySearch)

## Priorities

See `docs/product/priorities.md` for full Now/Next/Backlog/Blocked/Done.

## Active Decisions

- Using Memory Bank as primary context; supersedes other rules when in conflict
- Subagent orchestration: delegation.mdc auto-routes tasks to specialists
- Statsig gates scoped to Alpha only (FR-A-xxx); prd-compliance adjudicates flag changes
