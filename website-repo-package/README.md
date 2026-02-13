# Website Repo Package

This package contains everything needed to create and run a **separate git repo** for building the Sim Steward website (marketing + docs site). It is produced from the main Sim Steward plugin repo.

## Contents

| Item | Description |
|------|-------------|
| **cursor-agent-template-web/** | Reusable Cursor agent template tailored for a static website repo (Astro, Tailwind, Cloudflare Pages). Primary agent: web-developer; source dir: `website/`. |
| **prd-website.md** | Comprehensive PRD for the website git repo: purpose, audience, requirements (FR-IDs), tech stack, and roadmap alignment. |
| **roadmap-website-for-analysis.md** | Website roadmap (items that do not match SimHub plugin context). For analysis when the web repo is active. |

## How to Use

### 1. Create the web repo

- Create a new git repository (e.g. `sim-steward-website`).
- Copy `cursor-agent-template-web/` into the repo (e.g. as `.cursor/`, `memory-bank/`, `docs/` at the repo root).
- Copy `prd-website.md` to `docs/product/prd.md` (or keep both; use PRD as source of truth).
- Copy `roadmap-website-for-analysis.md` to `docs/product/roadmaps/roadmap-website-for-analysis.md`.

### 2. Initialize the site

- Create the `website/` directory in the new repo.
- Bootstrap an Astro project with Tailwind (see PRD tech stack).
- Configure Cloudflare Pages (wrangler or Git integration).

### 3. Fill placeholders

- In memory-bank and docs, replace any `{PROJECT_NAME}` / Sim Steward website–specific placeholders.
- Populate `docs/product/priorities.md` from the website roadmap when ready.

### 4. Work in Cursor

- Open the new repo in Cursor. The orchestration rules will route website work to **web-developer**.
- Use the PRD and roadmap for scope; align doc content with the plugin repo’s release milestones.

## Relationship to Plugin Repo

- **Plugin repo:** SimHub plugin (C#), iRacing, OBS, overlays. Roadmap: `docs/product/roadmaps/roadmap.md` (M0–M7).
- **Website repo:** Static site only. Roadmap: `roadmap-website-for-analysis.md`. The website documents the plugin (install, OBS setup, workflow); it does not implement plugin features.
- **Inventory:** The main repo’s website-related parts are documented in `docs/product/roadmaps/WEBSITE-REPO-INVENTORY.md` (in the plugin repo).

## Version

Generated from the Sim Steward plugin repo. When the plugin roadmap or web-developer agent changes, refresh this package (template, PRD, roadmap copy) as needed.
