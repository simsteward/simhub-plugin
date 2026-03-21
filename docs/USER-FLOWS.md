# Sim Steward — User Flows (today)

**What this is:** Step-by-step journeys through the actual shipped UI. Covers what happens when you click each thing, where flows work well, and where they break.

**Companion docs:**
- [PRODUCT-FLOW.md](PRODUCT-FLOW.md) — north-star vision, feature maturity table, what's missing
- [USER-FEATURES-PM.md](USER-FEATURES-PM.md) — PM-style feature descriptions and connections

---

## Overview

<details>
<summary>Overview diagram — how all flows connect</summary>

[diagrams/user-flows-overview.mmd](diagrams/user-flows-overview.mmd)

</details>

---

## Flow 1 — Check session health and connectivity

**Goal:** Confirm the dashboard is live, iRacing is in replay, and the plugin is connected before doing anything else.

<details>
<summary>Flow diagram</summary>

[diagrams/user-flow-1-session-health.mmd](diagrams/user-flow-1-session-health.mmd)

</details>

**What works:** Auto-reconnect is reliable (3 s loop). All three diagnostic dots and the WS badge give independent signals.

**Gap:** No toast or alert when WS reconnects after a drop — user must glance at the badge.

---

## Flow 2 — Review a specific incident

**Goal:** Jump to an incident frame and read its details.

<details>
<summary>Flow diagram</summary>

[diagrams/user-flow-2-review-incident.mmd](diagrams/user-flow-2-review-incident.mmd)

</details>

**Also works from:** [This driver's incidents](#flow-3--focus-on-one-driver) left panel and [Captured incidents](#flow-6--review-the-captured-incidents-tab) tab — same seek + meta strip behavior.

**Gap (issue 5):** Meta strip expands in the bottom dock. If you clicked from the left-column "This driver's incidents" panel, the expansion is far from your click and easy to miss.

---

## Flow 3 — Focus on one driver

**Goal:** Narrow everything to a single car's incidents and telemetry.

<details>
<summary>Flow diagram</summary>

[diagrams/user-flow-3-focus-driver.mmd](diagrams/user-flow-3-focus-driver.mmd)

</details>

**Gap (issue 2):** Car dropdown options are hardcoded (`#99 J. Smith (You)`, `#12 A. Jones`, etc.), not populated from the plugin's `drivers` state message. Works in PoC only.

**Important distinction:** "This driver's incidents" left panel is **not redundant** with the "Mine" chip. Mine shows only your own incidents (`player: true`). This panel shows the selected car — used by stewards to review opponent incidents.

---

## Flow 4 — Walk driver incidents (automated seek)

**Goal:** Automatically step through every incident for the selected car, logging a record of each.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Select telemetry car]) --> B[Click Find driver's incidents]
  B --> C{Incidents for this car\nin leaderboard?}
  C -- No --> D([Toast: No incidents for this driver\nScan aborted])
  C -- Yes --> E[Queue unique frames for that car\nmax 200]
  E --> F["⚠️ Queue = already-known frames from leaderboard\n(not a YAML scan — no new discovery)"]
  F --> G[Button pulses red: Stop scan\nOther scan button disabled]
  G --> H[Loop: for each frame in queue]
  H --> I[send seek_to_incident frame → plugin → iRacing]
  I --> J[Wait 600 ms]
  J --> K["Read frame# from DOM element frame-cur\n⚠️ Timing-based: if plugin state\narrives after 600 ms, captured frame# is wrong"]
  K --> L[Enrich record from incidents array\nsame data already in leaderboard]
  L --> M[Append to Captured incidents · render]
  M --> N[Update status: Driver N/total…]
  N --> O{More frames?}
  O -- Yes --> H
  O -- No  --> P[Scan ends · status: Done N found]
  B -- Stop clicked --> P
  P --> Q([Captured tab has visited records\n⚠️ Same data as leaderboard + reviewed-at timestamp])
```

</details>

**Gap (issue 3 + 4):** The "capture" here is not a real capture — it's a timing-based DOM read that may record the wrong frame number under WS lag. The resulting Captured list contains the same metadata already in the leaderboard, plus a timestamp. Value is unclear until `capture_incident` atomic action exists (pre-roll + OBS).

**Gap (issue 7):** Button label "Find driver's incidents" implies discovery. The incidents are already found — this walks them. Consider "Walk driver incidents" or adding subtext.

---

## Flow 5 — Walk all session incidents (automated seek)

**Goal:** Step through every incident for all drivers in the session.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Click Find all session incidents]) --> B[Confirm dialog shown\nExplains: seek every incident · rotate car · capture metadata]
  B --> C{User confirms?}
  C -- No  --> D([Cancelled — no-op])
  C -- Yes --> E[Queue all unique frames from leaderboard\nall drivers · max 200]
  E --> F[Same 600 ms seek loop as driver walk]
  F --> G[Each step: rotate Telemetry car dropdown\nto match incident's car]
  G --> H[Same timing + data-quality issues as Flow 4]
  H --> I([Captured tab grows with all visited incidents])
```

</details>

**Same issues as Flow 4.** The confirm dialog correctly sets expectations. Car rotation in the dropdown (`selectCarInDropdownForFrame`) is a nice touch — makes the walk visually track which car you're on.

---

## Flow 6 — Review the Captured incidents tab

**Goal:** Review everything the scan logged, optionally organized by driver.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Switch to Captured incidents tab]) --> B{Any captured records?}
  B -- No --> C([Empty: Run Find driver or session scan first])
  B -- Yes --> D[Records sorted by frame ascending]
  D --> E{Group by driver checkbox?}
  E -- Off --> F[Flat list of captured cards]
  E -- On  --> G[Accordion: one group per driver\ngroups sorted by first incident frame]
  G --> H{Click group header}
  H --> I[Group collapses / expands\nAccordion state persists during session]
  F --> J[Click captured card]
  I --> J
  J --> K[send seek_to_incident frame]
  J --> L[Meta strip expands with captured details\nshows: Scan step # · captured at time · frame · car · driver · sev · cause]
  K --> M([iRacing seeks to frame])
  L --> N{Click same card again?}
  N --> O([Meta strip collapses])
```

</details>

**Gap (issue 4):** Without OBS integration or `capture_incident`, Captured is a "reviewed" list of leaderboard incidents. The main added value is the `captured at` timestamp and the grouped accordion view for dense sessions.

---

## Flow 7 — Navigate with transport controls

**Goal:** Manually scrub, speed, and step through the replay.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  subgraph rc [Replay Controls panel - left col]
    R1[⏮ Jump to start]
    R2[⏭ Jump to end]
    R3[⏪ 4× rewind]
    R4[⏩ 4× forward]
    R5[⏸ / ▶ Play · Pause]
    R6[Speed pills: 0.25× 0.5× 1× 2× 4× 8× 16×]
    R7["Prev replay incident / Next replay incident ← ⚠️ DUPLICATE"]
    R8["Scrub bar click → ⚠️ PoC toast only, not wired"]
  end

  subgraph in [Incident Navigation panel - left col]
    N1["Prev replay incident / Next replay incident ← ⚠️ DUPLICATE"]
    N2[Telemetry car dropdown]
    N3[Find selected driver's incidents]
    N4[Find all incidents for all drivers]
  end

  R1 & R2 --> A[send replay_jump start or end]
  R3 & R4 --> B[send replay_speed -4 or 4]
  R5 --> C[Toggle: speed 0 = pause or restore last speed]
  R6 --> D[send replay_speed N · updates active pill]
  R7 --> E["send replay_seek prev or next"]
  N1 --> E
  R8 --> F([Toast: PoC scrub N%])
  A & B & C & D & E --> G([Plugin → iRacing acts on command])
```

</details>

**Gap (issue 1):** "Prev replay incident" / "Next replay incident" appear in both the Replay Controls panel and the Incident Navigation panel. They call the same action (`replay_seek prev/next`, session-wide replay jump). One set is redundant — consolidate to one location.

**Gap:** Scrub bar click fires a toast (`[PoC] Scrub to N%`) and is not wired to an actual seek action.

---

## PM issues open

| # | Type | Issue |
|---|------|-------|
| 1 | UX debt | Duplicate prev/next replay incident buttons in two panels — consolidate |
| 2 | Missing feature | Telemetry car dropdown must be populated from plugin `drivers` state |
| 3 | Data quality | Capture walk uses 600 ms DOM read — unreliable under WS lag; needs frame-confirmed handshake |
| 4 | Value gap | Captured incidents = leaderboard subset + timestamp; value unclear until `capture_incident` exists |
| 5 | UX gap | Meta strip expands in bottom dock; no feedback near clicked card in left column |
| 6 | Product decision | "This driver's incidents" is NOT redundant with Mine chip — keep it (steward opponent review) |
| 7 | Clarity gap | "Find driver's incidents" label implies discovery; it walks already-known frames — consider renaming |
