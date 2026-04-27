#!/usr/bin/env node
// Add legendFormat to sentinel stat panel targets that are missing it.
// Uses each panel's title as the legendFormat so value_and_name mode shows a clean label.

const fs = require('fs');
const path = require('path');

const filepath = path.join(
  __dirname,
  '../observability/local/grafana/provisioning/dashboards/simsteward-log-sentinel.json'
);

const json = JSON.parse(fs.readFileSync(filepath, 'utf8'));
let fixed = 0;

for (const panel of json.panels) {
  if (panel.type !== 'stat') continue;
  if (!panel.title || !panel.targets) continue;

  for (const target of panel.targets) {
    if (!target.legendFormat || target.legendFormat === '') {
      target.legendFormat = panel.title;
      fixed++;
    }
  }
}

fs.writeFileSync(filepath, JSON.stringify(json, null, 2), 'utf8');
console.log(`Fixed ${fixed} target(s) — added legendFormat from panel title`);
