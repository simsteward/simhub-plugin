# SimSteward redeploy concept (canonical)

**Purpose:** This is the canonical definition of *redeploy* for the SimSteward plugin project. It is intended to be **sourced from ContextStream** (or this doc after project index), not by reading `deploy.ps1` or the deploy skill file. When an agent or user asks "how do I redeploy?" or "redeploy", the answer should come from here / from ContextStream memory.

---

## Definition

**Redeploy** means running the project’s deploy pipeline to: build the plugin and dashboard, run all tests, copy artifacts into the SimHub installation, optionally restart SimHub, and run post-deploy checks.

---

## Commands

| Mode   | Command |
|--------|--------|
| Manual | From plugin repo root: `pwsh -File .\deploy.ps1` |
| Watch  | `pwsh -File .\scripts\watch-deploy.ps1` — watches `src/` and reruns deploy after debounced changes. Use `SIMHUB_SKIP_LAUNCH=1` so SimHub is not restarted each time. |

---

## Gate (required for success)

No deployment is considered successful unless:

1. **Build:** `dotnet build` succeeds with 0 errors (and 0 warnings-as-errors).
2. **Unit tests:** `dotnet test` passes 100%.
3. **Post-deploy:** All `tests/*.ps1` scripts exit 0 and print only `PASS:` lines (any `FAIL:` or non-zero exit = failure).

**Retry-once-then-stop:** On a failing phase, the agent gets one fix attempt and rerun; on second failure, hard stop (do not keep iterating).

---

## Targets

| What       | Where (under SimHub root)        |
|------------|-----------------------------------|
| Plugin DLLs| SimHub root (e.g. `C:\Program Files (x86)\SimHub` or `$env:SIMHUB_PATH`). Files: SimSteward.Plugin.dll, Fleck.dll, Newtonsoft.Json.dll, IRSDKSharper.dll, YamlDotNet.dll. |
| Dashboard  | `Web\sim-steward-dash\` (index.html, README.txt). |

---

## Behaviour (what the script does)

1. Locate SimHub (SIMHUB_PATH, registry, running process, or default path).
2. Build the plugin in Release; run `dotnet test`.
3. Copy plugin DLLs to SimHub root; copy dashboard files to `SimHub\Web\sim-steward-dash\`.
4. If SimHub is running: close it, verify copy, relaunch (unless `SIMHUB_SKIP_LAUNCH=1`).
5. Run post-deploy tests (e.g. `tests/WebSocketConnectTest.ps1`).

---

## Environment knobs

| Variable                 | Effect |
|--------------------------|--------|
| `SIMHUB_PATH`            | Override SimHub installation location. |
| `SIMHUB_SKIP_LAUNCH=1`   | Do not relaunch SimHub after copy (e.g. for watch mode). |
| `SIMSTEWARD_SKIP_TESTS=1`| Skip the test phase (escape hatch only; avoid in normal workflow). |

---

## Sourcing this definition

- **ContextStream:** Add this document to ContextStream memory (e.g. via create_doc or the ContextStream UI) so agents source redeploy from there instead of reading repo files.
- **Project index:** If ContextStream indexes this repo, `docs/REDEPLOY-CONCEPT.md` will be searchable; agents can be instructed to use "redeploy concept" or "REDEPLOY-CONCEPT" as the source.
