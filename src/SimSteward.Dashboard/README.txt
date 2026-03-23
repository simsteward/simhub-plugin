Sim Steward Dashboard (sim-steward-dash)
=========================================

This folder is deployed to SimHub's Web folder as "sim-steward-dash".

To use it in SimHub:
1. Open SimHub -> Dash Studio.
2. Create a new dashboard or open an existing one.
3. Add a "Web Page" (or "Web View") component.
4. Set the URL to: http://localhost:8888/Web/sim-steward-dash/index.html
   (Replay incident index page: .../replay-incident-index.html)
5. Resize the component to fit your layout. The page connects to the Sim Steward plugin on port 19847.
6. If you configured `SIMSTEWARD_WS_TOKEN`, append `?token=<value>` (or `?wsToken=<value>`) to the URL so the dashboard forwards the token when opening the WebSocket.

Requires the Sim Steward plugin to be loaded (SimHub must have the plugin DLLs installed and the plugin enabled).

If the status stays red: check that "Sim Steward" appears in SimHub's left menu and that port 19847 is not blocked.
Full steps: see the plugin docs/TROUBLESHOOTING.md in the source repo.
