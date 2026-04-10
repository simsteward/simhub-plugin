#!/bin/bash

files=("claude-token-cost.json" "claude-code-overview.json" "claude-intelligence.json" "claude-cache-context.json" "simsteward-log-sentinel.json")

for file in "${files[@]}"; do
  echo ""
  echo "====================================="
  echo "FILE: $file"
  echo "====================================="
  
  # Extract title, type, and expr for financial/token panels
  awk '
    /"title"/ { title=$0; gsub(/.*"title":\s*"/, "", title); gsub(/".*/, "", title) }
    /"type":\s*"(stat|gauge|timeseries|table)"/ { type=$0; gsub(/.*"type":\s*"/, "", type); gsub(/".*/, "", type) }
    /"expr"/ && (/"cost|token|cache|burn|output|input|rate|ratio|efficiency"/) { 
      expr=$0
      gsub(/.*"expr":\s*"/, "", expr)
      gsub(/".*/, "", expr)
      if (length(title) > 0) {
        printf "PANEL: %s [%s]\nEXPR: %s\n\n", title, type, expr
        title=""
        type=""
      }
    }
  ' "$file"
done
