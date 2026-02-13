# Tech Plan: Dash Studio Overlay Approach

**FR-IDs:** FR-003
**Related Story:** `docs/product/stories/FR-003-In-Game-Overlay.md`
**Status:** Draft
**Created:** 2026-02-13

---

## Question

Can SimHub Dash Studio handle the replay overlay requirements -- a dynamic incident list, button actions that trigger plugin logic, and conditional visibility based on replay mode -- or do we need a WPF overlay fallback?

---

## Dash Studio Capabilities Assessment

### Dynamic Lists

**Finding: No native repeater/list control.** Dash Studio does not have a repeater widget that renders N items from a variable-length collection. There is no `ItemsControl`, `ListView`, or equivalent.

**Workaround -- fixed-slot pattern:** Expose a fixed number of incident "slots" as individual SimHub properties (e.g., `SimSteward.Incident.0.Time`, `SimSteward.Incident.0.Delta`, ... up to `SimSteward.Incident.N.Time`). Each slot maps to a row of Dash Studio elements with visibility bound to whether that slot is populated.

- **Slot count:** 8 slots is practical. iRacing sessions rarely produce more than 5-8 protestable incidents. If more occur, show the most recent 8 and expose a count property (`SimSteward.Incident.Count`) so the overlay can display "8 of 12 incidents shown."
- **Tradeoff:** More manual layout work per slot. Each slot is a group of elements (text, button) duplicated N times with index-specific bindings. Tedious but functional.
- **Verdict:** Workable for a fixed upper bound. Not ideal, but acceptable for the overlay use case.

### Button Actions

**Finding: Supported via `TriggerSimHubInputName`.** Dash Studio buttons can trigger SimHub-registered input actions. The plugin registers actions with `pluginManager.AddAction(...)`, and Dash Studio buttons fire them via `TriggerSimHubInputName`.

- A bug in v7.4.7 broke `TriggerSimHubInputName` when `SimulatedKey` was disabled. Fixed in v7.4.9b3. SimHub 9.x (current) is well past this fix.
- Buttons cannot directly call arbitrary C# methods. They fire named SimHub actions, which the plugin handles. This is the correct pattern.
- **Concern:** For "jump to incident #3," the button must communicate *which* incident was selected. Options:
  - Register separate actions per slot: `SimSteward.JumpToIncident.0`, `SimSteward.JumpToIncident.1`, ... up to slot count. Each button triggers its slot-specific action. The plugin resolves the index to an `IncidentRecord`.
  - Register one action + a "selected index" property: Button first sets `SimSteward.SelectedIncidentIndex` via NCalc/JS, then triggers `SimSteward.JumpToIncident`. **Risky** -- Dash Studio's ability to write back to plugin properties is limited. Separate actions per slot is safer.
- **Verdict:** Works. Separate actions per slot align with the fixed-slot model.

### Conditional Visibility

**Finding: Fully supported.** Every Dash Studio element has a visibility binding that accepts NCalc or JavaScript formulas. Elements can show/hide based on any SimHub property.

- Bind overlay visibility to `[SimSteward.IsReplayMode]` -- show when `true`, hide when `false`.
- Individual slot rows bind visibility to `[SimSteward.Incident.{N}.IsPopulated]`.
- The live toast can bind to `[SimSteward.Toast.IsVisible]` with a timer-driven auto-dismiss.
- **Verdict:** This is a strength of Dash Studio. No concerns.

### Property Binding

**Finding: Straightforward.** Dash Studio binds to any SimHub property via `[PropertyName]` in NCalc or `$prop('PropertyName')` in JavaScript formulas. The plugin exposes properties with `pluginManager.SetPropertyValue(...)` and the overlay reads them.

- All standard types supported: `string`, `int`, `double`, `bool`.
- No direct support for complex objects -- properties must be flattened to primitives. This aligns with the fixed-slot pattern (each field is its own property).
- **Verdict:** No issues. Standard SimHub plugin pattern.

### Overlay Size Constraint

**Finding: 480,000 pixel surface area limit** (e.g., 800x600). Overlays exceeding this are blocked.

- The replay overlay panel does not need to be large. A 400x500 panel (200,000 px) comfortably fits 8 incident rows with buttons. Well within limits.
- The live toast is tiny (300x80 or similar). No concern.
- **Verdict:** Not a constraint for our use case.

---

## Replay Mode Detection

**Approach:** Read `IsReplayPlaying` from iRacing telemetry on each `DataUpdate` tick. Expose as a SimHub property.

```
// In DataUpdate:
bool isReplay = IsReplayPlaying == true;  // from GameRawData.Telemetry
pluginManager.SetPropertyValue("SimSteward.IsReplayMode", isReplay);
```

**Access path:** `DataCorePlugin.GameRawData.Telemetry.IsReplayPlaying` (confirmed in SDK investigation).

**Overlay binding:** The entire replay overlay's visibility binds to `[SimSteward.IsReplayMode]`. When the driver exits replay, the overlay disappears. When they enter replay, it appears.

**Edge case:** `IsReplayPlaying` may be null when iRacing is disconnected. Treat null as `false` (hide overlay).

---

## Overlay Layout Approach

### Surface 1: Replay Overlay

Visible only when `SimSteward.IsReplayMode == true`. This is the primary interaction surface.

**Layout (vertical stack, right-anchored):**

```
┌─────────────────────────────┐
│  SIM STEWARD                │  ← Header
├─────────────────────────────┤
│  ● OBS Connected            │  ← Status bar (OBS + recording state)
├─────────────────────────────┤
│  12:34  4x  Auto   [Jump]  │  ← Incident slot 0
│  15:07  2x  Manual [Jump]  │  ← Incident slot 1
│  18:42  4x  Auto   [Jump]  │  ← Incident slot 2
│  ...                        │  ← Slots 3-7 (visible if populated)
├─────────────────────────────┤
│  Showing 3 of 3 incidents   │  ← Count summary
├─────────────────────────────┤
│  [⏺ Start Recording]       │  ← OBS record toggle
└─────────────────────────────┘
```

**Element breakdown:**

| Element | Binding | Notes |
|---------|---------|-------|
| Header text | Static | "SIM STEWARD" |
| OBS status | `[SimSteward.OBS.StatusText]` | "Connected" / "Disconnected" / "Recording..." |
| OBS status dot color | `[SimSteward.OBS.IsConnected]` | Green dot if connected, red if not |
| Incident time (per slot) | `[SimSteward.Incident.{N}.TimeFormatted]` | e.g., "12:34" (mm:ss from session time) |
| Incident severity (per slot) | `[SimSteward.Incident.{N}.DeltaText]` | e.g., "4x" |
| Incident source (per slot) | `[SimSteward.Incident.{N}.Source]` | "Auto" / "Manual" |
| Jump button (per slot) | Action: `SimSteward.JumpToIncident.{N}` | `TriggerSimHubInputName` |
| Slot visibility (per slot) | `[SimSteward.Incident.{N}.IsPopulated]` | Hide empty slots |
| Count summary | `[SimSteward.Incident.CountText]` | "Showing 3 of 3 incidents" |
| Record button | Action: `SimSteward.ToggleRecording` | Label bound to recording state |
| Record button label | `[SimSteward.OBS.RecordButtonText]` | "Start Recording" / "Stop Recording" |

**Positioning:** Right edge of screen, vertically centered. Must not overlap iRacing's replay control bar (bottom of screen) or the session info bar (top). User can reposition via SimHub's overlay layout editor.

### Surface 2: Live Toast

Visible during live racing when `SimSteward.Toast.IsVisible == true`. Non-interactive.

**Layout (small horizontal banner, top-right):**

```
┌───────────────────────────┐
│  ⚠ 4x captured at 12:34  │
└───────────────────────────┘
```

**Element breakdown:**

| Element | Binding |
|---------|---------|
| Toast text | `[SimSteward.Toast.Text]` |
| Toast visibility | `[SimSteward.Toast.IsVisible]` |

**Auto-dismiss mechanism:** The plugin sets `SimSteward.Toast.IsVisible = true` on incident detection and starts a timer. After N seconds (default 4, configurable in FR-008), it sets `SimSteward.Toast.IsVisible = false`. The timer runs in the plugin, not in Dash Studio -- NCalc/JS timers in Dash Studio are unreliable for this.

**No buttons.** The driver is mid-race. Toast is read-only confirmation that the plugin is working.

---

## Property Binding Patterns

### Properties the Plugin Must Expose

**Replay mode:**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.IsReplayMode` | `bool` | `true` when `IsReplayPlaying` |

**Incident slots (repeat for N = 0..7):**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.Incident.{N}.IsPopulated` | `bool` | Slot has an incident |
| `SimSteward.Incident.{N}.TimeFormatted` | `string` | "12:34" (mm:ss) |
| `SimSteward.Incident.{N}.DeltaText` | `string` | "4x", "2x", "Manual" |
| `SimSteward.Incident.{N}.Source` | `string` | "Auto" / "Manual" |

**Incident summary:**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.Incident.Count` | `int` | Total incidents in session |
| `SimSteward.Incident.CountText` | `string` | "Showing 3 of 5 incidents" |

**OBS status (set by FR-005/006/007 implementation):**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.OBS.IsConnected` | `bool` | WebSocket connection alive |
| `SimSteward.OBS.IsRecording` | `bool` | OBS is currently recording |
| `SimSteward.OBS.StatusText` | `string` | Human-readable status |
| `SimSteward.OBS.RecordButtonText` | `string` | "Start Recording" / "Stop Recording" |

**Toast:**

| Property | Type | Description |
|----------|------|-------------|
| `SimSteward.Toast.IsVisible` | `bool` | Show/hide live toast |
| `SimSteward.Toast.Text` | `string` | e.g., "4x captured at 12:34" |

**Actions (registered via `AddAction`):**

| Action Name | Trigger |
|-------------|---------|
| `SimSteward.JumpToIncident.0` through `.7` | Replay overlay jump buttons |
| `SimSteward.ToggleRecording` | Replay overlay record button |

### Update Strategy

Properties update on each `DataUpdate` tick. The plugin maintains a snapshot of the incident list and refreshes all slot properties from it. This is simple and avoids partial-update flicker.

---

## Fallback: WPF Overlay Window

If Dash Studio proves unworkable (e.g., the fixed-slot pattern is too rigid, button actions are unreliable, or the 480K pixel limit becomes constraining), the alternative is a WPF overlay window managed by the plugin.

### What WPF Gives Us

- **True dynamic list:** `ItemsControl` or `ListView` bound to `ObservableCollection<IncidentRecord>`. No slot limits.
- **Direct method binding:** Button clicks call C# methods directly via `ICommand`. No need for named actions as intermediaries.
- **Rich layout:** Full WPF layout engine (Grid, StackPanel, DataTemplates). No NCalc formulas.
- **No pixel limit:** Window size is unconstrained.

### What WPF Costs

- **Topmost window management:** Must keep the overlay above the game. Requires `Topmost = true`, `WindowStyle = None`, `AllowsTransparency = true`. Can conflict with other overlays and fullscreen games.
- **No SimHub overlay layout integration:** Users can't reposition via SimHub's drag-and-drop overlay layout editor. We'd need our own position/size persistence.
- **Game focus issues:** Clicking a WPF button steals focus from iRacing. Need either click-through with hotkey activation, or `WS_EX_NOACTIVATE` extended window style to prevent focus theft. Both add complexity.
- **Separate rendering pipeline:** Not managed by SimHub's overlay renderer. No benefit from SimHub's overlay performance optimizations.
- **More code to maintain:** WPF XAML + view model + window management vs. a `.simhubdash` file with property bindings.

### Complexity Comparison

| Concern | Dash Studio | WPF Overlay |
|---------|-------------|-------------|
| Dynamic list | Fixed-slot workaround (medium effort) | Native `ItemsControl` (low effort) |
| Button actions | `TriggerSimHubInputName` + per-slot actions (medium) | Direct `ICommand` binding (low) |
| Conditional visibility | NCalc binding (low effort) | `BooleanToVisibilityConverter` (low effort) |
| Overlay positioning | SimHub layout editor (zero effort) | Custom window positioning (medium effort) |
| Game focus handling | Handled by SimHub (zero effort) | `WS_EX_NOACTIVATE` or click-through (high effort) |
| Distribution | `.simhubdash` file in plugin package | Compiled into plugin DLL |
| User customization | Users can tweak in Dash Studio editor | No user customization |
| Total effort | **Medium** | **Medium-High** |

---

## Recommendation

### Replay Overlay: Dash Studio (with fixed-slot pattern)

**Go with Dash Studio.** The fixed-slot pattern is a tolerable workaround for the lack of a repeater. 8 incident slots covers the realistic range. Button actions via `TriggerSimHubInputName` are proven (fixed since SimHub 7.4.9). Conditional visibility is a Dash Studio strength.

Key advantages over WPF:
- SimHub manages overlay rendering, positioning, and focus -- the hardest parts of an overlay
- Users can reposition the overlay via SimHub's layout editor
- Distributes as a `.simhubdash` file alongside the plugin DLL
- No focus-stealing issues

The main downside (manual slot layout) is a one-time design cost, not ongoing complexity.

### Live Toast: Dash Studio (same overlay file, separate screen or region)

The toast is trivially simple -- one text element with a visibility binding. No reason to use WPF. Package it as a separate screen within the same `.simhubdash` file, or as a standalone overlay. Either works; separate overlay is simpler because the replay overlay and toast have different visibility conditions.

### When to Reconsider WPF

Revisit if any of these prove true during implementation:
1. `TriggerSimHubInputName` is unreliable for per-slot actions (test early)
2. 8 slots is insufficient (unlikely based on iRacing incident frequency)
3. The overlay layout becomes too complex to maintain in Dash Studio (subjective -- assess after building)

---

## Open Questions

| # | Question | Impact | Resolution Path |
|---|----------|--------|-----------------|
| 1 | Can a single `.simhubdash` file contain both the replay overlay and the live toast as separate screens with independent visibility? Or do they need separate overlay files? | Packaging | Test in Dash Studio during implementation |
| 2 | Does `TriggerSimHubInputName` reliably fire when the game (iRacing) has focus and the overlay is shown? | Core functionality | Test early -- if this fails, WPF fallback is needed |
| 3 | Can the plugin update 30+ properties per `DataUpdate` tick (8 slots x 4 fields + status properties) without visible lag? | Performance | Likely fine -- SimHub properties are lightweight. Profile if sluggish. |
