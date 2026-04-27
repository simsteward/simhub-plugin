#!/usr/bin/env node
'use strict';
const fs = require('fs');

const loki = { type: 'loki', uid: 'loki_local' };
const exprDs = { type: '__expr__', uid: '__expr__' };
const path = 'observability/local/grafana/provisioning/dashboards/claude-intelligence.json';
const dash = JSON.parse(fs.readFileSync(path, 'utf8'));

const filters = 'model=~"$model" | project=~"$project" | effort=~"$effort"';
const startY = 64;

// Tool timing panels — queries against claude-dev-logging for per-tool breakdown
// and claude-token-metrics for the new tool_time_ms field (correlation with tokens)
const newPanels = [
  // ── Row ────────────────────────────────────────────────────
  {
    id: 300,
    type: 'row',
    title: 'Tool Performance — Duration & Cost Correlation',
    description: 'How long each tool type takes. Correlates tool execution time with token cost per turn.',
    collapsed: false,
    gridPos: { x: 0, y: startY, w: 24, h: 1 }
  },

  // Avg duration by tool (horizontal bar chart)
  {
    id: 301,
    title: 'Avg Duration by Tool (ms)',
    description: 'Average wall-clock milliseconds per tool call, broken down by tool name. Slowest tools = biggest opportunity for parallelization.',
    type: 'barchart',
    gridPos: { x: 0, y: startY + 1, w: 12, h: 10 },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `avg by (tool_name) (avg_over_time({app="claude-dev-logging"} | json | hook_type="post-tool-use" | tool_name!="" | ${filters} | unwrap duration_ms [$__range]))`,
      queryType: 'instant',
      legendFormat: '{{tool_name}}',
      hide: false
    }],
    options: {
      barWidth: 0.6,
      groupWidth: 0.7,
      orientation: 'horizontal',
      tooltip: { mode: 'multi', sort: 'desc' },
      legend: { displayMode: 'list', placement: 'bottom', showLegend: false },
      xTickLabelRotation: 0
    },
    fieldConfig: {
      defaults: {
        unit: 'ms',
        decimals: 0,
        color: { mode: 'palette-classic' }
      },
      overrides: []
    }
  },

  // Call count by tool (horizontal bar chart)
  {
    id: 302,
    title: 'Call Count by Tool',
    description: 'Total number of calls per tool in the selected time range. High counts = frequently used tools.',
    type: 'barchart',
    gridPos: { x: 12, y: startY + 1, w: 12, h: 10 },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `sum by (tool_name) (count_over_time({app="claude-dev-logging"} | json | hook_type="post-tool-use" | tool_name!="" | ${filters} [$__range]))`,
      queryType: 'instant',
      legendFormat: '{{tool_name}}',
      hide: false
    }],
    options: {
      barWidth: 0.6,
      groupWidth: 0.7,
      orientation: 'horizontal',
      tooltip: { mode: 'multi', sort: 'desc' },
      legend: { displayMode: 'list', placement: 'bottom', showLegend: false },
      xTickLabelRotation: 0
    },
    fieldConfig: {
      defaults: {
        unit: 'short',
        decimals: 0,
        color: { mode: 'palette-classic' }
      },
      overrides: []
    }
  },

  // Tool time vs Token cost per turn (timeseries dual overlay)
  {
    id: 303,
    title: 'Tool Wall-Clock Time vs Token Cost per Turn',
    description: 'Purple bars = total tool execution time per turn (ms). Teal line = API cost per turn (USD). Turns with high tool time but low cost = I/O bound. High cost but low tool time = LLM compute bound.',
    type: 'timeseries',
    gridPos: { x: 0, y: startY + 11, w: 24, h: 9 },
    datasource: loki,
    interval: '6h',
    targets: [
      {
        refId: 'A', datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap tool_time_ms [$__interval]))`,
        queryType: 'range',
        legendFormat: 'Tool Time (ms)',
        hide: false
      },
      {
        refId: 'B', datasource: loki,
        expr: `sum(sum_over_time({app="claude-token-metrics"} | json | ${filters} | unwrap cost_usd [$__interval]))`,
        queryType: 'range',
        legendFormat: 'API Cost (USD)',
        hide: false
      }
    ],
    options: {
      tooltip: { mode: 'multi', sort: 'none' },
      legend: { displayMode: 'list', placement: 'bottom', showLegend: true }
    },
    fieldConfig: {
      defaults: {
        unit: 'ms',
        decimals: 0,
        color: { mode: 'palette-classic' },
        custom: {
          drawStyle: 'bars',
          lineWidth: 1,
          fillOpacity: 75,
          gradientMode: 'none',
          spanNulls: false,
          stacking: { group: 'A', mode: 'normal' },
          barMaxWidth: 50
        }
      },
      overrides: [
        {
          matcher: { id: 'byFrameRefID', options: 'A' },
          properties: [
            { id: 'color', value: { mode: 'fixed', fixedColor: '#c77dff' } }
          ]
        },
        {
          matcher: { id: 'byFrameRefID', options: 'B' },
          properties: [
            { id: 'unit', value: 'currencyUSD' },
            { id: 'decimals', value: 4 },
            { id: 'custom.drawStyle', value: 'line' },
            { id: 'custom.lineWidth', value: 2 },
            { id: 'custom.fillOpacity', value: 0 },
            { id: 'color', value: { mode: 'fixed', fixedColor: '#00d4aa' } },
            { id: 'custom.axisPlacement', value: 'right' }
          ]
        }
      ]
    }
  },

  // Top 3 tool call stats
  {
    id: 304,
    title: 'Total Tool Overhead (This Range)',
    description: 'Sum of all tool wall-clock time. Divide by total API cost to see what % of session time was in tool execution vs LLM compute.',
    type: 'stat',
    gridPos: { x: 0, y: startY + 20, w: 8, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `sum(sum_over_time({app="claude-dev-logging"} | json | hook_type="post-tool-use" | tool_name!="" | ${filters} | unwrap duration_ms [$__range]))`,
      queryType: 'instant',
      legendFormat: 'Total Tool Time',
      hide: false
    }],
    options: {
      colorMode: 'background-gradient',
      graphMode: 'none',
      justifyMode: 'center',
      orientation: 'auto',
      textMode: 'value_and_name',
      text: { titleSize: 11, valueSize: 28 },
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
    },
    fieldConfig: {
      defaults: {
        unit: 'ms',
        decimals: 0,
        color: { mode: 'thresholds' },
        thresholds: { mode: 'absolute', steps: [
          { value: null, color: '#00d4aa' },
          { value: 30000, color: '#ffd166' },
          { value: 120000, color: '#f72585' }
        ]}
      },
      overrides: []
    }
  },

  {
    id: 305,
    title: 'Avg Tool Duration per Call',
    description: 'Mean duration across all tool calls. Spikes indicate slow external services (MCP, file I/O, etc.).',
    type: 'stat',
    gridPos: { x: 8, y: startY + 20, w: 8, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `avg(avg_over_time({app="claude-dev-logging"} | json | hook_type="post-tool-use" | tool_name!="" | ${filters} | unwrap duration_ms [$__range]))`,
      queryType: 'instant',
      legendFormat: 'Avg Duration',
      hide: false
    }],
    options: {
      colorMode: 'background-gradient',
      graphMode: 'none',
      justifyMode: 'center',
      orientation: 'auto',
      textMode: 'value_and_name',
      text: { titleSize: 11, valueSize: 28 },
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
    },
    fieldConfig: {
      defaults: {
        unit: 'ms',
        decimals: 0,
        color: { mode: 'thresholds' },
        thresholds: { mode: 'absolute', steps: [
          { value: null, color: '#00d4aa' },
          { value: 1000, color: '#ffd166' },
          { value: 5000, color: '#f72585' }
        ]}
      },
      overrides: []
    }
  },

  {
    id: 306,
    title: 'Total Tool Calls',
    description: 'Total number of tool invocations in the selected time range.',
    type: 'stat',
    gridPos: { x: 16, y: startY + 20, w: 8, h: 5 },
    datasource: loki,
    targets: [{
      refId: 'A', datasource: loki,
      expr: `sum(count_over_time({app="claude-dev-logging"} | json | hook_type="post-tool-use" | tool_name!="" | ${filters} [$__range]))`,
      queryType: 'instant',
      legendFormat: 'Tool Calls',
      hide: false
    }],
    options: {
      colorMode: 'background-gradient',
      graphMode: 'none',
      justifyMode: 'center',
      orientation: 'auto',
      textMode: 'value_and_name',
      text: { titleSize: 11, valueSize: 28 },
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false }
    },
    fieldConfig: {
      defaults: {
        unit: 'short',
        decimals: 0,
        color: { mode: 'thresholds' },
        thresholds: { mode: 'absolute', steps: [
          { value: null, color: '#00d4aa' },
          { value: 500, color: '#ffd166' },
          { value: 2000, color: '#f72585' }
        ]}
      },
      overrides: []
    }
  }
];

dash.panels.push(...newPanels);

fs.writeFileSync(path, JSON.stringify(dash, null, 2));
JSON.parse(fs.readFileSync(path, 'utf8'));
console.log('claude-intelligence.json: added Tool Performance section. Total panels:', dash.panels.length);
