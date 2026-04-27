#!/usr/bin/env node
'use strict';
const fs = require('fs');

const exprDs = { type: '__expr__', uid: '__expr__' };

// Fix the targets format: convert {datasource: {type:'__expr__'}, model:{...}}
// to {datasource: {type:'__expr__', uid:'__expr__'}, type:'math', expression:'...'}
function fixTargets(targets) {
  if (!targets) return targets;
  return targets.map(t => {
    if (t.datasource && t.datasource.type === '__expr__') {
      const fixed = { ...t, datasource: exprDs };
      if (t.model) {
        fixed.type = t.model.type || 'math';
        fixed.expression = t.model.expression;
        fixed.hide = t.model.hide !== undefined ? t.model.hide : (t.hide || false);
        delete fixed.model;
      }
      return fixed;
    }
    return t;
  });
}

const files = [
  'observability/local/grafana/provisioning/dashboards/claude-token-cost.json',
  'observability/local/grafana/provisioning/dashboards/claude-intelligence.json',
  'observability/local/grafana/provisioning/dashboards/claude-code-overview.json',
];

for (const path of files) {
  const dash = JSON.parse(fs.readFileSync(path, 'utf8'));
  let fixed = 0;
  for (const panel of dash.panels) {
    if (panel.targets) {
      const before = JSON.stringify(panel.targets);
      panel.targets = fixTargets(panel.targets);
      if (JSON.stringify(panel.targets) !== before) fixed++;
    }
  }
  fs.writeFileSync(path, JSON.stringify(dash, null, 2));
  JSON.parse(fs.readFileSync(path, 'utf8'));
  console.log(path.split('/').pop() + ': fixed ' + fixed + ' panels');
}
