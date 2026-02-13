# Memory Bank

The memory bank is the **highest source of truth** for project context. It holds 7 markdown files that give AI agents (and humans) persistent context across sessions.

## Files

| File | Purpose | Update Frequency |
|------|---------|-----------------|
| `activeContext.md` | Current focus, active decisions, what was removed | **Every session** |
| `progress.md` | What works, what's left, known issues, recent completions | **Every session** |
| `projectbrief.md` | Scope, core requirements, success criteria | Rarely (scope shifts only) |
| `productContext.md` | Why this exists, problems solved, how it works | Rarely (scope shifts only) |
| `systemPatterns.md` | Architecture, key decisions, component relationships | When architecture changes |
| `techContext.md` | Stack, tools, constraints, integrations | When tech changes |
| `journal.md` | Learned patterns, preferences, project intelligence | When you learn something |

## Tiered Reading Strategy

Agents don't read all 7 files every time. This saves tokens and keeps responses focused.

**Tier 1 (always read first):** `activeContext.md` and `progress.md`

**Tier 2 (read when needed):** The other 5 files -- read when the task involves architecture, product scope, or unfamiliar context, or when Tier 1 references something you need more detail on.

## Principles

- **Summaries, not duplication.** Memory bank summarizes and contextualizes. Operational files (`prd.md`, `priorities.md`) hold detailed data. Cross-reference rather than copy.
- **Memory bank wins.** If a memory bank file and an operational file disagree, the memory bank supersedes.
- **Scannable.** Each file should be readable in under 30 seconds. Use tables and bullets, not prose.
- **Date your entries.** Include dates in `progress.md` and `journal.md` so context has a timeline.

## Updating

- Focus updates on `activeContext.md` and `progress.md` (most volatile).
- Say "update memory bank" to trigger a comprehensive review of all files via the `memory-bank-updater` agent.
- After major implementation changes, update relevant files to keep context fresh.
