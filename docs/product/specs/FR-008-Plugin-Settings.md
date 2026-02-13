# Spec: Plugin Settings Tab

**FR-IDs:** FR-008
**Priority:** Must
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-008-Plugin-Settings.md`

---

## Overview

The plugin settings tab is Sim Steward's control panel. It replaces the placeholder `UserControl` from SCAFFOLD (R-SCAF-04) with an interactive WPF settings UI where users configure OBS connection details, hotkey bindings, incident detection preferences, and replay offset.

Settings are a prerequisite for almost every other feature: FR-005 needs OBS URL/password, FR-001 needs the auto-detect toggle, FR-004 needs the replay offset, and FR-002/FR-006 need hotkey actions. Without this, every feature either hardcodes values or can't function. Building settings early means every subsequent feature reads from a single, persisted, user-configurable source of truth.

Aligns with PRD Section 4 (FR-008) and Section 6 (Technical Architecture -- "Overlay / Settings UI" box, WPF settings tab).

---

## Detailed Requirements

### R-SET-01: Settings Model

A single C# class holds all Sim Steward settings with explicit defaults. This class is the canonical contract that all other features consume.

| Field | Type | Default | Validation | Consumed By |
|-------|------|---------|------------|-------------|
| `ObsWebSocketUrl` | `string` | `"ws://localhost:4455"` | Non-empty, must start with `ws://` or `wss://` | FR-005 |
| `ObsWebSocketPassword` | `string` | `""` (empty) | None -- empty means no auth | FR-005 |
| `AutoDetectIncidents` | `bool` | `true` | N/A | FR-001 |
| `ReplayOffsetSeconds` | `int` | `5` | Range: 0–30 inclusive | FR-004 |
| `ToastDurationSeconds` | `int` | `4` | Range: 2–8 inclusive | FR-003b (live toast auto-dismiss) |

**Hotkey fields are NOT stored in this model.** SimHub manages hotkey bindings internally via `AddAction` / controls mapping. The plugin registers named actions (R-SET-05); SimHub persists the user's key assignments. See Technical Design Notes.

**Design note:** The model should be a plain C# class with a parameterless constructor that sets all defaults. This allows `ReadCommonSettings<T>` to return a fully-defaulted instance on first run.

### R-SET-02: OBS WebSocket Section

UI group: **"OBS Connection"**

| Control | Behavior |
|---------|----------|
| URL/port text field | Editable. Pre-filled with `ObsWebSocketUrl` default. Validated on blur: must be non-empty and start with `ws://` or `wss://`. Invalid input shows inline error text (e.g., "Must be a WebSocket URL starting with ws:// or wss://") and is not saved. |
| Password field | Editable. Masked (`PasswordBox`). Pre-filled with `ObsWebSocketPassword`. Empty is valid (OBS supports no-auth mode). |
| Test Connection button | Attempts a WebSocket connect + OBS `Identify` handshake using current URL + password. Displays result inline: **"Connected"** (green) on success, **"Failed: {reason}"** (red) on failure. The button is disabled during the attempt and shows a brief "Connecting..." state. Connection is closed immediately after the test. |

**Test connection error cases:**

| Condition | Displayed Message |
|-----------|-------------------|
| OBS reachable, auth succeeds | "Connected" |
| OBS reachable, wrong password | "Failed: Authentication failed" |
| OBS reachable, no password required but one supplied | "Connected" (OBS ignores extra password) |
| OBS unreachable (refused / timeout) | "Failed: Could not reach OBS at {url}" |
| Invalid URL format | Blocked by field validation -- test button disabled when URL is invalid |

### R-SET-03: Detection Section

UI group: **"Incident Detection"**

| Control | Behavior |
|---------|----------|
| Auto-detect toggle | `CheckBox`. Bound to `AutoDetectIncidents`. Default: checked (on). When unchecked, FR-001 auto-detection is disabled; users rely on manual incident marking (FR-002) only. |
| Toast duration | Numeric input (integer). Bound to `ToastDurationSeconds`. Default: 4. Range: 2–8 seconds. Label: "Live toast display (seconds)". Controls how long the FR-003b incident toast stays visible before auto-dismiss. |

### R-SET-04: Replay Section

UI group: **"Replay"**

| Control | Behavior |
|---------|----------|
| Replay offset input | Numeric input (integer). Bound to `ReplayOffsetSeconds`. Default: 5. Constrained to 0–30. Values outside range are clamped on blur (e.g., typing 50 → saved as 30, typing -3 → saved as 0). Inline label: "seconds before incident". |

**Rationale for clamping vs. rejecting:** Clamping is more forgiving. The user sees the corrected value immediately and can adjust. No error state needed.

### R-SET-05: Hotkey Registration

UI group: **"Hotkeys"**

The plugin registers two named actions via SimHub's `AddAction` API during `Init`:

| Action Name | Purpose | Consumed By |
|-------------|---------|-------------|
| `SimSteward.MarkIncident` | Manual incident mark | FR-002 |
| `SimSteward.ToggleRecording` | Start/stop OBS recording | FR-006 |

SimHub's controls mapping UI handles the actual key binding assignment. The settings tab displays an informational label explaining that hotkeys are configured in SimHub's **Controls** section (e.g., "Configure hotkeys in SimHub → Controls → Sim Steward"). No custom key-capture UI is needed.

### R-SET-06: Persistence

- Settings are persisted using SimHub's `ReadCommonSettings<T>` (load) and `SaveCommonSettings<T>` (save) APIs.
- **Load:** Called during `Init`. If no saved settings exist, `ReadCommonSettings<T>` returns a new instance with defaults (per R-SET-01).
- **Save:** Called immediately on every valid field change (not on a "Save" button). The user never needs to manually save.
- Settings survive SimHub restarts.
- If the settings file is corrupted or missing, the plugin falls back to defaults silently (log a warning).

### R-SET-07: Immediate Effect

All settings changes take effect immediately without restarting SimHub or the plugin. Features that consume settings must read the current value from the live settings model instance, not cache stale copies at startup.

**Implementation note:** The simplest approach is a shared singleton settings instance. Features read from it on each use. No event system needed for Part 1 -- direct property reads are sufficient.

---

## Technical Design Notes

### WPF UserControl

The settings tab is a WPF `UserControl` returned by `GetWPFSettingsControl(PluginManager)`. It replaces the placeholder from R-SCAF-04. The XAML layout uses `GroupBox` or `StackPanel` sections for each settings group (OBS, Detection, Replay, Hotkeys).

**Recommended structure:**

```
plugin/Settings/
├── SimStewardSettings.cs           # Settings model class (R-SET-01)
├── SettingsControl.xaml             # WPF layout (replaces scaffold placeholder)
└── SettingsControl.xaml.cs          # Code-behind: binding, validation, save
```

### SimHub Settings API

```csharp
// Load (in Init)
Settings = this.ReadCommonSettings<SimStewardSettings>(
    "SimStewardSettings",
    () => new SimStewardSettings()   // factory for first-run defaults
);

// Save (on change)
this.SaveCommonSettings("SimStewardSettings", Settings);
```

`ReadCommonSettings` serializes to JSON in SimHub's settings directory. The plugin does not manage file paths.

### SimHub AddAction API

```csharp
// Register hotkey actions (in Init)
this.AddAction("SimSteward.MarkIncident", (a, b) => {
    // FR-002 handler -- will be wired in FR-001-002 story
});

this.AddAction("SimSteward.ToggleRecording", (a, b) => {
    // FR-006 handler -- will be wired in FR-005-006-007 story
});
```

Actions appear in SimHub's Controls mapping as "Sim Steward → MarkIncident" and "Sim Steward → ToggleRecording". The user assigns keys through SimHub's standard controls UI. SimHub persists key assignments independently.

### Validation Approach

- **URL:** Regex or `Uri.TryCreate` on blur. Invalid → show inline error, don't save.
- **Password:** No validation. Empty is valid.
- **Replay offset:** Clamp to [0, 30] on blur. Always valid after clamping.
- **Auto-detect:** Boolean toggle. Always valid.

No modal dialogs or error popups. All feedback is inline next to the relevant control.

### Settings Consumption Pattern

Other features access settings through the plugin's shared instance:

```csharp
// In any feature class that receives the plugin reference:
var url = plugin.Settings.ObsWebSocketUrl;
var offset = plugin.Settings.ReplayOffsetSeconds;
```

This keeps settings access simple and ensures R-SET-07 (immediate effect) is satisfied without an event bus.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **SCAFFOLD-Plugin-Foundation** | Settings tab replaces R-SCAF-04 placeholder. Plugin lifecycle (`Init`, `End`, `GetWPFSettingsControl`) must be in place. |
| **SimHub Settings API** | `ReadCommonSettings<T>`, `SaveCommonSettings<T>` -- provided by `IPlugin` base. |
| **SimHub AddAction API** | `AddAction` -- provided by `IPlugin` base. |
| **WPF (.NET 4.8)** | `PresentationFramework`, `PresentationCore`, `WindowsBase`. Already referenced by SCAFFOLD. |
| **No Dash Studio** | Settings tab is WPF only. Dash Studio is for overlays (FR-003), not settings. |
| **No external NuGet packages** | OBS test connection uses raw WebSocket (`System.Net.WebSockets.ClientWebSocket` in .NET 4.8). Full OBS client library is FR-005's concern -- test connection here is minimal (connect + identify + disconnect). |

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| Settings tab renders in SimHub's plugin settings area | R-SET-02 through R-SET-05 (replaces R-SCAF-04) |
| OBS WebSocket settings: URL/port, password (masked) | R-SET-01, R-SET-02 |
| Hotkey settings: manual mark key, recording key | R-SET-05 |
| Auto-detect toggle (default: on) | R-SET-01, R-SET-03 |
| Replay offset (default: 5, range 0-30) | R-SET-01, R-SET-04 |
| Settings persist across restarts | R-SET-06 |
| Changes take effect immediately | R-SET-07 |
| OBS connection test button | R-SET-02 |

---

## Open Questions

None. Settings APIs (`ReadCommonSettings`, `SaveCommonSettings`, `AddAction`) are well-documented in SimHub's plugin contract. The OBS test connection is a minimal WebSocket handshake -- full OBS integration is deferred to FR-005.
