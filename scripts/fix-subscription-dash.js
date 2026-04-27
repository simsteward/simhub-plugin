#!/usr/bin/env node
'use strict';
const fs = require('fs');

const loki = { type: 'loki', uid: 'loki_local' };
const exprDs = { type: '__expr__', uid: '__expr__' };

// ============================================================
// 1. claude-token-cost.json — fix panel 216 (Turn Cost Percentiles)
// ============================================================
{
  const path = 'observability/local/grafana/provisioning/dashboards/claude-token-cost.json';
  const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

  const p216 = dash.panels.find(p => p.id === 216);
  if (!p216) { console.error('Panel 216 not found'); process.exit(1); }

  const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';

  // Replace: stat panel with range queries → 3 separate stat panels side-by-side
  // Remove panel 216, add panels 216, 2161, 2162 in its place
  const idx = dash.panels.indexOf(p216);
  const { x, y, h } = p216.gridPos; // x=16, y=226, h=9

  const statOpts = {
    colorMode: 'background-gradient',
    graphMode: 'area',
    justifyMode: 'center',
    orientation: 'auto',
    textMode: 'value_and_name',
    text: { titleSize: 11, valueSize: 28 },
    reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
  };
  const fieldCfg = {
    defaults: {
      unit: 'currencyUSD', decimals: 4,
      color: { mode: 'thresholds' },
      thresholds: { mode: 'absolute', steps: [
        { value: null, color: '#00d4aa' },
        { value: 0.05, color: '#c77dff' },
        { value: 0.3, color: '#f72585' }
      ]}
    },
    overrides: []
  };

  const mkPercentilePanel = (id, title, desc, quantile, gridX, w) => ({
    id, title, description: desc, type: 'stat',
    gridPos: { x: gridX, y, w, h },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `quantile_over_time(${quantile}, {app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range])`,
      legendFormat: title,
      queryType: 'instant'
    }],
    options: statOpts,
    fieldConfig: fieldCfg
  });

  const replacement = [
    mkPercentilePanel(216,  'P50 Median Cost',   'Median per-turn cost. Half your turns cost less than this.',        0.50, x,   3),
    mkPercentilePanel(2161, 'P90 Turn Cost',     '90th percentile. 10% of turns cost more than this.',               0.90, x+3, 3),
    mkPercentilePanel(2162, 'P99 Turn Cost',     '99th percentile — tail cost. Agentic runaway turns show up here.', 0.99, x+6, 2)
  ];

  dash.panels.splice(idx, 1, ...replacement);
  fs.writeFileSync(path, JSON.stringify(dash, null, 2));
  JSON.parse(fs.readFileSync(path, 'utf8'));
  console.log('claude-token-cost.json: panel 216 split into 3 percentile stats. Total panels:', dash.panels.length);
}

// ============================================================
// 2. claude-subscription-economics.json — fix budget panels + add weekly/monthly gaps
// ============================================================
{
  const path = 'observability/local/grafana/provisioning/dashboards/claude-subscription-economics.json';
  const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

  const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';

  // ── Fix panel 30 (Monthly Budget Used gauge) — raise max, improve thresholds
  const p30 = dash.panels.find(p => p.id === 30);
  if (p30) {
    p30.title = 'Plan Value Claimed (%)';
    p30.description = 'API-retail cost as % of monthly plan cost. 100% = you broke even. 350% = VCs covered 2.5x your plan. Higher is more subsidy — not a problem.';
    p30.fieldConfig.defaults.max = 600;
    p30.fieldConfig.defaults.thresholds.steps = [
      { value: null, color: '#7c6ff7' },   // under 50% = under-using (purple)
      { value: 50, color: '#00d4aa' },    // 50-100% = using plan value (teal)
      { value: 100, color: '#ffd166' },   // 100-200% = subsidized 1-2x (yellow)
      { value: 200, color: '#c77dff' },   // 200-400% = heavy subsidy (purple)
      { value: 400, color: '#f72585' }    // >400% = max subsidy (pink)
    ];
  }

  // ── Fix panel 31 (Weekly API Spend vs Cap) — hide the raw $ series, show only %
  const p31 = dash.panels.find(p => p.id === 31);
  if (p31) {
    p31.title = 'Weekly Plan Value (%)';
    p31.description = 'Last 7d API cost as % of weekly plan budget (plan/4). >100% = VCs covering weekly overage.';
    // Make target A hidden, only show C (the %)
    const tA = p31.targets.find(t => t.refId === 'A');
    if (tA) { tA.hide = true; tA.legendFormat = ''; }
    p31.options.textMode = 'value_and_name';
  }

  // ── Fix panel 10 (Daily API Cost vs Daily Plan Budget) — update description to clarify framing
  const p10 = dash.panels.find(p => p.id === 10);
  if (p10) {
    p10.description = 'Purple bars = daily API-equivalent cost (what VCs compute costs). Pink line = what you pay per day ($plan/30). Bars ABOVE the line = VC subsidy in action. Bars BELOW the line = unused plan value (opportunity left on the table).';
    // Also update the C override label to show the $ amount clearly
    const cOverride = p10.fieldConfig.overrides.find(o => o.matcher.options === 'C');
    if (cOverride) {
      const nameProp = cOverride.properties.find(p => p.id === 'displayName');
      if (nameProp) nameProp.value = 'What You Pay/Day (plan÷30)';
    }
  }

  // ── Fix panel 24 (Weekly API Cost Breakdown) — fix C label, add gap series D
  const p24 = dash.panels.find(p => p.id === 24);
  if (p24) {
    // Update C override label
    const cOverride = p24.fieldConfig.overrides.find(o => o.matcher.options === 'C');
    if (cOverride) {
      const nameProp = cOverride.properties.find(p => p.id === 'displayName');
      if (nameProp) nameProp.value = 'What You Pay/Week (plan÷4)';
    }
    // Add D: weekly gap (API - plan/4) = positive means VC subsidy, negative means under-using
    p24.targets.push({
      refId: 'D', datasource: exprDs,
      type: 'math',
      expression: '$B - ($B * 0 + ${plan_monthly_cost} / 4)',
      hide: false
    });
    // Add override for D: yellow bars for gap
    p24.fieldConfig.overrides.push({
      matcher: { id: 'byFrameRefID', options: 'D' },
      properties: [
        { id: 'displayName', value: 'Weekly Gap (API − Plan Budget)' },
        { id: 'color', value: { mode: 'fixed', fixedColor: '#ffd166' } },
        { id: 'custom.fillOpacity', value: 50 }
      ]
    });
  }

  // ── Fix panel 43 (Missed Usage Opportunity — Daily Gap) — fix D label
  const p43 = dash.panels.find(p => p.id === 43);
  if (p43) {
    const dOverride = p43.fieldConfig.overrides.find(o => o.matcher.options === 'D');
    if (dOverride) {
      const nameProp = dOverride.properties.find(p => p.id === 'displayName');
      if (nameProp) nameProp.value = 'Daily Unused Budget (positive = under-utilized)';
    }
  }

  // ── Find the opportunity row (id=104) to add new panels after it
  const row104 = dash.panels.find(p => p.id === 104);
  const row104Y = row104 ? row104.gridPos.y : 61;

  // Current panels in the opportunity section:
  // p40 (y=62, h=6), p41 (y=62, h=6), p42 (y=62, h=6) → row ends at y=68
  // p43 (y=69, h=10) → ends at y=79
  // row 105 is at y=80

  // Add 3 new stats: weekly gap, monthly gap, monthly cumulative gauge
  // Place them at y=62 row (w=8 each = 24 total across)
  // But wait — p40/41/42 are already at y=62 filling x=0..24 with w=8 each
  // So put new panels AFTER p43 at y=79

  // Actually let's reorganize: move p40/41/42 up to y=62 (they're fine),
  // add new panels at y=68 (after p40 row) before p43

  // Let's insert 3 new gap stats between p40/41/42 row and p43
  // p40 row ends at y=68, p43 starts at y=69
  // Insert 3 stats at y=68 (h=6), shift p43 down to y=74
  if (p43) {
    p43.gridPos.y = 75;
  }

  // Shift row 105 and panel 50 down by 6
  const row105 = dash.panels.find(p => p.id === 105);
  if (row105) row105.gridPos.y = 86;
  const p50 = dash.panels.find(p => p.id === 50);
  if (p50) p50.gridPos.y = 87;

  const statOpts = (titleSize=11, valueSize=28) => ({
    colorMode: 'background-gradient',
    graphMode: 'none',
    justifyMode: 'center',
    orientation: 'auto',
    textMode: 'value_and_name',
    text: { titleSize, valueSize },
    reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
  });

  const newGapPanels = [
    // Weekly opportunity gap stat
    {
      id: 44,
      title: 'Weekly Gap (API vs Plan)',
      description: 'Last 7d: API cost minus weekly plan budget (plan/4). Positive = VC-subsidized. Negative = under-utilizing your plan this week.',
      type: 'stat',
      gridPos: { x: 0, y: 69, w: 8, h: 6 },
      datasource: loki,
      targets: [
        {
          refId: 'A', datasource: loki,
          expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [7d]))`,
          queryType: 'instant', legendFormat: '', hide: true
        },
        {
          refId: 'B', datasource: exprDs,
          type: 'math', expression: `$A * 0 + \${plan_monthly_cost} / 4`,
          hide: true
        },
        {
          refId: 'C', datasource: exprDs,
          type: 'math', expression: '$A - $B',
          hide: false
        }
      ],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 2,
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#7c6ff7' }, // negative = under-using
            { value: 0, color: '#00d4aa' },    // slight overage = good
            { value: 20, color: '#ffd166' },
            { value: 50, color: '#f72585' }
          ]}
        },
        overrides: []
      }
    },

    // Monthly opportunity gap stat (vs full plan cost, not prorated)
    {
      id: 45,
      title: 'Monthly Gap (API vs Plan)',
      description: 'API cost over selected range vs your full monthly plan cost. Positive = subsidy received. Negative = plan value not yet claimed. (Set time range to 30d for accurate monthly view.)',
      type: 'stat',
      gridPos: { x: 8, y: 69, w: 8, h: 6 },
      datasource: loki,
      targets: [
        {
          refId: 'A', datasource: loki,
          expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
          queryType: 'instant', legendFormat: '', hide: true
        },
        {
          refId: 'B', datasource: exprDs,
          type: 'math', expression: `$A * 0 + \${plan_monthly_cost}`,
          hide: true
        },
        {
          refId: 'C', datasource: exprDs,
          type: 'math', expression: '$A - $B',
          hide: false
        }
      ],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 2,
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#7c6ff7' }, // negative = under-using
            { value: 0, color: '#00d4aa' },
            { value: 50, color: '#ffd166' },
            { value: 150, color: '#f72585' }
          ]}
        },
        overrides: []
      }
    },

    // Unused plan value (monthly plan - API cost) - shows what's LEFT on the table
    {
      id: 46,
      title: 'Unclaimed Plan Value',
      description: 'Prorated plan budget minus API cost used. Positive = plan dollars not yet consumed (you could use more). Negative = exceeding plan (VCs covering the rest).',
      type: 'stat',
      gridPos: { x: 16, y: 69, w: 8, h: 6 },
      datasource: loki,
      targets: [
        {
          refId: 'A', datasource: loki,
          expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
          queryType: 'instant', legendFormat: '', hide: true
        },
        {
          refId: 'B', datasource: exprDs,
          type: 'math', expression: `$A * 0 + \${plan_monthly_cost} / 30 * \${billing_days}`,
          hide: true
        },
        {
          refId: 'C', datasource: exprDs,
          type: 'math', expression: '$B - $A',
          hide: false
        }
      ],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 2,
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#f72585' }, // negative = subsidized (good)
            { value: 0, color: '#ffd166' },    // break-even
            { value: 30, color: '#7c6ff7' }    // positive = under-using (leave on table)
          ]}
        },
        overrides: []
      }
    }
  ];

  // Also add a weekly opportunity chart (like the daily one but at 7d granularity)
  const weeklyOpportunityChart = {
    id: 47,
    title: 'Weekly Opportunity Gap — API Cost vs Plan Budget',
    description: 'Purple bars = weekly API cost. Pink line = weekly plan budget (plan/4). Yellow bars = weekly unused plan value (positive = under-utilized that week). Stacked above pink = VC subsidy week.',
    type: 'timeseries',
    gridPos: { x: 0, y: 75, w: 24, h: 9 },
    datasource: loki,
    interval: '7d',
    targets: [
      {
        refId: 'A', datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval]))`,
        queryType: 'range', legendFormat: 'Weekly API Cost', hide: false
      },
      {
        refId: 'B', datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval]))`,
        queryType: 'range', legendFormat: '', hide: true
      },
      {
        refId: 'C', datasource: exprDs,
        type: 'math', expression: `$B * 0 + \${plan_monthly_cost} / 4`,
        hide: false
      },
      {
        refId: 'D', datasource: exprDs,
        type: 'math', expression: `($B * 0 + \${plan_monthly_cost} / 4) - $B`,
        hide: false
      }
    ],
    options: {
      tooltip: { mode: 'multi', sort: 'none' },
      legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
    },
    fieldConfig: {
      defaults: {
        unit: 'currencyUSD', decimals: 2,
        custom: {
          drawStyle: 'bars', lineWidth: 1, fillOpacity: 75,
          gradientMode: 'none', spanNulls: false,
          stacking: { group: 'A', mode: 'none' }, barMaxWidth: 80
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
            { id: 'displayName', value: 'What You Pay/Week (plan÷4)' },
            { id: 'custom.drawStyle', value: 'line' },
            { id: 'custom.lineWidth', value: 2 },
            { id: 'custom.fillOpacity', value: 0 },
            { id: 'color', value: { mode: 'fixed', fixedColor: '#f72585' } }
          ]
        },
        {
          matcher: { id: 'byFrameRefID', options: 'D' },
          properties: [
            { id: 'displayName', value: 'Weekly Unused Budget (missed)' },
            { id: 'color', value: { mode: 'fixed', fixedColor: '#ffd166' } },
            { id: 'custom.fillOpacity', value: 40 }
          ]
        }
      ]
    }
  };

  // Insert new gap panels (44, 45, 46) after p43's original position (y=69)
  // and the weekly chart (47) at y=75 (before the old p43 which moved to y=75... wait)
  // p43 is now at y=75, so put 47 AFTER p43 at y=85
  weeklyOpportunityChart.gridPos.y = 86;  // after p43 (y=75, h=10 → ends at 85)
  if (row105) row105.gridPos.y = 96;
  if (p50) p50.gridPos.y = 97;

  // Push new panels into dash
  dash.panels.push(...newGapPanels, weeklyOpportunityChart);

  fs.writeFileSync(path, JSON.stringify(dash, null, 2));
  JSON.parse(fs.readFileSync(path, 'utf8'));
  console.log('claude-subscription-economics.json updated. Total panels:', dash.panels.length);
}
