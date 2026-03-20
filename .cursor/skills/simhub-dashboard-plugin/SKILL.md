---
name: simhub-dashboard-plugin
description: SimHub UI + C# Plugin + iRacing telemetry reference.
---
# SimHub Dashboard + Plugin
## UI & Comms
- **HTML/JS (ES6+)** for UI (NO WPF Dash Studio).
- Serve dash via SimHub web server at `Web/sim-steward-dash/` (port 8888).
- **WebSocket (Fleck)** in C# plugin on `0.0.0.0:19847` for bi-directional telemetry/actions. Do NOT use HttpListener.
## C# Plugin (.NET 4.8)
- Implement `IPlugin`, `IDataPlugin`. Use `Init()` for startup, `DataUpdate()` for ~60Hz loop.
- Use `IRSDKSharper` for direct iRacing shared memory, NOT `GameRawData`.
## iRacing Incidents
- **Delta = Type**: 1x (off-track), 2x (wall/spin), 4x (contact). Dirt: contact is 2x.
- **Admin Lock**: Other drivers' incidents stay 0 during live races unless admin. Replay tracks all.
- **Replay**: At 16x, YAML incidents batch. Cross-reference `CarIdxGForce` and `CarIdxTrackSurface` to decompose type.
## Testing & Deploy
- `pwsh -File .\deploy.ps1`. 
- **Retry-once-then-stop** rule. Hard stop after 2 fails.
- Lints: 0 new errors.
