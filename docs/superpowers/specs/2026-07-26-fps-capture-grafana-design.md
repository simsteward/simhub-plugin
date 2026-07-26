# FPS Capture → Grafana Design

**Date:** 2026-07-26
**Status:** Approved — pending implementation plan
**Environment:** Dev machine (WIN-PC), single host

## Goal

Capture real frame-rate data — both *game FPS* (how fast an app hands frames to the GPU driver) and *display FPS* (how fast frames actually land on the monitor) — for a curated set of processes, and ship it into the same Grafana Cloud pipeline that already carries `windows_cpu_*` / `windows_gpu_*` metrics, so frame-rate history is available for any session automatically, not just ones where a manual capture was remembered to be started.

This closes the gap identified during the 2026-07-25 overnight-session investigation: Windows exposes no standard performance counter for frame rate (only GPU engine *utilization*, which `windows_exporter`'s `gpu` collector already provides), so diagnosing in-race blips and a choppy second-monitor pit-wall display required guessing from indirect signals. Frame-rate history removes the guesswork.

## Non-goals

- Not a replacement for `scripts/start-fps-capture.ps1` (the existing manual, full-fidelity PresentMon capture script). That script remains for deep, ad hoc, per-frame CSV analysis of a specific session. This design adds a lightweight, always-on, *aggregated* stream for Grafana history — the two serve different purposes and both stay.
- Not per-physical-monitor FPS. PresentMon reports per-process/per-swapchain, not per-display. A process pinned to one monitor (e.g. iRacing on the ultrawide) is a reasonable proxy, but a single browser window is only a clean proxy for the pit wall if that browser window is dedicated to the dashboard (see Risks).
- Not a general-purpose GPU/game telemetry platform. Scope is the two processes below; extending the allowlist later is a config change, not a redesign.

## Architecture

```
PresentMon.exe (ETW capture, streaming CSV, --output_stdout)
  │  already installed + SHA-256 verified at
  │  %USERPROFILE%\Tools\PresentMon\PresentMon.exe
  ▼
fps-exporter (new, Node.js, scripts/fps-exporter/)
  │  spawns PresentMon as a child process, scoped to an allowlist:
  │    --process_name iRacingSim64DX11.exe --process_name chrome.exe
  │  parses each streamed CSV row
  │  aggregates a rolling 1s window per process into 3 metric families
  ▼
GET http://127.0.0.1:9101/metrics   (Prometheus text exposition format)
  ▼
Alloy (existing, C:\Program Files\GrafanaLabs\Alloy\config.alloy)
  │  ADD: one prometheus.scrape block targeting 127.0.0.1:9101
  │  reuses the EXISTING prometheus.remote_write.grafana_cloud component
  ▼
Grafana Cloud Mimir → Grafana dashboard panels (Game FPS vs Display FPS)
```

Both `fps-exporter` and Alloy run as native Windows Services on the host — see **Alternatives considered** for why this isn't embedded in SimHub and isn't Dockerized.

## Component: Alloy scrape target

- Scrape interval for this target specifically: **5s**, independent of whatever interval other Alloy targets use (the local stack's `windows_exporter`/self-monitoring targets default to 15s per `observability/local/prometheus.yml`, which is too coarse for a fast-changing quantity like FPS — at 15s you'd only ever see one 5s-window snapshot out of every three, an under-sampled/aliased view). Alloy's `prometheus.scrape` component supports a per-target `scrape_interval` override, so this doesn't affect other targets' cadence or cost.
- At 5s × 6 series, ingestion volume is negligible regardless.

## Component: Grafana dashboard panels

Two new panels, same visual style as the existing "GPU engine utilization by type" / "VRAM usage" panels (dark theme, per-series legend, time range selector):

1. **Game vs. display FPS** — line panel, one line per `process` per metric (`game_fps` solid, `display_fps` dashed, or two panels side by side if overlapping series reads as cluttered). The useful signal is where the two lines for the same process diverge.
2. **Dropped frames** — smaller panel, `rate(frames_dropped_total[$__interval])` per process, to correlate spikes against the FPS panel above.

Exact panel JSON/placement is an implementation-plan-level detail, not a design-level one — this repo currently has no dashboard provisioning files checked in (`docs/GRAFANA-LOGGING.md` notes "no provisioned dashboard JSON files in the repo at the moment"), so these panels get added directly in the Grafana Cloud UI, consistent with how the existing GPU/VRAM panels were added.

## Component: `fps-exporter`

Zero new npm dependencies (matches this repo's existing lean `devDependencies`) — Node's built-in `http` and `child_process` modules are sufficient for the CSV parsing and the metrics endpoint.

### Process scope (allowlist)

| Process | Represents |
|---|---|
| `iRacingSim64DX11.exe` | The game itself — main-monitor racing |
| `chrome.exe` | The pit-wall dashboard, currently displayed as a browser window pointed at SimHub's local web server |

### Metrics

| Metric | Type | Derivation |
|---|---|---|
| `game_fps{process}` | gauge | `1000 / avg(MsBetweenPresents)` over a trailing 5s rolling window — how fast the app is submitting frames |
| `display_fps{process}` | gauge | `1000 / avg(MsBetweenDisplayChange)` over the same trailing 5s window, computed only from rows where the frame was actually displayed (not dropped) — how fast frames actually reach the screen |
| `frames_dropped_total{process}` | counter | Frames the app rendered that never reached the screen |

When `display_fps` diverges from `game_fps` for a process, that's the signature of a display/compositor-side stutter rather than the app itself hitching — this is the specific diagnostic the two-metric split is for.

Cardinality: 2 processes × 3 metrics = 6 series. Negligible.

When a tracked process isn't running, its metrics simply stop updating (no fabricated `0`) — Grafana renders a genuine gap. The gauge is recomputed continuously from the rolling window, independent of when it's actually polled, so it reads correctly whether polled by Alloy's periodic scrape or an ad hoc `curl`.

### Resilience

- If the PresentMon child process exits (driver hiccup, etc.), `fps-exporter` detects it and respawns with backoff (5s → capped at 60s), logging each restart. This is continuous retry, not the deploy script's retry-once-then-stop rule — a long-lived monitor should keep trying, not give up.
- Logs to `%LOCALAPPDATA%\FpsExporter\fps-exporter.log`, mirroring where SimHub's own plugin logs live.
- If the `/metrics` HTTP listener fails to bind (port conflict), log clearly and exit non-zero so the Windows Service Manager's restart policy takes over.

### Deployment

- Installed as a Windows Service via NSSM (nssm.cc — same category of lightweight service wrapper Alloy itself uses), running under `LocalSystem`. This satisfies PresentMon's admin/ETW requirement once, at install time — no manual elevation ever needed at capture time, unlike `scripts/start-fps-capture.ps1` which requires an elevated interactive shell each run.
- Install script: `scripts/fps-exporter/install-service.ps1` (downloads/verifies NSSM the same way `PresentMon.exe` was downloaded and SHA-256 verified, registers the service, points it at `node fps-exporter.mjs`).

### Alloy config change

One additive `prometheus.scrape` block in `config.alloy` targeting `127.0.0.1:9101`, wired to the existing `prometheus.remote_write.grafana_cloud` component. **This file (`C:\Program Files\GrafanaLabs\Alloy\config.alloy`) is ACL-restricted to SYSTEM/Administrators** — confirmed during this design's investigation (a non-elevated Read/Get-Content both failed with access denied). The edit must be made from an elevated session; it cannot be done from a standard Claude Code session on this machine. The implementation plan should call this out as a manual/elevated step, not something to script unattended.

## Alternatives considered

**Embed capture inside the SimHub plugin (C#).** Rejected. The 2026-07-25 incident is direct counter-evidence: SimHub was closed at 10:20 PM but the user kept racing (and kept seeing issues) until ~12:30 AM. Tying FPS capture to SimHub's own lifecycle would reproduce that exact blind spot. Additionally: PresentMon needs admin rights and SimHub doesn't normally run elevated (forcing it to would be an invasive change for one diagnostic feature), and CLAUDE.md already steers this codebase away from ad hoc HTTP listeners in-process (`Use Fleck for WebSocket... Do NOT use HttpListener`), which a Prometheus `/metrics` GET endpoint would fight against. Embedding also adds a new process-spawning/ETW-adjacent subsystem into the same process that already showed real instability last session (4 plugin restarts) — added blast radius for a diagnostic feature, not a core one.

**Run in Docker.** Rejected — hard constraint, not a preference. PresentMon reads GPU present events via ETW tied to the interactive desktop session; containers (including the WSL2-backed Linux containers Docker Desktop uses on this machine) are isolated from that session and can't see host-level GPU/display events. This project already has the right precedent: Alloy itself runs as a **native** Windows Service (not in the `observability/local` Docker stack) specifically because it needs host-level Windows performance counters, while Grafana/Prometheus/Loki — which only store and query data — run in Docker. `fps-exporter` needs even more direct host/session access than Alloy, so it belongs in the same "runs native" bucket.

**Fold FPS into `windows_exporter`'s textfile collector instead of a dedicated HTTP endpoint.** Considered, not chosen. Would avoid one new port/service, but couples a fast-changing custom gauge into a mechanism designed for periodic snapshots, and entangles our metric with `windows_exporter`'s own collector config. A dedicated small exporter keeps capture+expose and scrape+ship cleanly separated, matching how Alloy already treats `windows_exporter` as one independent scrape target among others.

**Use an existing PresentMon→Prometheus exporter.** Rejected — none found. Checked Alloy's own component library (no DirectX/present-tracking component exists), `windows_exporter`'s `gpu` collector (confirmed via its GitHub docs/issues: built entirely on Windows perf counters, which don't expose frame rate — only GPU engine utilization %), and searched for a standalone maintained exporter (none found). Frame-present events only exist inside each app's DXGI swapchain, visible only via ETW tracing into the `Microsoft-Windows-DXGI`/`D3D9` providers — exactly what PresentMon does and what perf-counter-based tools structurally cannot.

## Testing

- `curl http://127.0.0.1:9101/metrics` returns valid Prometheus text format, including with zero tracked processes running.
- Launch iRacing (or any allow-listed app) → `game_fps`/`display_fps` populate within a few seconds; close it → series gaps out cleanly (no fabricated 0).
- After the Alloy config change: confirm the new series lands in Grafana Cloud (same `list_prometheus_metric_names` / `query_prometheus` check used earlier in this investigation to confirm `windows_gpu_*` existed).
- Kill the PresentMon child process manually (Task Manager) and confirm `fps-exporter` detects the exit and respawns it within the backoff window without the service itself crashing.

## Risks / open verification items

- **Pit-wall process ambiguity.** `chrome.exe`'s FPS reflects whatever that process is rendering — if multiple Chrome windows/tabs are open and actively presenting (not just idle), the metric blends them. Recommend keeping the pit-wall dashboard in its own dedicated browser window (or profile) regardless of this feature, both for cleaner metrics and as generally better practice.

**Resolved during implementation planning:**

- **PresentMon 2.5.1 CSV column names** — confirmed against PresentMon's own `README-ConsoleApplication.md` (not the earlier smoke test, which never produced output since it wasn't elevated). The relevant columns are `Application`, `MsBetweenPresents`, `MsBetweenDisplayChange`, and `DisplayedTime` (value `NA` — not a boolean `Dropped` column — indicates a frame that was rendered but never displayed). The implementation plan's parser is written against these confirmed names.
- **Service-install mechanism** — NSSM via `winget install NSSM.NSSM`, confirmed available on this machine (`winget --version` → 1.29.280, package present in `winget search NSSM`). Keeps `fps-exporter` itself free of the `node-windows` runtime dependency that a self-install approach would have required.
