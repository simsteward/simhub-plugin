# Ralph test-harden — make the gate trustworthy (tests only)

Fresh context. Your job: increase determinism + coverage of the test suites. NO production behavior changes.

1. Run `bash .ralph/gate.sh` a few times. If any test flakes or depends on wall-clock/order/env/path/network, FIX it (fixtures, injected clocks).
2. Find a coverage gap in `SimSteward.IncidentEngine.Tests` (or the test-rig harness logic) and add a deterministic test for real behavior — no vacuous asserts.
3. Gate must be green. Commit `test(...)`/`test(engine): …` to `ralph/auto`; log to `.ralph/progress.md`. Never touch iRacing/SimHub/deploy/main, and never weaken an assertion to make it pass.
