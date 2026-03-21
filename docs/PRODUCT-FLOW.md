# Sim Steward — Product Flow

**PM summary (feature buckets and how pieces connect):** [USER-FEATURES-PM.md](USER-FEATURES-PM.md)

**User flows (step-by-step journeys through today's UI):** [USER-FLOWS.md](USER-FLOWS.md)

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
| Status bar (mode / time / WS / diag dots) | ✅ shipped | |
| Replay transport (jump / speed / play-pause) | ✅ shipped | Scrub bar seek is PoC / toast only |
| Prev / Next replay incident (`replay_seek`) | ✅ shipped | Session-wide replay jump, not telemetry-car scoped |
| Jump to incident frame | ✅ shipped | `seek_to_incident` action |
| Incident leaderboard + severity filters | ✅ shipped | All / 1× / 2× / 4× / Mine chips |
| Incident meta strip (selected detail) | ✅ shipped | Click-to-expand/collapse; frame · car · driver · sev · cause |
| This driver's incidents (left panel) | ✅ shipped | Filters by selected car — **distinct from Mine filter** (see PM issues) |
| Captured incidents tab + group-by-driver accordion | ✅ shipped | |
| Find driver incidents (scan walk) | ✅ shipped | Walks already-known leaderboard frames; timing-based (600 ms); fragile |
| Find all session incidents (scan walk) | ✅ shipped | Same walk for all drivers; confirm dialog |
| Driver standings (collapsible) | ✅ shipped | |
| Telemetry strip (throttle / brake / steering) | ✅ shipped | Real telemetry from plugin state |
| Telemetry car selection | ⚠️ partial | Dropdown hardcoded mock; incident filter works; telem is mock |
| Duplicate prev/next replay incident buttons | ⚠️ UX debt | Same buttons in Replay Controls AND Incident Navigation panels |
| Scrub bar seek | ⚠️ PoC | Shows toast only; not wired to seek action |
| Find All Incidents (true YAML scan) | ❌ missing | Current walks leaderboard frames, not a plugin-side YAML scan |
| Selected Incident Panel (camera + capture UI) | ❌ missing | Meta strip ≠ full panel; no camera selector or capture action |
| Camera list from plugin (`cameraGroups`) | ❌ missing | |
| `suggestedCamera` field on incident | ❌ missing | Plugin does not emit this |
| `set_camera` plugin action | ❌ missing | No WS action to change iRacing camera |
| `capture_incident` atomic action | ❌ missing | Pre-roll seek + set camera + set 1× speed as one action |
| Car dropdown from live plugin data | ❌ missing | Currently hardcoded mock options |
| Pre-roll buffer on capture | ❌ missing | Seek goes directly to incident frame |
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
| Replace timing-based capture snapshot with frame-confirmed handshake | Wait for plugin `state.frame` to match seeked frame before recording — current 600 ms DOM read is unreliable |
| Populate car dropdown from plugin `drivers` state | Currently hardcoded mock; must match live iRacing grid |
| Keep "This driver's incidents" left-col panel | **Not redundant** — Mine chip = `player:true` (your car only); driver panel = any selected car; required for steward opponent review |

---

## PM flow issues (open)

> Detailed flows with diagrams: [USER-FLOWS.md](USER-FLOWS.md)

| # | Type | Issue |
|---|------|-------|
| 1 | UX debt | Duplicate prev/next replay incident buttons in both panels — same `replay_seek prev/next` (session-wide) twice |
| 2 | Missing feature | Telemetry car dropdown is hardcoded mock; must be populated from plugin `drivers` state |
| 3 | Data quality | Capture walk uses 600 ms DOM timeout to read frame#; unreliable under WS lag — needs frame-confirmed handshake |
| 4 | Value gap | Captured incidents tab is leaderboard subset + `capturedAt` timestamp; value only clear once `capture_incident` atomic action + OBS exists |
| 5 | UX gap | Meta strip expands in bottom dock; clicking a card in the left-column driver panel gives no visible feedback near the click point |
| 6 | Product decision (resolved) | "This driver's incidents" is **not** equivalent to Mine chip — keep it; it is the primary flow for stewards reviewing opponent incidents |
| 7 | Clarity gap | "Find driver's incidents" / "Find all session incidents" walk already-known frames; they do not discover new incidents — consider renaming |
