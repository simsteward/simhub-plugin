# Sim Steward — Gap Closure Plan

**Status: implemented in repo** (plugin + dashboard). Keep for history and verification; feature maturity lives in [`PRODUCT-FLOW.md`](PRODUCT-FLOW.md).

Closes all ⚠️ and ❌ items from `PRODUCT-FLOW.md`.
**Deferred:** true YAML scan, scrub-bar seek, dual-view, OBS.

**Files touched:**
- `src/SimSteward.Plugin/SimStewardPlugin.cs`
- `src/SimSteward.Dashboard/index.html`

---

## Task 1 — Remove duplicate Prev/Next replay incident buttons

**File:** `index.html`

Delete the `<div class="btn-row">` block inside the **Incident navigation** panel that contains the two `replay_seek` prev/next buttons (~lines 538–541). Identical buttons already exist in Replay Controls.

---

## Task 2 — Populate car dropdown from live plugin data

### 2a. Plugin — add `drivers` to state JSON

**File:** `SimStewardPlugin.cs`

Add helper `BuildDriverList()` that reads `_irsdk.Data.SessionInfo.DriverInfo.Drivers` via reflection (same pattern as `ResolveSessionNameFromYaml`). Return `object[]` with items `{ carIdx, carNum, name, isPlayer }`:
- `carIdx` = `CarIdx` property (int)
- `carNum` = `CarNumber` property (string)
- `name` = `UserName` property (string)
- `isPlayer` = `carIdx == SafeGetInt("PlayerCarIdx")`
- Wrap in try/catch; return `Array.Empty<object>()` on any failure.

In `BuildStateJson(PluginSnapshot snapshot)`, add to the anonymous object:
- `drivers = BuildDriverList()`
- `cameraGroups = GetCameraGroupNames()` ← also needed for Task 3

### 2b. Dashboard — replace hardcoded `<select>` options

**File:** `index.html`

1. Remove the 4 hardcoded `<option>` elements from `<select id="car-sel">`.

2. Add `populateCarDropdown(driversArr)`:
   - Rebuild `car-sel` innerHTML from array
   - Each option: `value=carIdx`, `data-car=carNum`, `data-player="1"` if isPlayer, label = `#carNum name (You)` or `#carNum name`
   - After rebuild restore previous selection by `data-car` match; if not found default to isPlayer entry
   - Call `renderDriverIncidents()` at end

3. In `onState(m)`, after `if (m.drivers) { drivers = m.drivers; renderStd(); }`, also call `populateCarDropdown(m.drivers)`.

---

## Task 3 — Plugin: camera groups + camera switch + capture

### 3a. Add `GetCameraGroupNames()`

```csharp
private string[] GetCameraGroupNames()
{
    try
    {
        var groups = _irsdk?.Data?.SessionInfo?.CameraInfo?.Groups as IList;
        if (groups == null) return Array.Empty<string>();
        var names = new System.Collections.Generic.List<string>();
        foreach (var g in groups)
        {
            var n = g?.GetType().GetProperty("GroupName")?.GetValue(g)?.ToString();
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return names.ToArray();
    }
    catch { return Array.Empty<string>(); }
}
```

### 3b. Add `ResolveCameraGroupNum(string groupName)`

```csharp
private int ResolveCameraGroupNum(string groupName)
{
    try
    {
        var groups = _irsdk?.Data?.SessionInfo?.CameraInfo?.Groups as IList;
        if (groups == null) return -1;
        int idx = 0;
        foreach (var g in groups)
        {
            var t = g?.GetType();
            var n = t?.GetProperty("GroupName")?.GetValue(g)?.ToString();
            if (string.Equals(n, groupName, StringComparison.OrdinalIgnoreCase))
            {
                var numProp = t.GetProperty("GroupNum");
                return numProp != null ? Convert.ToInt32(numProp.GetValue(g)) : idx;
            }
            idx++;
        }
    }
    catch { }
    return -1;
}
```

### 3c. Add `set_camera` action branch in `DispatchAction`

Insert before the final `not_supported` return:

```csharp
if (string.Equals(action, "set_camera", StringComparison.OrdinalIgnoreCase))
{
    var groupName = (arg ?? "").Trim();
    if (string.IsNullOrEmpty(groupName))
    { LogActionResult(action, arg, correlationId, false, "bad_arg"); return (false, null, "bad_arg"); }
    if (_irsdk == null || !_irsdk.IsConnected)
    { LogActionResult(action, arg, correlationId, false, "not_connected"); return (false, null, "not_connected"); }
    try
    {
        int g = ResolveCameraGroupNum(groupName);
        if (g < 0) { LogActionResult(action, arg, correlationId, false, "group_not_found"); return (false, null, "group_not_found"); }
        _irsdk.CamSwitchPos(0, g, 0);  // 0 = focused car; adapt if IRSDKSharper uses different API
        LogActionResult(action, arg, correlationId, true, "");
        return (true, "ok", null);
    }
    catch (Exception ex)
    { LogActionResult(action, arg, correlationId, false, ex.Message); return (false, null, ex.Message); }
}
```

> **IRSDKSharper note:** If `CamSwitchPos` doesn't exist, find the equivalent broadcast method in the IRSDKSharper library (e.g. `BroadcastMsg.CamSwitchPos` or `BroadcastMsg.CamSwitchNum`) and adapt.

### 3d. Add `capture_incident` action + arg class

Add constant near other constants at top of class:
```csharp
private const int CapturePreRollFrames = 180; // ~3 s at 60 fps
```

Add private nested class:
```csharp
private class CaptureIncidentArg
{
    [JsonProperty("frame")]  public int    frame  { get; set; } = -1;
    [JsonProperty("camera")] public string camera { get; set; }
}
```

Add action branch in `DispatchAction` (before `not_supported`):
```csharp
if (string.Equals(action, "capture_incident", StringComparison.OrdinalIgnoreCase))
{
    CaptureIncidentArg parsed;
    try { parsed = JsonConvert.DeserializeObject<CaptureIncidentArg>(arg ?? ""); }
    catch { LogActionResult(action, arg, correlationId, false, "bad_arg"); return (false, null, "bad_arg"); }
    if (parsed == null || parsed.frame < 0)
    { LogActionResult(action, arg, correlationId, false, "bad_arg"); return (false, null, "bad_arg"); }
    if (_irsdk == null || !_irsdk.IsConnected)
    { LogActionResult(action, arg, correlationId, false, "not_connected"); return (false, null, "not_connected"); }
    try
    {
        int seekFrame = Math.Max(0, parsed.frame - CapturePreRollFrames);
        _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, seekFrame);
        if (!string.IsNullOrEmpty(parsed.camera))
        {
            int g = ResolveCameraGroupNum(parsed.camera);
            if (g >= 0) _irsdk.CamSwitchPos(0, g, 0);
        }
        _irsdk.ReplaySetPlaySpeed(1, false);
        LogActionResult(action, arg, correlationId, true, "");
        return (true, "ok", null);
    }
    catch (Exception ex)
    { LogActionResult(action, arg, correlationId, false, ex.Message); return (false, null, ex.Message); }
}
```

---

## Task 4 — Dashboard: Selected Incident Panel

### 4a. Add HTML panel

Insert **after** the closing `</div>` of the Replay Controls panel and **before** the Incident navigation panel:

```html
<!-- Selected Incident Panel -->
<div class="panel" id="selected-inc-panel" hidden>
  <div class="panel-title">Selected Incident</div>
  <div id="selected-inc-meta" style="font-size:0.82rem;margin-bottom:10px"></div>
  <div style="margin-bottom:8px">
    <label style="font-size:0.68rem;color:var(--muted);text-transform:uppercase;letter-spacing:.08em;display:block;margin-bottom:4px">Incident View</label>
    <select class="car-sel" id="camera-sel" onchange="onCameraSel()" title="Camera for capture"></select>
    <button type="button" id="use-suggested-btn" hidden onclick="useSuggestedCamera()"
      style="font-size:0.68rem;color:var(--accent);background:none;border:none;cursor:pointer;padding:2px 0;margin-top:3px">
      use suggested view ↗
    </button>
  </div>
  <div class="btn-row" style="margin-bottom:0">
    <button type="button" class="btn label-inc" onclick="selectPrevIncident()">← Prev</button>
    <button type="button" class="btn cap label-inc" id="capture-btn" onclick="captureSelectedIncident()">▶ Capture</button>
    <button type="button" class="btn label-inc" onclick="selectNextIncident()">Next →</button>
  </div>
</div>
```

### 4b. Add JS state variables

In the State section near other `let` declarations:
```js
let selectedIncident = null;
let cameraGroups = [];
```

### 4c. Update `onState(m)`

After the existing `if (m.drivers)` block add:
```js
if (m.drivers) populateCarDropdown(m.drivers);
if (Array.isArray(m.cameraGroups) && m.cameraGroups.length) {
  cameraGroups = m.cameraGroups;
  populateCameraDropdown();
}
```

### 4d. Add new JS functions (after `renderDriverIncidents`)

```js
function populateCarDropdown(driversArr) {
  const sel = document.getElementById('car-sel');
  if (!sel || !Array.isArray(driversArr) || !driversArr.length) return;
  const prevCar = getSelectedCarNumber();
  sel.innerHTML = driversArr.map(d =>
    `<option value="${d.carIdx}" data-car="${d.carNum}"${d.isPlayer?' data-player="1"':''}>` +
    `#${d.carNum} ${d.name}${d.isPlayer?' (You)':''}</option>`
  ).join('');
  let restored = false;
  for (let i = 0; i < sel.options.length; i++) {
    if (+sel.options[i].getAttribute('data-car') === prevCar) { sel.selectedIndex = i; restored = true; break; }
  }
  if (!restored) {
    for (let i = 0; i < sel.options.length; i++) {
      if (sel.options[i].getAttribute('data-player') === '1') { sel.selectedIndex = i; break; }
    }
  }
  renderDriverIncidents();
}

function populateCameraDropdown() {
  const sel = document.getElementById('camera-sel');
  if (!sel) return;
  const prev = sel.value;
  sel.innerHTML = cameraGroups.map(n =>
    `<option value="${escapeHtmlForCaptured(n)}">${escapeHtmlForCaptured(n)}</option>`
  ).join('');
  if (prev && cameraGroups.includes(prev)) sel.value = prev;
}

function showSelectedIncidentPanel(i) {
  const panel = document.getElementById('selected-inc-panel');
  if (!panel) return;
  panel.hidden = false;
  const meta = document.getElementById('selected-inc-meta');
  if (meta) {
    const cause = (i.cause||'').replace(/-/g,' ');
    meta.innerHTML =
      `<strong>#${i.car}</strong> ${i.driver}&nbsp;` +
      `<span class="sev-badge s${i.sev}">${i.sev}×</span>&nbsp;` +
      `<span class="cause-tag ${i.cause}">${cause}</span>&nbsp;` +
      `<span style="color:var(--muted)">${i.time}</span>`;
  }
  populateCameraDropdown();
  const sugBtn = document.getElementById('use-suggested-btn');
  if (sugBtn) sugBtn.hidden = !i.suggestedCamera;
  if (i.suggestedCamera && cameraGroups.includes(i.suggestedCamera)) {
    const sel = document.getElementById('camera-sel');
    if (sel) sel.value = i.suggestedCamera;
  }
}

function hideSelectedIncidentPanel() {
  const panel = document.getElementById('selected-inc-panel');
  if (panel) panel.hidden = true;
  document.querySelectorAll('.inc-card-expanded').forEach(el => el.classList.remove('inc-card-expanded'));
}

function onCameraSel() {
  const sel = document.getElementById('camera-sel');
  const v = sel ? sel.value : '';
  sendDashboardUiEvent({ element_id: 'camera-sel', event_type: 'change', message: 'Camera selected', value: v });
  if (v) send('set_camera', v);
}

function useSuggestedCamera() {
  if (!selectedIncident || !selectedIncident.suggestedCamera) return;
  const sel = document.getElementById('camera-sel');
  if (sel && cameraGroups.includes(selectedIncident.suggestedCamera)) sel.value = selectedIncident.suggestedCamera;
  sendDashboardUiEvent({ element_id: 'use-suggested-btn', event_type: 'click', message: 'Use suggested camera', value: selectedIncident.suggestedCamera });
}

function captureSelectedIncident() {
  if (!selectedIncident) return;
  const sel = document.getElementById('camera-sel');
  const camera = sel ? sel.value : '';
  const arg = JSON.stringify({ frame: selectedIncident.frame, camera });
  sendDashboardUiEvent({ element_id: 'capture-btn', event_type: 'click', message: 'Capture incident', value: String(selectedIncident.frame) });
  if (!send('capture_incident', arg)) toast('[PoC] capture_incident not connected');
  log('events', 'INFO ', 'capture_incident', 'frame:' + selectedIncident.frame + '  camera:' + camera);
}

function selectPrevIncident() {
  if (!selectedIncident) return;
  const filtered = getFilteredIncidents();
  const idx = filtered.findIndex(i => i.frame === selectedIncident.frame);
  sendDashboardUiEvent({ element_id: 'selected-inc-prev', event_type: 'click', message: 'Selected incident prev' });
  if (idx > 0) onSessionIncidentClick(filtered[idx - 1].frame);
}

function selectNextIncident() {
  if (!selectedIncident) return;
  const filtered = getFilteredIncidents();
  const idx = filtered.findIndex(i => i.frame === selectedIncident.frame);
  sendDashboardUiEvent({ element_id: 'selected-inc-next', event_type: 'click', message: 'Selected incident next' });
  if (idx >= 0 && idx < filtered.length - 1) onSessionIncidentClick(filtered[idx + 1].frame);
}
```

### 4e. Replace `onSessionIncidentClick` body

```js
function onSessionIncidentClick(frame) {
  const i = incidents.find(x => x.frame === frame);
  if (!i) return;
  if (selectedIncident && selectedIncident.frame === frame) {
    selectedIncident = null;
    hideSelectedIncidentPanel();
    collapseIncidentMetaStrip();
    sendDashboardUiEvent({ element_id: 'incident-meta-strip', event_type: 'ui_interaction', message: 'Session incident deselected', value: String(frame) });
    return;
  }
  selectedIncident = i;
  seekInc(frame);
  showSelectedIncidentPanel(i);
  expandIncidentMetaStrip(formatIncidentMetaHtml(i));
  incidentMetaSelection = { kind: 'session', frame };
  highlightIncidentCards(frame, null);
  sendDashboardUiEvent({ element_id: 'incident-meta-strip', event_type: 'ui_interaction', message: 'Session incident selected', value: String(frame) });
}
```

### 4f. Reset on WS connect

In `ws.onopen`, after clearing `incidents = []` and `drivers = []`, add:
```js
selectedIncident = null;
hideSelectedIncidentPanel();
cameraGroups = [];
```

---

## Action Coverage Checklist (CLAUDE.md)

- [x] `capture-btn` → `dashboard_ui_event` click in `captureSelectedIncident()`
- [x] `selected-inc-prev` / `selected-inc-next` → `dashboard_ui_event` click
- [x] `use-suggested-btn` → `dashboard_ui_event` click
- [x] `camera-sel` → `dashboard_ui_event` change in `onCameraSel()`
- [x] `set_camera` DispatchAction branch → `action_dispatched` + `action_result`
- [x] `capture_incident` DispatchAction branch → `action_dispatched` + `action_result`

---

## Verification

```
dotnet build   # 0 errors
dotnet test    # pass
./tests/*.ps1  # pass
```

Manual in iRacing replay:
1. Car dropdown shows live driver list, defaults to player car
2. No duplicate Prev/Next buttons in Incident navigation panel
3. Camera dropdown shows iRacing camera group names
4. Click incident → Selected Incident Panel shows with correct driver/sev/cause/time
5. Press ▶ Capture → plugin logs `capture_incident` dispatched+result; replay seeks to `frame − 180` at 1× with selected camera
6. ← Prev / Next → cycles through filtered incident list
