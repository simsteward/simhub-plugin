# Ralph increment — build the Incident Engine, one narrow test-validated step

You are one iteration of an autonomous loop building the **pure greenfield `SimSteward.IncidentEngine`** library
(`src/SimSteward.IncidentEngine/`). Fresh context; the filesystem is your memory. Do exactly ONE small thing, prove it
with tests, commit it, and update the logs. Then stop.

## 0. Orient (read first)
- `.ralph/fix_plan.md` — the prioritized backlog (your task source).
- `.ralph/progress.md` — what prior iterations did (do not repeat).
- `.ralph/AGENT.md` — discovered commands, gotchas, recovery.
- The relevant spec under `docs/superpowers/specs/incident-engine/` — the **single source of truth** for what to build.

## 1. Pick exactly ONE task
- Choose the **single highest-priority incomplete** item in `.ralph/fix_plan.md`. Track 0 before Track 1 before Track 2/3.
- **Search before implementing** (use Explore subagents) — do NOT assume something isn't already there. This is incremental.
- If the task is bigger than one iteration or under-specified, **split it in `fix_plan.md`** and take only the smallest piece. One thing per loop.

## 2. Build it test-first (TDD — non-negotiable)
- Invoke the `superpowers:test-driven-development` skill. Write the **failing test first** (xUnit in `SimSteward.IncidentEngine.Tests`), run it, watch it fail for the right reason, then write the **minimal full implementation** to pass.
- **NO placeholders, NO stubs, NO "TODO later".** Full implementations only. Every test asserts real behavior and is **deterministic** (no wall-clock, no ordering/env/path/network dependence — inject what you need).
- Build *from the spec*. If the spec is silent/ambiguous, the task is a **spec task**: write/refine the spec first (in `docs/superpowers/specs/incident-engine/`), then implement.

## 3. Prove it green — the gate
- Run `bash .ralph/gate.sh`. It must exit **0**.
- If anything is red — **including pre-existing/unrelated failures** — it is your job to fix it this iteration until green. A test that *can* flake is a defect: make it deterministic.
- The gate is offline only. **You must NEVER**: touch SimHub or iRacing, run `deploy.ps1`, start a live sweep, open the live WS, or modify `main`.

## 4. Commit + record (one tight commit)
- `git add` only the files you changed; commit to **`ralph/auto`** with a clear `feat(engine): …` / `test(engine): …` / `spec(engine): …` message.
- Append one entry to `.ralph/progress.md`: `iter — <task> — <result> — <commit7>`.
- Tick the task in `.ralph/fix_plan.md` (use a subagent to keep this context lean). Record any reusable command/gotcha in `.ralph/AGENT.md`.
- If the entire `fix_plan.md` is complete and the gate is green, write the file `.ralph/state/COMPLETE` and say so.

## Priorities (higher number wins if in tension)
- 9 — One narrow increment, fully done and tested, beats three half-done.
- 99 — The gate must be GREEN (not skipped) before you commit. No exceptions.
- 999 — Quality is king. No placeholders. No flaky tests. Determinism always.
- 999 — Grafana is the SOURCE OF TRUTH for logging. If your change affects logging/observability, validate it by querying Grafana (Loki datasource `grafanacloud-logs`, on `simsteward.grafana.net`) — not just by reading code. After any dashboard JSON edit, re-sync to Cloud with `npm run dash:deploy`.
- 9999 — NEVER touch iRacing/SimHub/deploy/main. The autonomous loop is offline-only.
- 99999 — Keep the engine PURE: no IRSDKSharper/SimHub/Fleck references ever enter `SimSteward.IncidentEngine`.
