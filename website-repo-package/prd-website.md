# Sim Steward Website — Product Requirements Document

> **Version:** 1.0  
> **Purpose:** Comprehensive PRD for the **website git repo** (static marketing and documentation site). Use when creating or maintaining the separate web repository for Sim Steward.  
> **Plugin context:** The SimHub plugin is built in a separate repo; this PRD covers only the website. Roadmap items that do not match the plugin context are in `roadmap-website-for-analysis.md`.

---

## 1. Executive Summary

The Sim Steward **website** is a static marketing and documentation site. It communicates what Sim Steward is (fast incident clipping for iRacing protests), how to install and use it (SimHub plugin + OBS), and where to get it. The site is **not** a web app or backend; it has no auth, no APIs, and no server logic.

---

## 2. Problem Statement

- Users need a single place to **discover** Sim Steward, **understand** the value (seconds not minutes to clip incidents), and **install** it (SimHub + OBS).
- Without a clear landing page and docs, adoption and support burden increase. The website fills that gap with a fast, static, maintainable site.

---

## 3. User Personas

| Persona | Goal | Pain Point |
|---------|------|------------|
| **New user** | Install the plugin and get first clip working | Doesn’t know SimHub/OBS setup or where to download |
| **Racer** | Decide if Sim Steward is worth trying | Needs quick overview of features and “how it works” |
| **Existing user** | Troubleshoot or look up a step | Needs findable docs (install, OBS, first-use, FAQ) |

---

## 4. Requirements — Website Scope

**Foundation:** Static site only. No backend, no auth, no CMS. Content in repo (Markdown/MDX).

| ID | Requirement | Description | Priority |
|----|-------------|-------------|----------|
| WR-001 | Landing page | Hero, feature overview, screenshots/demos, “how it works,” download/install CTA | Must |
| WR-002 | Docs — Installation | How to install the SimHub plugin, first-time setup | Must |
| WR-003 | Docs — OBS setup | OBS WebSocket setup, connection, troubleshooting | Must |
| WR-004 | Docs — First-use walkthrough | End-to-end: detect incident → replay → record → save | Should |
| WR-005 | Docs — Troubleshooting & FAQ | Common issues, FAQ; expand as product ships | Should |
| WR-006 | Changelog / release notes | When applicable | Should |
| WR-007 | SEO & shareability | Meta tags, Open Graph, basic accessibility, responsive layout | Must |
| WR-008 | Deploy & CI | Deploy to Cloudflare Pages (wrangler or Git integration); reproducible build | Must |

---

## 5. Tech Stack

| Layer | Choice | Notes |
|-------|--------|------|
| Framework | Astro | Content collections, Markdown-native, islands only if needed |
| Styling | Tailwind CSS | Utility-first, design system in config |
| Hosting | Cloudflare Pages | Git-push or wrangler deploy |
| Content | Markdown/MDX in repo | No CMS |
| Backend | None | Static only |

---

## 6. Roadmap Alignment

### 6.1 Roadmap items included (for website repo)

The **website roadmap** is in `roadmap-website-for-analysis.md`. It is scoped for the website only and does not duplicate the plugin roadmap. Summary of website phases for analysis:

1. **Foundation** — Repo setup, Astro + Tailwind, Cloudflare Pages deploy, basic layout.
2. **Landing** — Landing page (hero, features, CTA).
3. **Docs core** — Installation, OBS setup, first-use.
4. **Docs expand** — Troubleshooting, FAQ, changelog.
5. **Polish** — SEO, Open Graph, performance, accessibility.

### 6.2 Relationship to plugin roadmap

- **Plugin repo roadmap** (main repo): M0–M7 (Detect + Clip + Save, then multi-camera). All plugin work.
- **Website repo roadmap**: Content and site features only (landing, docs, deploy). The website **documents** plugin releases; it does not **implement** plugin features.
- Align doc updates with plugin milestones (e.g. install guide when plugin ships, OBS section when OBS integration ships).

---

## 7. Constraints & Risks

| # | Item | Impact | Mitigation |
|---|------|--------|------------|
| 1 | Content must stay in sync with plugin | Stale docs | Tie doc updates to plugin release checklist; single source of truth in plugin repo for behavior |
| 2 | No backend | No dynamic data | Acceptable; static content only |
| 3 | Cloudflare Pages | Vendor lock-in for host | Low; static export is portable |

---

## 8. Out of Scope (Website Repo)

- Backend APIs, Cloudflare Workers, serverless functions.
- User authentication, accounts, databases.
- Video upload, processing, or storage.
- Payment, licensing, or feature gates.
- Implementing or building the SimHub plugin (lives in plugin repo).

---

## 9. Success Criteria

- New user can find the site, read what Sim Steward does, and follow install + OBS + first-use docs to produce a first clip.
- Site is fast (minimal JS), accessible, and deployable via Cloudflare Pages.
- Roadmap in `roadmap-website-for-analysis.md` is used for prioritization when the web repo is active.

---

## Appendix: Glossary

| Term | Definition |
|------|------------|
| WR-ID | Website Requirement ID (e.g. WR-001) |
| Cloudflare Pages | Static site hosting; deploy via Git or wrangler |
| Astro | Static-site framework; Markdown/MDX, optional islands |
