# Product Context

## Why This Exists

Automate the friction-heavy process of reviewing incidents and filing protests in iRacing. Replace 20+ minutes of video editing with instant AI analysis.

## Problems Solved

- League racers: Unbiased rulings on incidents to improve racecraft and settle disputes
- Public lobby drivers: Fast, low-effort way to report bad actors without manual video work

## How It Works

1. **Drive** – Telemetry buffered in RAM (30s pre/post incident)
2. **Incident** – Auto (PlayerCarTeamIncidentCount) or Manual (hotkey)
3. **Send** – CSV payload → Cloudflare Worker
4. **AI** – Workers AI (Llama) as "Chief Steward" → JSON ruling
5. **Display** – Verdict, timeline, protest statement in SimHub UI

## Full PRD

See `docs/product/prd.md` for complete requirements.
