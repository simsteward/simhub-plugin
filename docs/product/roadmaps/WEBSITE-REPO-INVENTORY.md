# Website-Related Parts of the Repo

Inventory of all references to the website across the repository. Use this when packaging the web repo or when splitting website vs plugin work.

**Note:** There is no `website/` directory in this repo yet. The website is intended to be built in a **separate git repo**; this inventory and the website-repo package support that.

---

## 1. Cursor agents

| File | Purpose |
|------|---------|
| `.cursor/agents/web-developer.md` | Static-site developer for the Sim Steward project website. Domain: `website/`. Stack: Astro, Tailwind CSS, Cloudflare Pages. Content: landing page, docs, install guide, SEO. |

**Other agents** (plugin/backend): simhub-developer, cloudflare-worker, code-reviewer, product-owner, priority-steward, prd-compliance, memory-bank-updater, statsig-feature-ops. None of these are website-specific; code-reviewer and product-owner can support website work when delegated.

---

## 2. Cursor rules

| File | Website reference |
|------|-------------------|
| `.cursor/rules/delegation.mdc` | Row: "Editing `website/**`, Astro, Tailwind, landing page, docs site, Cloudflare Pages, marketing site → web-developer". Model preference: "web-developer: Prefer code-focused model". |

No other rules mention the website. `memory-bank.mdc`, `incremental-work.mdc` apply to all work including website.

---

## 3. Skills

No website-specific skills. `.cursor/skills/priority-tracking/SKILL.md` is generic (priorities.md workflow) and applies to any product work including website.

---

## 4. Memory bank

| File | Website reference |
|------|-------------------|
| `memory-bank/activeContext.md` | "web platform (4 layers)" listed under **What Was Removed** (gone, not deferred). |
| `memory-bank/projectbrief.md` | Scope **Out:** "web platform". |
| `memory-bank/journal.md` | "web platform" among purged old-era artifacts. |
| `memory-bank/techContext.md` | "No Cloudflare" (no server); no explicit website mention. |

No memory-bank file defines website requirements; they only state that the web platform is out of scope for this (plugin) repo.

---

## 5. Product docs

| File | Website reference |
|------|-------------------|
| `docs/product/prd.md` | Plugin PRD only. No FR-IDs for website. Section 8 / Future: "Web platform" in "Explicitly Out of Scope". |
| `docs/product/roadmaps/roadmap.md` | SimHub plugin roadmap (M0–M7). "Future (Explicitly Out of Scope)": "Web platform". |
| `docs/product/priorities.md` | Plugin priorities only (SCAFFOLD, FR-001–FR-015). No website items. |

---

## 6. Cursor agent template (reusable)

| File | Website reference |
|------|-------------------|
| `cursor-agent-template/README.md` | Optional extensions: "Ops agents (Statsig, Vercel, **Cloudflare**, etc.)" for feature flags or infra. No website-specific content; template is generic. |

The template’s `delegation.mdc` uses placeholders `{CODE_AGENT}`, `{SOURCE_DIR}`; for a web repo these would be set to `web-developer` and `website/`.

---

## 7. Other

- **cloudflare-worker.md** agent: Cloudflare Worker/R2/Workers AI backend. **Not** website; it’s backend/API. Website uses Cloudflare **Pages** (static), not Workers.
- **No** `website/` directory in repo.

---

## Summary

| Category | Website-specific items |
|----------|-------------------------|
| Agents | `web-developer.md` only |
| Rules | `delegation.mdc` (one row + model preference) |
| Skills | None |
| Memory bank | Mentions of "web platform" as out of scope only |
| Product docs | Out-of-scope note in PRD and roadmap |
| Template | Generic; can be filled with web-developer + `website/` |

The **website-repo package** (see `website-repo-package/` or equivalent) contains a reusable Cursor agent template tailored for a web repo and a comprehensive PRD for the website git repo, plus a second roadmap file for website-only items for later analysis.
