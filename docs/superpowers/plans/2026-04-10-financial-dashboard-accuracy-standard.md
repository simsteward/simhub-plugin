# Financial Dashboard Accuracy Standard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the canonical financial metrics standard to all 6 Grafana dashboards — fixing accuracy issues and adding missing token/ratio panels.

**Architecture:** Pure JSON edits to Grafana provisioned dashboard files. No plugin or hook code changes. Each task targets one dashboard and produces a self-contained commit. Python inline scripts handle complex JSON mutations; direct JSON edits for targeted field changes.

**Tech Stack:** Python 3 (stdlib only — json, sys), Grafana 11.2 provisioned dashboards, LogQL (Loki), Grafana math expressions.

**Spec:** `docs/superpowers/specs/2026-04-10-financial-dashboard-accuracy-standard-design.md`

---

## File Map

| File | Changes |
|---|---|
| `observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json` | Add `__error__=""` to all queries, fix Plan ROI formula, add billing_days variable, add 2 new panels |
| `observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json` | Replace cache savings panel, add 2 new panels |
| `observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json` | Add Raw Token Consumption row with 5 new panels |
| `observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json` | Fix "This Session" rate panels to filter by session_id, clarify Session Cost label |
| `observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json` | Fix Cache Reuse Ratio formula, add "per-turn" to relevant panel titles |
| `observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json` | Add "Sentinel (Ollama)" labels to token panels |

**Canonical filter chain** (reference for all tasks):
```logql
{app="claude-token-metrics"}
| json
| __error__=""
| model=~"$model"
| project=~"$project"
| effort=~"$effort"
```

---

## Task 1: Intelligence — Add `__error__=""` to All Queries

**The problem:** Every Loki query in `claude-intelligence.json` is missing `| __error__=""`. Parse failures are included in all financial numbers in this dashboard. This is the highest-priority accuracy fix.

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json`

- [ ] **Step 1: Verify the problem**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json') as f:
    data = json.load(f)
missing = []
for p in data['panels']:
    for t in p.get('targets', []):
        expr = t.get('expr', '')
        if 'unwrap' in expr and '__error__' not in expr:
            missing.append((p['id'], p['title'], t['refId']))
print(f'Panels missing __error__: {len(missing)}')
for pid, title, ref in missing:
    print(f'  Panel {pid} [{title}] refId={ref}')
"
```

Expected output: ~15 panels listed, all missing `__error__=""`.

- [ ] **Step 2: Apply the fix**

```bash
python -c "
import json, re

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json'
with open(path) as f:
    data = json.load(f)

fixed = 0
for panel in data['panels']:
    for target in panel.get('targets', []):
        expr = target.get('expr', '')
        if '| json' in expr and '__error__' not in expr:
            # Insert | __error__=\"\" after | json (with optional field list)
            target['expr'] = re.sub(
                r'(\| json(?:\s+[\w,\s]+)?)\s*\|',
                r'\1 | __error__=\"\" |',
                expr,
                count=1
            )
            fixed += 1
        elif '| json' in expr and '__error__' not in expr:
            # json at end of stream (no following pipe)
            target['expr'] = expr.replace('| json', '| json | __error__=\"\"')
            fixed += 1

print(f'Fixed {fixed} queries')
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Done.')
"
```

Expected: `Fixed 15 queries` (approximately).

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json > /dev/null && echo "JSON valid"
```

Expected: `JSON valid`

- [ ] **Step 4: Verify the fix**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json') as f:
    data = json.load(f)
missing = []
for p in data['panels']:
    for t in p.get('targets', []):
        expr = t.get('expr', '')
        if 'unwrap' in expr and '__error__' not in expr:
            missing.append((p['id'], p['title'], t['refId']))
print(f'Panels still missing __error__: {len(missing)}')
for pid, title, ref in missing:
    print(f'  Panel {pid} [{title}] refId={ref}')
"
```

Expected: `Panels still missing __error__: 0`

- [ ] **Step 5: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json
git commit -m "fix(dashboards): add __error__=\"\" filter to all intelligence queries

All ~15 Loki queries in claude-intelligence were missing the error filter,
causing parse failures to be included in financial metrics."
```

---

## Task 2: Intelligence — Fix Plan ROI Formula + Add billing_days Variable

**The problem:** Plan ROI computes `API_cost / plan_monthly_cost` — comparing against the full monthly plan cost regardless of the selected time range. Subscription-economics prorates by billing_days. These should be consistent.

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json`

- [ ] **Step 1: Verify current Plan ROI formula**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json') as f:
    data = json.load(f)
p = next(p for p in data['panels'] if p.get('id') == 3)
print('Plan ROI targets:')
for t in p['targets']:
    print(f'  {t[\"refId\"]}: {t.get(\"expr\", t.get(\"expression\",\"\"))}')
print()
# Also check if billing_days variable exists
vars = [v['name'] for v in data.get('templating', {}).get('list', [])]
print('Variables:', vars)
"
```

Expected: refId B shows `$A * 0 + ${plan_monthly_cost}` (no billing_days). Variables list does NOT include `billing_days`.

- [ ] **Step 2: Add billing_days variable and fix Plan ROI**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json'
with open(path) as f:
    data = json.load(f)

# 1. Add billing_days variable (after plan_monthly_cost)
billing_days_var = {
    'name': 'billing_days',
    'label': 'Billing Days in Range',
    'type': 'textbox',
    'query': '30',
    'current': {'text': '30', 'value': '30'},
    'hide': 0,
    'description': 'Days in selected time range. Used to prorate plan cost. Default 30 = full month.'
}
vars_list = data['templating']['list']
# Insert after plan_monthly_cost if present, else append
plan_idx = next((i for i, v in enumerate(vars_list) if v['name'] == 'plan_monthly_cost'), len(vars_list)-1)
vars_list.insert(plan_idx + 1, billing_days_var)

# 2. Fix Plan ROI panel (id=3) formula: B should prorate
panel = next(p for p in data['panels'] if p.get('id') == 3)
for t in panel['targets']:
    if t['refId'] == 'B' and t.get('type') == 'math':
        # Old: \$A * 0 + \${plan_monthly_cost}
        # New: \$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}
        t['expression'] = '\$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}'
        print('Fixed Plan ROI formula')
        break

# 3. Update Plan ROI description
panel['description'] = 'API retail cost as a multiple of your prorated plan cost. 1.0 = break even. 3.5 = you got \$3.50 of compute per \$1 paid. Adjust Billing Days to match your time range.'

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Done.')
"
```

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 4: Verify**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json') as f:
    data = json.load(f)
p = next(p for p in data['panels'] if p.get('id') == 3)
for t in p['targets']:
    if t['refId'] == 'B':
        print('Plan ROI B formula:', t.get('expression',''))
vars = [v['name'] for v in data['templating']['list']]
print('Variables:', vars)
"
```

Expected: `Plan ROI B formula: $A * 0 + ${plan_monthly_cost} / 30 * ${billing_days}` and `billing_days` in variables list.

- [ ] **Step 5: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json
git commit -m "fix(dashboards): fix Plan ROI formula in intelligence dashboard

Add billing_days variable and prorate Plan ROI against selected window,
matching the canonical subsidy multiplier formula in subscription-economics."
```

---

## Task 3: Intelligence — Add Output Tokens per \$1 and Cache Efficiency % Panels

**The problem:** Intelligence dashboard has no token efficiency ratio panels. These are in the canonical standard and appear in token-cost; intelligence should show them too.

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json`

- [ ] **Step 1: Check current max panel ID and row structure**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json') as f:
    data = json.load(f)
ids = [p['id'] for p in data['panels'] if isinstance(p.get('id'), int)]
print('Max panel ID:', max(ids))
rows = [(p['id'], p['title'], p['gridPos']['y']) for p in data['panels'] if p['type'] == 'row']
for rid, rtitle, ry in sorted(rows, key=lambda x: x[2]):
    print(f'  Row {rid} y={ry}: {rtitle}')
"
```

Note the max ID and the y-position of the last panel/row to know where to append.

- [ ] **Step 2: Add two new panels to the rates row**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json'
with open(path) as f:
    data = json.load(f)

# Find the y-position of the last panel to append after
max_y = max(p['gridPos']['y'] + p['gridPos']['h'] for p in data['panels'])

# New panel 1: Output Tokens per \$1
output_per_dollar = {
    'id': 901,
    'title': 'Output Tokens per \$1',
    'description': 'How many output tokens you receive per dollar of API compute cost. Higher = more efficient. Canonical ratio: sum(total_output_tokens) / sum(cost_usd).',
    'type': 'stat',
    'gridPos': {'x': 0, 'y': max_y + 1, 'w': 6, 'h': 6},
    'datasource': {'type': 'loki', 'uid': 'loki_local'},
    'targets': [
        {
            'refId': 'A',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__range]))',
            'queryType': 'instant',
            'legendFormat': '',
            'hide': True
        },
        {
            'refId': 'B',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap cost_usd [\$__range]))',
            'queryType': 'instant',
            'legendFormat': '',
            'hide': True
        },
        {
            'refId': 'C',
            'datasource': {'type': '__expr__', 'uid': '__expr__'},
            'type': 'math',
            'expression': '\$A / \$B',
            'hide': False
        }
    ],
    'options': {
        'colorMode': 'background-gradient',
        'graphMode': 'none',
        'justifyMode': 'center',
        'orientation': 'auto',
        'textMode': 'value_and_name',
        'text': {'titleSize': 11, 'valueSize': 28},
        'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False}
    },
    'fieldConfig': {
        'defaults': {
            'unit': 'short',
            'decimals': 0,
            'displayName': 'tok / \$1',
            'color': {'mode': 'thresholds'},
            'thresholds': {
                'mode': 'absolute',
                'steps': [
                    {'value': None, 'color': '#c77dff'},
                    {'value': 1000, 'color': '#00d4aa'},
                    {'value': 5000, 'color': '#ffd166'},
                    {'value': 20000, 'color': '#f72585'}
                ]
            }
        },
        'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Output tok / \$1'}]}]
    }
}

# New panel 2: Cache Efficiency %
cache_efficiency = {
    'id': 902,
    'title': 'Cache Efficiency',
    'description': 'What % of cache-related tokens are served from cache (reads) vs generated fresh (creations). Higher = better cache reuse. Formula: cache_read / (cache_read + cache_creation) * 100.',
    'type': 'gauge',
    'gridPos': {'x': 6, 'y': max_y + 1, 'w': 6, 'h': 6},
    'datasource': {'type': 'loki', 'uid': 'loki_local'},
    'targets': [
        {
            'refId': 'A',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_cache_read_tokens [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'B',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_cache_creation_tokens [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'C',
            'datasource': {'type': '__expr__', 'uid': '__expr__'},
            'type': 'math',
            'expression': '\$A / (\$A + \$B) * 100',
            'hide': False
        }
    ],
    'options': {
        'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False},
        'orientation': 'auto',
        'textMode': 'auto',
        'colorMode': 'thresholds',
        'displayMode': 'gradient',
        'minVizWidth': 75,
        'minVizHeight': 75
    },
    'fieldConfig': {
        'defaults': {
            'unit': 'percent',
            'decimals': 1,
            'min': 0,
            'max': 100,
            'displayName': 'Cache Efficiency',
            'color': {'mode': 'thresholds'},
            'thresholds': {
                'mode': 'absolute',
                'steps': [
                    {'value': None, 'color': '#f72585'},
                    {'value': 30, 'color': '#ffd166'},
                    {'value': 60, 'color': '#00d4aa'},
                    {'value': 85, 'color': '#c77dff'}
                ]
            }
        },
        'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Cache Efficiency %'}]}]
    }
}

data['panels'].append(output_per_dollar)
data['panels'].append(cache_efficiency)

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Added panels 901 and 902.')
"
```

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 4: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json
git commit -m "feat(dashboards): add token efficiency panels to intelligence dashboard

Add Output Tokens per \$1 and Cache Efficiency % using canonical formulas
from the financial metrics standard."
```

---

## Task 4: Token-Cost — Replace Cache Savings (\$/day) with Cache Read Tokens

**The problem:** Panel 502 "Cache Savings Estimate ($/day)" uses a hardcoded `$2.70/M` rate — correct for Sonnet-4, wrong for all other models. Replace with a tokens-based panel that is model-agnostic and honest.

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json`

- [ ] **Step 1: Confirm panel 502 exists with hardcoded rate**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json') as f:
    data = json.load(f)
p = next((p for p in data['panels'] if p.get('id') == 502), None)
if p:
    print('Panel 502:', p['title'])
    for t in p.get('targets', []):
        print(' ', t.get('expr', t.get('expression','')))
else:
    print('Panel 502 not found')
"
```

Expected: Panel 502 with title "Cache Savings Estimate ($/day)" and expression containing `* 2.70`.

- [ ] **Step 2: Replace panel 502**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json'
with open(path) as f:
    data = json.load(f)

# Find and replace panel 502
for i, panel in enumerate(data['panels']):
    if panel.get('id') == 502:
        # Preserve gridPos from the old panel
        old_gridpos = panel['gridPos']
        
        data['panels'][i] = {
            'id': 502,
            'title': 'Cache Read Tokens — Daily',
            'description': 'Tokens served from cache per day. Cache reads cost ~10x less than generating new tokens. This is your compute avoided — not expressed in dollars because the saving depends on model pricing.',
            'type': 'timeseries',
            'gridPos': old_gridpos,
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'interval': '1d',
            'targets': [{
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_cache_read_tokens [\$__interval]))',
                'queryType': 'range',
                'legendFormat': 'Cache Read Tokens / Day'
            }],
            'options': {
                'tooltip': {'mode': 'multi', 'sort': 'none'},
                'legend': {'displayMode': 'list', 'placement': 'bottom', 'showLegend': True}
            },
            'fieldConfig': {
                'defaults': {
                    'unit': 'short',
                    'decimals': 0,
                    'color': {'mode': 'fixed', 'fixedColor': '#00d4aa'},
                    'custom': {
                        'drawStyle': 'bars',
                        'lineWidth': 1,
                        'fillOpacity': 80,
                        'gradientMode': 'none',
                        'spanNulls': False,
                        'barMaxWidth': 60
                    }
                },
                'overrides': []
            }
        }
        print(f'Replaced panel 502. Old gridPos: {old_gridpos}')
        break

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Done.')
"
```

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 4: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json
git commit -m "fix(dashboards): replace hardcoded cache savings \$/day with token count

Cache savings in dollars used a hardcoded \$2.70/M rate (Sonnet-4 only).
Replace with cache read tokens per day — model-agnostic and accurate."
```

---

## Task 5: Token-Cost — Add Output Tokens per \$1 and Cache Efficiency % Panels

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json`

- [ ] **Step 1: Add two panels to the Cross-References row (after panel 502)**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json'
with open(path) as f:
    data = json.load(f)

# Find y-position of panel 505 (last in Cross-References row) to place after it
p505 = next((p for p in data['panels'] if p.get('id') == 505), None)
if p505:
    append_y = p505['gridPos']['y']
    append_x_after = p505['gridPos']['x'] + p505['gridPos']['w']
else:
    append_y = max(p['gridPos']['y'] + p['gridPos']['h'] for p in data['panels'])
    append_x_after = 0

print(f'Appending after panel 505 at y={append_y}, x_start={append_x_after}')

# New panel: Output Tokens per \$1 (canonical)
output_per_dollar = {
    'id': 506,
    'title': 'Output Tokens per \$1',
    'description': 'How many output tokens per dollar of API compute cost. Canonical ratio: sum(total_output_tokens) / sum(cost_usd). Higher = more efficient.',
    'type': 'stat',
    'gridPos': {'x': 0, 'y': append_y + 10, 'w': 6, 'h': 6},
    'datasource': {'type': 'loki', 'uid': 'loki_local'},
    'targets': [
        {
            'refId': 'A',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'B',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap cost_usd [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'C',
            'datasource': {'type': '__expr__', 'uid': '__expr__'},
            'type': 'math',
            'expression': '\$A / \$B',
            'hide': False
        }
    ],
    'options': {
        'colorMode': 'background-gradient',
        'graphMode': 'none',
        'justifyMode': 'center',
        'orientation': 'auto',
        'textMode': 'value_and_name',
        'text': {'titleSize': 11, 'valueSize': 28},
        'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False}
    },
    'fieldConfig': {
        'defaults': {
            'unit': 'short',
            'decimals': 0,
            'color': {'mode': 'thresholds'},
            'thresholds': {
                'mode': 'absolute',
                'steps': [
                    {'value': None, 'color': '#c77dff'},
                    {'value': 1000, 'color': '#00d4aa'},
                    {'value': 5000, 'color': '#ffd166'},
                    {'value': 20000, 'color': '#f72585'}
                ]
            }
        },
        'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Output tok / \$1'}]}]
    }
}

# New panel: Cache Efficiency % (canonical)
cache_efficiency = {
    'id': 507,
    'title': 'Cache Efficiency',
    'description': 'Canonical: cache_read / (cache_read + cache_creation) * 100. Higher = better cache reuse, less generation cost.',
    'type': 'gauge',
    'gridPos': {'x': 6, 'y': append_y + 10, 'w': 6, 'h': 6},
    'datasource': {'type': 'loki', 'uid': 'loki_local'},
    'targets': [
        {
            'refId': 'A',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_cache_read_tokens [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'B',
            'datasource': {'type': 'loki', 'uid': 'loki_local'},
            'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_cache_creation_tokens [\$__range]))',
            'queryType': 'instant',
            'hide': True
        },
        {
            'refId': 'C',
            'datasource': {'type': '__expr__', 'uid': '__expr__'},
            'type': 'math',
            'expression': '\$A / (\$A + \$B) * 100',
            'hide': False
        }
    ],
    'options': {
        'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False},
        'orientation': 'auto',
        'textMode': 'auto',
        'colorMode': 'thresholds',
        'displayMode': 'gradient',
        'minVizWidth': 75,
        'minVizHeight': 75
    },
    'fieldConfig': {
        'defaults': {
            'unit': 'percent',
            'decimals': 1,
            'min': 0,
            'max': 100,
            'color': {'mode': 'thresholds'},
            'thresholds': {
                'mode': 'absolute',
                'steps': [
                    {'value': None, 'color': '#f72585'},
                    {'value': 30, 'color': '#ffd166'},
                    {'value': 60, 'color': '#00d4aa'},
                    {'value': 85, 'color': '#c77dff'}
                ]
            }
        },
        'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Cache Efficiency %'}]}]
    }
}

data['panels'].append(output_per_dollar)
data['panels'].append(cache_efficiency)

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Added panels 506 and 507.')
"
```

- [ ] **Step 2: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 3: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json
git commit -m "feat(dashboards): add token efficiency panels to token-cost dashboard

Add Output Tokens per \$1 (canonical) and Cache Efficiency % (canonical)
to the Cross-References section."
```

---

## Task 6: Subscription-Economics — Add Raw Token Consumption Row

**The problem:** Subscription-economics has no raw token data — every panel is in dollars. Adding a token layer makes the plan value story complete (how many tokens does your \$100/mo actually buy?).

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json`

- [ ] **Step 1: Check current max y-position**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json') as f:
    data = json.load(f)
max_y = max(p['gridPos']['y'] + p['gridPos']['h'] for p in data['panels'])
print('Max y:', max_y)
ids = [p['id'] for p in data['panels'] if isinstance(p.get('id'), int)]
print('Max panel ID:', max(ids))
"
```

Note the max_y and max panel ID. New row will start at max_y + 1.

- [ ] **Step 2: Add Raw Token Consumption row + 5 panels**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json'
with open(path) as f:
    data = json.load(f)

max_y = max(p['gridPos']['y'] + p['gridPos']['h'] for p in data['panels'])
row_y = max_y + 1

new_panels = [
    # Row header
    {
        'id': 200,
        'type': 'row',
        'title': 'Raw Token Consumption',
        'description': 'How many tokens your plan is buying you. Token counts are model-agnostic — a token is a token regardless of price.',
        'collapsed': False,
        'gridPos': {'x': 0, 'y': row_y, 'w': 24, 'h': 1}
    },
    # Panel 1: Total Tokens Consumed (stat)
    {
        'id': 201,
        'title': 'Total Tokens Consumed',
        'description': 'Total input + output tokens in the selected window. Cache tokens excluded. This is your raw compute footprint.',
        'type': 'stat',
        'gridPos': {'x': 0, 'y': row_y + 1, 'w': 4, 'h': 6},
        'datasource': {'type': 'loki', 'uid': 'loki_local'},
        'targets': [
            {
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_input_tokens [\$__range]))',
                'queryType': 'instant',
                'hide': True
            },
            {
                'refId': 'B',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__range]))',
                'queryType': 'instant',
                'hide': True
            },
            {
                'refId': 'C',
                'datasource': {'type': '__expr__', 'uid': '__expr__'},
                'type': 'math',
                'expression': '\$A + \$B',
                'hide': False
            }
        ],
        'options': {
            'colorMode': 'background-gradient', 'graphMode': 'none', 'justifyMode': 'center',
            'orientation': 'auto', 'textMode': 'value_and_name',
            'text': {'titleSize': 11, 'valueSize': 28},
            'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False}
        },
        'fieldConfig': {
            'defaults': {
                'unit': 'short', 'decimals': 0,
                'color': {'mode': 'fixed', 'fixedColor': '#4895ef'},
                'thresholds': {'mode': 'absolute', 'steps': [{'value': None, 'color': '#4895ef'}]}
            },
            'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Total Tokens'}]}]
        }
    },
    # Panel 2: Output Tokens per \$1 of Plan (stat)
    {
        'id': 202,
        'title': 'Output Tokens per \$1 of Plan',
        'description': 'How many output tokens your plan dollar buys. Formula: sum(output_tokens) / (plan_monthly / 30 * billing_days). This is your token purchasing power.',
        'type': 'stat',
        'gridPos': {'x': 4, 'y': row_y + 1, 'w': 4, 'h': 6},
        'datasource': {'type': 'loki', 'uid': 'loki_local'},
        'targets': [
            {
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__range]))',
                'queryType': 'instant',
                'hide': True
            },
            {
                'refId': 'B',
                'datasource': {'type': '__expr__', 'uid': '__expr__'},
                'type': 'math',
                'expression': '\$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}',
                'hide': True
            },
            {
                'refId': 'C',
                'datasource': {'type': '__expr__', 'uid': '__expr__'},
                'type': 'math',
                'expression': '\$A / \$B',
                'hide': False
            }
        ],
        'options': {
            'colorMode': 'background-gradient', 'graphMode': 'none', 'justifyMode': 'center',
            'orientation': 'auto', 'textMode': 'value_and_name',
            'text': {'titleSize': 11, 'valueSize': 28},
            'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False}
        },
        'fieldConfig': {
            'defaults': {
                'unit': 'short', 'decimals': 0,
                'color': {'mode': 'thresholds'},
                'thresholds': {'mode': 'absolute', 'steps': [{'value': None, 'color': '#c77dff'}, {'value': 500, 'color': '#00d4aa'}, {'value': 2000, 'color': '#ffd166'}]}
            },
            'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Output tok / \$1 plan'}]}]
        }
    },
    # Panel 3: Total Tokens per \$1 of Plan (stat)
    {
        'id': 203,
        'title': 'Total Tokens per \$1 of Plan',
        'description': 'Input + output tokens per dollar of plan cost. Shows total compute footprint per plan dollar, including context sent.',
        'type': 'stat',
        'gridPos': {'x': 8, 'y': row_y + 1, 'w': 4, 'h': 6},
        'datasource': {'type': 'loki', 'uid': 'loki_local'},
        'targets': [
            {
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_input_tokens [\$__range]))',
                'queryType': 'instant',
                'hide': True
            },
            {
                'refId': 'B',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__range]))',
                'queryType': 'instant',
                'hide': True
            },
            {
                'refId': 'D',
                'datasource': {'type': '__expr__', 'uid': '__expr__'},
                'type': 'math',
                'expression': '\$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}',
                'hide': True
            },
            {
                'refId': 'C',
                'datasource': {'type': '__expr__', 'uid': '__expr__'},
                'type': 'math',
                'expression': '(\$A + \$B) / \$D',
                'hide': False
            }
        ],
        'options': {
            'colorMode': 'background-gradient', 'graphMode': 'none', 'justifyMode': 'center',
            'orientation': 'auto', 'textMode': 'value_and_name',
            'text': {'titleSize': 11, 'valueSize': 28},
            'reduceOptions': {'calcs': ['lastNotNull'], 'fields': '', 'values': False}
        },
        'fieldConfig': {
            'defaults': {
                'unit': 'short', 'decimals': 0,
                'color': {'mode': 'thresholds'},
                'thresholds': {'mode': 'absolute', 'steps': [{'value': None, 'color': '#c77dff'}, {'value': 1000, 'color': '#00d4aa'}, {'value': 5000, 'color': '#ffd166'}]}
            },
            'overrides': [{'matcher': {'id': 'byFrameRefID', 'options': 'C'}, 'properties': [{'id': 'displayName', 'value': 'Total tok / \$1 plan'}]}]
        }
    },
    # Panel 4: Token Velocity (timeseries, tokens/hour)
    {
        'id': 204,
        'title': 'Token Velocity — Tokens / Hour',
        'description': 'Total token throughput (input + output) per hour. Shows WHEN you consume compute. Spikes = heavy sessions. Use alongside the dollar burn rate to see token-to-cost correlation.',
        'type': 'timeseries',
        'gridPos': {'x': 0, 'y': row_y + 7, 'w': 14, 'h': 8},
        'datasource': {'type': 'loki', 'uid': 'loki_local'},
        'targets': [
            {
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(rate({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_input_tokens [\$__interval])) * 3600',
                'queryType': 'range',
                'legendFormat': 'Input tok/hr'
            },
            {
                'refId': 'B',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(rate({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__interval])) * 3600',
                'queryType': 'range',
                'legendFormat': 'Output tok/hr'
            }
        ],
        'options': {
            'tooltip': {'mode': 'multi', 'sort': 'none'},
            'legend': {'displayMode': 'list', 'placement': 'bottom', 'showLegend': True}
        },
        'fieldConfig': {
            'defaults': {
                'unit': 'short', 'decimals': 0,
                'custom': {
                    'drawStyle': 'line', 'lineWidth': 2, 'fillOpacity': 20,
                    'gradientMode': 'opacity', 'spanNulls': False
                }
            },
            'overrides': [
                {'matcher': {'id': 'byName', 'options': 'Input tok/hr'}, 'properties': [{'id': 'color', 'value': {'mode': 'fixed', 'fixedColor': '#4895ef'}}]},
                {'matcher': {'id': 'byName', 'options': 'Output tok/hr'}, 'properties': [{'id': 'color', 'value': {'mode': 'fixed', 'fixedColor': '#f4a261'}}]}
            ]
        }
    },
    # Panel 5: Input vs Output Split (stacked bar)
    {
        'id': 205,
        'title': 'Input vs Output Token Split',
        'description': 'Output tokens cost 3-5x more than input per token. A high output share = higher cost per total token. Cache read tokens (cheapest) excluded from this split.',
        'type': 'timeseries',
        'gridPos': {'x': 14, 'y': row_y + 7, 'w': 10, 'h': 8},
        'datasource': {'type': 'loki', 'uid': 'loki_local'},
        'interval': '1d',
        'targets': [
            {
                'refId': 'A',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_input_tokens [\$__interval]))',
                'queryType': 'range',
                'legendFormat': 'Input Tokens'
            },
            {
                'refId': 'B',
                'datasource': {'type': 'loki', 'uid': 'loki_local'},
                'expr': 'sum(sum_over_time({app=\"claude-token-metrics\"} | json | __error__=\"\" | model=~\"\$model\" | project=~\"\$project\" | effort=~\"\$effort\" | unwrap total_output_tokens [\$__interval]))',
                'queryType': 'range',
                'legendFormat': 'Output Tokens'
            }
        ],
        'options': {
            'tooltip': {'mode': 'multi', 'sort': 'none'},
            'legend': {'displayMode': 'list', 'placement': 'bottom', 'showLegend': True}
        },
        'fieldConfig': {
            'defaults': {
                'unit': 'short', 'decimals': 0,
                'custom': {
                    'drawStyle': 'bars', 'lineWidth': 1, 'fillOpacity': 80,
                    'gradientMode': 'none', 'spanNulls': False,
                    'stacking': {'group': 'A', 'mode': 'normal'},
                    'barMaxWidth': 60
                }
            },
            'overrides': [
                {'matcher': {'id': 'byName', 'options': 'Input Tokens'}, 'properties': [{'id': 'color', 'value': {'mode': 'fixed', 'fixedColor': '#4895ef'}}]},
                {'matcher': {'id': 'byName', 'options': 'Output Tokens'}, 'properties': [{'id': 'color', 'value': {'mode': 'fixed', 'fixedColor': '#f4a261'}}]}
            ]
        }
    }
]

data['panels'].extend(new_panels)

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print(f'Added {len(new_panels)} panels (row + 5 data panels).')
"
```

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 4: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json
git commit -m "feat(dashboards): add Raw Token Consumption row to subscription-economics

Add 5 panels: total tokens consumed, output tokens per \$1 of plan,
total tokens per \$1 of plan, token velocity timeseries, input/output
stacked bar. Completes the token layer alongside the cost layer."
```

---

## Task 7: Code-Overview — Fix Session Rate Panels Missing session_id Filter

**The problem:** Panels 51–54 in code-overview are labeled "This Session" but their Loki queries have no `session_id` filter. They show the global rate, not the current session's rate. Panel 20 (Session Cost) correctly filters by session_id.

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json`

- [ ] **Step 1: Verify the bug**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json') as f:
    data = json.load(f)
for p in data['panels']:
    if p.get('id') in [20, 51, 52, 53, 54]:
        print(f'Panel {p[\"id\"]}: {p[\"title\"]}')
        for t in p.get('targets', []):
            expr = t.get('expr', '')
            if expr:
                has_session = 'session_id' in expr
                print(f'  has session_id filter: {has_session}')
                print(f'  expr: {expr[:120]}')
        print()
"
```

Expected: Panels 51-54 show `has session_id filter: False`.

- [ ] **Step 2: Add session_id filter to panels 51-54**

First confirm the session_id variable name in this dashboard:

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json') as f:
    data = json.load(f)
vars = [(v['name'], v.get('label','')) for v in data.get('templating',{}).get('list',[])]
print('Variables:', vars)
"
```

Then apply the fix:

```bash
python -c "
import json, re

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json'
with open(path) as f:
    data = json.load(f)

fixed = 0
for panel in data['panels']:
    if panel.get('id') in [51, 52, 53, 54]:
        for target in panel.get('targets', []):
            expr = target.get('expr', '')
            if 'claude-token-metrics' in expr and 'session_id' not in expr:
                # Add session_id filter after __error__=\"\"
                target['expr'] = expr.replace(
                    '| __error__=\"\"',
                    '| __error__=\"\" | session_id=~\"\$session_id\"'
                )
                fixed += 1

print(f'Fixed {fixed} queries')
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
"
```

- [ ] **Step 3: Update Panel 20 display name to clarify scope**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json'
with open(path) as f:
    data = json.load(f)

p = next(p for p in data['panels'] if p.get('id') == 20)
p['title'] = 'Session Cost (Full Session)'
p['description'] = 'Total API-equivalent cost for this session, regardless of selected time range. Scoped by session_id — shows the complete session cost even if it started before the time window.'

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
print('Updated panel 20 title and description.')
"
```

- [ ] **Step 4: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 5: Verify fix**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json') as f:
    data = json.load(f)
for p in data['panels']:
    if p.get('id') in [20, 51, 52, 53, 54]:
        print(f'Panel {p[\"id\"]}: {p[\"title\"]}')
        for t in p.get('targets', []):
            expr = t.get('expr', '')
            if expr:
                print(f'  session_id present: {\"session_id\" in expr}')
"
```

Expected: All panels 20, 51-54 show `session_id present: True`.

- [ ] **Step 6: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json
git commit -m "fix(dashboards): scope session rate panels to current session in code-overview

Panels 51-54 ('This Session' rates) were missing session_id filter and
showed global rates instead. Add session_id=~\"\$session_id\" to all four.
Clarify Session Cost panel title to show it is full-session scoped."
```

---

## Task 8: Cache-Context — Fix Cache Reuse Ratio to Canonical Formula + Per-Turn Labels

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json`

- [ ] **Step 1: Find the Cache Reuse Ratio panel and per-turn panels**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json') as f:
    data = json.load(f)
for p in data['panels']:
    title = p.get('title','')
    if 'reuse' in title.lower() or 'ratio' in title.lower() or 'per-turn' in title.lower() or 'turn' in title.lower():
        print(f'Panel {p[\"id\"]}: {title}')
        for t in p.get('targets', []):
            print(f'  {t.get(\"refId\")}: {t.get(\"expr\", t.get(\"expression\",\"\"))[:150]}')
        print()
"
```

Note the panel IDs and current formulas.

- [ ] **Step 2: Fix Cache Reuse Ratio to canonical Cache Efficiency % formula**

The canonical formula is: `cache_read / (cache_read + cache_creation) * 100`

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json'
with open(path) as f:
    data = json.load(f)

for panel in data['panels']:
    title = panel.get('title', '')
    if 'reuse' in title.lower() or ('ratio' in title.lower() and 'cache' in title.lower()):
        print(f'Found panel {panel[\"id\"]}: {title}')
        # Find the math expression target and replace with canonical formula
        for target in panel.get('targets', []):
            expr = target.get('expression', '')
            if expr and ('/' in expr or 'ratio' in expr.lower()):
                # Canonical: \$READ / (\$READ + \$CREATE) * 100
                # We need to identify which refIds are read vs create
                print(f'  Old expression: {expr}')
                # Replace with canonical: assumes A=cache_read, B=cache_creation
                target['expression'] = '\$A / (\$A + \$B) * 100'
                print(f'  New expression: {target[\"expression\"]}')
        
        # Update panel metadata
        panel['title'] = 'Cache Efficiency %'
        panel['description'] = 'Canonical: cache_read / (cache_read + cache_creation) * 100. What fraction of cache-related tokens came from cache (cheap) vs were freshly written to cache (full price).'
        
        # Update fieldConfig unit to percent
        defaults = panel.get('fieldConfig', {}).get('defaults', {})
        defaults['unit'] = 'percent'
        defaults['min'] = 0
        defaults['max'] = 100
        print('  Updated panel metadata.')
        break

with open(path, 'w') as f:
    json.dump(data, f, indent=2)
"
```

- [ ] **Step 3: Add "Per-Turn" prefix to turn-level panels**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json'
with open(path) as f:
    data = json.load(f)

per_turn_keywords = ['turn input', 'turn output', 'turn cache', 'per turn', 'token flow', 'output burst']
renamed = 0
for panel in data['panels']:
    title = panel.get('title', '')
    # Panels from claude-dev-logging with turn-level fields need clear labeling
    uses_turn_fields = any(
        'turn_input_tokens' in t.get('expr','') or
        'turn_output_tokens' in t.get('expr','') or
        'turn_cache' in t.get('expr','')
        for t in panel.get('targets', [])
    )
    if uses_turn_fields and not title.startswith('Per-Turn'):
        panel['title'] = 'Per-Turn — ' + title
        renamed += 1
        print(f'Renamed: {title} -> {panel[\"title\"]}')

print(f'Renamed {renamed} panels.')
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
"
```

- [ ] **Step 4: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 5: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json
git commit -m "fix(dashboards): canonical cache efficiency formula + per-turn labels in cache-context

Replace Cache Reuse Ratio with canonical Cache Efficiency % formula.
Add 'Per-Turn' prefix to turn-level panels to distinguish from session
aggregates in other dashboards."
```

---

## Task 9: Log-Sentinel — Add Sentinel (Ollama) Labels to Token Panels

**Files:**
- Modify: `observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json`

- [ ] **Step 1: Find all token panels**

```bash
python -c "
import json
with open('observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json') as f:
    data = json.load(f)
for p in data['panels']:
    for t in p.get('targets', []):
        expr = t.get('expr', '')
        if 'output_tokens' in expr or 'tokens_per_sec' in expr or 'input_tokens' in expr:
            print(f'Panel {p[\"id\"]}: {p[\"title\"]}')
            break
"
```

- [ ] **Step 2: Add Sentinel (Ollama) to descriptions**

```bash
python -c "
import json

path = 'observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json'
with open(path) as f:
    data = json.load(f)

sentinel_disclaimer = 'Sentinel (Ollama) — local LLM, not billed. Field name is output_tokens (not total_output_tokens). Not comparable to Claude API token metrics.'

updated = 0
for panel in data['panels']:
    uses_tokens = any(
        'output_tokens' in t.get('expr','') or 'tokens_per_sec' in t.get('expr','')
        for t in panel.get('targets', [])
    )
    if uses_tokens:
        existing_desc = panel.get('description', '')
        if 'Sentinel' not in existing_desc:
            panel['description'] = sentinel_disclaimer + (' ' + existing_desc if existing_desc else '')
            updated += 1
            print(f'Updated panel {panel[\"id\"]}: {panel[\"title\"]}')

print(f'Updated {updated} panels.')
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
"
```

- [ ] **Step 3: Validate JSON**

```bash
python -m json.tool observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json > /dev/null && echo "JSON valid"
```

- [ ] **Step 4: Commit**

```bash
git add observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json
git commit -m "docs(dashboards): label sentinel token panels as Sentinel (Ollama)

Add description to all token panels clarifying they show local Ollama
throughput, not Claude API tokens. Field name difference (output_tokens
vs total_output_tokens) noted to prevent cross-dashboard confusion."
```

---

## Task 10: Smoke Test — Restart Grafana and Verify All Dashboards Load

- [ ] **Step 1: Validate all modified dashboard JSON files**

```bash
for f in \
  observability/local/grafana/provisioning/dashboards/claude-usage/claude-intelligence.json \
  observability/local/grafana/provisioning/dashboards/claude-economics/claude-token-cost.json \
  observability/local/grafana/provisioning/dashboards/claude-economics/claude-subscription-economics.json \
  observability/local/grafana/provisioning/dashboards/claude-usage/claude-code-overview.json \
  observability/local/grafana/provisioning/dashboards/claude-usage/claude-cache-context.json \
  observability/local/grafana/provisioning/dashboards/simsteward-ops/simsteward-log-sentinel.json; do
  python -m json.tool "$f" > /dev/null && echo "OK: $f" || echo "INVALID: $f"
done
```

Expected: All files print `OK`.

- [ ] **Step 2: Restart Grafana**

```bash
pnpm obs:down && pnpm obs:up
```

- [ ] **Step 3: Verify in Grafana (manual)**

Open http://localhost:3000 and confirm:

| Dashboard | Check |
|---|---|
| Claude — Economics / Subscription Economics | New "Raw Token Consumption" row visible at bottom with 5 panels |
| Claude — Economics / Token & Cost | Panel 502 now shows "Cache Read Tokens — Daily" (teal bars, no dollar sign). Panels 506+507 visible. |
| Claude — Usage / Intelligence | All panels have data (no longer broken by parse errors). Plan ROI now matches subscription-economics values. Panels 901+902 visible. |
| Claude — Usage / Code Overview | Session rate panels (Avg $/hour etc.) show session-scoped data |
| Claude — Usage / Cache & Context | "Cache Reuse Ratio" renamed to "Cache Efficiency %" with 0-100% gauge |
| SimSteward — Operations / Log Sentinel | Token panels have "Sentinel (Ollama)" in description |

- [ ] **Step 4: Cross-check intelligence vs subscription-economics Plan ROI**

Set both dashboards to the same 30-day window with the same model/project filters.

- Intelligence "Plan ROI" value should equal Subscription-economics "Subsidy Multiplier" value (both = API_cost / plan_cost_prorated).
- If they differ, check that `billing_days` variable is set to 30 in intelligence.

- [ ] **Step 5: Final commit if any visual tweaks were needed**

```bash
git add observability/local/grafana/provisioning/dashboards/
git commit -m "fix(dashboards): post-restart visual adjustments"
```
