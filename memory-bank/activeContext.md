# Active Context

**Current Task**: Dashboard does not capture user incidents; no visible logging or signals that incident tracking is working.

**Reported (user)**:
- The dashboard does not capture any of my incidents.
- I do not see any logging or signals that things are working as expected.

**Implications**:
- Either incidents are not being detected (iRacing SDK connection, `PlayerCarMyIncidentCount` / session YAML not updating), or the UI is not showing them.
- Plugin does log to `plugin.log` and streams a log tail to the dashboard via WebSocket (`getLogTailForNewClient`); if the user sees no logs, the dashboard may not be displaying the log stream, or the plugin may not be connected to iRacing.
- Next steps: (1) Confirm dashboard shows plugin log stream and connection status. (2) Add or surface clear signals when iRacing connects, when session YAML is read, and when an incident is captured (plugin already logs "Incident captured: +Nx ..." in code). (3) Verify incident pipeline end-to-end (IRSDKSharper connected → IncidentTracker receiving data → events broadcast to dashboard and memory-bank).

**Last Updated**: 2026-02-28 (documented from user report).
