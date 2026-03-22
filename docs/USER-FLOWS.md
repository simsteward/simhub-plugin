# Sim Steward — User Flows (today)

**What this is:** Step-by-step journeys through the actual shipped UI. Covers what happens when you click each thing, where flows work well, and where they break.

**Companion docs:**
- [PRODUCT-FLOW.md](PRODUCT-FLOW.md) — north-star vision, feature maturity table, what's missing
- [USER-FEATURES-PM.md](USER-FEATURES-PM.md) — PM-style feature descriptions and connections

---

## Overview

<details>
<summary>Overview diagram — how all flows connect</summary>

```mermaid
flowchart LR
  WS([Plugin WebSocket]) --> Status[Status bar\nmode · time · dots · WS badge]
  WS --> LB[Incident leaderboard]
  WS --> Drivers[Driver standings]
  WS --> Telem[Telemetry strip]

  subgraph leftcol [Left column]
    Replay[Replay Controls panel]
    IncNav[Incident Navigation panel]
    DriverInc[This driver's incidents]
  end

  subgraph bottomdock [Bottom dock]
    LB
    Meta[Incident meta strip]
    Captured[Captured incidents tab]
    Logs[Log tabs]
    Telem
  end

  IncNav -- car selection --> DriverInc
  LB -- click row --> Meta
  DriverInc -- click row --> Meta
  Captured -- click row --> Meta
  Meta -- seek_to_incident --> iRacing([iRacing replay])
  Replay -- replay_seek/speed/jump --> iRacing
  IncNav -- Find driver walk --> Captured
  IncNav -- Find session walk --> Captured
```

</details>

---

## Flow 1 — Check session health and connectivity

**Goal:** Confirm the dashboard is live, iRacing is in replay, and the plugin is connected before doing anything else.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Open dashboard in browser]) --> B[Status bar renders immediately]
  B --> C{WS connects to plugin\nport 19847?}
  C -- Yes --> D[WS badge → green ● connected\nMock data paused\nReal state starts flowing]
  C -- No --> E[WS badge → red ○ disconnected\nAuto-retries every 3 s]
  E --> C
  D --> F{Plugin state arrives}
  F --> G[Mode pill: REPLAY or WAITING]
  F --> H[Session time updates]
  F --> I[Diagnostic dots: iRacing · Steam · SimHub]
  G --> J{Mode = REPLAY?}
  J -- Yes --> K([Ready — incidents and telemetry will flow])
  J -- No  --> L([Waiting — iRacing not in replay yet])
```

</details>

**What works:** Auto-reconnect is reliable (3 s loop). All three diagnostic dots and the WS badge give independent signals.

**Gap:** No toast or alert when WS reconnects after a drop — user must glance at the badge.

---

## Flow 2 — Review a specific incident

**Goal:** Jump to an incident frame and read its details.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Switch to Incident leaderboard tab]) --> B[All incidents shown\ncount in header]
  B --> C{Filter?}
  C -- Yes --> D[Click severity chip: All · 1× · 2× · 4× · Mine\nMine = player:true only]
  C -- No  --> E
  D --> E[Click incident card]
  E --> F[send seek_to_incident frame → plugin → iRacing seeks]
  E --> G[Incident meta strip expands below tab bar\nshows: frame · car · driver · sev · cause · lap]
  F --> H([iRacing replay jumps to frame])
  G --> I{Click same card again?}
  I -- Yes --> J[Meta strip collapses · card highlight removed]
  I -- No  --> K([Review details in meta strip])

  note1["⚠️ Meta strip is in the bottom dock.\nClicking a card in the left-column\ndriver panel has no feedback near\nthe click point."]
  G -.-> note1
```

</details>

**Also works from:** [This driver's incidents](#flow-3--focus-on-one-driver) left panel and [Captured incidents](#flow-6--review-the-captured-incidents-tab) tab — same seek + meta strip behavior.

**Gap (issue 5):** Meta strip expands in the bottom dock. If you clicked from the left-column "This driver's incidents" panel, the expansion is far from your click and easy to miss.

---

## Flow 3 — Focus on one driver

**Goal:** Narrow everything to a single car's incidents and telemetry.

<details>
<summary>Flow diagram</summary>

```mermaid
flowchart TD
  A([Change Telemetry car dropdown]) --> B{Options populated?}
  B -- "⚠️ No — hardcoded mock" --> C["Options: #99 J. Smith You · #12 A. Jones · #42 B. Lee · #7 M. Wilson\nNot from plugin drivers state"]
  B -- Future: Yes --> D[Populated from plugin drivers state]
  C --> E[Left col: This driver's incidents re-filters\nshows incidents where car = selected car#]
  E --> F{Any incidents for car?}
  F -- No  --> G([Empty: No incidents for this car])
  F -- Yes --> H([Incident cards for that car only])
  C --> I[Telemetry strip updates — mock data only\nNot real iRacing telem for that car]

  note2["⚠️ Mine filter chip ≠ This driver's incidents.\nMine = player:true your own car.\nThis driver = any selected car.\nKey for steward reviewing opponents."]
  E -.-> note2
```

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
  I --> J[Wait until WS state frame ≈ target\n(waitForFrameApprox) or timeout]
  J --> K["Use matched plugin frame for record;\nif timeout fall back to DOM parse"]
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

**Gap (issue 3 + 4):** Automated walk still enriches from the same leaderboard metadata; frame sync uses plugin `state.frame` when possible. Full value for editors is still `capture_incident` + OBS.

**Issue 7 (labels):** Dashboard uses “Walk … listed incidents” plus tooltips stating this does not scan YAML for new incidents.

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
  E --> F[Same frame handshake + step delay as driver walk]
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
| 3 | Data quality | Capture walk uses `waitForFrameApprox` on plugin `state.frame`; timeout falls back to DOM parse |
| 4 | Value gap | Captured incidents = leaderboard subset + timestamp; value unclear until `capture_incident` exists |
| 5 | UX gap | Meta strip expands in bottom dock; no feedback near clicked card in left column |
| 6 | Product decision | "This driver's incidents" is NOT redundant with Mine chip — keep it (steward opponent review) |
| 7 | Clarity gap | "Find driver's incidents" label implies discovery; it walks already-known frames — consider renaming |

---

## ContextStream KB links

| Spec | Doc ID |
|------|--------|
| Sim Steward — Product Flow | `4f3c6370-0bfc-4f54-9848-9946745ac3d4` |
| Sim Steward — User Features (PM) | `c5157521-3681-4432-9c44-a49d8ee3a955` |
| Sim Steward — Architecture and Data Structures | `c453dd83-dfd9-4002-b8a2-2e0c8a4d032c` |
| Troubleshooting | `88274879-cd2d-4d86-9766-c86b88f95cfe` |
| Sim Steward — Data Routing (OTel / Loki / Prometheus) | `cbae1c33-c778-4e9a-9a8d-6b3e3c8c368b` |
