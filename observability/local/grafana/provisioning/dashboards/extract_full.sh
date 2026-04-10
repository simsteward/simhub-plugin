#!/bin/bash

for file in claude-token-cost.json claude-code-overview.json claude-intelligence.json claude-cache-context.json simsteward-log-sentinel.json; do
  echo ""
  echo "========== $file ==========="
  echo ""
  
  # Extract panels containing cost/token/cache metrics
  sed -n '/"title":/,/"targets":/p' "$file" | \
  grep -B5 '"cost_usd\|total_cache_read_tokens\|total_output_tokens\|total_input_tokens' | \
  grep -E '"title"|unwrap' | head -40
done
