# Sim Steward — Product Flow

**PM summary (feature buckets and how pieces connect):** [USER-FEATURES-PM.md](USER-FEATURES-PM.md)

## Problem

Reviewing a race in iRacing replay means manually scrubbing through footage to find incidents, then adjusting camera angles, then recording each clip. For a typical race this takes 20–40 minutes of tedious setup per session.

Sim Steward eliminates that. It turns a replay session into a structured review queue: find all incidents instantly, jump to each one in sequence, frame the right camera, and capture — ready for editing or review.

---

## User Flow Diagram

```mermaid
flowchart TD

  %% ── Entry ─────────────────────────────────────────────────
  A([User opens iRacing\nand loads a replay]) --> B[SimHub plugin detects\nReplay mode]
  B --> C[User opens Sim Steward\ndashboard in browser]
  C --> D{Incidents already\nscanned?}

  D -- No --> E[User clicks\nFind All Incidents]
  D -- Yes --> L

  %% ── Scan ──────────────────────────────────────────────────
  E --> F[Plugin scrubs replay YAML\nfor CurDriverIncidentCount deltas\nacross all cars all frames]
  F --> G[Authoritative incident list sent\nto dashboard via WS incidents message\nfields: driver · type · time · frame · suggestedCamera]
  G --> L

  %% ── Review queue ──────────────────────────────────────────
  L([Incident List\nshown in leaderboard]) --> M{Any incidents?}
  M -- No --> N([No incidents found\nSession clean])
  M -- Yes --> O[User clicks incident row\nin list]

  %% ── Selected Incident Panel ───────────────────────────────
  O --> P[Selected Incident Panel activates\n───────────────────────\nDriver · car · type · session time\nIncident View 1 dropdown\n↳ use suggested view link\nPrev · Capture · Next]

  P --> Q{Camera OK?}
  Q -- No  --> R[User picks camera\nfrom View 1 dropdown]
  Q -- Yes --> S
  R --> S

  %% ── Capture sequence ──────────────────────────────────────
  S[User clicks Capture] --> T[Plugin: seek to\nstart_frame − pre-roll buffer]
  T --> U[Plugin: set iRacing camera\nto selected Incident View 1]
  U --> V[Plugin: set playback speed → 1×]
  V --> W[OBS records the clip\nmanual trigger today]
  W --> X{More incidents?}

  X -- Yes --> Y[User clicks Next →\nor selects from list]
  Y --> O
  X -- No  --> Z([Session review complete])

  %% ── Dual-view future path ─────────────────────────────────
  P -.-> DV[Future: 2-view mode toggle]
  DV -.-> DV2[View 1 + View 2 selectors\nboth with suggested view links]
  DV2 -.-> DV3[Capture: play View 1\nthen auto-switch to View 2]
  DV3 -.-> W

  %% ── OBS future path ───────────────────────────────────────
  W -.-> OBS[Future: OBS integration\nauto start · stop · name clip]

  %% ── Styles ────────────────────────────────────────────────
  classDef future stroke-dasharray:5 5,color:#888
  class DV,DV2,DV3,OBS future
```

---

## Feature Maturity

| Feature | Status | Notes |
|---|---|---|
| Replay mode detection | ✅ shipped | SimMode YAML field |
| Find All Incidents (scan) | ⚠️ partial | Dashboard polls next-incident hotkey every 600ms — not a true YAML scan; fragile |
| Prev / Next incident navigation | ✅ shipped | Frame seek via replay controls |
| Jump to incident frame | ✅ shipped | `seek_to_incident` action |
| **Selected Incident Panel** | ❌ missing | Clicking a row seeks but shows no dedicated panel |
| Incident View 1 selector | ❌ missing | No camera dropdown exists anywhere |
| `suggestedCamera` field on incident | ❌ missing | Plugin does not emit this; dashboard has no field to display it |
| Available camera list from plugin | ❌ missing | Needed to populate the dropdown |
| "Use suggested view" link | ❌ missing | Depends on above two |
| `set_camera` plugin action | ❌ missing | No WS action exists to change iRacing camera |
| `capture_incident` plugin action | ❌ missing | Pre-roll seek + set camera + set 1× speed as one atomic action |
| Pre-roll buffer on capture | ❌ missing | Currently seek goes directly to incident frame |
| 1× playback enforced on capture | ❌ missing | Speed not set automatically |
| Dual-view capture (View 1 + View 2) | 🗓 future | Two selectors, auto-switch mid-clip |
| OBS integration | 🗓 future | Auto record/stop/name per incident |

---

## Incident Card — Target State

The incident list rows are read-only. Clicking a row activates the **Selected Incident Panel** — a persistent area above or beside the list that shows full context and controls.

```
INCIDENT LIST (scrollable rows)
┌────────────────────────────────────────┐
│  #99  J. Smith    contact   4×  0:43  │  ← clicked → activates panel below
│  #12  A. Jones    wall      2×  0:41  │
│  #42  B. Lee      off-track 1×  0:38  │
└────────────────────────────────────────┘

SELECTED INCIDENT PANEL
┌──────────────────────────────────────────────────────┐
│  #99  J. Smith              4×  contact   0:43:12    │
│  ──────────────────────────────────────────────────  │
│  Incident View 1:  [ Chase Camera            ▼ ]     │
│                    use suggested view ↗               │
│                                                      │
│  ·  ·  ·  (future — 2-view mode)  ·  ·  ·           │
│  Incident View 2:  [ TV Camera 2             ▼ ]     │
│                    use suggested view ↗               │
│                                                      │
│       [ ← Prev ]      [ ▶ Capture ]      [ Next → ] │
└──────────────────────────────────────────────────────┘
```

**Capture** triggers one atomic action on the plugin:
1. Seek to `start_frame − PRE_ROLL_FRAMES`
2. Set camera to selected Incident View 1
3. Set playback speed to 1×

User then watches it play and OBS records. They press **Next →** when done.

---

## What Needs to Change

### Plugin (C#)

| Change | Why |
|---|---|
| Add `suggestedCamera` to incident log fields | Dashboard needs it to pre-fill View 1 selector and show "use suggested" link |
| New WS message: `cameraGroups` | Sends available camera group names so dashboard can populate dropdowns |
| New action: `set_camera` + arg = camera name | Changes iRacing camera group |
| New action: `capture_incident` + arg = frame | Atomic: seek to pre-roll, set camera, set 1× speed |
| Replace find-all polling with true YAML scan | Current 600ms-step loop is fragile; plugin should scan all frames and return full list |

### Dashboard (JS)

| Change | Why |
|---|---|
| `selectedIncident` state | Track which incident is active |
| Selected Incident Panel HTML | Dedicated area showing camera selector and capture controls |
| Camera dropdown populated from `cameraGroups` message | Replaces hardcoded options |
| "Use suggested view" link | Resets dropdown to `incident.suggestedCamera` |
| `capture_incident` call replaces seek-only click | Single action does pre-roll + camera + speed |
| Remove `capturedIncidents` parallel list | Confusing; rename or unify with main incident list using a `captured: true` flag |
| Remove "This driver's incidents" left-col panel | Redundant — Mine filter chip on main list covers this |
