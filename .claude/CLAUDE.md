# SimHub Development Rules

Project instructions for SimHub dashboard and plugin development (sim-steward). Keep this file in sync with `.cursor/rules/SimHub.mdc` — same content, no frontmatter here.

## Dashboard UI

- Prefer **HTML/JavaScript** for dashboard UI over native Windows/Dash Studio WPF objects.
- Dashboards run in a real browser — use full **ES6+** JavaScript (async/await, fetch, WebSocket, arrow functions).
- Do NOT confuse browser JS with SimHub's Jint formula engine (ES5.1, `$prop()`, `root[]`). These are completely separate contexts.

## Plugin Development

- Plugins target **.NET Framework 4.8** (not .NET Core / .NET 5+).
- Reference SDK demos at `C:\Program Files (x86)\SimHub\PluginSdk`.
- Use `Init()` for AddProperty/AddAction and WebSocket server startup.
- `DataUpdate()` runs at ~60 Hz — keep it fast (< 1/60s).

## Plugin ↔ Dashboard Communication

- Use **Fleck** (TcpListener-based WebSocket library) for the plugin server. Do NOT use `HttpListener` — it requires Windows administrator privileges.
- Plugin broadcasts telemetry JSON to connected WebSocket clients from `DataUpdate`.
- Dashboard sends action JSON messages via WebSocket when buttons are clicked.
- Plugin serves its own HTML files from the same or adjacent port — no CORS issues.

## iRacing Shared Memory

- Access iRacing data directly via **IRSDKSharper** (NuGet), NOT through SimHub's limited `GameRawData` (which only exposes `drivers01` / `session01`).
- **Primary use case**: Catching incidents and unfair events that happen to the **driving user**. In replay mode, capture the full telemetry stack.
- **ADMIN LIMITATION**: `ResultsPositions[].Incidents` in session YAML stays 0 for other drivers during a **live race** unless the user is **session admin**. During **replay** (`SimMode == "replay"`), all data is available. Always check `WeekendInfo.SimMode`.
- **Incident point type = delta value**: delta 1 = 1x off-track, delta 2 = 2x wall/spin, delta 4 = 4x heavy contact (paved). Dirt racing: heavy contact = 2x. Check `WeekendInfo.Category`.
- **Quick-succession rule**: iRacing promotes lower→higher (2x spin then 4x contact = delta 4 only, not 6). Physics layers still see both events.
- **Physics disambiguates within a type**: 2x could be wall (G-force spike) or spin (yaw rate spike). Cross-reference at the incident frame.
- **Multi-layer incident detection** (all four layers must run concurrently):
  - Layer 1: `PlayerCarMyIncidentCount` — player per-incident deltas. **Delta IS the type**. Physics cross-reference gives exact cause.
  - Layer 2: `CarIdxLapDistPct` velocity → G-force — all-car impact detection, ~0.27s resolution at 16x.
  - Layer 3: `CarIdxTrackSurface` transitions (OnTrack → OffTrack) — all-car off-track detection (1x events).
  - Layer 4: Session YAML `ResultsPositions[].Incidents` — official totals (batched at 16x, decompose via Layers 2+3 + point arithmetic).
- At **16x replay speed**: TickRate stays 60 Hz (real-clock). Player incidents are per-frame exact with type classification. All-car G-force/off-track works at ~0.27s resolution. YAML batches incidents — correlate with Layer 2/3 events and use delta arithmetic to decompose.
- Use `IRacingSdk.UpdateInterval = 1` for maximum polling fidelity.
- The SDK broadcast API supports `RpySrch_NextIncident` / `RpySrch_PrevIncident` — expose as "Jump to Incident" buttons.
- Always call `_irsdk.Stop()` in `End()` to clean up background threads.

## Deployment & Testing

- **No deploy without 100% passing tests.** The `deploy.ps1` script enforces this: build must succeed, `dotnet test` must pass (if test projects exist), and post-deploy scripts in `tests/` must exit 0.
- **Retry-once-then-stop rule**: when a test or build fails, the agent gets **one** additional attempt to fix and rerun. If it fails again, **hard stop** — do not keep iterating. Either halt the deploy entirely (if downstream work depends on it) or skip and move on to the next independent task.
- **Linter checks** (`ReadLints`) on edited files must show 0 new errors before committing or deploying.
- Tests include: `dotnet build` (0 errors), `dotnet test` (0 failures), `tests/*.ps1` scripts (exit 0, `PASS:` lines only), and linter checks on changed files.
- See `.cursor/skills/simsteward-deploy/SKILL.md` for the full deploy workflow and test phase details.

## Community References

- [SimHubPropertyServer](https://github.com/pre-martin/SimHubPropertyServer) — primary architecture reference (TCP server in plugin + action triggering).
- [Fleck](https://github.com/statianzo/Fleck) — WebSocket server library for .NET.
- [IRSDKSharper](https://github.com/mherbold/IRSDKSharper) — high-performance C# iRacing SDK wrapper (session info + telemetry threads, YAML parsing, replay event system).
- [RaceAdmin](https://github.com/GameDotPlay/RaceAdmin) — reference for all-driver incident tracking from iRacing SDK with delta detection and caution logic.

## Detailed reference

For full technical reference, see `.cursor/skills/simhub-dashboard-plugin/SKILL.md` in this repo.

## Memory Bank Context Sync

- SimSteward mirrors its telemetry/state snapshot (`snapshot.json`) plus Markdown helpers (`tasks.md`, `activeContext.md`, `progress.md`) into `memory-bank/`. The path comes from `MEMORY_BANK_PATH` (default: `SimSteward` plugin data directory → `memory-bank/`), so keep that directory writable for the MCP server.
- The Memory Bank snapshot repeats the data from `BuildStateJson` and adds `ProjectMarkers` (task id, description, complexity level, last dashboard action). Commands should prefer `snapshot.json` for incident state.
- Command ordering for lean context:
  - **Core**: `/van` always loads `activeContext.md` first so the assistant resumes the correct ticket with minimal tokens.
  - **Planning/Implementation**: `/plan`, `/build`, and `/creative` load `tasks.md`, `progress.md`, and creative archives only when complexity demands it (Level 2+).
  - **Reflection/Archive**: After finishing work, update `activeContext.md`, `progress.md`, and `snapshot.json` so the next session picks up immediately.
- When status changes or a sub-task completes, write back to `tasks.md`/`progress.md` in the Markdown format the plugin emits (notes in `tasks`, counts in `progress`). These writes keep Memory Bank consistent across commands and sessions.
- Respect the Memory Bank hierarchy: read only what you need and rely on the plugin's background snapshot writes instead of reloading its full context every time.
