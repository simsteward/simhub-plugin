# SimHub Development Rules

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
- **CrewChief is the primary reference** for iRacing SDK property semantics and detection patterns (incidents, flags, spotter, damage). When designing a new detector or interpreting an SDK field, consult CrewChiefV4 (github.com/mrbelowski/CrewChiefV4) before reverse-engineering from scratch. See `docs/IRACING-DATA-AVAILABILITY.md` appendix for the module map.

## Credentials
- **Always read `.env` before asking the user for any credential or token.** It is not gitignored from Claude's perspective and contains live keys.
- **Sentry CLI** is installed at `C:\Users\winth\.sentry\bin\sentry.exe`, authenticated as `billing@simsteward.com` via `~/.sentryclirc`. Claude and agents can invoke it directly.
- Run `pnpm dlx sentry --help` to see all available Sentry CLI commands.

## Deployment & Testing
- Deploy via `deploy.ps1`. MUST pass build (0 errs), `dotnet test`, and `tests/*.ps1`.
- `deploy.ps1` auto-kills SimHub before copying DLLs and re-launches it on success — no manual SimHub steps.
- Deploy via `deploy.ps1`.
- **Retry-once-then-stop** rule. Hard stop after 2 fails.
- Lints: 0 new errors.

## Memory Bank
- Memory Bank is personal vibe-coding. OUT OF SCOPE. Do not implement or reference.

## Minimal Output
Read and strictly follow the output rules defined in `docs/RULES-MinimalOutput.md`.

---

## Logging — Verbose, Intentional, 100% Coverage

**Debugging ability is king.** Every meaningful event must be observable in Grafana without a debugger or repro session.

### What always requires a structured log

- **Every `DispatchAction` branch**: `action_dispatched` (before) + `action_result` (after) — fields: `action`, `arg`, `correlation_id`, success/error, session context via `MergeSessionAndRoutingFields()`
- **Every dashboard button click**: `{ action:"log", event:"dashboard_ui_event", element_id, event_type:"click", message }` before WS send
- **Every state machine transition**: previous state, new state, reason
- **Every long-running operation (> ~2s)**: start log + periodic Loki heartbeat (not WS-only) + completion log with `duration_ms`, counts, `completion_reason`
- **Every iRacing SDK playback command**: log intent; log telemetry read-back confirmation or `*_speed_lost` WARN on mismatch
- **Every detection** (incident, flag, anomaly): fingerprint, `car_idx`, `session_time`, `replay_frame`, `lap`
- **Every prerequisite guard failure**: `check`, `actual`, `required` — WARN level
- **Every `catch` block with meaningful context**: never silently swallow exceptions

### Context fields — always injected via `MergeSessionAndRoutingFields()`

`subsession_id`, `parent_session_id`, `session_num`, `track_display_name`, `lap`, `session_yaml_fingerprint_sha256_16`

When a replay index build is active: also `replay_total_frames`, `replay_session_count` (automatic).

### Log levels

`INFO` — completed normally. `WARN` — unexpected but continued. `ERROR` — operation failed, degraded state. `DEBUG` — high-frequency checks, gated on `_logger.IsDebugMode`.

### Anti-patterns (never do)

- Log in `DataUpdate()` unconditionally (60 Hz = millions of lines)
- Silent `catch { }` with no log
- WS-only heartbeats for long-running ops (WS is ephemeral)
- Live-reading `ReplayFrameNumEnd` inside a sweep — use the snapshot taken at frame 0

**Full spec:** [docs/RULES-ActionCoverage.md](docs/RULES-ActionCoverage.md)

---

## Logging — env label is the filter

All Loki entries carry the `env` stream label set from `SIMSTEWARD_LOG_ENV` (`local` or `production`). Filter queries by `env` to scope to the right environment:

```
{app="sim-steward", env="production"}
{app="claude-dev-logging", env="local"}
```

Streams in active use:
- `app="sim-steward"` — C# plugin, dashboard, deploy markers (see `docs/GRAFANA-LOGGING.md` for the event taxonomy)
- `app="claude-dev-logging"` — Claude Code hook telemetry (`scripts/hooks/loki-log.js`)
- `app="claude-token-metrics"` — per-turn cost metrics, **main session only** (one entry per Claude response). Undercounts subagents — see below.

**Authoritative Claude Code cost/tokens = native OpenTelemetry, not the Loki hook stream.** The hook parses only the parent transcript; subagents run as separate transcripts and are missed. `CLAUDE_CODE_ENABLE_TELEMETRY=1` (`.claude/settings.json` → `env`) ships `claude_code.cost.usage` / `claude_code.token.usage` (Prometheus/Mimir, `grafanacloud-prom`) with a `query_source` label so subagent usage is counted and totals match `/status`. The OTel collector is in `observability/local`. The **5h-session / weekly subscription limit %** from `/status` is **not** exposed by any feed — dashboards track cost/volume, not plan limits. Backfill historical subagents with `scripts/backfill-subagent-usage.js`. Full detail: `docs/GRAFANA-LOGGING.md` → *Claude Code native telemetry*.

There is no global alert covenant or fixed alert-domain framework. Alert rules are managed individually in Grafana Cloud — review the specific rule(s) impacted by a change rather than referencing a domain table.
