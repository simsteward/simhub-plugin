# Ralph plan — refine the backlog (no code changes)

Fresh context. Your only job this iteration: keep `.ralph/fix_plan.md` correct and well-ordered. NO implementation.

1. Read `.ralph/fix_plan.md`, `.ralph/progress.md`, and the specs under `docs/superpowers/specs/incident-engine/`.
2. Search the engine (`src/SimSteward.IncidentEngine/`) + its tests for what already exists (use subagents). Mark done items done.
3. Decompose any item that is bigger than one iteration into the smallest shippable sub-tasks, each with a one-line acceptance criterion. Order: Track 0 (harness/gate hardening) → Track 1 (identity) → Track 2 (detectors) → Track 3 (index+jump).
4. Commit only `.ralph/fix_plan.md` (and any new spec files) to `ralph/auto`. Append a note to `.ralph/progress.md`. Do NOT touch code, iRacing, SimHub, deploy, or main.
