# sim-steward-plugin

The official SimHub plugin for simsteward.com – incident detection and protest automation for iRacing. Spend more time racing, less time clipping.

## Project Structure

```
├── memory-bank/          # Primary AI context (read at start of every task)
│   ├── projectbrief.md
│   ├── productContext.md
│   ├── systemPatterns.md
│   ├── techContext.md
│   ├── activeContext.md
│   └── progress.md
│
├── .cursor/
│   ├── agents/           # Subagents (simhub-developer, priority-steward)
│   ├── rules/            # memory-bank.mdc (supersedes), simhub-csharp.mdc (plugin/**/*.cs)
│   └── skills/           # Project skills (priority-tracking)
│
├── docs/                 # Product & technical planning
│   ├── product/          # PRD, priorities, user stories
│   └── tech/             # Architecture, API design, ADRs
│
├── plugin/               # SimHub plugin source
│   ├── src/
│   ├── Properties/
│   └── assets/
│
├── LICENSE
└── README.md
```

## Cursor Configuration

- **Memory Bank** (primary): Read all `memory-bank/*.md` at start of every task. Supersedes other rules when in conflict.
- **Journal**: Captures learned patterns and preferences.
- **Agents & skills**: Supplemental; use when relevant.

## Optional: .cursorignore

Create `.cursorignore` to exclude build outputs from indexing (e.g. `bin/`, `obj/`, `.vs/`). Uses gitignore syntax.

## Quick Links

- [Product Requirements](docs/product/README.md)
- [Tech Plans](docs/tech/README.md)
- [Plugin](plugin/README.md)
