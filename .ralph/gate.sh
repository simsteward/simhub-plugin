#!/usr/bin/env bash
# .ralph/gate.sh — the OFFLINE validation gate. Deterministic, ~40-60s.
# NO SimHub, NO iRacing, NO deploy.ps1, NO live WS. Exit 0 iff every step passes.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 9
fail=0
step(){ echo; echo "### GATE $1"; }

step "1/5 IncidentEngine build+test (Debug) — the greenfield module"
dotnet test src/SimSteward.IncidentEngine.Tests --nologo -v q -c Debug || { echo "!! engine tests FAILED"; fail=1; }

step "2/5 Plugin tests (regression, Debug)"
dotnet test src/SimSteward.Plugin.Tests --nologo -v q -c Debug || { echo "!! plugin tests FAILED"; fail=1; }

step "3/5 test-rig node unit tests"
node --test scripts/test-rig/run.test.js || { echo "!! node tests FAILED"; fail=1; }

step "4/5 node --check (harness + rig scripts)"
for f in scripts/test-rig/harness/*.mjs scripts/test-rig/run.js; do
  [ -f "$f" ] && { node --check "$f" || { echo "!! syntax FAIL: $f"; fail=1; }; }
done

step "5/5 secret lint"
pnpm -s secrets:lint || { echo "!! secretlint FAILED"; fail=1; }

echo
if [ "$fail" -eq 0 ]; then echo "=== GATE GREEN ✓ ==="; exit 0; else echo "=== GATE RED ✗ ==="; exit 1; fi
