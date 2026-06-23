#!/usr/bin/env bash
# .ralph/ralph-loop.sh N [prompt] — N capped Ralph iterations on ralph/auto (Sonnet default).
# Stops on: COMPLETE signal, 3 consecutive no-commit iterations (stall), or N reached.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)" || exit 9
[ "$(git branch --show-current)" = "ralph/auto" ] || { echo "refuse: not on ralph/auto"; exit 1; }
N="${1:-15}"; PROMPT="${2:-increment}"; PFILE=".ralph/prompts/${PROMPT}.md"
[ -f "$PFILE" ] || { echo "no prompt file: $PFILE"; exit 1; }
MODEL="${RALPH_MODEL:-sonnet}"; mkdir -p .ralph/state; stall=0
for i in $(seq 1 "$N"); do
  echo "=========== RALPH $i/$N  prompt=$PROMPT model=$MODEL  $(date -u +%H:%M:%S) ==========="
  before=$(git rev-parse HEAD)
  claude -p "$(cat "$PFILE")" --model "$MODEL" --permission-mode acceptEdits || echo "(iter returned non-zero — continuing)"
  if [ "$(git rev-parse HEAD)" = "$before" ]; then
    stall=$((stall+1)); echo "[no new commit; stall=$stall/3]"
    [ "$stall" -ge 3 ] && { echo "!! STALL — 3 iters, no commit. Stopping for human review."; exit 2; }
  else stall=0; fi
  [ -f .ralph/state/COMPLETE ] && { echo "COMPLETE signaled — stopping."; break; }
done
echo ">>> batch done. Review 'ralph/auto', run usage checkpoint, then continue."
