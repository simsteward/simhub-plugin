---
name: agent-plan
description: Generate structured plan markdown optimized for agent execution, then save to ContextStream. Use when the user asks to write a plan, create an implementation plan, or says /plan.
---

# Agent Plan Writer

Generate a structured implementation plan optimized for agent consumption, then save it to ContextStream.

## When Invoked

Use this skill when the user asks to write a plan, create an implementation plan, or uses `/plan`.

## Workflow

### 1. Load context
Call `mcp__contextstream__context(user_message=<task description>)` to load relevant lessons, decisions, and project context before planning.

### 2. Search for affected files
Call `mcp__contextstream__search(mode="auto", query=<feature area>)` to identify existing files the plan will touch. Use returned file paths in step definitions — do not guess paths.

### 3. Produce plan markdown

Output the plan in this exact format:

```markdown
---
context: <1-2 sentences: why this change is needed and what prompted it>
goals:
  - <measurable outcome — not a task, a result>
  - <measurable outcome>
---

## Steps

### 1. <Step Title>
**Files:** `path/to/file.ext`, `path/to/other.ext`
**What:** <Exact change — which function/class/config to add or modify and how>
**Done when:** <Observable, testable acceptance criteria — what you can check>

### 2. <Step Title>
**Files:** `...`
**What:** `...`
**Done when:** `...`

## Verification
<One end-to-end test: command to run, action to take, or output to observe that confirms the entire plan worked>
```

**Rules for agent-consumable plans:**
- `context` answers *why*, so agents make correct decisions when steps are ambiguous
- `goals` are outcomes ("incidents appear in dashboard"), not actions ("add WebSocket handler")
- Every step must list exact file paths — no "find the relevant file"
- `Done when` must be observable without reading code (a log line, a UI state, a passing test, a return value)
- `Verification` covers the whole plan, not just the last step
- No step should depend on an unstated assumption

### 4. Save to ContextStream

After producing the markdown, save the plan:

```
mcp__contextstream__session(
  action="capture_plan",
  title=<plan title>,
  description=<context field value>,
  steps=[
    { id: "step-1", title: <step title>, description: <what + done-when combined>, order: 1, estimated_effort: "small"|"medium"|"large" },
    ...
  ]
)
```

Then create a task:
```
mcp__contextstream__memory(
  action="create_task",
  title=<plan title>,
  plan_id=<returned plan ID>
)
```

Confirm to the user: "Plan saved to ContextStream — ID: `<plan_id>`"

## Output

Present the full plan markdown to the user, then show the ContextStream plan ID on a single line at the end.
