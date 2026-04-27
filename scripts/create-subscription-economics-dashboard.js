#!/usr/bin/env node
'use strict';
const fs = require('fs');

const loki = { type: 'loki', uid: 'loki_local' };
const exprDs = { type: '__expr__', uid: '__expr__' };

const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';
const stream = `{app="claude-token-metrics"} | json | ${filters}`;
const sessionStream = `{app="claude-dev-logging"} | json`;

// ── helpers ────────────────────────────────────────────────────────────────
const statOpts = (calcs = ['lastNotNull']) => ({
  colorMode: 'background-gradient',
  graphMode: 'none',
  justifyMode: 'center',
  orientation: 'auto',
  textMode: 'value_and_name',
  text: { titleSize: 11, valueSize: 32 },
  reduceOptions: { calcs, fields: '', values: false }
});

const currencyDefaults = (steps) => ({
  unit: 'currencyUSD',
  decimals: 2,
  color: { mode: 'thresholds' },
  thresholds: { mode: 'absolute', steps }
});

const lokiTarget = (refId, expr, queryType = 'instant', hide = false, legendFormat = '') => ({
  refId,
  datasource: loki,
  expr,
  queryType,
  legendFormat,
  hide
});

const mathTarget = (refId, expression, hide = false) => ({
  refId,
  datasource: exprDs,
  type: 'math',
  expression,
  hide
});

// ── API cost instant query (shared base) ──────────────────────────────────
const apiCostExpr = `sum(sum_over_time(${stream} | unwrap cost_usd [$__range]))`;
// Prorated plan cost: plan_monthly / 30 * billing_days
const proratedPlanExpr = `\$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}`;

// ── panel builders ─────────────────────────────────────────────────────────
function row(id, title, y, description = '') {
  return {
    id,
    type: 'row',
    title,
    description,
    collapsed: false,
    gridPos: { x: 0, y, w: 24, h: 1 }
  };
}

// 6-panel hero stat — all share api_cost (A) and prorated_plan (B)
function heroStat(id, title, description, x, y, expression, unit, decimals, steps) {
  return {
    id,
    title,
    description,
    type: 'stat',
    gridPos: { x, y, w: 4, h: 7 },
    datasource: loki,
    targets: [
      lokiTarget('A', apiCostExpr, 'instant', true),
      mathTarget('B', proratedPlanExpr, true),
      mathTarget('C', expression)
    ],
    options: statOpts(['lastNotNull']),
    fieldConfig: {
      defaults: {
        unit,
        decimals,
        color: { mode: 'thresholds' },
        thresholds: { mode: 'absolute', steps }
      },
      overrides: []
    }
  };
}

// ── SECTION 1: Hero stats ──────────────────────────────────────────────────
const heroY = 1;

// Panel 1: True Cost (simple query, no math needed)
const panel1 = {
  id: 1,
  title: 'True Cost (API Retail)',
  description: 'What you would pay at Anthropic\'s published API rates if there were no subscription plan. This is your actual compute consumption cost.',
  type: 'stat',
  gridPos: { x: 0, y: heroY, w: 4, h: 7 },
  datasource: loki,
  targets: [lokiTarget('A', apiCostExpr, 'instant', false, 'API Retail Cost')],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: currencyDefaults([
      { value: null, color: '#00d4aa' },
      { value: 100, color: '#ffd166' },
      { value: 300, color: '#f72585' }
    ]),
    overrides: []
  }
};

// Panels 2-6: hero stats using shared A/B targets
const panel2 = {
  id: 2,
  title: 'Your Plan Cost (Prorated)',
  description: `Your flat Claude subscription cost prorated to the selected time window. Formula: plan_monthly / 30 × billing_days. Adjust the "Billing Days" variable to match your selected time range.`,
  type: 'stat',
  gridPos: { x: 4, y: heroY, w: 4, h: 7 },
  datasource: loki,
  targets: [
    lokiTarget('A', apiCostExpr, 'instant', true),
    mathTarget('B', proratedPlanExpr)
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: currencyDefaults([{ value: null, color: '#7c6ff7' }]),
    overrides: []
  }
};

const panel3 = heroStat(
  3,
  'VC Subsidy Covering You',
  'True API Cost minus prorated plan cost. Positive = Anthropic investors are covering your excess compute costs. Negative = you\'re under-utilizing your plan for this window.',
  8, heroY,
  '$A - $B',
  'currencyUSD', 2,
  [
    { value: null, color: '#7c6ff7' },
    { value: 0, color: '#00d4aa' },
    { value: 50, color: '#ffd166' },
    { value: 150, color: '#f72585' }
  ]
);

const panel4 = heroStat(
  4,
  'Subsidy Multiplier',
  'API retail cost ÷ prorated plan cost. 1.0 = break even. 3.6 = you got $3.60 of compute per $1 paid. Higher = more VC subsidy.',
  12, heroY,
  '$A / $B',
  'short', 2,
  [
    { value: null, color: '#7c6ff7' },
    { value: 1, color: '#00d4aa' },
    { value: 3, color: '#ffd166' },
    { value: 10, color: '#f72585' }
  ]
);

const panel5 = heroStat(
  5,
  'You Pay (¢ per $1 API)',
  'Your plan cost as a percentage of API retail cost. 28% = you paid 28 cents for every $1 of compute. Lower % = bigger VC subsidy.',
  16, heroY,
  '($B / $A) * 100',
  'percent', 1,
  [
    { value: null, color: '#f72585' },
    { value: 50, color: '#ffd166' },
    { value: 25, color: '#00d4aa' },
    { value: 10, color: '#7c6ff7' }
  ]
);

const panel6 = heroStat(
  6,
  'Annualized VC Subsidy',
  'If current subsidy rate continued for a full year. (True Cost − Plan Cost) / billing_days × 365.',
  20, heroY,
  '($A - $B) / ${billing_days} * 365',
  'currencyUSD', 0,
  [
    { value: null, color: '#7c6ff7' },
    { value: 0, color: '#00d4aa' },
    { value: 500, color: '#ffd166' },
    { value: 2000, color: '#f72585' }
  ]
);

// ── SECTION 2: Daily spend vs budget ──────────────────────────────────────
const sec2Y = heroY + 8;

const panel10 = {
  id: 10,
  title: 'Daily API Cost vs Daily Plan Budget',
  description: 'Purple bars = daily API-equivalent cost (true compute cost). Pink line = your daily plan budget (plan_monthly / 30). Bars above the line = days VCs are subsidizing you. Bars below = days you\'re under-utilizing your plan.',
  type: 'timeseries',
  gridPos: { x: 0, y: sec2Y + 1, w: 24, h: 9 },
  datasource: loki,
  interval: '1d',
  targets: [
    lokiTarget('A', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', false, 'Daily API Cost'),
    lokiTarget('B', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', true),
    mathTarget('C', '$B * 0 + ${plan_monthly_cost} / 30')
  ],
  options: {
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
  },
  fieldConfig: {
    defaults: {
      unit: 'currencyUSD',
      decimals: 2,
      custom: {
        drawStyle: 'bars',
        lineWidth: 1,
        fillOpacity: 80,
        gradientMode: 'none',
        spanNulls: false,
        stacking: { group: 'A', mode: 'none' },
        barMaxWidth: 60
      }
    },
    overrides: [
      {
        matcher: { id: 'byFrameRefID', options: 'A' },
        properties: [{ id: 'color', value: { mode: 'fixed', fixedColor: '#c77dff' } }]
      },
      {
        matcher: { id: 'byFrameRefID', options: 'C' },
        properties: [
          { id: 'displayName', value: 'Daily Plan Budget' },
          { id: 'custom.drawStyle', value: 'line' },
          { id: 'custom.lineWidth', value: 2 },
          { id: 'custom.fillOpacity', value: 0 },
          { id: 'color', value: { mode: 'fixed', fixedColor: '#f72585' } }
        ]
      }
    ]
  }
};

const panel11 = {
  id: 11,
  title: 'Hourly Burn Rate ($/hr)',
  description: 'API-equivalent cost rate per hour. Spikes = active heavy sessions. Flat periods = idle. Shows WHEN you consume compute, not just totals.',
  type: 'timeseries',
  gridPos: { x: 0, y: sec2Y + 11, w: 24, h: 8 },
  datasource: loki,
  interval: '6h',
  targets: [
    lokiTarget('A', `sum(rate(${stream} | unwrap cost_usd [$__interval])) * 3600`, 'range', false, '$/hr (API equiv)')
  ],
  options: {
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
  },
  fieldConfig: {
    defaults: {
      unit: 'currencyUSD',
      decimals: 4,
      color: { mode: 'fixed', fixedColor: '#c77dff' },
      custom: {
        drawStyle: 'bars',
        lineWidth: 1,
        fillOpacity: 85,
        gradientMode: 'none',
        spanNulls: false,
        stacking: { group: 'A', mode: 'normal' },
        barMaxWidth: 50
      }
    },
    overrides: []
  }
};

// ── SECTION 3: Where subsidy comes from ───────────────────────────────────
const sec3Y = sec2Y + 21;

const panel20 = {
  id: 20,
  title: 'API Cost by Model',
  description: 'Which model drives the most compute cost (= most VC subsidy). Opus-class models cost ~5x more per token than Sonnet.',
  type: 'piechart',
  gridPos: { x: 0, y: sec3Y + 1, w: 6, h: 9 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum by (model) (sum_over_time(${stream} | unwrap cost_usd [$__range]))`, 'instant', false, '{{model}}')
  ],
  options: {
    pieType: 'donut',
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'right', showLegend: true }
  },
  fieldConfig: { defaults: { unit: 'currencyUSD', decimals: 2 }, overrides: [] }
};

const panel21 = {
  id: 21,
  title: 'API Cost by Effort Level',
  description: 'High/Max effort sessions use extended thinking and generate more tokens — these are your biggest subsidy sources.',
  type: 'barchart',
  gridPos: { x: 6, y: sec3Y + 1, w: 9, h: 9 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum by (effort) (sum_over_time(${stream} | unwrap cost_usd [$__range]))`, 'instant', false, '{{effort}}')
  ],
  options: {
    barWidth: 0.6,
    groupWidth: 0.7,
    orientation: 'auto',
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true },
    xTickLabelRotation: 0
  },
  fieldConfig: {
    defaults: { unit: 'currencyUSD', decimals: 2, color: { mode: 'palette-classic' } },
    overrides: []
  }
};

const panel22 = {
  id: 22,
  title: 'API Cost by Project',
  description: 'Which projects consume the most API-equivalent budget.',
  type: 'barchart',
  gridPos: { x: 15, y: sec3Y + 1, w: 9, h: 9 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum by (project) (sum_over_time(${stream} | unwrap cost_usd [$__range]))`, 'instant', false, '{{project}}')
  ],
  options: {
    barWidth: 0.6,
    groupWidth: 0.7,
    orientation: 'auto',
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true },
    xTickLabelRotation: -20
  },
  fieldConfig: {
    defaults: { unit: 'currencyUSD', decimals: 2, color: { mode: 'palette-classic' } },
    overrides: []
  }
};

const panel23 = {
  id: 23,
  title: 'Sessions by Model',
  description: 'Volume of sessions per model. Compare with cost by model to see which model is cost-efficient vs expensive per session.',
  type: 'piechart',
  gridPos: { x: 0, y: sec3Y + 11, w: 6, h: 8 },
  datasource: loki,
  targets: [
    lokiTarget('A', `count by (model) (sum_over_time(${stream} | unwrap cost_usd [$__range]))`, 'instant', false, '{{model}}')
  ],
  options: {
    pieType: 'donut',
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'right', showLegend: true }
  },
  fieldConfig: { defaults: { unit: 'short' }, overrides: [] }
};

const panel24 = {
  id: 24,
  title: 'Weekly API Cost Breakdown',
  description: 'Week-by-week API cost bars vs weekly plan budget (plan_monthly / 4). Shows cadence of heavy vs light usage weeks.',
  type: 'timeseries',
  gridPos: { x: 6, y: sec3Y + 11, w: 18, h: 8 },
  datasource: loki,
  interval: '7d',
  targets: [
    lokiTarget('A', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', false, 'Weekly API Cost'),
    lokiTarget('B', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', true),
    mathTarget('C', '$B * 0 + ${plan_monthly_cost} / 4')
  ],
  options: {
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
  },
  fieldConfig: {
    defaults: {
      unit: 'currencyUSD',
      decimals: 2,
      custom: {
        drawStyle: 'bars',
        lineWidth: 1,
        fillOpacity: 80,
        gradientMode: 'none',
        spanNulls: false,
        stacking: { group: 'A', mode: 'none' },
        barMaxWidth: 80
      }
    },
    overrides: [
      {
        matcher: { id: 'byFrameRefID', options: 'A' },
        properties: [{ id: 'color', value: { mode: 'fixed', fixedColor: '#c77dff' } }]
      },
      {
        matcher: { id: 'byFrameRefID', options: 'C' },
        properties: [
          { id: 'displayName', value: 'Weekly Plan Budget (plan/4)' },
          { id: 'custom.drawStyle', value: 'line' },
          { id: 'custom.lineWidth', value: 2 },
          { id: 'custom.fillOpacity', value: 0 },
          { id: 'color', value: { mode: 'fixed', fixedColor: '#f72585' } }
        ]
      }
    ]
  }
};

// ── SECTION 4: Budget caps & utilization ──────────────────────────────────
const sec4Y = sec3Y + 21;

const panel30 = {
  id: 30,
  title: 'Monthly Budget Used (%)',
  description: '% of monthly plan cost consumed at API-retail rates. >100% = VC subsidized. Set time range to 30d for accurate monthly view.',
  type: 'gauge',
  gridPos: { x: 0, y: sec4Y + 1, w: 6, h: 8 },
  datasource: loki,
  targets: [
    lokiTarget('A', apiCostExpr, 'instant', true),
    mathTarget('B', '$A * 0 + ${plan_monthly_cost}', true),
    mathTarget('C', '($A / $B) * 100')
  ],
  options: {
    reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false },
    orientation: 'auto',
    textMode: 'auto',
    colorMode: 'thresholds',
    graphMode: 'none',
    justifyMode: 'center',
    displayMode: 'gradient',
    minVizWidth: 75,
    minVizHeight: 75
  },
  fieldConfig: {
    defaults: {
      unit: 'percent',
      decimals: 1,
      min: 0,
      max: 200,
      color: { mode: 'thresholds' },
      thresholds: {
        mode: 'absolute',
        steps: [
          { value: null, color: '#7c6ff7' },
          { value: 50, color: '#00d4aa' },
          { value: 100, color: '#ffd166' },
          { value: 150, color: '#f72585' }
        ]
      }
    },
    overrides: []
  }
};

const panel31 = {
  id: 31,
  title: 'Weekly API Spend vs Cap',
  description: 'Last 7 days API cost vs weekly plan budget (plan/4). >100% = active week, VCs covering the overage.',
  type: 'stat',
  gridPos: { x: 6, y: sec4Y + 1, w: 6, h: 8 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum(sum_over_time(${stream} | unwrap cost_usd [7d]))`, 'instant', false, 'Weekly API'),
    mathTarget('B', '$A * 0 + ${plan_monthly_cost} / 4', true),
    mathTarget('C', '($A / $B) * 100')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: {
      unit: 'percent',
      decimals: 1,
      color: { mode: 'thresholds' },
      thresholds: {
        mode: 'absolute',
        steps: [
          { value: null, color: '#7c6ff7' },
          { value: 50, color: '#00d4aa' },
          { value: 100, color: '#ffd166' },
          { value: 200, color: '#f72585' }
        ]
      }
    },
    overrides: []
  }
};

const panel32 = {
  id: 32,
  title: 'Avg Cost / Turn',
  description: 'Average API-retail cost per Claude response turn. Multiply by turn count to estimate session cost.',
  type: 'stat',
  gridPos: { x: 12, y: sec4Y + 1, w: 6, h: 8 },
  datasource: loki,
  targets: [
    lokiTarget('A', `avg(avg_over_time(${stream} | unwrap cost_usd [$__range]))`, 'instant', false, 'Avg/Turn')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: currencyDefaults([
      { value: null, color: '#00d4aa' },
      { value: 0.1, color: '#ffd166' },
      { value: 0.5, color: '#f72585' }
    ]),
    overrides: []
  }
};

const panel33 = {
  id: 33,
  title: 'Sessions to Exhaust Plan Value',
  description: 'How many sessions at current avg cost = your monthly plan cost. Below this = under-utilizing your subscription.',
  type: 'stat',
  gridPos: { x: 18, y: sec4Y + 1, w: 6, h: 8 },
  datasource: loki,
  targets: [
    lokiTarget('A', apiCostExpr, 'instant', true),
    lokiTarget('B', `count_over_time({app="claude-dev-logging"} | json | event="session_start" [$__range])`, 'instant', true),
    mathTarget('C', '${plan_monthly_cost} * $B / $A')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: {
      unit: 'short',
      decimals: 0,
      displayName: 'sessions',
      color: { mode: 'thresholds' },
      thresholds: {
        mode: 'absolute',
        steps: [
          { value: null, color: '#f72585' },
          { value: 5, color: '#ffd166' },
          { value: 20, color: '#00d4aa' }
        ]
      }
    },
    overrides: []
  }
};

// ── SECTION 5: Missed usage / forecast ────────────────────────────────────
const sec5Y = sec4Y + 10;

const panel40 = {
  id: 40,
  title: 'Remaining Plan Value',
  description: 'Prorated plan cost minus API cost used. Positive = unused plan value (you could use more). Negative = exceeded your plan\'s implied budget (VCs covering the rest).',
  type: 'stat',
  gridPos: { x: 0, y: sec5Y + 1, w: 8, h: 6 },
  datasource: loki,
  targets: [
    lokiTarget('A', apiCostExpr, 'instant', true),
    mathTarget('B', proratedPlanExpr, true),
    mathTarget('C', '$B - $A')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: currencyDefaults([
      { value: null, color: '#f72585' },
      { value: 0, color: '#ffd166' },
      { value: 30, color: '#7c6ff7' }
    ]),
    overrides: []
  }
};

const panel41 = {
  id: 41,
  title: 'Projected Monthly API Cost',
  description: 'If current burn rate continues for 30 days. Compare with your plan cost to see if you\'re on track to over- or under-use.',
  type: 'stat',
  gridPos: { x: 8, y: sec5Y + 1, w: 8, h: 6 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum(rate(${stream} | unwrap cost_usd [$__range])) * 86400 * 30`, 'instant', false, 'Projected Monthly')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: currencyDefaults([
      { value: null, color: '#00d4aa' },
      { value: 100, color: '#ffd166' },
      { value: 300, color: '#f72585' }
    ]),
    overrides: []
  }
};

const panel42 = {
  id: 42,
  title: 'Days Until API = Plan Cost',
  description: 'At current burn rate, how many days until your API-equivalent cost equals your monthly plan cost. If < days left in month, VCs will cover the rest.',
  type: 'stat',
  gridPos: { x: 16, y: sec5Y + 1, w: 8, h: 6 },
  datasource: loki,
  targets: [
    lokiTarget('A', `sum(rate(${stream} | unwrap cost_usd [$__range])) * 86400`, 'instant', true),
    mathTarget('B', '$A * 0 + ${plan_monthly_cost}', true),
    mathTarget('C', '$B / $A')
  ],
  options: statOpts(['lastNotNull']),
  fieldConfig: {
    defaults: {
      unit: 'd',
      decimals: 1,
      color: { mode: 'thresholds' },
      thresholds: {
        mode: 'absolute',
        steps: [
          { value: null, color: '#f72585' },
          { value: 7, color: '#ffd166' },
          { value: 30, color: '#00d4aa' }
        ]
      }
    },
    overrides: []
  }
};

const panel43 = {
  id: 43,
  title: 'Missed Usage Opportunity — Daily Gap',
  description: 'Purple bars = daily API cost (true compute). Pink line = daily plan budget (plan/30). Yellow bars = daily plan value NOT consumed (positive = under-utilizing). Purple above line = VC-subsidized day. Use this to see when you are leaving plan value on the table vs when VCs are covering you.',
  type: 'timeseries',
  gridPos: { x: 0, y: sec5Y + 8, w: 24, h: 10 },
  datasource: loki,
  interval: '1d',
  targets: [
    lokiTarget('A', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', false, 'Daily API Cost'),
    lokiTarget('B', `sum(sum_over_time(${stream} | unwrap cost_usd [$__interval]))`, 'range', true),
    mathTarget('C', '$B * 0 + ${plan_monthly_cost} / 30'),
    mathTarget('D', '($B * 0 + ${plan_monthly_cost} / 30) - $B')
  ],
  options: {
    tooltip: { mode: 'multi', sort: 'none' },
    legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
  },
  fieldConfig: {
    defaults: {
      unit: 'currencyUSD',
      decimals: 2,
      custom: {
        drawStyle: 'bars',
        lineWidth: 1,
        fillOpacity: 75,
        gradientMode: 'none',
        spanNulls: false,
        stacking: { group: 'A', mode: 'none' },
        barMaxWidth: 60
      }
    },
    overrides: [
      {
        matcher: { id: 'byFrameRefID', options: 'A' },
        properties: [{ id: 'color', value: { mode: 'fixed', fixedColor: '#c77dff' } }]
      },
      {
        matcher: { id: 'byFrameRefID', options: 'C' },
        properties: [
          { id: 'displayName', value: 'Daily Plan Budget' },
          { id: 'custom.drawStyle', value: 'line' },
          { id: 'custom.lineWidth', value: 2 },
          { id: 'custom.fillOpacity', value: 0 },
          { id: 'color', value: { mode: 'fixed', fixedColor: '#f72585' } }
        ]
      },
      {
        matcher: { id: 'byFrameRefID', options: 'D' },
        properties: [
          { id: 'displayName', value: 'Unused Plan Value (missed usage)' },
          { id: 'color', value: { mode: 'fixed', fixedColor: '#ffd166' } },
          { id: 'custom.fillOpacity', value: 40 }
        ]
      }
    ]
  }
};

// ── SECTION 6: Research text ───────────────────────────────────────────────
const sec6Y = sec5Y + 19;

const panel50 = {
  id: 50,
  title: 'What Would Claude Cost Without VC Funding?',
  type: 'text',
  gridPos: { x: 0, y: sec6Y + 1, w: 24, h: 8 },
  options: {
    mode: 'markdown',
    content: `## What Would Claude Cost Without VC Funding?

Without Anthropic's $7.3B in investor capital, you would pay **retail API prices** directly — exactly what the "True Cost (API Retail)" stat shows above.

**Anthropic's actual economics (2024–2025):**
- Gross margin: **−94% in 2024** → investors covered $0.94 of every $1 of API revenue in compute costs
- Annual investor burn: ~**$3 billion** keeping inference prices below true cost
- Max plan subscribers consumed **$1,000–$5,000/day** at API retail vs $100/month subscription
- Your **Subsidy Multiplier** tells you: for every $1 you paid, how many $1 of API compute did you receive?

**The subsidy shrinks over time:**
- Hardware efficiency + model compression drops inference cost ~**10x per year**
- Anthropic projects **77% gross margin by 2028** — API retail prices will reflect true cost by then
- When that happens, subscription plans will either cost more or throttle heavy users

**Bottom line:** The gap between "True Cost" and "Your Plan Cost" is exactly what VC capital is absorbing right now. The "Missed Usage Opportunity" chart shows which days you left plan value on the table vs which days VCs covered your compute.`,
    code: { language: 'plaintext', showLineNumbers: false }
  },
  fieldConfig: { defaults: {}, overrides: [] }
};

// ── ASSEMBLE DASHBOARD ─────────────────────────────────────────────────────
const dashboard = {
  id: null,
  uid: 'claude-subscription-economics',
  title: 'Claude AI — Subscription Economics',
  description: 'True compute cost vs plan cost vs VC subsidy. Shows what you\'d pay at API retail rates, what your plan costs (prorated), and how much Anthropic investors are absorbing. Set time range to 30d for monthly comparison. Adjust "Billing Days" variable to match your selected range.',
  tags: ['claude-code', 'cost', 'economics', 'observability'],
  timezone: 'browser',
  editable: true,
  graphTooltip: 1,
  time: { from: 'now-30d', to: 'now' },
  refresh: '5m',
  schemaVersion: 39,
  fiscalYearStartMonth: 0,
  liveNow: false,
  style: 'dark',
  templating: {
    list: [
      {
        name: 'model',
        label: 'Model',
        type: 'query',
        datasource: loki,
        query: '{app="claude-token-metrics"} | json',
        regex: '"model":"([^"]+)"',
        refresh: 2,
        includeAll: true,
        multi: true,
        allValue: '.*',
        current: { text: 'All', value: '$__all' },
        sort: 1
      },
      {
        name: 'project',
        label: 'Project',
        type: 'query',
        datasource: loki,
        query: '{app="claude-token-metrics"} | json',
        regex: '"project":"([^"]+)"',
        refresh: 2,
        includeAll: true,
        multi: true,
        allValue: '.*',
        current: { text: 'All', value: '$__all' },
        sort: 1
      },
      {
        name: 'effort',
        label: 'Effort',
        type: 'query',
        datasource: loki,
        query: '{app="claude-token-metrics"} | json',
        regex: '"effort":"([^"]+)"',
        refresh: 2,
        includeAll: true,
        multi: true,
        allValue: '.*',
        current: { text: 'All', value: '$__all' },
        sort: 1
      },
      {
        name: 'plan_monthly_cost',
        label: 'Plan ($/mo)',
        type: 'custom',
        query: 'Pro -- $20/mo : 20,Max 5x -- $100/mo : 100,Max 20x (Ultra) -- $200/mo : 200',
        options: [
          { selected: false, text: 'Pro -- $20/mo', value: '20' },
          { selected: true,  text: 'Max 5x -- $100/mo', value: '100' },
          { selected: false, text: 'Max 20x (Ultra) -- $200/mo', value: '200' }
        ],
        current: { selected: true, text: 'Max 5x -- $100/mo', value: '100' },
        hide: 0,
        includeAll: false,
        multi: false,
        skipUrlSync: false,
        description: 'Your Claude subscription tier. Controls plan cost used in all subsidy calculations.'
      },
      {
        name: 'billing_days',
        label: 'Billing Days in Range',
        type: 'textbox',
        query: '30',
        current: { text: '30', value: '30' },
        hide: 0,
        description: 'Number of days in your selected time range. Used to prorate the monthly plan cost. Default 30 = full month. Set to 7 if you selected a 7-day window.'
      }
    ]
  },
  panels: [
    // ── Section 1: Hero stats
    row(100, 'True Cost vs Plan Cost', 0,
      'True Cost = API retail price for every token consumed. Plan Cost = your flat subscription (prorated to selected window). The gap = VC subsidy. Adjust "Billing Days" to match your time range.'),
    panel1, panel2, panel3, panel4, panel5, panel6,

    // ── Section 2: Spend vs budget over time
    row(101, 'Spend vs Budget Over Time', sec2Y),
    panel10, panel11,

    // ── Section 3: Where subsidy comes from
    row(102, 'Where the Subsidy Comes From', sec3Y, 'Which models, effort levels, and projects generate the most API cost — and therefore the most VC subsidy.'),
    panel20, panel21, panel22, panel23, panel24,

    // ── Section 4: Budget caps
    row(103, 'Budget Caps & Utilization', sec4Y, 'Monthly, weekly, and per-session budget tracking.'),
    panel30, panel31, panel32, panel33,

    // ── Section 5: Missed usage / forecast
    row(104, 'Token Opportunity — Missed Usage Forecast', sec5Y,
      'Are you using your plan\'s full value? The "Missed Usage" chart shows days you left plan value on the table vs days VCs covered you.'),
    panel40, panel41, panel42, panel43,

    // ── Section 6: Research
    row(105, 'The Economics Behind the Subsidy', sec6Y),
    panel50
  ]
};

const outPath = 'observability/local/grafana/provisioning/dashboards/claude-subscription-economics.json';
fs.writeFileSync(outPath, JSON.stringify(dashboard, null, 2));
const check = JSON.parse(fs.readFileSync(outPath, 'utf8'));
console.log(`Written: ${outPath}`);
console.log(`Panels: ${check.panels.length}`);
console.log('JSON valid OK');
