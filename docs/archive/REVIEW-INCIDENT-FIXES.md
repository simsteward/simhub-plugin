# Review: Incident Detection Fixes (2025-02)

## Summary of Changes Made

1. **Plugin logging** — Log "Incident captured: +Nx #X DriverName" when incidents are detected
2. **Dashboard feedback** — Flash toast "✓ Captured: +Nx #X DriverName" when incidentEvents arrive
3. **Troubleshooting doc** — Added Section 6 for replay incident detection diagnostics

---

## Gaps and Issues Found

### 1. **ResultsPositions session mixing (BUG)**

**Location:** `IncidentTracker.RefreshFromYaml` (lines 194–244)

**Problem:** The code iterates over *all* sessions and processes every `ResultsPositions`:

```csharp
foreach (var session in sessions)
{
    var positions = session.ResultsPositions;
    // ... processes ALL positions from practice, qualify, race
}
```

Each session (practice, qualify, race) has its own `ResultsPositions` with *per-session* incident counts. Mixing them causes:

- Incorrect baseline (e.g. practice incidents used for race deltas)
- Wrong or double-counted deltas when switching between sessions
- In replay, we should use only `ReplaySessionNum`’s session

**Fix:** Filter to the session matching the current replay (or live) session before processing:

```csharp
var sessionNum = GetInt(irsdk, "ReplaySessionNum");
if (sessionNum < 0) sessionNum = GetInt(irsdk, "SessionNum");
var session = sessions?.FirstOrDefault(s => s.SessionNum == sessionNum);
if (session?.ResultsPositions == null) return;
// Process only session.ResultsPositions
```

---

### 2. **app.ini path clarity**

**Location:** `docs/TROUBLESHOOTING.md`

**Problem:** "Documents\iRacing\app.ini" is ambiguous.

**Fix:** Use `%USERPROFILE%\Documents\iRacing\app.ini` or "(Your Documents folder)\iRacing\app.ini".

---

### 3. **Options > Graphics for shared memory**

**Location:** `docs/TROUBLESHOOTING.md` Section 6

**Problem:** The doc says "In iRacing, open Options > Graphics" for the shared memory setting. iRacing’s UI may differ; the `app.ini` edit is the reliable method.

**Fix:** Emphasize editing `app.ini` as the primary method; UI path is optional if it exists.

---

### 4. **No fallback when ReplaySessionNum mismatch**

**Location:** `IncidentTracker.RefreshFromYaml`

**Problem:** If no session matches `ReplaySessionNum` (e.g. YAML structure differs, session numbering differs), we process nothing. We may want a fallback (e.g. last session with results) to avoid total failure.

**Fix:** Add a fallback such as: if no session matches, use the last session in the list that has `ResultsPositions`.

---

### 5. **IRSDKSharper API compatibility**

**Problem:** `irsdk.Data.SessionInfo`, `session.SessionNum`, `session.ResultsPositions` were assumed to exist. If IRSDKSharper uses different property names or structure, we may get null and silently skip.

**Mitigation:** Null checks are in place; we could add a one-time startup log when SessionInfo or Sessions is null to aid debugging.

---

### 6. **DriverInfo.CurDriverIncidentCount vs ResultsPositions**

**Location:** `IncidentTracker.RefreshFromYaml`

**Problem:** Driver roster uses `d.CurDriverIncidentCount` while deltas use `ResultsPositions[].Incidents`. These can differ in timing or session scope. For display we overwrite with `ResultsPositions` when available (lines 207–208), so final display is consistent. This is acceptable.

---

### 7. **Flash feedback and rapid incidents**

**Location:** `flashIncidentsCaptured`

**Observation:** At 16x replay, many incidents can arrive quickly. Each triggers a flash; the 7s feedback timer resets. The last message stays visible. This is acceptable UX.

---

## Recommended Next Steps

1. **Implement session filtering** in `IncidentTracker.RefreshFromYaml` to use only the session matching `ReplaySessionNum` / `SessionNum`.
2. **Clarify app.ini path** in troubleshooting.
3. **Verify IRSDKSharper** exposes `SessionNum` and `ResultsPositions` with the expected names (e.g. via a quick test or their docs/source).
