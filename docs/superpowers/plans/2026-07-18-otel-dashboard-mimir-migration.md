# Claude Code Dashboard — Loki → Mimir Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate the "All Sources" cost/token panels in the SimSteward Claude Code Grafana dashboard from the Loki hook-pipeline (LogQL) to the now-confirmed-live Mimir OTel metrics (PromQL), and redeploy.

**Architecture:** Edit `observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json` in place — swap each target's `datasource`/`expr` from `loki`/LogQL to `grafanacloud-prom`/PromQL against the two confirmed-live metrics. The "Legacy — Hook Pipeline" and "Tool Health" sections are explicitly out of scope (see Global Constraints). Redeploy via the existing `scripts/deploy-dashboard.mjs`.

**Tech Stack:** Grafana dashboard JSON (schemaVersion 38), PromQL against Grafana Cloud Mimir (`grafanacloud-prom` datasource), Node.js deploy script.

## Global Constraints

- **Verified facts only, no re-guessing.** Every metric name, label name, and label value below was confirmed live against Grafana Cloud Mimir in this session (via the `grafanacloud-prom` datasource proxy, using the `CURSOR_ELEVATED_GRAFANA_TOKEN` service-account token from `.env`, which has Admin role). Do not invent a metric/label name that isn't in this doc — if a new panel needs one not listed here, query Mimir first via the same proxy pattern shown in Task 1.
- **Confirmed metric names:** `claude_code_cost_usage_USD_total` and `claude_code_token_usage_tokens_total`. Both use the tolerant regex form `{__name__=~"claude_code_cost_usage.*", ...}` / `{__name__=~"claude_code_token_usage.*", ...}` in queries — this is the existing project convention (see `docs/GRAFANA-LOGGING.md` line 111: "PromQL uses `{__name__=~\"claude_code_cost_usage.*\"}` to tolerate exporter unit suffixes"), not a new decision.
- **Confirmed labels on `claude_code_cost_usage_USD_total`:** `model`, `query_source` (values: `main`, `subagent`, `auxiliary` — confirmed via `https://code.claude.com/docs/en/monitoring-usage`: "Category of the subsystem that issued the request"), `env`, `session_id`, `effort`, plus machine/user metadata (`host_arch`, `os_type`, `os_version`, `service_name`, `service_version`, `terminal_type`, `organization_id`, `user_account_id`, `user_account_uuid`, `user_email`, `user_id`).
- **Confirmed labels on `claude_code_token_usage_tokens_total`:** everything above, plus `type` (values confirmed live: `input`, `output`, `cacheRead`, `cacheCreation` — camelCase, NOT the old Loki panels' snake_case `cache_read`/`cache_write`), and `agent_name`, `skill_name`, `plugin_name`, `marketplace_name` (present on this metric only).
- **Datasource UID is `grafanacloud-prom`** — confirmed live (the proxy query `/api/datasources/proxy/uid/grafanacloud-prom/api/v1/query` returned 200 with real data in this session).
- **`env` is always `"dev"` for these metrics on this machine** (set by the collector's `attributes/env` processor from `SIMSTEWARD_LOG_ENV=dev` in `.env`) — `local`/`production` are template-variable options that exist for other panels on this dashboard, not for these. The default `$env` selection (`dev`) already covers this; no template variable change needed.
- **No historical data before 2026-07-18 in Mimir.** Confirmed via a 60-day range query — every day before today returned zero. The June 22–26 OTel work never actually landed queryable data (the two logged export attempts on 2026-06-26 both hit a 429 rate-limit and were dropped; the collector container was then down for ~3 weeks until this session restarted it). This is expected, not a bug — new panels will show a real, valid cliff before today's date.
- **Counter-reset behavior:** the collector's `deltatocumulative` processor holds its cumulative-counter state in memory and resets to 0 on every collector restart (verified directly in this session — values dropped after `pnpm obs:down && pnpm obs:up`). Every PromQL query in this plan therefore uses `increase(...[range])`, never a bare instant `sum(metric)` — `increase()` is the standard Prometheus idiom that correctly handles counter resets within the query window and matches what the existing LogQL `sum_over_time(...unwrap...[range])` queries already compute (a range-summed delta, not a raw last-value).
- **Scope is the "All Sources" row (8 panels) + the "API-equivalent Spend" stat only.** The "Legacy — Hook Pipeline (Loki)" row (Total Cost cross-check, Cache Savings, Cache Hit Rate, Tool Errors) and "Tool Health" row stay on Loki — `Cache Savings` and per-tool `duration_ms`/`is_retry`/`tool_time_ms` have no OTel equivalent, and the cross-check row's entire purpose is to compare against a second, independent source. Do not touch these sections. The "Session Audit" row (raw logs panels) also stays on Loki — it's a log view, not a metric.

---

### Task 1: Migrate the 4 stat panels (Total Cost, Total Tokens, Subagent Share of Cost, Subagent Cost)

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json:39-73` (the 4 stat panel objects in the "All Sources" row)

**Interfaces:**
- Produces: 4 panels now querying `grafanacloud-prom` instead of `grafanacloud-logs`, consumed visually — no other panel or script depends on these panel objects' internal shape.

- [ ] **Step 1: Rewrite "Total Cost (all sources)" panel** (currently lines 38-46)

Replace the `datasource` and single `targets` entry:

```json
{
  "type": "stat", "title": "Total Cost (all sources)",
  "description": "Sum of claude_code.cost.usage across main sessions, subagents, and auxiliary requests. OTel-native (Mimir) — replaces the Loki hook-pipeline estimate.",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 4, "w": 6, "x": 0, "y": 1},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "decimals": 2, "color": {"fixedColor": "#7AA2F7", "mode": "fixed"}, "noValue": "$0.00"}, "overrides": []},
  "options": {"colorMode": "background", "graphMode": "none", "justifyMode": "center", "textMode": "auto", "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": false}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum(increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__range]))", "legendFormat": "Total Cost", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 2: Rewrite "Total Tokens (all sources)" panel** (currently lines 47-55)

```json
{
  "type": "stat", "title": "Total Tokens (all sources)",
  "description": "Total tokens (input+output+cacheRead+cacheCreation) across main sessions, subagents, and auxiliary requests. OTel-native (Mimir).",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 4, "w": 6, "x": 6, "y": 1},
  "fieldConfig": {"defaults": {"unit": "short", "decimals": 1, "color": {"fixedColor": "#BB9AF7", "mode": "fixed"}, "noValue": "0"}, "overrides": []},
  "options": {"colorMode": "background", "graphMode": "none", "justifyMode": "center", "textMode": "auto", "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": false}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum(increase({__name__=~\"claude_code_token_usage.*\", env=~\"$env\"}[$__range]))", "legendFormat": "Total Tokens", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 3: Rewrite "Subagent Share of Cost" panel** (currently lines 56-64)

```json
{
  "type": "stat", "title": "Subagent Share of Cost",
  "description": "Fraction of total cost where query_source=subagent. Excludes query_source=auxiliary from the numerator (auxiliary is a separate, third category — see Cost by Source below), but auxiliary is still included in the denominator.",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 4, "w": 6, "x": 12, "y": 1},
  "fieldConfig": {"defaults": {"unit": "percentunit", "decimals": 1, "min": 0, "max": 1, "color": {"mode": "thresholds"}, "noValue": "0%", "thresholds": {"mode": "absolute", "steps": [{"color": "#9ECE6A", "value": null}, {"color": "#E0AF68", "value": 0.4}, {"color": "#7AA2F7", "value": 0.7}]}}, "overrides": []},
  "options": {"colorMode": "background", "graphMode": "none", "justifyMode": "center", "textMode": "auto", "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": false}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum(increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\", query_source=\"subagent\"}[$__range])) / sum(increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__range]))", "legendFormat": "Subagent %", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 4: Rewrite "Subagent Cost" panel** (currently lines 65-73, titled "Subagent Cost (backfilled)")

Drop "(backfilled)" from the title — Mimir data is live, not a backfill:

```json
{
  "type": "stat", "title": "Subagent Cost",
  "description": "Absolute cost where query_source=subagent. OTel-native (Mimir) — live, not backfilled.",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 4, "w": 6, "x": 18, "y": 1},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "decimals": 2, "color": {"fixedColor": "#7DCFFF", "mode": "fixed"}, "noValue": "$0.00"}, "overrides": []},
  "options": {"colorMode": "background", "graphMode": "none", "justifyMode": "center", "textMode": "auto", "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": false}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum(increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\", query_source=\"subagent\"}[$__range]))", "legendFormat": "Subagent Cost", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 5: Validate JSON syntax**

Run: `node -e "JSON.parse(require('fs').readFileSync('observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json', 'utf8')); console.log('valid')"`
Expected: `valid`

- [ ] **Step 6: Cross-check the new queries against Mimir directly before trusting the panel**

Run (uses the same working token from this session's `.env`):

```bash
set -a; source <(grep -E "^CURSOR_ELEVATED_GRAFANA_TOKEN=" .env); set +a
curl -s -G -H "Authorization: Bearer ${CURSOR_ELEVATED_GRAFANA_TOKEN}" \
  "https://simsteward.grafana.net/api/datasources/proxy/uid/grafanacloud-prom/api/v1/query" \
  --data-urlencode 'query=sum(increase({__name__=~"claude_code_cost_usage.*", env=~"dev"}[24h]))' | python3 -m json.tool
```

Expected: `"status": "success"` with a non-empty `result` array and a positive numeric value (today's session cost). If `result` is empty, the label matcher or metric name regex is wrong — do not proceed to Task 2 until this returns real data.

- [ ] **Step 7: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json
git commit -m "feat(obs): migrate dashboard stat panels from Loki to Mimir OTel metrics"
```

---

### Task 2: Migrate the 4 chart panels (Cost by Source, Cost Share by Model, Tokens by Type, Cost by Model)

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json:74-117` (the 4 chart panel objects)

**Interfaces:**
- Consumes: same confirmed metric/label names as Task 1.
- Produces: 4 panels on `grafanacloud-prom`. "Cost by Source" now shows 3 series (`main`/`subagent`/`auxiliary`) via a single grouped query instead of 2 hardcoded LogQL targets — this is a deliberate accuracy fix (see Global Constraints: `query_source` has 3 real values, not 2), not scope creep.

- [ ] **Step 1: Rewrite "Cost by Source" panel** (currently lines 74-85)

Note this collapses the old 2 hardcoded targets (`subagent`, `main`) into 1 grouped-by target that naturally produces all 3 real `query_source` values:

```json
{
  "type": "timeseries", "title": "Cost by Source (main / subagent / auxiliary)",
  "description": "Cost split by query_source. OTel-native (Mimir) — auxiliary is a genuine third category (see docs/GRAFANA-LOGGING.md), not folded into subagent.",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 8, "w": 12, "x": 0, "y": 5},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "custom": {"fillOpacity": 70, "lineInterpolation": "smooth", "lineWidth": 1, "showPoints": "never", "spanNulls": false, "stacking": {"group": "A", "mode": "normal"}}}, "overrides": []},
  "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi", "sort": "desc"}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum by (query_source) (increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__interval]))", "legendFormat": "{{query_source}}", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 2: Rewrite "Cost Share by Model" panel** (currently lines 86-94)

```json
{
  "type": "piechart", "title": "Cost Share by Model",
  "description": "Cost breakdown by model. OTel-native (Mimir).",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 8, "w": 12, "x": 12, "y": 5},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "color": {"mode": "palette-classic"}}, "overrides": []},
  "options": {"legend": {"displayMode": "table", "placement": "right", "values": ["value", "percent"]}, "pieType": "donut", "reduceOptions": {"calcs": ["sum"], "fields": "", "values": false}, "tooltip": {"mode": "single"}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum by (model) (increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__range]))", "legendFormat": "{{model}}", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 3: Rewrite "Tokens by Type" panel** (currently lines 95-108)

Collapses the old 4 hardcoded targets into 1 grouped-by target. Legend values will read `input`/`output`/`cacheRead`/`cacheCreation` (the real, confirmed label values) instead of the old Loki panel's `input`/`output`/`cache_read`/`cache_write` — this naming change is expected and correct, not a bug:

```json
{
  "type": "timeseries", "title": "Tokens by Type",
  "description": "Token breakdown: input, output, cacheRead, cacheCreation. OTel-native (Mimir) — label values are claude_code_token_usage_tokens_total's own type attribute, camelCase.",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 8, "w": 12, "x": 0, "y": 13},
  "fieldConfig": {"defaults": {"unit": "short", "custom": {"fillOpacity": 70, "lineInterpolation": "smooth", "lineWidth": 1, "showPoints": "never", "spanNulls": false, "stacking": {"group": "A", "mode": "normal"}}}, "overrides": []},
  "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi", "sort": "desc"}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum by (type) (increase({__name__=~\"claude_code_token_usage.*\", env=~\"$env\"}[$__interval]))", "legendFormat": "{{type}}", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 4: Rewrite "Cost by Model" panel** (currently lines 109-117)

```json
{
  "type": "timeseries", "title": "Cost by Model",
  "description": "Cost over time broken down by model. OTel-native (Mimir).",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 8, "w": 12, "x": 12, "y": 13},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "custom": {"fillOpacity": 70, "lineInterpolation": "smooth", "lineWidth": 1, "showPoints": "never", "spanNulls": false, "stacking": {"group": "A", "mode": "normal"}}}, "overrides": []},
  "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi", "sort": "desc"}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum by (model) (increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__interval]))", "legendFormat": "{{model}}", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 5: Validate JSON syntax**

Run: `node -e "JSON.parse(require('fs').readFileSync('observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json', 'utf8')); console.log('valid')"`
Expected: `valid`

- [ ] **Step 6: Cross-check "Cost by Source" grouping returns 3 series**

```bash
set -a; source <(grep -E "^CURSOR_ELEVATED_GRAFANA_TOKEN=" .env); set +a
curl -s -G -H "Authorization: Bearer ${CURSOR_ELEVATED_GRAFANA_TOKEN}" \
  "https://simsteward.grafana.net/api/datasources/proxy/uid/grafanacloud-prom/api/v1/query" \
  --data-urlencode 'query=sum by (query_source) (increase({__name__=~"claude_code_cost_usage.*", env=~"dev"}[24h]))' | python3 -m json.tool
```

Expected: 3 entries in `result`, one per `query_source` value (`main`, `subagent`, `auxiliary`), each with a positive value.

- [ ] **Step 7: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json
git commit -m "feat(obs): migrate dashboard chart panels from Loki to Mimir OTel metrics"
```

---

### Task 3: Migrate "API-equivalent Spend" stat and clean up stale dashboard-level comments

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json:2-4` (top-level `description`), `:124-130` (the "API-equivalent Spend" panel)

**Interfaces:**
- Consumes: same confirmed metric names as Task 1/2.
- Produces: dashboard-level `description` field no longer claims OTel/Mimir panels are blocked on a missing token (they're live now).

- [ ] **Step 1: Rewrite "API-equivalent Spend (selected range)" panel** (currently lines 123-131)

```json
{
  "type": "stat", "title": "API-equivalent Spend (selected range)",
  "description": "Retail API-price equivalent of usage in the selected range. A volume/cost proxy — NOT your subscription plan-limit usage. OTel-native (Mimir).",
  "datasource": {"type": "prometheus", "uid": "grafanacloud-prom"},
  "gridPos": {"h": 6, "w": 12, "x": 0, "y": 22},
  "fieldConfig": {"defaults": {"unit": "currencyUSD", "decimals": 2, "color": {"fixedColor": "#7AA2F7", "mode": "fixed"}, "noValue": "$0.00"}, "overrides": []},
  "options": {"colorMode": "background", "graphMode": "area", "justifyMode": "center", "textMode": "auto", "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": false}},
  "targets": [{"datasource": {"type": "prometheus", "uid": "grafanacloud-prom"}, "expr": "sum(increase({__name__=~\"claude_code_cost_usage.*\", env=~\"$env\"}[$__range]))", "legendFormat": "Spend", "queryType": "range", "refId": "A"}]
}
```

- [ ] **Step 2: Update the dashboard-level `description`** (line 4)

Old text claims: `"NOTE: OTEL PromQL panels (grafanacloud-prom) will replace these once SIMSTEWARD_OTLP_TOKEN is provisioned at grafana.com → Access Policies (metrics:write + logs:write)."` — this is stale; the token is provisioned and the panels are migrated. Replace the full `description` field with:

```json
"description": "Claude Code usage. TOP section = OTel-native metrics (claude_code.cost.usage / claude_code.token.usage) via Grafana Cloud Mimir — live since 2026-07-18; no data before that date (see docs/GRAFANA-LOGGING.md). BOTTOM = legacy Loki hook-pipeline cross-check + tool health + session audit (no Mimir equivalent for these). Subscription 5h/weekly limit % is NOT available via telemetry — see the Spend Context note.",
```

- [ ] **Step 3: Update the row title for the top section** (line 34)

Old: `"title": "All Sources — cost & tokens incl. backfilled subagents (Loki · OTEL PromQL once OTLP creds provisioned)"`. Replace with:

```json
"title": "All Sources — cost & tokens (OTel / Mimir, live)",
```

- [ ] **Step 4: Validate JSON syntax**

Run: `node -e "JSON.parse(require('fs').readFileSync('observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json', 'utf8')); console.log('valid')"`
Expected: `valid`

- [ ] **Step 5: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude/simsteward-claude-code.json
git commit -m "feat(obs): migrate remaining spend panel to Mimir, update stale dashboard description"
```

---

### Task 4: Deploy to Grafana Cloud and verify live

**Files:**
- No file changes — this task runs `scripts/deploy-dashboard.mjs` (already exists, unmodified) against `.env`.

**Interfaces:**
- Consumes: `GRAFANA_URL` or `SIMSTEWARD_GRAFANA_BASE_URL` (`.env` has `SIMSTEWARD_GRAFANA_BASE_URL=https://simsteward.grafana.net`), and the first present of `GRAFANA_DEPLOY_TOKEN` / `CURSOR_ELEVATED_GRAFANA_TOKEN` / `GRAFANA_API_TOKEN` (in that order — `.env` currently has no `GRAFANA_DEPLOY_TOKEN`, so this will use `CURSOR_ELEVATED_GRAFANA_TOKEN`, confirmed Admin-role and live in this session; `GRAFANA_API_TOKEN` is Viewer-only and would 403 on a dashboard write, so it must NOT be the one that ends up used — verify this in Step 1).

- [ ] **Step 1: Dry-run the deploy to confirm the right token and target are picked up**

Run: `pnpm dash:deploy -- --dry-run` (or `node scripts/deploy-dashboard.mjs --dry-run` if `.env` is already loaded in the shell)
Expected: output shows `Target: https://simsteward.grafana.net/api/dashboards/db` and does NOT print a "Missing GRAFANA_DEPLOY_TOKEN" error — confirming it fell through to `CURSOR_ELEVATED_GRAFANA_TOKEN`.

- [ ] **Step 2: Deploy for real**

Run: `pnpm dash:deploy`
Expected: HTTP 200 from `/api/dashboards/db`, response JSON has `"status": "success"`.

- [ ] **Step 3: Verify live in Grafana Cloud**

```bash
set -a; source <(grep -E "^CURSOR_ELEVATED_GRAFANA_TOKEN=" .env); set +a
curl -s -H "Authorization: Bearer ${CURSOR_ELEVATED_GRAFANA_TOKEN}" \
  "https://simsteward.grafana.net/api/dashboards/uid/simsteward-claude-code" \
  | python3 -c "
import json, sys
d = json.load(sys.stdin)
panels = d['dashboard']['panels']
top = [p for p in panels if p.get('gridPos', {}).get('y', 99) < 30 and p.get('type') != 'row']
loki_left = [p['title'] for p in top if p.get('datasource', {}).get('uid') == 'grafanacloud-logs']
prom_now = [p['title'] for p in top if p.get('datasource', {}).get('uid') == 'grafanacloud-prom']
print('still on loki (should be empty, aside from Spend Context text panel with no datasource):', loki_left)
print('now on prometheus:', prom_now)
"
```

Expected: `now on prometheus` lists all 9 migrated panels; `still on loki` is empty (the "Why there is no plan-limit gauge" text panel has no `datasource` field at all, so it won't appear in either list).

- [ ] **Step 4: Visually confirm data renders (not just that the query succeeds)**

Open `https://simsteward.grafana.net/d/simsteward-claude-code/simsteward-e28094-claude-code` in a browser (or via Chrome automation if driving headlessly), set the time range to "Today", and confirm the "Total Cost (all sources)" stat shows a non-zero dollar figure and "Cost by Source" shows at least a `main` series. Do not mark this task done from the API response alone — the panel JSON can be syntactically valid and still render "No data" if a label matcher is subtly wrong (e.g. `env="dev"` vs `env=~"$env"`).

---

### Task 5: Update docs/GRAFANA-LOGGING.md

**Files:**
- Modify: `docs/GRAFANA-LOGGING.md` (the "Claude Code native telemetry" section, and the dashboard panel table around line 104-115)

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Update the panel table description**

The current text (line 111) reads: *"The 'SimSteward — Claude Code' dashboard's top section queries them (PromQL uses `{__name__=~\"claude_code_cost_usage.*\"}` to tolerate exporter unit suffixes like `_USD_total`)."* — this was previously aspirational (written while the panels were still on Loki). Confirm/update it to state plainly that this is now the live, deployed state (no wording change needed if it already reads this way — just remove any adjacent "once provisioned" framing if present elsewhere in the file).

- [ ] **Step 2: Add a data-completeness note**

Add a line noting that Mimir has no `claude_code.*` data before 2026-07-18 (the collector was down for ~3 weeks after the June 22 bootstrap; see git history on `observability/local/otel-collector-config.yaml`), so historical dashboard views before that date will show a real gap, not a bug.

- [ ] **Step 3: Commit**

```bash
git add docs/GRAFANA-LOGGING.md
git commit -m "docs: reflect that Claude Code Mimir dashboard panels are live, not pending"
```
