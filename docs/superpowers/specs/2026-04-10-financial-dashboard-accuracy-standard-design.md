# Financial Dashboard Accuracy Standard — Design Spec

**Date:** 2026-04-10  
**Approach:** B — Define canonical metrics standard first, then patch all dashboards to conform.  
**Scope:** All 6 Grafana dashboards with financial or token panels.

---

## Problem Statement

12 inconsistencies found across dashboards. Critical accuracy issues:

1. Cache savings hardcoded at `$2.70/M` — correct for Sonnet-4, wrong for Opus/Haiku
2. `output_tokens` vs `total_output_tokens` field name mismatch (sentinel vs Claude API)
3. `__error__=""` filter missing in Session Overview — parse failures included in session cost
4. `$__interval` vs `$__range` mixed in rate expressions — same metric, different numbers
5. Token-to-dollar ratio panels missing from all dashboards (explicitly requested)
6. No unified session cost definition across dashboards

---

## Section 1: Canonical Metrics Standard

### 1A — Canonical Filter Chain

Every panel querying `claude-token-metrics` MUST use this base filter, in this order:

```logql
{app="claude-token-metrics"}
| json
| __error__=""
| model=~"$model"
| project=~"$project"
| effort=~"$effort"
```

No exceptions. Panels missing `__error__=""` include parse failures in their numbers.

### 1B — Canonical Field Names

| Concept | Canonical field | Notes |
|---|---|---|
| API cost | `cost_usd` | Per-turn, in USD |
| Input tokens | `total_input_tokens` | Session cumulative at log time |
| Output tokens | `total_output_tokens` | Session cumulative |
| Cache read tokens | `total_cache_read_tokens` | Served from cache, cheap |
| Cache creation tokens | `total_cache_creation_tokens` | Written to cache, full price |
| Total tokens | `total_input_tokens + total_output_tokens` | Cache tokens excluded unless explicitly stated |
| Session ID | `session_id` | Only used in session-scoped dashboards |

**Sentinel exception:** Sentinel events use `output_tokens` (no `total_` prefix) — this is correct for those log events and must NOT be renamed. Panels displaying sentinel tokens alongside Claude API tokens MUST label source explicitly: "Sentinel (Ollama)" vs "Claude API".

### 1C — Canonical Rate Multipliers

| Display unit | Multiplier | Query variable | Notes |
|---|---|---|---|
| per second | ×1 | `$__interval` | Time-series panels |
| per minute | ×60 | `$__interval` | Time-series panels |
| per hour | ×3600 | `$__interval` | Time-series panels |
| per day | ×86400 | `$__interval` | Time-series panels |
| projected monthly | ×2592000 | `$__range` | Instant/scalar panels only |

**Rule:** `$__interval` for time-series panels (changes with zoom). `$__range` for instant scalar projections (fixed to selected window). Never mix.

### 1D — Canonical Ratio Definitions

Five ratios appear across multiple dashboards. Every panel showing one of these MUST use exactly this formula:

| Ratio name | Formula | Unit | Direction |
|---|---|---|---|
| Subsidy multiplier | `sum(cost_usd) / (plan_monthly / 30 * billing_days)` | `×` | Higher = more VC subsidy |
| Plan utilization % | `(sum(cost_usd) / (plan_monthly / 30 * billing_days)) * 100` | `%` | 100% = break even |
| Output tokens per dollar | `sum(total_output_tokens) / sum(cost_usd)` | `tok/$` | Higher = more efficient |
| Total tokens per dollar | `sum(total_input_tokens + total_output_tokens) / sum(cost_usd)` | `tok/$` | Higher = more efficient |
| Cache efficiency % | `sum(total_cache_read_tokens) / (sum(total_cache_read_tokens) + sum(total_cache_creation_tokens)) * 100` | `%` | Higher = better cache reuse |

**Cache savings is NEVER expressed in dollars.** Only as tokens saved (cache read token count). The `$2.70/M` hardcoded rate is removed entirely.

### 1E — Canonical Color Conventions

| Concept | Color | Hex |
|---|---|---|
| API / compute cost | Purple | `#c77dff` |
| Plan cost / budget line | Pink | `#f72585` |
| VC subsidy / savings | Teal | `#00d4aa` |
| Warning / approaching limit | Yellow | `#ffd166` |
| Output tokens | Orange | `#f4a261` |
| Input tokens | Blue | `#4895ef` |
| Cache read tokens | Teal | `#00d4aa` |
| Cache creation tokens | Purple | `#c77dff` |
| Sentinel / Ollama tokens | Grey | `#8d99ae` |

---

## Section 2: Dashboard-by-Dashboard Conformance Plan

### `claude-subscription-economics` — Economics / VC Subsidy

**Accuracy fixes:**
- Confirm `__error__=""` present in all 6 stat panel queries (currently appears present)
- Replace "You Pay (¢ per $1 API)" formula with canonical Plan Utilization % definition
- Verify "Subsidy Multiplier" uses canonical formula exactly

**New panels — add Row: Raw Token Consumption (between rows 2 and 3):**
- `Total Tokens Consumed` stat — `sum(total_input_tokens + total_output_tokens)` over range
- `Output Tokens per $1 of Plan` stat — canonical output tokens per dollar ratio
- `Total Tokens per $1 of Plan` stat — canonical total tokens per dollar ratio
- `Token Velocity` timeseries — tokens/hour over time (`rate(...) * 3600`), shows usage spikes beyond cost
- `Input vs Output Token Split` stacked bar — shows output token dominance (they cost 3-5× more)

### `claude-token-cost` — Token & Cost Deep-Dive

**Accuracy fixes:**
- **Remove** `Cache Savings Estimate ($/day)` panel — replace with `Cache Read Tokens Saved (daily)` timeseries (tokens, not dollars, using `total_cache_read_tokens`)
- `Projected Monthly`: switch from `$__interval` to `$__range` in rate expression
- `Total Tokens/min` and `Output Tokens/min`: confirm use `$__interval`
- Add `__error__=""` to 3 queries found missing it
- `Output Tokens / Dollar`: verify uses `sum/sum` not `avg/avg`

**New panels:**
- `Output Tokens per $1` stat — canonical definition, matches subscription-economics
- `Cache Efficiency %` gauge — canonical cache efficiency ratio

### `claude-intelligence` — LLM Quality / Session Analysis

**Accuracy fixes:**
- `Avg $/hour` and `Avg $/minute`: switch to canonical `$__interval` multiplier
- `Plan ROI`: verify formula matches canonical subsidy multiplier (check for inversion)
- `Token Usage by Type` timeseries: confirm field names are `total_*`

**New panels:**
- `Output Tokens per $1` stat — makes comparable to token-cost dashboard
- `Cache Efficiency %` gauge — present in cache-context but missing here

### `claude-code-overview` — Session View

**Accuracy fixes:**
- `Session Cost`: keep session_id-scoped (correct for this dashboard); add display label "Full Session Cost" to make scope explicit
- Add `__error__=""` to session cost query (currently missing)
- `Avg $/hour` and `Avg $/minute`: confirm canonical `$__interval` multipliers

**No new panels** — session-scoped view; ratio panels don't make sense at single-session granularity.

### `claude-cache-context` — Cache & Context Health

**Accuracy fixes:**
- `Cache Reuse Ratio`: replace current formula with canonical Cache Efficiency % definition
- Per-turn panels: add "per-turn" to panel titles to distinguish from session-aggregate panels in other dashboards
- Confirm canonical field names `total_cache_read_tokens`, `total_cache_creation_tokens` throughout

**No cost panels to add** — intentionally cost-free; token/dollar view lives in token-cost.

### `simsteward-log-sentinel` — Sentinel Pipeline

**Accuracy fixes:**
- All token panels: add "Sentinel (Ollama)" to panel titles or descriptions
- Add panel descriptions noting `output_tokens` is not comparable to `total_output_tokens` from Claude API
- No `cost_usd` additions — Sentinel uses local Ollama, not billed

---

## Section 3: Out of Scope

The following are explicitly excluded from this spec:

- Visual redesign / layout changes to any dashboard (separate spec)
- Grafana alert rule changes (separate review per Grafana Alert Covenant)
- Adding new log fields to the plugin or hook — all work uses fields that already exist in `claude-token-metrics`
- Sentinel pricing / cost tracking — Ollama is not billed

---

## Implementation Order

1. Write and commit this spec
2. Implement canonical standard in `claude-token-cost` (most panels, good test bed)
3. Implement in `claude-subscription-economics` (adds new token panels)
4. Implement in `claude-intelligence` (smaller changes)
5. Implement in `claude-code-overview` (smallest changes)
6. Implement in `claude-cache-context` (field name and ratio fix only)
7. Implement in `simsteward-log-sentinel` (label-only changes)
