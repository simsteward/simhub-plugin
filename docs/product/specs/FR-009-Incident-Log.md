# Spec: Incident Log

**FR-IDs:** FR-009
**Priority:** Should
**Status:** Ready
**Part:** 1
**Source Story:** `docs/product/stories/FR-009-Incident-Log.md`

---

## Overview

The incident log is a desktop-accessible incident history in the plugin's WPF settings tab. It gives users a scrollable list of every detected incident in the current session -- auto and manual -- with timestamps, severity, and a "Jump to Replay" action per row.

This complements the in-game overlay (FR-003) by providing a persistent, browsable view of the full incident history. Useful when reviewing incidents between sessions, when the overlay is dismissed, or when switching into replay mode from the desktop.

Aligns with PRD Section 4 (FR-009): "In-session list of detected incidents with timestamps. User can select any incident to jump to replay."

---

## Detailed Requirements

### R-LOG-01: Scrollable Incident List

Display all incidents from the in-memory incident list (R-INC-06 from FR-001-002 spec) in a scrollable WPF control within the settings tab.

- **Content:** Every `IncidentRecord` in the current session, regardless of source (auto or manual).
- **Empty state:** When no incidents have been detected, display a centered message: "No incidents detected this session." The message is replaced by the list as soon as the first incident arrives.
- **Scroll:** Standard vertical scrollbar when the list exceeds the visible area.

### R-LOG-02: Incident Entry Display

Each row in the list shows the following fields, derived from the `IncidentRecord` model (R-INC-03):

| Column | Source Field | Format | Notes |
|--------|-------------|--------|-------|
| Timestamp | `SessionTime` | `mm:ss` or `h:mm:ss` | See formatting rule below |
| Severity | `Delta` | `{Delta}x` (e.g., "4x") | Manual marks (Delta=0): display "Manual" |
| Source | `Source` | "Auto" or "Manual" | Enum display name |
| ID | `Id` | Hidden or subtle | For internal tracking; not prominent in UI |

**Timestamp formatting:**
- Convert `SessionTime` (raw seconds) to `mm:ss` for sessions under 1 hour.
- Use `h:mm:ss` when `SessionTime` >= 3600 seconds.
- Example: `SessionTime = 754.3` → `12:34`. `SessionTime = 4500.0` → `1:15:00`.

### R-LOG-03: Sort Order

List is sorted **newest-first** (descending by `SessionTime`). The most recent incident always appears at the top.

- When a new incident arrives, it is inserted at the top of the list.
- Merged incidents (R-INC-04) update in-place; the row's severity may change but its position does not (it keeps the original `SessionTime`).

### R-LOG-04: Real-Time Updates

The list updates automatically as new incidents are detected. No manual refresh action required.

- Subscribe to `OnIncidentDetected` (R-INC-05) to receive new and updated records.
- **New record:** Insert at top per R-LOG-03.
- **Merged record (same `Id`):** Update the existing row's severity display. Do not duplicate the row.
- UI updates must marshal to the WPF dispatcher thread. `OnIncidentDetected` fires on the `DataUpdate` thread (R-INC-05); the UI binding layer handles the thread hop.

### R-LOG-05: Jump to Replay Action

Each incident row has a "Jump to Replay" button (or the row is selectable with a single action button).

- **On click:** Call FR-004's `JumpToReplay(sessionNum, sessionTime, offsetSeconds)` using the row's `IncidentRecord.SessionNum` and `IncidentRecord.SessionTime`.
- **Offset:** Read `ReplayOffsetSeconds` from the settings model (R-SET-01 from FR-008 spec).
- Works for any incident in the list, not just the most recent.

### R-LOG-06: Clear on Session Change

The log clears when the session resets, mirroring the incident list lifecycle (R-INC-07):

- **`SessionNum` changes:** Log clears, empty state message shows.
- **iRacing disconnects:** Log clears, empty state message shows.
- **Replay mode enter/exit:** Log does **not** clear. Incidents are session-scoped, not mode-scoped.

No explicit "Clear" button is needed. The log follows the underlying incident list's lifecycle automatically.

---

## Technical Design Notes

### WPF Control

Use a `ListView` or `DataGrid` bound to the incident collection. `ListView` is simpler and sufficient -- incidents are read-only rows with a single action button.

Recommended placement: a new section within (or tab alongside) the existing settings `UserControl` from FR-008. Group under a "Session Incidents" header.

### Data Binding

Bind to the in-memory incident list from FR-001-002 (R-INC-06). Two approaches:

1. **ObservableCollection wrapper:** Maintain an `ObservableCollection<IncidentRecord>` in the settings control. Subscribe to `OnIncidentDetected` and insert/update items. `ObservableCollection` natively raises `INotifyCollectionChanged` events that WPF `ListView` respects.
2. **CollectionViewSource for sorting:** Wrap the `ObservableCollection` in a `CollectionViewSource` with a `SortDescription` on `SessionTime` descending to enforce R-LOG-03 without manual insert ordering.

Option 1 with explicit insert-at-index-0 is simplest. Option 2 is cleaner if sorting requirements grow.

### Timestamp Formatting

A simple value converter or helper method:

```
FormatSessionTime(double seconds):
  totalSeconds = (int)seconds
  if totalSeconds >= 3600:
    return $"{totalSeconds / 3600}:{(totalSeconds % 3600) / 60:D2}:{totalSeconds % 60:D2}"
  else:
    return $"{totalSeconds / 60}:{totalSeconds % 60:D2}"
```

Implement as an `IValueConverter` for XAML binding or a static helper called from the view model.

### Thread Marshaling

`OnIncidentDetected` fires on a background thread. UI collection mutations must dispatch to the WPF thread:

```
Application.Current.Dispatcher.Invoke(() => {
    incidents.Insert(0, record);
});
```

### Jump Button Wiring

The "Jump to Replay" button's click handler reads the selected/bound `IncidentRecord` and calls FR-004's `JumpToReplay`:

```
replayController.JumpToReplay(
    record.SessionNum,
    record.SessionTime,
    plugin.Settings.ReplayOffsetSeconds
);
```

### Recommended File Placement

```
plugin/Settings/
├── SettingsControl.xaml            # Add incident log section to existing control
└── SettingsControl.xaml.cs         # Add OnIncidentDetected subscription, collection management
```

No new files needed if the log is a section within the existing settings control. If the settings tab grows too large, extract to a separate `IncidentLogControl.xaml` user control embedded in the settings tab.

---

## Dependencies & Constraints

| Dependency | Detail |
|------------|--------|
| **FR-001-002 Incident Detection** | Provides `IncidentRecord` model (R-INC-03), in-memory incident list (R-INC-06), `OnIncidentDetected` event (R-INC-05), and session lifecycle (R-INC-07). |
| **FR-004 Replay Control** | Provides `JumpToReplay(sessionNum, sessionTime, offsetSeconds)` method. |
| **FR-008 Plugin Settings** | Provides the WPF settings tab (`SettingsControl`) where the log is hosted, and `ReplayOffsetSeconds` setting. |
| **WPF (.NET 4.8)** | `ListView` / `DataGrid`, `ObservableCollection`, `Dispatcher`. Already referenced by SCAFFOLD and FR-008. |

**Should priority** -- this feature is valuable but not on the critical path for the core clip workflow (detect → overlay → jump → record → save). It can be built after all Must-priority features are in place.

---

## Acceptance Criteria Traceability

| Story AC | Spec Requirement |
|----------|-----------------|
| All detected incidents (auto and manual) appear in a scrollable list | R-LOG-01, R-LOG-02 |
| Each entry shows: timestamp (mm:ss or hh:mm:ss), severity (Nx), source | R-LOG-02 |
| Selecting an incident and clicking "Jump to Replay" triggers replay jump | R-LOG-05 |
| List clears on session change | R-LOG-06 |
| List updates in real-time as new incidents are detected | R-LOG-04 |
| Most recent incident is highlighted or at the top of the list | R-LOG-03 |

---

## Open Questions

- **SimHub property exposure:** The story notes a potential enhancement to expose incident data as SimHub properties so users could build custom Dash Studio dashboards showing the log. Low priority -- not specced here; note as a future enhancement if there's user demand.
