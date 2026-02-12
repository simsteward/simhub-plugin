---
name: priority-tracking
description: Maintains and updates project priorities in docs/product/priorities.md. Use when the user asks to update priorities, track tasks, plan work, reprioritize, complete a task, start new work, or review what's next.
---

# Priority Tracking

## Canonical File

All priorities live in `docs/product/priorities.md`.

## When to Update

| Trigger | Action |
|---------|--------|
| User completes a task | Move from Now/Next to Done; promote next item |
| User starts work | Move item to Now; ensure Next is populated |
| User adds a task | Add to Next (with priority) or Backlog |
| User reports blocker | Add to Blocked with blocker description |
| User asks "what's next?" | Read priorities; suggest next item |
| User reprioritizes | Reorder Next; update Backlog |

## Section Definitions

| Section | Purpose |
|---------|---------|
| **Now** | Currently in progress (1–3 items max) |
| **Next** | Queued, ordered by priority |
| **Backlog** | Not yet scheduled |
| **Blocked** | Waiting on something |
| **Done** | Recently completed |

## Format

- Use markdown tables for Now, Next, Blocked, Done.
- Use bullet list for Backlog.
- Include **Last updated** at top; update when editing.
- Keep Now lean (1–3 items); avoid context switching.

## Workflow

1. Read current priorities.
2. Apply the requested change (add, move, complete, block).
3. Reorder Next if needed.
4. Update Last updated date.
5. Summarize changes for the user.

## Post-Task Delegation

After implementing a task, delegate to priority-steward to update priorities (move completed item to Done, promote Next). This happens automatically via the delegation rule.
