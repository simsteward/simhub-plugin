# Product Context

## Why This Exists

Filing an iRacing protest takes 15-20 minutes of manual replay scrubbing, screen recording, and editing. Most drivers don't bother. Bad actors go unreported. Sim racing stays dirty.

Sim Steward turns incident clipping from a chore into a one-click action.

## Problems Solved

- **The Protester:** Wants to report bad driving but the 15-20 min manual process kills motivation
- **The Clean Racer:** Wants accountability but knows bad behavior goes unreported because protesting is too hard

## How It Works

1. **Detect** -- Auto-detect via `PlayerCarTeamIncidentCount` delta, or manual hotkey mark
2. **Notify** -- In-game overlay shows incident detected with timestamp
3. **Jump** -- One-click replay jump to incident moment (offset configurable)
4. **Record** -- OBS records the clip via WebSocket control
5. **Save** -- Clip file path shown; confirm or discard

Part 2 adds automated multi-camera clipping: loop replay through multiple camera angles, record each, stitch into one file.

## What This Is NOT

- Not an AI tool (AI analysis is a future phase, out of scope)
- Not a web platform or SaaS product
- Not monetized -- free tool, get users first

## Full PRD

See `docs/product/prd.md` (v2.0) for complete requirements.
