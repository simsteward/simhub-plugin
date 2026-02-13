# Website Roadmap — For Later Analysis

> **Purpose:** Roadmap items that do **not** match the SimHub plugin context. The main roadmap (`roadmap.md`) is 100% plugin (M0–M7). This file captures **web platform / website** scope for a separate git repo, to be analyzed and scheduled when the website is in scope.
>
> **Source:** Derived from `.cursor/agents/web-developer.md`, PRD "Future — Out of Scope" (web platform), and product context. Not part of the plugin release sequence.

---

## Context

- In the **plugin repo**, "Web platform" is explicitly out of scope (PRD Section 8; roadmap "Future (Explicitly Out of Scope)").
- The **website** is intended to be built in a **separate git repo**, with its own PRD and Cursor agent setup (see `website-repo-package/`).
- This file is the placeholder for **website-specific roadmap items** so they are not lost and can be analyzed when the web repo is created or prioritized.

---

## Website Scope (from web-developer agent)

**Product:** Static marketing and documentation site for Sim Steward — not a web app or backend.

**Tech:** Astro, Tailwind CSS, Cloudflare Pages. Markdown/MDX in repo; no CMS.

**Content surfaces:**

| # | Item | Description | Priority |
|---|------|-------------|----------|
| W1 | Landing page | Hero, feature overview, screenshots/demos, "how it works," download/install CTA | Must |
| W2 | Docs — Installation | How to install the SimHub plugin, first-time setup | Must |
| W3 | Docs — OBS setup | OBS WebSocket setup, connection, troubleshooting | Must |
| W4 | Docs — First-use walkthrough | End-to-end: detect incident → replay → record → save | Should |
| W5 | Docs — Troubleshooting & FAQ | Common issues, FAQ; expand as product ships | Should |
| W6 | Changelog / release notes | When applicable | Should |

**Quality:** Fast load (zero JS by default), semantic HTML, basic SEO (meta tags, Open Graph), accessibility, responsive design.

**Out of scope for website:** Backend APIs, Cloudflare Workers, serverless, auth, databases, video upload/processing, payment, licensing. Static only.

---

## Suggested Phases (for analysis)

When analyzing this roadmap for the web repo, consider sequencing along these lines:

1. **Foundation** — Repo setup, Astro + Tailwind, Cloudflare Pages deploy, basic layout.
2. **Landing** — Landing page (hero, features, CTA).
3. **Docs core** — Installation, OBS setup, first-use.
4. **Docs expand** — Troubleshooting, FAQ, changelog.
5. **Polish** — SEO, Open Graph, performance, accessibility pass.

Dependencies and dates to be set when the website repo is active.

---

## Relationship to Plugin Roadmap

- **Plugin roadmap** (`roadmap.md`): M0–M7, MVP then Part 2. No website work.
- **This file:** Website-only work; does not change plugin milestones.
- The website will **document** the plugin (install, OBS, workflow); it does not **implement** plugin features. Align doc updates with plugin releases (e.g. after MVP, after Part 2 features).

---

## Status

**Status:** For later analysis. Not scheduled in the plugin repo. Use when creating or planning the website git repo and when using the website-repo package (PRD + Cursor agent template).
