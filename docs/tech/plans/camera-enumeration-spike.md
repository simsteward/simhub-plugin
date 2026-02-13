# Tech Plan: iRacing Camera Enumeration Spike

**Related FR-IDs:** FR-010, FR-011  
**Related Story:** `docs/product/stories/FR-010-011-Camera-Control.md`  
**Risk Level:** Medium (PRD Constraint #3)  
**Last updated:** 2026-02-13

---

## Question to Answer

Can we reliably discover iRacing camera groups and IDs at runtime so the plugin can present a selection UI (FR-010) and switch cameras via `CamSwitchNum` (FR-011)?

---

## Session Info YAML

- **Source:** iRacing SDK exposes session data. The exact location and format need validation during the spike.
- **Candidates:** `irsdk_getSessionInfo()` returns a string (YAML or similar). Alternative: session info written to a file in iRacing's directory when a session is active. SDK docs and iRSDKSharp wrapper should be checked for the access pattern.
- **Spike deliverable:** Document the exact API or file path, the raw structure of the returned data, and where camera-related nodes live in the tree.

---

## Camera Group Model (Target)

For FR-010/011 we need:

| Field | Purpose |
|-------|---------|
| **Id** (or equivalent) | Value passed to `CamSwitchNum` (camera group identifier) |
| **Name** | Display name for the settings UI (e.g., "Far Chase", "Helicopter") |
| **Car index** | For `CamSwitchNum`: car number + camera group + camera number. Player's car index comes from telemetry. |

**CamSwitchNum:** `irsdk_broadcastMsg` with message type `CamSwitchNum` takes (car number, camera group, camera number). For the player's car we use the player's car index. The spike must confirm parameter order and types from SDK docs or wrapper.

---

## Spike Test Plan

1. **Locate session info:** With iRacing in a session (practice or replay), call or read the session info source. Capture a sample (e.g., save to file) and document the path/API.
2. **Parse camera data:** Identify which YAML keys (or equivalent) list camera groups. Extract at least (id, name) or equivalent. Document the structure.
3. **Map to CamSwitchNum:** For one known camera (e.g., "Chase"), determine the group/id to pass. Send `CamSwitchNum` via the same broadcast mechanism used for replay jump (separate iRacingSDK instance). Verify the in-game camera changes.
4. **Session lifecycle:** Confirm when session info becomes available (session load, first frame, etc.) and whether it changes when switching tracks or sessions. Document refresh strategy for the plugin (e.g., on SessionNum change).

---

## Success Criteria

- **GREEN:** We can programmatically get a list of camera groups with stable IDs and names, and switch to any of them via `CamSwitchNum` during replay. Documented structure and code path for FR-010-011 implementation.
- **YELLOW:** Camera list is available but incomplete or track-specific; or switching works only for a subset. Document limitations and a fallback (e.g., fixed list of common groups).
- **RED:** No reliable enumeration or no working `CamSwitchNum`; Part 2 camera selection would require a different approach (e.g., user-entered group IDs, or defer multi-camera).

---

## Fallbacks

- **No YAML camera list:** Maintain a curated list of common camera group names/IDs from community/SDK docs; user picks from that list. Less ideal but unblocks FR-010.
- **CamSwitchNum unclear:** Research alternative broadcast messages or document that camera switching is not supported and Part 2 ships without multi-camera or with manual camera change only.

---

## Key Risks

| Risk | Mitigation |
|------|------------|
| Camera availability varies by track | Refresh camera list on session/track change; handle "selected camera not in list" in FR-010 (fallback to first available or prompt user). |
| Session info timing | Poll or subscribe; ensure we don't show the camera UI until at least one successful read. |
| YAML structure differs by iRacing version | Document minimum iRacing version if we depend on a specific structure. |

---

## Spike Output Template

When the spike is complete, add to this file or to `sdk-investigation.md`:

- **Session info source:** (API name / file path)
- **Camera structure:** (sample YAML fragment or schema)
- **CamSwitchNum signature:** (parameter order, types, example call)
- **Verdict:** GREEN / YELLOW / RED
- **Recommendation:** Proceed with FR-010/011 as specified / proceed with fallback / defer
