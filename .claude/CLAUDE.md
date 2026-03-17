# SimHub Development Rules (sync with .cursor/rules/SimHub.mdc)

## Dashboard UI
- Prefer **HTML/JavaScript** (ES6+) for UI. NO Dash Studio WPF.
- Dashboards run in real browser. Do NOT confuse with Jint (ES5.1).

## Plugin Development
- Target **.NET Framework 4.8**.
- Use `Init()` for properties/actions. `DataUpdate()` runs ~60Hz.

## Plugin <-> Dashboard Communication
- Use **Fleck** for WebSocket (bind to `0.0.0.0`). Do NOT use `HttpListener`.
- Dashboard HTML served by SimHub HTTP server (`Web/sim-steward-dash/`).

## iRacing Shared Memory
- Use **IRSDKSharper**. Do NOT use `GameRawData`.
- **ADMIN LIMITATION**: Live races show 0 incidents for others unless admin. Replays track all.
- **Incident types (deltas)**: 1x (off-track), 2x (wall/spin), 4x (heavy contact). Dirt: 2x heavy.
- **Quick-succession**: 2x spin -> 4x contact records as +4 delta.
- **Replay**: At 16x speed, YAML incident events are batched. Cross-reference `CarIdxGForce` and `CarIdxTrackSurface` to decompose type.

## Deployment & Testing
- Deploy via `deploy.ps1`. MUST pass build (0 errs), `dotnet test`, and `tests/*.ps1`.
- **Retry-once-then-stop** rule. Hard stop after 2 fails.
- Lints: 0 new errors.

## Memory Bank
- Memory Bank is personal vibe-coding. OUT OF SCOPE. Do not implement or reference.

## Minimal Output
Read and strictly follow the output rules defined in `docs/RULES-MinimalOutput.md`.