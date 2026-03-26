/**
 * Cost Analysis Dashboard Generator — v2
 *
 * Verified against actual Loki data:
 *   model labels:  claude-sonnet-4-6 | claude-opus-4-6
 *   effort labels: med | extended_thinking
 *   JSON fields:   assistant_turns, cost_usd, effort, is_final, machine, model,
 *                  project, session_id, thinking (bool), timestamp, tool_use_count,
 *                  total_cache_creation_tokens, total_cache_read_tokens,
 *                  total_input_tokens, total_output_tokens, total_tokens
 *   MISSING:       session_duration_ms, compaction_count  (do not use)
 *
 * Cost rule: ALWAYS use pre-computed cost_usd — never recompute from token counts.
 */

import { writeFileSync } from 'fs';

// ── constants ─────────────────────────────────────────────────────────────────

const DS = { type: 'loki', uid: 'loki_local' };

// Actual model label values in Loki
const MODEL_OPUS   = 'claude-opus-4-6';
const MODEL_SONNET = 'claude-sonnet-4-6';
const MODEL_COLORS = [[MODEL_OPUS, '#B877D9'], [MODEL_SONNET, '#5794F2']];

// Actual effort label values in Loki
const EFFORT_THINKING = 'extended_thinking';
const EFFORT_MED      = 'med';
const EFFORT_COLORS   = [[EFFORT_THINKING, '#F2495C'], [EFFORT_MED, '#5794F2']];

// Base stream selectors
const M = '{app="claude-token-metrics", model=~"$model", project=~"$project", effort=~"$effort"}';
const M_ALL = '{app="claude-token-metrics"}';  // no variable filters, for model-comparison ungrouped
const TURNS_STREAM = '{app="claude-dev-logging", component="tokens"}';

// Thinking filters (boolean in JSON → use regex match)
const THINK_ON  = `{app="claude-token-metrics"} | json thinking`;
const THINK_OFF = `{app="claude-token-metrics"} | json thinking`;

// ── query builders ────────────────────────────────────────────────────────────

/** Sum a numeric field over a range (stat KPI). */
function sumRange(field, selector = M) {
  return `sum(sum_over_time(${selector} | json ${field} | unwrap ${field} [$__range]))`;
}

/** Sum a numeric field per interval (timeseries). */
function sumInterval(field, selector = M) {
  return `sum(sum_over_time(${selector} | json ${field} | unwrap ${field} [$__interval]))`;
}

/** Sum grouped by a stream label per interval. */
function sumByInterval(label, field, selector = M) {
  return `sum by (${label}) (sum_over_time(${selector} | json ${field} | unwrap ${field} [$__interval]))`;
}

/** Sum grouped by a stream label over range. */
function sumByRange(label, field, selector = M) {
  return `sum by (${label}) (sum_over_time(${selector} | json ${field} | unwrap ${field} [$__range]))`;
}

/** Count of sessions over range. */
function countRange(selector = M) {
  return `count(count_over_time(${selector} [$__range]))`;
}

/**
 * Avg cost per session = total_cost / session_count.
 * Explicit formula — no avg_over_time ambiguity.
 */
function avgCostPerSession(selector = M) {
  return `${sumRange('cost_usd', selector)} / ${countRange(selector)}`;
}

/**
 * Cache hit rate = cache_read / (input + cache_creation + cache_read) * 100
 * Exact formula, no approximation.
 */
function cacheHitRate(selector = M) {
  const cr  = `sum(sum_over_time(${selector} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
  const inp = `sum(sum_over_time(${selector} | json total_input_tokens | unwrap total_input_tokens [$__range]))`;
  const cw  = `sum(sum_over_time(${selector} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
  return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
}

function cacheHitRateInterval(selector = M) {
  const cr  = `sum(sum_over_time(${selector} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__interval]))`;
  const inp = `sum(sum_over_time(${selector} | json total_input_tokens | unwrap total_input_tokens [$__interval]))`;
  const cw  = `sum(sum_over_time(${selector} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__interval]))`;
  return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
}

/** Cache ROI = total reads / total writes (dimensionless). */
function cacheROI(selector = M) {
  const cr = `sum(sum_over_time(${selector} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
  const cw = `sum(sum_over_time(${selector} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
  return `${cr} / ${cw}`;
}

/** Output tokens per dollar = total_output / total_cost. */
function outputPerDollar(selector = M) {
  return `${sumRange('total_output_tokens', selector)} / ${sumRange('cost_usd', selector)}`;
}

/** Cost per 1K output tokens = (total_cost / total_output) * 1000. */
function costPer1KOutput(selector = M) {
  return `${sumRange('cost_usd', selector)} / ${sumRange('total_output_tokens', selector)} * 1000`;
}

/** Cost per assistant turn = total_cost / total_turns. */
function costPerTurn(selector = M) {
  return `${sumRange('cost_usd', selector)} / ${sumRange('assistant_turns', selector)}`;
}

/** % of spend from thinking sessions. */
function thinkingSpendPct() {
  // Comma-separated json extraction required by LogQL for multiple fields
  const thinkCost = `sum(sum_over_time({app="claude-token-metrics"} | json thinking, cost_usd | thinking=~"true" | unwrap cost_usd [$__range]))`;
  const totalCost = `sum(sum_over_time({app="claude-token-metrics"} | json cost_usd | unwrap cost_usd [$__range]))`;
  return `${thinkCost} / ${totalCost} * 100`;
}

// ── panel builders ────────────────────────────────────────────────────────────

function target(expr, legendFormat = '', queryType = 'range', refId = 'A') {
  return { datasource: DS, expr, legendFormat, queryType, refId };
}

function row(title, y) {
  return { type: 'row', title, collapsed: false, gridPos: { x: 0, y, w: 24, h: 1 } };
}

function statPanel(id, title, targets, unit, colorSpec, x, y, w = 5, h = 4) {
  const isSteps = Array.isArray(colorSpec);
  return {
    id, title, type: 'stat', datasource: DS,
    gridPos: { x, y, w, h }, transparent: true,
    fieldConfig: {
      defaults: {
        noValue: '0', unit,
        decimals: unit === 'currencyUSD' ? 2 : unit === 'percent' ? 1 : 0,
        color: isSteps ? { mode: 'thresholds' } : { mode: 'fixed', fixedColor: colorSpec },
        thresholds: {
          mode: 'absolute',
          steps: isSteps ? colorSpec : [{ value: null, color: colorSpec }]
        },
        mappings: []
      },
      overrides: []
    },
    options: {
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false },
      orientation: 'horizontal', textMode: 'auto',
      colorMode: 'background', graphMode: 'none', justifyMode: 'center'
    },
    targets
  };
}

function timeseriesPanel(id, title, targets, x, y, w, h, opts = {}) {
  const {
    unit = 'short', stacked = false, fillOpacity = 12, lineWidth = 1,
    legendCalcs = ['sum'], legendPlacement = 'bottom',
    colorOverrides = [], drawStyle = 'line'
  } = opts;
  return {
    id, title, type: 'timeseries', datasource: DS,
    gridPos: { x, y, w, h },
    fieldConfig: {
      defaults: {
        unit,
        decimals: unit === 'currencyUSD' ? 2 : unit === 'percent' ? 1 : 0,
        color: { mode: 'palette-classic' },
        custom: {
          drawStyle, lineWidth, fillOpacity,
          gradientMode: 'opacity', showPoints: 'never',
          lineInterpolation: 'smooth', spanNulls: true,
          axisBorderShow: false,
          stacking: { mode: stacked ? 'normal' : 'none', group: 'A' }
        }
      },
      overrides: colorOverrides.map(([name, color]) => ({
        matcher: { id: 'byName', options: name },
        properties: [{ id: 'color', value: { fixedColor: color, mode: 'fixed' } }]
      }))
    },
    options: {
      legend: {
        displayMode: 'list',
        placement: legendPlacement,
        calcs: legendCalcs,
        showLegend: true
      },
      tooltip: { mode: 'single', sort: 'desc' }
    },
    targets
  };
}

function tablePanel(id, title, targets, x, y, w, h, opts = {}) {
  const { unit = 'short', maxValue = null } = opts;
  return {
    id, title, type: 'table', datasource: DS,
    gridPos: { x, y, w, h },
    transformations: [
      { id: 'reduce', options: { reducers: ['lastNotNull'], mode: 'seriesToRows' } }
    ],
    options: {
      frameIndex: 0,
      showHeader: true,
      cellHeight: 'sm',
      sortBy: [{ desc: true, displayName: 'Last *' }],
      footer: { show: false }
    },
    fieldConfig: {
      defaults: {
        unit,
        decimals: unit === 'currencyUSD' ? 3 : unit === 'percent' ? 1 : 0,
        color: { mode: 'palette-classic' },
        custom: { inspect: false }
      },
      overrides: [
        {
          matcher: { id: 'byType', options: 'number' },
          properties: [
            { id: 'custom.width', value: 200 },
            { id: 'min', value: 0 },
            ...(maxValue !== null ? [{ id: 'max', value: maxValue }] : []),
            {
              id: 'custom.cellOptions',
              value: { type: 'gauge', mode: 'basic', valueDisplayMode: 'color' }
            }
          ]
        },
        {
          matcher: { id: 'byName', options: 'Field' },
          properties: [{ id: 'custom.width', value: 280 }]
        }
      ]
    },
    targets
  };
}

function piechartPanel(id, title, targets, x, y, w, h, opts = {}) {
  const { unit = 'currencyUSD', colorOverrides = [] } = opts;
  return {
    id, title, type: 'piechart', datasource: DS,
    gridPos: { x, y, w, h },
    options: {
      reduceOptions: { calcs: ['lastNotNull'], fields: '', values: false },
      pieType: 'donut',
      displayLabels: ['name', 'percent'],
      legend: {
        displayMode: 'list',
        placement: 'bottom',
        showLegend: true
      },
      tooltip: { mode: 'single' }
    },
    fieldConfig: {
      defaults: {
        unit,
        decimals: unit === 'currencyUSD' ? 2 : 1,
        color: { mode: 'palette-classic' }
      },
      overrides: colorOverrides.map(([name, color]) => ({
        matcher: { id: 'byName', options: name },
        properties: [{ id: 'color', value: { fixedColor: color, mode: 'fixed' } }]
      }))
    },
    targets
  };
}

function barchartPanel(id, title, targets, x, y, w, h, opts = {}) {
  const {
    unit = 'short', orientation = 'horizontal',
    colorOverrides = [], stacked = false, xTickMaxLen = 20
  } = opts;
  return {
    id, title, type: 'barchart', datasource: DS,
    gridPos: { x, y, w, h },
    options: {
      orientation,
      xTickLabelMaxLength: xTickMaxLen,
      xTickLabelRotation: orientation === 'auto' ? -45 : 0,
      groupWidth: 0.7, barWidth: 0.9,
      stacking: stacked ? 'normal' : 'none',
      fillOpacity: 80, gradientMode: 'opacity',
      legend: { displayMode: 'list', placement: 'bottom', showLegend: true },
      tooltip: { mode: 'single' }
    },
    fieldConfig: {
      defaults: {
        unit,
        decimals: unit === 'currencyUSD' ? 2 : unit === 'percent' ? 1 : 0,
        color: { mode: 'palette-classic' },
        custom: { fillOpacity: 80, lineWidth: 0 }
      },
      overrides: colorOverrides.map(([name, color]) => ({
        matcher: { id: 'byName', options: name },
        properties: [{ id: 'color', value: { fixedColor: color, mode: 'fixed' } }]
      }))
    },
    targets
  };
}

function templateVar(name, label, regex) {
  return {
    name, label, type: 'query',
    datasource: DS,
    query: `{app="claude-token-metrics"} | json`,
    regex,
    refresh: 2, includeAll: true, allValue: '.*',
    sort: 1,
    current: { text: 'All', value: '$__all' }
  };
}

function dashboard(uid, title, defaultRange, refresh, vars, panels) {
  return {
    id: null, uid, title,
    description: '',
    tags: ['claude-code', 'cost-analysis'],
    timezone: 'browser', editable: true,
    graphTooltip: 1,
    time: { from: defaultRange, to: 'now' },
    refresh, schemaVersion: 39,
    fiscalYearStartMonth: 0, liveNow: false, style: 'dark',
    templating: { list: vars },
    panels,
    links: []
  };
}

const VARS_ALL = [
  templateVar('model',   'Model',   '"model":"([^"]+)"'),
  templateVar('project', 'Project', '"project":"([^"]+)"'),
  templateVar('effort',  'Effort',  '"effort":"([^"]+)"')
];

// ══════════════════════════════════════════════════════════════════════════════
// Dashboard 1 — Cost Intelligence
// ══════════════════════════════════════════════════════════════════════════════

function buildCostIntelligence() {
  let id = 0; let y = 0;
  const p = [];

  // ── KPI row ─────────────────────────────────────────────────────────────────
  p.push(row('💰 Budget At-a-Glance', y++));

  p.push(statPanel(++id, 'Total Spend', [
    target(sumRange('cost_usd'))
  ], 'currencyUSD', [
    { value: null, color: '#73BF69' }, { value: 5, color: '#FADE2A' },
    { value: 20, color: '#FF9830' },  { value: 50, color: '#F2495C' }
  ], 0, y, 5));

  p.push(statPanel(++id, 'Avg Cost / Session', [
    target(avgCostPerSession())
  ], 'currencyUSD', '#5794F2', 5, y, 5));

  p.push(statPanel(++id, 'Sessions', [
    target(countRange())
  ], 'short', '#8AB8FF', 10, y, 4));

  p.push(statPanel(++id, 'Total Output Tokens', [
    target(sumRange('total_output_tokens'))
  ], 'short', '#B877D9', 14, y, 5));

  p.push(statPanel(++id, 'Cache Hit Rate', [
    target(cacheHitRate())
  ], 'percent', [
    { value: null, color: '#F2495C' }, { value: 30, color: '#FADE2A' }, { value: 60, color: '#73BF69' }
  ], 19, y, 5));
  y += 4;

  // ── Spend over time ──────────────────────────────────────────────────────────
  p.push(row('📈 Spend Over Time', y++));
  p.push(timeseriesPanel(++id, 'Daily Spend', [
    target(
      `sum(sum_over_time(${M} | json cost_usd | unwrap cost_usd [1d]))`,
      'Spend'
    )
  ], 0, y, 24, 9, {
    unit: 'currencyUSD', fillOpacity: 70, lineWidth: 2,
    legendCalcs: ['sum'], legendPlacement: 'bottom', drawStyle: 'bars',
    colorOverrides: [['Spend', '#FADE2A']]
  }));
  y += 9;

  // ── Breakdown ───────────────────────────────────────────────────────────────
  p.push(row('🍕 Cost Breakdown', y++));

  p.push(piechartPanel(++id, 'Spend by Model', [
    target(sumByRange('model', 'cost_usd'), '{{model}}')
  ], 0, y, 8, 10, { colorOverrides: MODEL_COLORS }));

  p.push(tablePanel(++id, 'Spend by Project', [
    target(sumByRange('project', 'cost_usd'), '{{project}}')
  ], 8, y, 8, 10, { unit: 'currencyUSD' }));

  p.push(tablePanel(++id, 'Spend by Effort Level', [
    target(sumByRange('effort', 'cost_usd'), '{{effort}}')
  ], 16, y, 8, 10, { unit: 'currencyUSD' }));
  y += 10;

  // ── Session rankings ─────────────────────────────────────────────────────────
  p.push(row('🏆 Session Rankings', y++));

  p.push(tablePanel(++id, 'Top 15 Sessions by Cost', [
    target(
      `topk(15, sum by (session_id) (sum_over_time(${M} | json cost_usd | unwrap cost_usd [$__range])))`,
      '{{session_id}}'
    )
  ], 0, y, 14, 10, { unit: 'currencyUSD' }));

  p.push(statPanel(++id, 'Min Session Cost', [
    target(`min(min_over_time(${M} | json cost_usd | unwrap cost_usd [$__range]))`)
  ], 'currencyUSD', '#73BF69', 14, y, 3, 10));

  p.push(statPanel(++id, 'Avg Cost / Session', [
    target(avgCostPerSession())
  ], 'currencyUSD', '#5794F2', 17, y, 3, 10));

  p.push(statPanel(++id, 'Max Session Cost', [
    target(`max(max_over_time(${M} | json cost_usd | unwrap cost_usd [$__range]))`)
  ], 'currencyUSD', [
    { value: null, color: '#73BF69' }, { value: 1, color: '#FADE2A' },
    { value: 5, color: '#FF9830' },    { value: 20, color: '#F2495C' }
  ], 20, y, 4, 10));
  y += 10;

  // ── Burn rate ────────────────────────────────────────────────────────────────
  p.push(row('⏱ Hourly Burn Rate', y++));
  p.push(timeseriesPanel(++id, 'Spend per Hour', [
    target(sumInterval('cost_usd'), 'Spend/hr')
  ], 0, y, 24, 8, {
    unit: 'currencyUSD', fillOpacity: 30, lineWidth: 2,
    legendCalcs: ['sum', 'mean'], legendPlacement: 'bottom',
    colorOverrides: [['Spend/hr', '#FADE2A']]
  }));
  y += 8;

  return dashboard('claude-cost-intelligence', 'Claude Code — Cost Intelligence',
    'now-30d', '5m', VARS_ALL, p);
}

// ══════════════════════════════════════════════════════════════════════════════
// Dashboard 2 — Model & Effort Comparison
// ══════════════════════════════════════════════════════════════════════════════

function buildModelComparison() {
  let id = 0; let y = 0;
  const p = [];

  // ── KPIs ─────────────────────────────────────────────────────────────────────
  p.push(row('📊 Summary', y++));

  p.push(statPanel(++id, 'Total Spend', [
    target(sumRange('cost_usd', M_ALL))
  ], 'currencyUSD', '#FADE2A', 0, y, 5));

  p.push(statPanel(++id, 'Avg Cost / Session', [
    target(avgCostPerSession(M_ALL))
  ], 'currencyUSD', '#5794F2', 5, y, 5));

  p.push(statPanel(++id, 'Cost / Turn', [
    target(costPerTurn(M_ALL))
  ], 'currencyUSD', '#B877D9', 10, y, 4));

  p.push(statPanel(++id, 'Thinking Sessions', [
    target(
      `count(count_over_time(${THINK_ON} | thinking=~"true" [$__range]))`,
      '', 'range'
    )
  ], 'short', [
    { value: null, color: '#5794F2' }, { value: 10, color: '#FADE2A' }
  ], 14, y, 5));

  p.push(statPanel(++id, '% Spend: Thinking', [
    target(thinkingSpendPct())
  ], 'percent', [
    { value: null, color: '#73BF69' }, { value: 30, color: '#FADE2A' }, { value: 60, color: '#F2495C' }
  ], 19, y, 5));
  y += 4;

  // ── Spend by model over time ──────────────────────────────────────────────────
  p.push(row('🤖 Model Breakdown', y++));
  p.push(timeseriesPanel(++id, 'Spend by Model Over Time', [
    target(sumByInterval('model', 'cost_usd', M_ALL), '{{model}}')
  ], 0, y, 24, 9, {
    unit: 'currencyUSD', stacked: true, fillOpacity: 40,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: MODEL_COLORS
  }));
  y += 9;

  // ── Efficiency ────────────────────────────────────────────────────────────────
  p.push(row('⚡ Efficiency', y++));

  p.push(barchartPanel(++id, 'Output Tokens per Dollar (by Model)', [
    target(
      `sum by (model) (sum_over_time(${M_ALL} | json total_output_tokens | unwrap total_output_tokens [$__range])) / sum by (model) (sum_over_time(${M_ALL} | json cost_usd | unwrap cost_usd [$__range]))`,
      '{{model}}', 'instant'
    )
  ], 0, y, 12, 10, { unit: 'short', colorOverrides: MODEL_COLORS }));

  p.push(barchartPanel(++id, 'Cost per Turn (by Model)', [
    target(
      `sum by (model) (sum_over_time(${M_ALL} | json cost_usd | unwrap cost_usd [$__range])) / sum by (model) (sum_over_time(${M_ALL} | json assistant_turns | unwrap assistant_turns [$__range]))`,
      '{{model}}', 'instant'
    )
  ], 12, y, 12, 10, { unit: 'currencyUSD', colorOverrides: MODEL_COLORS }));
  y += 10;

  // ── Token profile — SPLIT by scale ─────────────────────────────────────────
  p.push(row('🔢 Token Profile', y++));

  // Small-scale: input + output (~hundreds to thousands)
  p.push(timeseriesPanel(++id, 'Input & Output Tokens Over Time', [
    target(sumInterval('total_input_tokens',  M_ALL), 'Input',  'range', 'A'),
    target(sumInterval('total_output_tokens', M_ALL), 'Output', 'range', 'B')
  ], 0, y, 12, 10, {
    unit: 'short', stacked: false, fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Input', '#5794F2'], ['Output', '#B877D9']]
  }));

  // Large-scale: cache read + cache write (~millions)
  p.push(timeseriesPanel(++id, 'Cache Tokens Over Time', [
    target(sumInterval('total_cache_read_tokens',     M_ALL), 'Cache Read',  'range', 'A'),
    target(sumInterval('total_cache_creation_tokens', M_ALL), 'Cache Write', 'range', 'B')
  ], 12, y, 12, 10, {
    unit: 'short', stacked: false, fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Cache Read', '#73BF69'], ['Cache Write', '#FF9830']]
  }));
  y += 10;

  // ── Effort ────────────────────────────────────────────────────────────────────
  p.push(row('🎯 Effort Level', y++));

  p.push(timeseriesPanel(++id, 'Spend by Effort Over Time', [
    target(sumByInterval('effort', 'cost_usd', M_ALL), '{{effort}}')
  ], 0, y, 16, 9, {
    unit: 'currencyUSD', stacked: true, fillOpacity: 40,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: EFFORT_COLORS
  }));

  p.push(piechartPanel(++id, 'Sessions by Effort', [
    target(
      `sum by (effort) (count_over_time(${M_ALL} [$__range]))`,
      '{{effort}}'
    )
  ], 16, y, 8, 9, { unit: 'short', colorOverrides: EFFORT_COLORS }));
  y += 9;

  // ── Thinking deep dive ────────────────────────────────────────────────────────
  p.push(row('🧠 Thinking Mode', y++));

  const thinkOn  = `${THINK_ON} | thinking=~"true"`;
  const thinkOff = `${THINK_OFF} | thinking!~"true"`;

  p.push(barchartPanel(++id, 'Avg Session Cost: Thinking vs Standard', [
    target(
      `sum(sum_over_time(${thinkOn} | json cost_usd | unwrap cost_usd [$__range])) / count(count_over_time(${thinkOn} [$__range]))`,
      'Thinking On', 'instant', 'A'
    ),
    target(
      `sum(sum_over_time(${thinkOff} | json cost_usd | unwrap cost_usd [$__range])) / count(count_over_time(${thinkOff} [$__range]))`,
      'Thinking Off', 'instant', 'B'
    )
  ], 0, y, 8, 9, {
    unit: 'currencyUSD', orientation: 'auto',
    colorOverrides: [['Thinking On', '#F2495C'], ['Thinking Off', '#5794F2']]
  }));

  p.push(barchartPanel(++id, 'Avg Output Tokens: Thinking vs Standard', [
    target(
      `sum(sum_over_time(${thinkOn} | json total_output_tokens | unwrap total_output_tokens [$__range])) / count(count_over_time(${thinkOn} [$__range]))`,
      'Thinking On', 'instant', 'A'
    ),
    target(
      `sum(sum_over_time(${thinkOff} | json total_output_tokens | unwrap total_output_tokens [$__range])) / count(count_over_time(${thinkOff} [$__range]))`,
      'Thinking Off', 'instant', 'B'
    )
  ], 8, y, 8, 9, {
    unit: 'short', orientation: 'auto',
    colorOverrides: [['Thinking On', '#F2495C'], ['Thinking Off', '#5794F2']]
  }));

  p.push(barchartPanel(++id, 'Cache Hit Rate: Thinking vs Standard', [
    target(
      (() => {
        const s = thinkOn;
        const cr = `sum(sum_over_time(${s} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
        const inp = `sum(sum_over_time(${s} | json total_input_tokens | unwrap total_input_tokens [$__range]))`;
        const cw = `sum(sum_over_time(${s} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
        return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
      })(),
      'Thinking On', 'instant', 'A'
    ),
    target(
      (() => {
        const s = thinkOff;
        const cr = `sum(sum_over_time(${s} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
        const inp = `sum(sum_over_time(${s} | json total_input_tokens | unwrap total_input_tokens [$__range]))`;
        const cw = `sum(sum_over_time(${s} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
        return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
      })(),
      'Thinking Off', 'instant', 'B'
    )
  ], 16, y, 8, 9, {
    unit: 'percent', orientation: 'auto',
    colorOverrides: [['Thinking On', '#F2495C'], ['Thinking Off', '#5794F2']]
  }));
  y += 9;

  return dashboard('claude-model-comparison', 'Claude Code — Model & Effort Comparison',
    'now-7d', '1m',
    [templateVar('project', 'Project', '"project":"([^"]+)"')],
    p);
}

// ══════════════════════════════════════════════════════════════════════════════
// Dashboard 3 — Cache & Context Health
// ══════════════════════════════════════════════════════════════════════════════

function buildCacheHealth() {
  let id = 0; let y = 0;
  const p = [];

  p.push(row('📦 Cache At-a-Glance', y++));

  p.push(statPanel(++id, 'Cache Hit Rate', [
    target(cacheHitRate())
  ], 'percent', [
    { value: null, color: '#F2495C' }, { value: 30, color: '#FADE2A' }, { value: 60, color: '#73BF69' }
  ], 0, y, 5));

  p.push(statPanel(++id, 'Total Cache Reads', [
    target(sumRange('total_cache_read_tokens'))
  ], 'short', '#73BF69', 5, y, 5));

  p.push(statPanel(++id, 'Total Cache Writes', [
    target(sumRange('total_cache_creation_tokens'))
  ], 'short', '#FF9830', 10, y, 4));

  p.push(statPanel(++id, 'Cache ROI (reads / write)', [
    target(cacheROI())
  ], 'short', [
    { value: null, color: '#F2495C' }, { value: 1, color: '#FADE2A' }, { value: 3, color: '#73BF69' }
  ], 14, y, 5));

  p.push(statPanel(++id, 'Avg Turns / Session', [
    target(
      `${sumRange('assistant_turns')} / ${countRange()}`
    )
  ], 'short', '#5794F2', 19, y, 5));
  y += 4;

  // ── Cache hit trend ──────────────────────────────────────────────────────────
  p.push(row('📉 Cache Efficiency Trend', y++));
  p.push(timeseriesPanel(++id, 'Cache Hit Rate Over Time', [
    target(cacheHitRateInterval(), 'Cache Hit %')
  ], 0, y, 24, 9, {
    unit: 'percent', fillOpacity: 25, lineWidth: 2,
    legendCalcs: ['mean', 'last'], legendPlacement: 'bottom',
    colorOverrides: [['Cache Hit %', '#73BF69']]
  }));
  y += 9;

  // ── Token flows — SEPARATE PANELS by scale ──────────────────────────────────
  p.push(row('🔄 Token Flows', y++));

  p.push(timeseriesPanel(++id, 'Input & Output Tokens Over Time', [
    target(sumInterval('total_input_tokens'),  'Input',  'range', 'A'),
    target(sumInterval('total_output_tokens'), 'Output', 'range', 'B')
  ], 0, y, 12, 10, {
    unit: 'short', fillOpacity: 20, legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Input', '#5794F2'], ['Output', '#B877D9']]
  }));

  p.push(timeseriesPanel(++id, 'Cache Read & Write Tokens Over Time', [
    target(sumInterval('total_cache_read_tokens'),     'Cache Read',  'range', 'A'),
    target(sumInterval('total_cache_creation_tokens'), 'Cache Write', 'range', 'B')
  ], 12, y, 12, 10, {
    unit: 'short', fillOpacity: 20, legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Cache Read', '#73BF69'], ['Cache Write', '#FF9830']]
  }));
  y += 10;

  // ── Cache by dimension ──────────────────────────────────────────────────────
  p.push(row('🏷 Cache by Dimension', y++));

  p.push(barchartPanel(++id, 'Cache Hit Rate by Model', [
    target(
      (() => {
        const s = '{app="claude-token-metrics"}';
        const cr  = `sum by (model) (sum_over_time(${s} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
        const inp = `sum by (model) (sum_over_time(${s} | json total_input_tokens | unwrap total_input_tokens [$__range]))`;
        const cw  = `sum by (model) (sum_over_time(${s} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
        return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
      })(),
      '{{model}}', 'instant'
    )
  ], 0, y, 12, 10, { unit: 'percent', colorOverrides: MODEL_COLORS }));

  p.push(tablePanel(++id, 'Cache Hit Rate by Project', [
    target(
      (() => {
        const s = '{app="claude-token-metrics"}';
        const cr  = `sum by (project) (sum_over_time(${s} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range]))`;
        const inp = `sum by (project) (sum_over_time(${s} | json total_input_tokens | unwrap total_input_tokens [$__range]))`;
        const cw  = `sum by (project) (sum_over_time(${s} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__range]))`;
        return `${cr} / (${inp} + ${cw} + ${cr}) * 100`;
      })(),
      '{{project}}'
    )
  ], 12, y, 12, 10, { unit: 'percent', maxValue: 100 }));
  y += 10;

  // ── Cache ROI trend ─────────────────────────────────────────────────────────
  p.push(row('📈 Cache ROI Over Time', y++));

  p.push(timeseriesPanel(++id, 'Cache ROI Trend (reads per write)', [
    target(
      `sum(sum_over_time(${M} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__interval])) / sum(sum_over_time(${M} | json total_cache_creation_tokens | unwrap total_cache_creation_tokens [$__interval]))`,
      'ROI'
    )
  ], 0, y, 16, 8, {
    unit: 'short', fillOpacity: 20, lineWidth: 2,
    legendCalcs: ['mean', 'last'], legendPlacement: 'bottom',
    colorOverrides: [['ROI', '#73BF69']]
  }));

  p.push(tablePanel(++id, 'Top Sessions by Cache Reads', [
    target(
      `topk(10, sum by (session_id) (sum_over_time(${M} | json total_cache_read_tokens | unwrap total_cache_read_tokens [$__range])))`,
      '{{session_id}}'
    )
  ], 16, y, 8, 8, { unit: 'short' }));
  y += 8;

  return dashboard('claude-cache-health', 'Claude Code — Cache & Context Health',
    'now-7d', '1m', VARS_ALL, p);
}

// ══════════════════════════════════════════════════════════════════════════════
// Dashboard 4 — Session Profiling
// NOTE: session_duration_ms and compaction_count do NOT exist in logs.
//       All panels use confirmed fields only.
// ══════════════════════════════════════════════════════════════════════════════

function buildSessionProfiling() {
  let id = 0; let y = 0;
  const p = [];

  p.push(row('🔬 Session Shape', y++));

  p.push(statPanel(++id, 'Avg Turns / Session', [
    target(`${sumRange('assistant_turns')} / ${countRange()}`)
  ], 'short', '#73BF69', 0, y, 5));

  p.push(statPanel(++id, 'Max Turns (any session)', [
    target(`max(max_over_time(${M} | json assistant_turns | unwrap assistant_turns [$__range]))`)
  ], 'short', [
    { value: null, color: '#73BF69' }, { value: 30, color: '#FADE2A' }, { value: 80, color: '#F2495C' }
  ], 5, y, 5));

  p.push(statPanel(++id, 'Avg Total Tokens / Session', [
    target(`${sumRange('total_tokens')} / ${countRange()}`)
  ], 'short', '#B877D9', 10, y, 5));

  p.push(statPanel(++id, 'Avg Tool Uses / Session', [
    target(`${sumRange('tool_use_count')} / ${countRange()}`)
  ], 'short', '#5794F2', 15, y, 4));

  p.push(statPanel(++id, 'Sessions', [
    target(countRange())
  ], 'short', '#8AB8FF', 19, y, 5));
  y += 4;

  // ── Turn depth over time ─────────────────────────────────────────────────────
  p.push(row('📊 Turn Depth', y++));

  p.push(timeseriesPanel(++id, 'Avg & Max Turns per Session Over Time', [
    target(
      `sum(sum_over_time(${M} | json assistant_turns | unwrap assistant_turns [$__interval])) / count(count_over_time(${M} [$__interval]))`,
      'Avg Turns', 'range', 'A'
    ),
    target(
      `max(max_over_time(${M} | json assistant_turns | unwrap assistant_turns [$__interval]))`,
      'Max Turns', 'range', 'B'
    )
  ], 0, y, 16, 10, {
    fillOpacity: 15, lineWidth: 2,
    legendCalcs: ['mean', 'max'], legendPlacement: 'bottom',
    colorOverrides: [['Avg Turns', '#73BF69'], ['Max Turns', '#FADE2A']]
  }));

  p.push(tablePanel(++id, 'Top 10 Sessions by Turns', [
    target(
      `topk(10, sum by (session_id) (max_over_time(${M} | json assistant_turns | unwrap assistant_turns [$__range])))`,
      '{{session_id}}'
    )
  ], 16, y, 8, 10, { unit: 'short' }));
  y += 10;

  // ── Token depth ──────────────────────────────────────────────────────────────
  p.push(row('🔢 Token Depth', y++));

  // Input/Output — small scale
  p.push(timeseriesPanel(++id, 'Input & Output Tokens Over Time', [
    target(sumInterval('total_input_tokens'),  'Input',  'range', 'A'),
    target(sumInterval('total_output_tokens'), 'Output', 'range', 'B')
  ], 0, y, 12, 10, {
    unit: 'short', fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Input', '#5794F2'], ['Output', '#B877D9']]
  }));

  // Cache — large scale (separate axis)
  p.push(timeseriesPanel(++id, 'Cache Tokens Over Time', [
    target(sumInterval('total_cache_read_tokens'),     'Cache Read',  'range', 'A'),
    target(sumInterval('total_cache_creation_tokens'), 'Cache Write', 'range', 'B')
  ], 12, y, 12, 10, {
    unit: 'short', fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Cache Read', '#73BF69'], ['Cache Write', '#FF9830']]
  }));
  y += 10;

  // ── Cost vs depth ─────────────────────────────────────────────────────────────
  p.push(row('💸 Cost vs Depth', y++));

  p.push(timeseriesPanel(++id, 'Avg & Max Session Cost Over Time', [
    target(
      `sum(sum_over_time(${M} | json cost_usd | unwrap cost_usd [$__interval])) / count(count_over_time(${M} [$__interval]))`,
      'Avg Cost', 'range', 'A'
    ),
    target(
      `max(max_over_time(${M} | json cost_usd | unwrap cost_usd [$__interval]))`,
      'Max Cost', 'range', 'B'
    )
  ], 0, y, 16, 10, {
    unit: 'currencyUSD', fillOpacity: 15, lineWidth: 2,
    legendCalcs: ['mean', 'max'], legendPlacement: 'bottom',
    colorOverrides: [['Avg Cost', '#FADE2A'], ['Max Cost', '#F2495C']]
  }));

  p.push(tablePanel(++id, 'Top 15 Sessions by Cost', [
    target(
      `topk(15, sum by (session_id) (sum_over_time(${M} | json cost_usd | unwrap cost_usd [$__range])))`,
      '{{session_id}}'
    )
  ], 16, y, 8, 10, { unit: 'currencyUSD' }));
  y += 10;

  // ── Per-turn flow ─────────────────────────────────────────────────────────────
  p.push(row('📡 Per-Turn Token Flow (stop hook)', y++));

  p.push(timeseriesPanel(++id, 'Turn Input & Output Tokens Over Time', [
    target(
      `sum(sum_over_time(${TURNS_STREAM} | json turn_input_tokens | unwrap turn_input_tokens [$__interval]))`,
      'Turn Input', 'range', 'A'
    ),
    target(
      `sum(sum_over_time(${TURNS_STREAM} | json turn_output_tokens | unwrap turn_output_tokens [$__interval]))`,
      'Turn Output', 'range', 'B'
    )
  ], 0, y, 12, 9, {
    unit: 'short', fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Turn Input', '#5794F2'], ['Turn Output', '#B877D9']]
  }));

  p.push(timeseriesPanel(++id, 'Turn Cache Tokens Over Time', [
    target(
      `sum(sum_over_time(${TURNS_STREAM} | json turn_cache_read_tokens | unwrap turn_cache_read_tokens [$__interval]))`,
      'Turn Cache Read', 'range', 'A'
    ),
    target(
      `sum(sum_over_time(${TURNS_STREAM} | json turn_cache_creation_tokens | unwrap turn_cache_creation_tokens [$__interval]))`,
      'Turn Cache Write', 'range', 'B'
    )
  ], 12, y, 12, 9, {
    unit: 'short', fillOpacity: 20,
    legendCalcs: ['sum'], legendPlacement: 'bottom',
    colorOverrides: [['Turn Cache Read', '#73BF69'], ['Turn Cache Write', '#FF9830']]
  }));
  y += 9;

  return dashboard('claude-session-profiling', 'Claude Code — Session Profiling',
    'now-7d', '1m', VARS_ALL, p);
}

// ══════════════════════════════════════════════════════════════════════════════
// Dashboard 5 — Developer Productivity
// ══════════════════════════════════════════════════════════════════════════════

function buildDevProductivity() {
  let id = 0; let y = 0;
  const p = [];

  p.push(row('🚀 Productivity At-a-Glance', y++));

  p.push(statPanel(++id, 'Total Sessions', [
    target(countRange(M_ALL))
  ], 'short', '#5794F2', 0, y, 5));

  p.push(statPanel(++id, 'Total Spend', [
    target(sumRange('cost_usd', M_ALL))
  ], 'currencyUSD', '#FADE2A', 5, y, 5));

  p.push(statPanel(++id, 'Avg Output / Session', [
    target(`${sumRange('total_output_tokens', M_ALL)} / ${countRange(M_ALL)}`)
  ], 'short', '#B877D9', 10, y, 5));

  p.push(statPanel(++id, 'Cost per 1K Output', [
    target(costPer1KOutput(M_ALL))
  ], 'currencyUSD', [
    { value: null, color: '#73BF69' }, { value: 0.05, color: '#FADE2A' }, { value: 0.2, color: '#F2495C' }
  ], 15, y, 5));

  p.push(statPanel(++id, 'Active Projects', [
    target(`count(sum by (project) (count_over_time(${M_ALL} [$__range])))`)
  ], 'short', '#73BF69', 20, y, 4));
  y += 4;

  // ── Project cost ─────────────────────────────────────────────────────────────
  p.push(row('📁 Project Cost', y++));

  p.push(timeseriesPanel(++id, 'Spend by Project Over Time', [
    target(sumByInterval('project', 'cost_usd', M_ALL), '{{project}}')
  ], 0, y, 16, 10, {
    unit: 'currencyUSD', stacked: true, fillOpacity: 40,
    legendCalcs: ['sum'], legendPlacement: 'bottom'
  }));

  p.push(piechartPanel(++id, 'Spend Share by Project', [
    target(sumByRange('project', 'cost_usd', M_ALL), '{{project}}')
  ], 16, y, 8, 10));
  y += 10;

  // ── Efficiency ────────────────────────────────────────────────────────────────
  p.push(row('📊 Value & Efficiency', y++));

  p.push(tablePanel(++id, 'Output Tokens by Project', [
    target(sumByRange('project', 'total_output_tokens', M_ALL), '{{project}}')
  ], 0, y, 12, 10, { unit: 'short' }));

  p.push(tablePanel(++id, 'Cost per 1K Output by Project', [
    target(
      `sum by (project) (sum_over_time(${M_ALL} | json cost_usd | unwrap cost_usd [$__range])) / sum by (project) (sum_over_time(${M_ALL} | json total_output_tokens | unwrap total_output_tokens [$__range])) * 1000`,
      '{{project}}'
    )
  ], 12, y, 12, 10, { unit: 'currencyUSD' }));
  y += 10;

  // ── Temporal patterns ─────────────────────────────────────────────────────────
  p.push(row('📅 Temporal Patterns', y++));

  p.push(timeseriesPanel(++id, 'Daily Session Count', [
    target(`count(count_over_time(${M_ALL} [$__interval]))`, 'Sessions')
  ], 0, y, 12, 9, {
    fillOpacity: 40, lineWidth: 2,
    legendCalcs: ['sum', 'mean'], legendPlacement: 'bottom',
    colorOverrides: [['Sessions', '#5794F2']]
  }));

  p.push(timeseriesPanel(++id, 'Daily Spend', [
    target(
      `sum(sum_over_time(${M_ALL} | json cost_usd | unwrap cost_usd [1d]))`,
      'Spend'
    )
  ], 12, y, 12, 9, {
    unit: 'currencyUSD', fillOpacity: 60, lineWidth: 2, drawStyle: 'bars',
    legendCalcs: ['sum', 'mean'], legendPlacement: 'bottom',
    colorOverrides: [['Spend', '#FADE2A']]
  }));
  y += 9;

  // ── Model & effort cross-tabs ─────────────────────────────────────────────────
  p.push(row('🏷 Model & Effort by Project', y++));

  p.push(tablePanel(++id, 'Model Sessions by Project', [
    target(
      `sum by (project, model) (count_over_time(${M_ALL} [$__range]))`,
      '{{project}} — {{model}}'
    )
  ], 0, y, 12, 10, { unit: 'short' }));

  p.push(tablePanel(++id, 'Effort Level by Project', [
    target(
      `sum by (project, effort) (count_over_time(${M_ALL} [$__range]))`,
      '{{project}} — {{effort}}'
    )
  ], 12, y, 12, 10, { unit: 'short' }));
  y += 10;

  // ── Sentinel Findings ─────────────────────────────────────────────────────────
  const SF = '{app="sim-steward", component="log-sentinel", event="sentinel_finding"}';
  const SF_WARN = `${SF} | json severity | severity=~"warn|critical"`;

  p.push(row('🔍 Sentinel Findings', y++));

  p.push(statPanel(++id, 'Total Findings', [
    target(`count(count_over_time(${SF} [$__range]))`)
  ], 'short', '#5794F2', 0, y, 5));

  p.push(statPanel(++id, 'Warn / Critical', [
    target(`count(count_over_time(${SF_WARN} [$__range]))`)
  ], 'short', [
    { value: null, color: '#73BF69' },
    { value: 1,    color: '#FADE2A' },
    { value: 5,    color: '#F2495C' },
  ], 5, y, 5));

  p.push(timeseriesPanel(++id, 'Findings Over Time by Severity', [
    target(`sum by (severity) (count_over_time(${SF} | json severity [$__interval]))`, '{{severity}}')
  ], 10, y, 14, 5, {
    colorOverrides: [
      ['critical', '#F2495C'],
      ['warn',     '#FADE2A'],
      ['info',     '#5794F2'],
    ],
    legendPlacement: 'right',
    legendCalcs: ['sum'],
  }));
  y += 5;

  p.push(tablePanel(++id, 'Findings by Detector', [
    target(`sum by (detector) (count_over_time(${SF} | json detector [$__range]))`, '{{detector}}', 'instant')
  ], 0, y, 8, 8, { unit: 'short' }));

  p.push(tablePanel(++id, 'Findings by Severity', [
    target(`sum by (severity) (count_over_time(${SF} | json severity [$__range]))`, '{{severity}}', 'instant')
  ], 8, y, 8, 8, { unit: 'short' }));

  p.push(timeseriesPanel(++id, 'Escalated (T2) Investigations', [
    target(
      `count(count_over_time(${SF} | json escalated_to_t2 | escalated_to_t2=~"true" [$__interval]))`,
      'Escalated'
    )
  ], 16, y, 8, 8, {
    colorOverrides: [['Escalated', '#B877D9']],
    legendCalcs: ['sum'],
    fillOpacity: 40,
  }));
  y += 8;

  return dashboard('claude-dev-productivity', 'Claude Code — Developer Productivity',
    'now-30d', '5m',
    [templateVar('project', 'Project', '"project":"([^"]+)"')],
    p);
}

// ── write all files ───────────────────────────────────────────────────────────

const OUT = 'observability/local/grafana/provisioning/dashboards';

const builds = [
  ['claude-cost-intelligence.json',  buildCostIntelligence()],
  ['claude-model-comparison.json',   buildModelComparison()],
  ['claude-cache-health.json',       buildCacheHealth()],
  ['claude-session-profiling.json',  buildSessionProfiling()],
  ['claude-dev-productivity.json',   buildDevProductivity()],
];

for (const [filename, dash] of builds) {
  writeFileSync(`${OUT}/${filename}`, JSON.stringify(dash, null, 2));
  console.log(`✅ ${filename} — ${dash.panels.length} panels`);
}
console.log('\nAll 5 dashboards written.');
