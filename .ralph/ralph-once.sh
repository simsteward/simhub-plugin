#!/usr/bin/env bash
# .ralph/ralph-once.sh [increment|plan|test-harden] — ONE Ralph iteration (HITL). Sonnet by default.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 9
[ "$(git branch --show-current)" = "ralph/auto" ] || { echo "refuse: not on ralph/auto"; exit 1; }
PROMPT="${1:-increment}"; PFILE=".ralph/prompts/${PROMPT}.md"
[ -f "$PFILE" ] || { echo "no prompt file: $PFILE"; exit 1; }
MODEL="${RALPH_MODEL:-sonnet}"
echo ">>> ralph-once prompt=$PROMPT model=$MODEL $(date -u +%H:%M:%S)"
claude -p "$(cat "$PFILE")" --model "$MODEL" --permission-mode acceptEdits
