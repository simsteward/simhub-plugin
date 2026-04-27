#!/usr/bin/env node
'use strict';
const fs = require('fs');
const dash = JSON.parse(fs.readFileSync('observability/local/grafana/provisioning/dashboards/claude-token-cost.json', 'utf8'));

const loki = { type: 'loki', uid: 'loki_local' };
const exprDs = { type: '__expr__' };

const currencyField = (steps) => ({
  defaults: {
    unit: 'currencyUSD',
    decimals: 4,
    color: { mode: 'thresholds' },
    thresholds: { mode: 'absolute', steps }
  },
  overrides: []
});

const statOpts = () => ({
  colorMode: 'background-gradient',
  graphMode: 'none',
  justifyMode: 'center',
  orientation: 'auto',
  textMode: 'value',
  text: { titleSize: 12, valueSize: 36 },
  reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
});

const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';

const newPanels = [

  // ===== BURN RATE ROW =====
  {
    id: 220,
    type: 'row',
    title: 'Burn Rate — $/hr & Tokens/min',
    description: 'How fast you are spending at API-equivalent rates. Rate queries average over the selected time window.',
    collapsed: false,
    gridPos: { x: 0, y: 235, w: 24, h: 1 }
  },

  {
    id: 221,
    title: 'Avg $/hour',
    description: 'Average API-equivalent spending rate per hour. rate(cost_usd) * 3600 over selected range.',
    type: 'stat',
    gridPos: { x: 0, y: 236, w: 6, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A',
      datasource: loki,
      expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range])) * 3600`,
      legendFormat: '$/hr',
      queryType: 'instant'
    }],
    options: statOpts(),
    fieldConfig: currencyField([
      { value: null, color: '#00d4aa' },
      { value: 0.01, color: '#c77dff' },
      { value: 0.10, color: '#f72585' }
    ])
  },

  {
    id: 222,
    title: 'Avg $/minute',
    description: 'Average API-equivalent spending rate per minute. rate(cost_usd) * 60 over selected range.',
    type: 'stat',
    gridPos: { x: 6, y: 236, w: 6, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A',
      datasource: loki,
      expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range])) * 60`,
      legendFormat: '$/min',
      queryType: 'instant'
    }],
    options: statOpts(),
    fieldConfig: currencyField([
      { value: null, color: '#00d4aa' },
      { value: 0.001, color: '#c77dff' },
      { value: 0.010, color: '#f72585' }
    ])
  },

  {
    id: 223,
    title: 'Output Tokens/min',
    description: 'Average output token generation rate per minute across the selected range.',
    type: 'stat',
    gridPos: { x: 12, y: 236, w: 6, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A',
      datasource: loki,
      expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_output_tokens [$__range])) * 60`,
      legendFormat: 'output tokens/min',
      queryType: 'instant'
    }],
    options: statOpts(),
    fieldConfig: {
      defaults: {
        unit: 'short',
        decimals: 1,
        displayName: 'tokens/min',
        color: { mode: 'thresholds' },
        thresholds: {
          mode: 'absolute',
          steps: [
            { value: null, color: '#00d4aa' },
            { value: 500, color: '#ffd166' },
            { value: 2000, color: '#f72585' }
          ]
        }
      },
      overrides: []
    }
  },

  {
    id: 224,
    title: 'Total Tokens/min',
    description: 'Combined input + output token rate per minute. Proxy for active context consumption rate.',
    type: 'stat',
    gridPos: { x: 18, y: 236, w: 6, h: 5 },
    datasource: loki,
    targets: [
      {
        refId: 'A',
        datasource: loki,
        expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_input_tokens [$__range])) * 60`,
        legendFormat: 'input/min',
        queryType: 'instant',
        hide: true
      },
      {
        refId: 'B',
        datasource: loki,
        expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_output_tokens [$__range])) * 60`,
        legendFormat: 'output/min',
        queryType: 'instant',
        hide: true
      },
      {
        refId: 'C',
        datasource: exprDs,
        model: { type: 'math', expression: '$A + $B', refId: 'C' }
      }
    ],
    options: statOpts(),
    fieldConfig: {
      defaults: {
        unit: 'short',
        decimals: 1,
        displayName: 'tokens/min',
        color: { mode: 'thresholds' },
        thresholds: {
          mode: 'absolute',
          steps: [
            { value: null, color: '#00d4aa' },
            { value: 1000, color: '#ffd166' },
            { value: 5000, color: '#f72585' }
          ]
        }
      },
      overrides: []
    }
  },

  // Burn rate timeseries
  {
    id: 225,
    title: 'Cost Rate Over Time ($/hr)',
    description: 'Rolling hourly cost rate. Each bar = API-equivalent cost rate at that interval. Spikes = heavy sessions.',
    type: 'timeseries',
    gridPos: { x: 0, y: 241, w: 24, h: 8 },
    datasource: loki,
    interval: '6h',
    targets: [{
      refId: 'A',
      datasource: loki,
      expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval])) * 3600`,
      legendFormat: '$/hr (API equiv)',
      queryType: 'range'
    }],
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
        },
        thresholds: {
          mode: 'absolute',
          steps: [{ value: null, color: '#00d4aa' }, { value: 0.1, color: '#f72585' }]
        }
      },
      overrides: []
    }
  },

  // ===== VC SUBSIDY ROW =====
  {
    id: 226,
    type: 'row',
    title: 'True Cost vs Plan Cost — VC Subsidy',
    description: 'How much Anthropic investors are subsidizing your usage. API retail price is the true cost floor; your flat plan fee is what you pay. The gap = VC subsidy.',
    collapsed: false,
    gridPos: { x: 0, y: 250, w: 24, h: 1 }
  },

  {
    id: 227,
    title: 'API Equiv. (This Range)',
    description: 'What you would pay at retail API rates. Compare with your flat plan cost to see subsidy. Set time range to last 30d for monthly view.',
    type: 'stat',
    gridPos: { x: 0, y: 251, w: 6, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A',
      datasource: loki,
      expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
      legendFormat: 'API Equiv.',
      queryType: 'instant'
    }],
    options: statOpts(),
    fieldConfig: currencyField([
      { value: null, color: '#00d4aa' },
      { value: 50, color: '#ffd166' },
      { value: 100, color: '#f72585' }
    ])
  },

  {
    id: 228,
    title: 'Plan Monthly Cost',
    description: 'Your flat Claude subscription cost. Controlled by the Plan dropdown. Fixed regardless of API usage.',
    type: 'stat',
    gridPos: { x: 6, y: 251, w: 6, h: 5 },
    datasource: loki,
    targets: [
      {
        refId: 'A',
        datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
        legendFormat: 'base',
        queryType: 'instant',
        hide: true
      },
      {
        refId: 'B',
        datasource: exprDs,
        model: { type: 'math', expression: '$A * 0 + ${plan_monthly_cost}', refId: 'B' }
      }
    ],
    options: statOpts(),
    fieldConfig: currencyField([
      { value: null, color: '#7c6ff7' }
    ])
  },

  {
    id: 229,
    title: 'VC Subsidy Received',
    description: 'API_equiv - plan_monthly. Positive = Anthropic investors paid the difference. Negative = under budget (good saving headroom).',
    type: 'stat',
    gridPos: { x: 12, y: 251, w: 6, h: 5 },
    datasource: loki,
    targets: [
      {
        refId: 'A',
        datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
        legendFormat: 'api_equiv',
        queryType: 'instant',
        hide: true
      },
      {
        refId: 'B',
        datasource: exprDs,
        model: { type: 'math', expression: '$A * 0 + ${plan_monthly_cost}', refId: 'B', hide: true }
      },
      {
        refId: 'C',
        datasource: exprDs,
        model: { type: 'math', expression: '$A - $B', refId: 'C' }
      }
    ],
    options: statOpts(),
    fieldConfig: currencyField([
      { value: null, color: '#f72585' },
      { value: 0, color: '#ffd166' },
      { value: 50, color: '#f72585' }
    ])
  },

  {
    id: 230,
    title: 'Subsidy Multiplier',
    description: 'API_equiv / plan_monthly. >1 = more value than you paid. Heavy Claude Code users often see 5x-50x. Powered by Anthropic VC burn.',
    type: 'stat',
    gridPos: { x: 18, y: 251, w: 6, h: 5 },
    datasource: loki,
    targets: [
      {
        refId: 'A',
        datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range]))`,
        legendFormat: 'api_equiv',
        queryType: 'instant',
        hide: true
      },
      {
        refId: 'B',
        datasource: exprDs,
        model: { type: 'math', expression: '$A * 0 + ${plan_monthly_cost}', refId: 'B', hide: true }
      },
      {
        refId: 'C',
        datasource: exprDs,
        model: { type: 'math', expression: '$A / $B', refId: 'C' }
      }
    ],
    options: statOpts(),
    fieldConfig: {
      defaults: {
        unit: 'short',
        decimals: 1,
        displayName: 'x',
        color: { mode: 'thresholds' },
        thresholds: {
          mode: 'absolute',
          steps: [
            { value: null, color: '#7c6ff7' },
            { value: 1, color: '#00d4aa' },
            { value: 3, color: '#ffd166' },
            { value: 10, color: '#f72585' }
          ]
        }
      },
      overrides: []
    }
  },

  // Daily API cost vs daily plan budget
  {
    id: 231,
    title: 'Daily API Cost vs Daily Plan Budget',
    description: 'Purple bars = actual API-equivalent cost per day. Pink line = your daily plan budget (plan_monthly / 30). Bars above line = VC-subsidized days.',
    type: 'timeseries',
    gridPos: { x: 0, y: 257, w: 24, h: 9 },
    datasource: loki,
    interval: '1d',
    targets: [
      {
        refId: 'A',
        datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval]))`,
        legendFormat: 'Daily API Cost',
        queryType: 'range'
      },
      {
        refId: 'B',
        datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval]))`,
        legendFormat: 'base',
        queryType: 'range',
        hide: true
      },
      {
        refId: 'C',
        datasource: exprDs,
        model: { type: 'math', expression: '$B * 0 + ${plan_monthly_cost} / 30', refId: 'C' }
      }
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
          matcher: { id: 'byFrameRefID', options: 'A' },
          properties: [
            { id: 'color', value: { mode: 'fixed', fixedColor: '#c77dff' } }
          ]
        }
      ]
    }
  }
];

dash.panels.push(...newPanels);
fs.writeFileSync('observability/local/grafana/provisioning/dashboards/claude-token-cost.json', JSON.stringify(dash, null, 2));
console.log('Done. Total panels:', dash.panels.length);
// Validate
JSON.parse(fs.readFileSync('observability/local/grafana/provisioning/dashboards/claude-token-cost.json', 'utf8'));
console.log('JSON valid OK');
