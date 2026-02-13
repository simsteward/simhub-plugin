# Cursor Agent Orchestration Template

A self-contained template for setting up an AI-assisted development workflow in [Cursor](https://cursor.com). It provides a layered orchestration system where specialist AI agents collaborate through a shared memory bank, structured product docs, and automatic task routing.

## What's Included

```
cursor-agent-template/
  .cursor/
    rules/
      memory-bank.mdc            # Always-on: memory bank as source of truth
      delegation.mdc              # Always-on: auto-routing to specialist agents
      incremental-work.mdc        # Always-on: small-scope work, review checkpoints
      domain-coding.mdc           # Conditional: coding standards (glob-scoped)
    agents/
      code-agent.md               # Implementation specialist (rename for your stack)
      product-owner.md            # Ideation, decomposition, user stories
      priority-steward.md         # Priority/backlog management
      prd-compliance.md           # Requirements traceability
      code-reviewer.md            # Code review
      memory-bank-updater.md      # Memory bank maintenance
    skills/
      priority-tracking/
        SKILL.md                  # Priority file workflow
  memory-bank/
    README.md                     # Explains the 7 files and tiered reading
    projectbrief.md               # Scope, requirements, success criteria
    productContext.md              # Why, problems, how it works
    systemPatterns.md              # Architecture, decisions, component flow
    techContext.md                 # Stack, tools, constraints
    activeContext.md               # Current focus, active decisions
    progress.md                   # What works, what's left, done
    journal.md                    # Learned patterns, preferences
  docs/
    product/
      prd.md                      # Product requirements with FR-IDs
      priorities.md               # Now / Next / Backlog / Blocked / Done
      stories/
        _template.md              # User story template
    tech/
      architecture.md             # System architecture (optional)
      decisions/                  # ADRs (optional, .gitkeep preserved)
```

## Quick Start

### 1. Copy the template into your project

```bash
cp -r cursor-agent-template/ ~/your-project/
```

Or selectively copy the directories you need (`.cursor/`, `memory-bank/`, `docs/`).

### 2. Fill in placeholders

Every template file contains `{PLACEHOLDER}` markers. Search and replace these with your project-specific values.

**Core placeholders (must fill in):**

| Placeholder | Where Used | What to Fill In |
|---|---|---|
| `{PROJECT_NAME}` | All files | Your project name |
| `{PROJECT_DESCRIPTION}` | projectbrief, productContext, prd | One-line project description |
| `{TECH_STACK}` | techContext, coding rules, agents | Languages, frameworks, libraries |
| `{CODE_AGENT}` | delegation.mdc, code-agent.md | Your implementation agent name (e.g., `react-developer`, `go-backend`) |
| `{SOURCE_DIR}` | delegation.mdc, code-agent.md, techContext | Source code directory (e.g., `src`, `app`, `backend`) |
| `{GLOB_PATTERNS}` | domain-coding.mdc | File patterns for coding standards (e.g., `src/**/*.ts`) |
| `{FR_ID_PREFIX}` | prd.md, stories, prd-compliance | Your requirement ID scheme (e.g., `FR`) |
| `{PHASES}` | prd.md, priorities, activeContext | Your release phases (e.g., `MVP`, `v1.0`, `Phase 1`) |
| `{ARCHITECTURE}` | systemPatterns, architecture.md | Your system diagram (ASCII, mermaid, or text) |

**Quick find all unfilled placeholders:**

```bash
grep -rn '{' cursor-agent-template/ --include='*.md' --include='*.mdc' | grep -v 'node_modules'
```

### 3. Rename the code agent

The template ships with a generic `code-agent.md`. Rename it to match your stack:

```bash
mv .cursor/agents/code-agent.md .cursor/agents/react-developer.md  # example
```

Update the `name` field in the frontmatter and all references in `delegation.mdc`.

### 4. Write your PRD

Open `docs/product/prd.md` and fill in your requirements. Each requirement gets a unique FR-ID (e.g., `FR-001`, `FR-002`). These IDs are used throughout the system for traceability.

### 5. Populate memory bank

Fill in the 7 memory bank files with your project's context. Start with:
- `projectbrief.md` -- scope and requirements
- `techContext.md` -- your tech stack
- `activeContext.md` -- what you're working on now
- `progress.md` -- current state

The other files (`productContext.md`, `systemPatterns.md`, `journal.md`) can be filled in as the project evolves.

### 6. Start working

Open Cursor and start a conversation. The orchestration system will:
- Read memory bank for context (Tier 1 always, Tier 2 when needed)
- Auto-route tasks to specialist agents via the delegation rule
- Track priorities through `priorities.md`
- Maintain context across sessions through the memory bank

## How It Works

### The Five Layers

1. **Always-On Rules** (`.cursor/rules/`, `alwaysApply: true`) -- Memory bank governance, task routing, incremental work principles. Active in every conversation.

2. **Memory Bank** (`memory-bank/`) -- 7 markdown files holding persistent project context. Tiered reading strategy: Tier 1 files (`activeContext.md`, `progress.md`) are always read; Tier 2 files are read when needed.

3. **Specialist Agents** (`.cursor/agents/`) -- Each agent has a defined role, trigger terms, and workflow. The delegation rule auto-routes tasks to the right agent.

4. **Conditional Rules** (`.cursor/rules/`, glob-scoped) -- Coding standards that activate only when editing matching files.

5. **Skills** (`.cursor/skills/`) -- Procedural how-to instructions for repeatable workflows (e.g., priority tracking).

### Agent Chain

The agents work together in a chain:

```
product-owner (decomposes features into stories)
  → priority-steward (schedules stories into priorities)
    → code-agent (implements the work)
      → code-reviewer (reviews the changes)
        → priority-steward (marks done, promotes next)
          → memory-bank-updater (captures context)
```

### FR-ID Traceability

Every story, priority, and compliance check traces back to a PRD requirement ID. This ensures nothing falls through the cracks and scope stays controlled.

## Optional Extensions

These are not included in the template but can be added as your project grows:

| Extension | When to Add | How |
|---|---|---|
| **Ops agents** (Statsig, Vercel, Cloudflare, etc.) | If you use feature flags or infra-as-code | Add agent definitions in `.cursor/agents/` and routing rows in `delegation.mdc` |
| **MCP server references** | If agents should use MCP integrations | Add MCP notes to relevant agent definitions and `memory-bank.mdc` |
| **Execution plans** (`docs/product/plans/`) | If you want detailed implementation plans | Create `plans/` directory; reference from stories |
| **API design** (`docs/tech/api-design.md`) | If your project has API contracts | Create the file; reference from `techContext.md` |
| **Release roadmaps** | If you want timeline-based planning | Add to `docs/product/` |
| **Additional coding rules** | For multi-language projects | Add more `.mdc` files in `.cursor/rules/` with appropriate glob patterns |
| **Domain-specific skills** | For repeatable project workflows | Add to `.cursor/skills/{skill-name}/SKILL.md` |
