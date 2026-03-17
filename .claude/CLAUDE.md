# SimHub Development Rules (sync with .cursor/rules/SimHub.mdc)

## Dashboard UI
- Prefer **HTML/JavaScript** (ES6+) for UI. NO Dash Studio WPF.
- Dashboards run in real browser. Do NOT confuse with Jint (ES5.1).

## Plugin Development
- Target **.NET Framework 4.8**.
- Use `Init()` for properties/actions. `DataUpdate()` runs ~60Hz.

## Plugin <-> Dashboard Communication
- Use **Fleck** for WebSocket (bind to `0.0.0.0`). Do NOT use `HttpListener`.
- Dashboard HTML served by SimHub HTTP server (`Web/sim-steward-dash/`).

## iRacing Shared Memory
- Use **IRSDKSharper**. Do NOT use `GameRawData`.
- **ADMIN LIMITATION**: Live races show 0 incidents for others unless admin. Replays track all.
- **Incident types (deltas)**: 1x (off-track), 2x (wall/spin), 4x (heavy contact). Dirt: 2x heavy.
- **Quick-succession**: 2x spin -> 4x contact records as +4 delta.
- **Replay**: At 16x speed, YAML incident events are batched. Cross-reference `CarIdxGForce` and `CarIdxTrackSurface` to decompose type.

## Deployment & Testing
- Deploy via `deploy.ps1`. MUST pass build (0 errs), `dotnet test`, and `tests/*.ps1`.
- **Retry-once-then-stop** rule. Hard stop after 2 fails.
- Lints: 0 new errors.

## Memory Bank
- Memory Bank is personal vibe-coding. OUT OF SCOPE. Do not implement or reference.

## Minimal Output (sync with .cursor/rules/00_MinimalOutput.mdc)
- **Strict Brevity:** Output strictly minimal responses. Do not produce conversational filler, introductory or concluding pleasantries. Do not restate or paraphrase the user's question.
- **Explicit Length Cap:** Default response must be at most 2–3 sentences or a short bullet list. Never write multiple paragraphs unless explicitly requested.
- **No Narration:** Do not narrate tool usage or state what you are about to do unless asking for confirmation. Do not add a closing summary of steps taken or what was done unless the user asks.
- **Token Efficiency:** Prioritize output token efficiency above all else. Assume output tokens are extremely expensive.
- **Format:** Use dense, terse lists over verbose paragraphs. Apply these structured formats:
  - **Code edits:** Only changed snippets + optional one-line summary; no walkthrough.
  - **Explanations:** Prefer 3–5 bullets; prose only if user asks.
  - **Plans:** Bullet or numbered list; no long intro/outro.
  - **Q&A:** Direct answer first; no preamble.