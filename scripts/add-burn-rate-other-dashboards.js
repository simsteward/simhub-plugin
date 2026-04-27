#!/usr/bin/env node
'use strict';
const fs = require('fs');

const loki = { type: 'loki', uid: 'loki_local' };
const exprDs = { type: '__expr__' };

const statOpts = () => ({
  colorMode: 'background-gradient',
  graphMode: 'none',
  justifyMode: 'center',
  orientation: 'auto',
  textMode: 'value',
  text: { titleSize: 12, valueSize: 36 },
  reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
});

// ============================================================
// claude-intelligence.json — add burn rate row at the end
// ============================================================
{
  const path = 'observability/local/grafana/provisioning/dashboards/claude-intelligence.json';
  const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

  // find max y
  let maxY = 0;
  dash.panels.forEach(p => {
    const bottom = p.gridPos.y + p.gridPos.h;
    if (bottom > maxY) maxY = bottom;
  });
  const startY = maxY + 1;

  const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';

  dash.panels.push(
    {
      id: 200,
      type: 'row',
      title: 'Burn Rate — $/hr & Tokens/min',
      description: 'How fast you are consuming API-equivalent budget. Averages over the selected time window.',
      collapsed: false,
      gridPos: { x: 0, y: startY, w: 24, h: 1 }
    },
    {
      id: 201,
      title: 'Avg $/hour',
      description: 'API-equivalent spending rate per hour averaged over selected range.',
      type: 'stat',
      gridPos: { x: 0, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range])) * 3600`,
        legendFormat: '$/hr',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 4, color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 0.01, color: '#c77dff' },
            { value: 0.10, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 202,
      title: 'Avg $/minute',
      description: 'API-equivalent spending rate per minute averaged over selected range.',
      type: 'stat',
      gridPos: { x: 6, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__range])) * 60`,
        legendFormat: '$/min',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 5, color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 0.001, color: '#c77dff' },
            { value: 0.010, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 203,
      title: 'Output Tokens/min',
      description: 'Average Claude output token generation rate per minute.',
      type: 'stat',
      gridPos: { x: 12, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_output_tokens [$__range])) * 60`,
        legendFormat: 'tokens/min',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'short', decimals: 1, displayName: 'tokens/min',
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 500, color: '#ffd166' },
            { value: 2000, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 204,
      title: 'Total Tokens/min',
      description: 'Input + output token consumption rate per minute.',
      type: 'stat',
      gridPos: { x: 18, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [
        {
          refId: 'A',
          datasource: loki,
          expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_input_tokens [$__range])) * 60`,
          queryType: 'instant',
          hide: true
        },
        {
          refId: 'B',
          datasource: loki,
          expr: `sum(rate({app="claude-token-metrics"} | json | ${filters} | unwrap total_output_tokens [$__range])) * 60`,
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
          unit: 'short', decimals: 1, displayName: 'tokens/min',
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 1000, color: '#ffd166' },
            { value: 5000, color: '#f72585' }
          ]}
        }, overrides: []
      }
    }
  );

  fs.writeFileSync(path, JSON.stringify(dash, null, 2));
  JSON.parse(fs.readFileSync(path, 'utf8'));
  console.log('claude-intelligence.json OK, panels:', dash.panels.length);
}

// ============================================================
// claude-code-overview.json — add burn rate row at the end
// ============================================================
{
  const path = 'observability/local/grafana/provisioning/dashboards/claude-code-overview.json';
  const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

  let maxY = 0;
  dash.panels.forEach(p => {
    const bottom = p.gridPos.y + p.gridPos.h;
    if (bottom > maxY) maxY = bottom;
  });
  const startY = maxY + 1;

  dash.panels.push(
    {
      type: 'row',
      id: 50,
      title: 'Burn Rate — $/hr & Tokens/min',
      collapsed: false,
      gridPos: { x: 0, y: startY, w: 24, h: 1 }
    },
    {
      id: 51,
      title: 'Avg $/hour (This Session)',
      description: 'API-equivalent spending rate per hour for the selected time range.',
      type: 'stat',
      gridPos: { x: 0, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: 'sum(rate({app="claude-token-metrics"} | json | unwrap cost_usd [$__range])) * 3600',
        legendFormat: '$/hr',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 4, color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 0.01, color: '#c77dff' },
            { value: 0.10, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 52,
      title: 'Avg $/minute',
      description: 'API-equivalent spending rate per minute for the selected time range.',
      type: 'stat',
      gridPos: { x: 6, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: 'sum(rate({app="claude-token-metrics"} | json | unwrap cost_usd [$__range])) * 60',
        legendFormat: '$/min',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'currencyUSD', decimals: 5, color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 0.001, color: '#c77dff' },
            { value: 0.010, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 53,
      title: 'Output Tokens/min',
      description: 'Claude output token generation rate per minute.',
      type: 'stat',
      gridPos: { x: 12, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [{
        refId: 'A',
        datasource: loki,
        expr: 'sum(rate({app="claude-token-metrics"} | json | unwrap total_output_tokens [$__range])) * 60',
        legendFormat: 'tokens/min',
        queryType: 'instant'
      }],
      options: statOpts(),
      fieldConfig: {
        defaults: {
          unit: 'short', decimals: 1,
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 500, color: '#ffd166' },
            { value: 2000, color: '#f72585' }
          ]}
        }, overrides: []
      }
    },
    {
      id: 54,
      title: 'Total Tokens/min',
      description: 'Input + output token rate per minute.',
      type: 'stat',
      gridPos: { x: 18, y: startY + 1, w: 6, h: 5 },
      datasource: loki,
      targets: [
        {
          refId: 'A',
          datasource: loki,
          expr: 'sum(rate({app="claude-token-metrics"} | json | unwrap total_input_tokens [$__range])) * 60',
          queryType: 'instant',
          hide: true
        },
        {
          refId: 'B',
          datasource: loki,
          expr: 'sum(rate({app="claude-token-metrics"} | json | unwrap total_output_tokens [$__range])) * 60',
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
          unit: 'short', decimals: 1,
          color: { mode: 'thresholds' },
          thresholds: { mode: 'absolute', steps: [
            { value: null, color: '#00d4aa' },
            { value: 1000, color: '#ffd166' },
            { value: 5000, color: '#f72585' }
          ]}
        }, overrides: []
      }
    }
  );

  fs.writeFileSync(path, JSON.stringify(dash, null, 2));
  JSON.parse(fs.readFileSync(path, 'utf8'));
  console.log('claude-code-overview.json OK, panels:', dash.panels.length);
}
