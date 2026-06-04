# Grafana Dashboard Improvements — Design Spec

**Date:** 2026-06-03  
**Status:** Implemented  
**Dashboard UID:** `simsteward-claude-code`  
**Grafana URL:** `https://simsteward.grafana.net/d/simsteward-claude-code`

---

## Overview

This spec covers the design and implementation of the **SimSteward — Claude Code** Grafana dashboard. It complements the existing `claude-hook-telemetry` dashboard (hook event rates, MCP service distribution, session summaries) with a cost-and-efficiency focus: per-turn spend tracking, cache ROI, token volume, tool health, and monthly budget tracking.

---

## Dashboards in This Ecosystem

### 1. `claude-hook-telemetry` — "Claude Code — Hook Telemetry" (existing)

**Purpose:** Raw observability of Claude Code hook events — tool call rates, MCP service usage, retry detection, session summaries.

**Log streams:**
- `app="claude-dev-logging"` — all hook events (pre/post-tool-use, session-start/end, subagent events)
- `app="claude-token-metrics"` — per-turn cost and token data (stop hook only)

**Key panels:** Session stats (cost, tokens, turns), token distribution stacked area, cost by type, model/effort breakdown, avg duration by tool, hook event rate, MCP component donut, session summary logs.

---

### 2. `simsteward-claude-code` — "SimSteward — Claude Code" (this dashboard)

**Purpose:** Cost efficiency, budget tracking, cache ROI, and session audit. Optimized for the question "how much am I spending and is caching working?"

**Log streams:**
- `app="claude-token-metrics"` (primary) — one entry per Claude turn, `event="claude_turn_metrics"`. Fields at **top level** (not nested under `fields.*`). Use `| json | unwrap field_name` directly.
- `app="claude-dev-logging"` (secondary) — hook events for tool health panels and session audit.

---

## Panel Layout

### Row 1 — Cost Summary (4 stat panels)

| Panel | Query | Unit | Notes |
|-------|-------|------|-------|
| Total Cost | `sum(sum_over_time(... unwrap cost_usd [$__range]))` | currencyUSD | Blue background |
| Cache Savings | `sum(sum_over_time(... unwrap cache_savings_usd [$__range]))` | currencyUSD | Green background |
| Cache Hit Rate | `sum(cache_read_tokens) / (sum(input_tokens) + sum(cache_read_tokens) + sum(cache_creation_tokens))` | percentunit | Thresholds: red <30%, orange <60%, green ≥60% |
| Tool Errors | `count_over_time(level="ERROR" [$__range])` | short | Thresholds: green=0, orange≥1, red≥5 |

### Row 2 — Monthly Limit Tracker (1 stat panel, full width)

Tracks spend as a percentage of the $100/month Max plan limit. Query divides `sum(cost_usd [$__range]) / 100` so the result is a 0–1 ratio displayed as a percentage. Thresholds: green <60%, orange 60–85%, red ≥85%.

**Usage:** Set the dashboard time picker to `now-30d` for accurate monthly tracking. The stat always reflects the selected time range.

### Row 3 — Cost Trend (stacked area timeseries, full width)

Four series stacked, `fillOpacity=70`, `stacking: { mode: "normal" }`:
- Output Cost — `#F7768E` (red/coral) — dominant: output tokens are 5× input price on Sonnet
- Input Cost — `#7AA2F7` (blue)
- Cache Write Cost — `#E0AF68` (amber)
- Cache Read Cost — `#9ECE6A` (green, cheapest)

Uses `[$__interval]` for smooth per-interval bucketing.

### Row 4 — Token Volume (stacked area timeseries, full width)

Same stacking pattern as Row 3, token counts instead of costs:
- Output Tokens — `#FF9E64` (orange)
- Input Tokens — `#7AA2F7` (blue)
- Cache Write Tokens — `#E0AF68` (amber)
- Cache Read Tokens — `#9ECE6A` (green)

### Row 5 — Tool Health (bargauge + 4 stat panels)

- **Slowest Tools** (w=16, horizontal bargauge, gradient color) — `avg by (tool_name)` of `duration_ms` from `post-tool-use` hooks. Shows real-world MCP tool latency.
- **Tool Errors** — count of ERROR-level hook events
- **Retries** — count of `is_retry="true"` tool events
- **Avg Tool Time / Turn** — mean `tool_time_ms` per turn (aggregated by the stop hook)
- **Total Tool Calls** — count of `post-tool-use` events

### Row 6 — Session Audit (2 logs panels)

- **Session Summaries** — `event="claude_session_summary"` from `component="lifecycle"`, prettified JSON, sorted descending. Each entry contains: session duration, model, effort, assistant turns, tool use count, full token/cost breakdown, compaction count.
- **Raw Logs** — full `claude-dev-logging` stream for ad-hoc investigation.

---

## Key Design Decisions

### 1. Fields at top level — no `fields.*` nesting

`app="claude-token-metrics"` and `app="claude-dev-logging"` both emit fields at the **top level** of the JSON log line. The correct LogQL pattern is:

```logql
{app="claude-token-metrics", env=~"$env"} | json | unwrap cost_usd
```

NOT `| json field="fields.cost_usd"` (which is the pattern for `app="sim-steward"` plugin logs, where data is nested under `fields.*`).

### 2. Cache hit rate denominator

The correct denominator for cache hit rate is **all input-side tokens**:
```
total_cache_read_tokens / (total_input_tokens + total_cache_read_tokens + total_cache_creation_tokens)
```
Using only `total_input_tokens` as denominator produces values > 100% because in a heavily cached session, `total_input_tokens` (the raw prompt-only count) is tiny relative to the cache tokens being served.

### 3. Monthly gauge → stat with $__range

A hard-coded `[30d]` range in a Loki `sum_over_time` query is rejected by Loki when the dashboard time window is shorter than 30d. Solution: use `[$__range]` and instruct users to set the time picker to `now-30d` for monthly budget tracking. This avoids the "No data" error while keeping the query correct for any time window.

### 4. Stacked area configuration

Grafana timeseries stacking requires both:
- `custom.stacking: { mode: "normal", group: "A" }` on the `defaults` fieldConfig
- `fillOpacity: 70` (integer 0–100 in `timeseries`, not 0–1)

Series color overrides use `byName` matchers keyed to the `legendFormat` string of each target.

### 5. Monthly spend normalization

The monthly stat query divides `sum(cost_usd)` by `100` (the plan monthly limit) so Grafana receives a 0–1 value that maps to 0–100% with `unit: "percentunit"` and `max: 1`. No Grafana transformations required. Thresholds are set at `0.60` and `0.85` on the absolute scale.

### 6. Visual review via get_panel_image

Visual QA used `mcp__MCP_DOCKER__get_panel_image` (Grafana Image Renderer, server-side, no browser auth required) rather than Playwright browser navigation. This caught two bugs on the first render:
- Cache hit rate showing 283,783% (wrong denominator — fixed)
- Monthly gauge showing "No data" (Loki rejected `[30d]` in 24h window — fixed by switching to stat + `[$__range]`)

---

## Template Variable

```json
{
  "name": "env",
  "type": "custom",
  "query": "dev,local,production",
  "current": "dev",
  "allValue": ".*",
  "includeAll": true
}
```

All queries use `env=~"$env"` (regex match, not `env="$env"`) to support the `allValue=".*"` wildcard.

---

## File Location

Dashboard JSON provisioned at:
```
observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json
```

This file contains the dashboard definition only (no `meta` block). It can be used for Grafana provisioning or as a backup for `update_dashboard` re-imports.
