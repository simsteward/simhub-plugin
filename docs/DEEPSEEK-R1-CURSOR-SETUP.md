# DeepSeek-R1 Local Setup and Cursor Integration

This guide covers running [DeepSeek-R1](https://github.com/deepseek-ai/DeepSeek-R1) (or R1-Distill) locally via Ollama, and using it in Cursor. 

**Recommended Setup: Two Models**
Keep **Composer 1.5** (or Claude/GPT) as the main model for agentic coding and file edits. Use **local DeepSeek-R1** purely for **offload** (reasoning, logic extraction, and summarization) via the Ollama MCP `generate` (or `chat`) tool.

---

## 1. Local DeepSeek-R1 runtime

### Recommended: Ollama

- **Install**: Download and install [Ollama](https://ollama.com).
- **Configure Context**: Set `OLLAMA_NUM_CTX=131072` (128k) to ensure the local model can process full conversation history.
  - **Windows**: Press Win + R, type `sysdm.cpl`, Enter. Go to **Advanced** -> **Environment Variables**. Add a new User variable `OLLAMA_NUM_CTX` with value `131072`. Restart Ollama.
  - **Linux/macOS**: Add `export OLLAMA_NUM_CTX=131072` to your shell profile (e.g., `~/.bashrc`, `~/.zshrc`).
- **Start**: Run `ollama serve` (or ensure the background tray app is running).
- **Pull Models**:
  - For **offload**: `ollama pull deepseek-r1:8b` or `deepseek-r1:7b` (fast, low VRAM).
  - For **max reasoning**: `ollama pull deepseek-r1:32b` (requires more VRAM).
- **Verify API**: Check if the daemon is reachable:
  - **PowerShell**: `Invoke-RestMethod http://localhost:11434/api/tags`
  - **Bash/Browser**: `curl http://localhost:11434/api/tags` (Expect JSON listing your pulled models).

### Alternatives

- **LM Studio**: Exposes an OpenAI-compatible API (e.g. `http://localhost:1234/v1`). Load a DeepSeek-R1-Distill variant and set max context to 128k in model settings.
- **vLLM / SGLang**: For multi-GPU or full 671B R1; see [DeepSeek-V3](https://github.com/deepseek-ai/DeepSeek-V3) and [SGLang DeepSeek](https://docs.sglang.ai/basic_usage/deepseek_v3.html). Serve with an OpenAI-compatible API.

### DeepSeek-R1 usage (official)

- Temperature **0.5–0.7** (0.6 recommended) to reduce repetition.
- Prefer **instructions in the user prompt**; DeepSeek recommends avoiding a system prompt when possible (see [Skills and agents](#5-cursor-skills-and-agents-optional) below).
- For reasoning: they recommend prompting the model to start with `<think>\n` for better chain-of-thought.

---

## 2. Using Local R1 for Offload (Recommended)

When offloading sub-tasks to the local model, **no tunnel is required**. Ollama MCP connects directly to `localhost`, while the main model (Composer) stays in the cloud.

### First-Pass Reasoning (auto/composer)

When the Cursor agent is **auto** or **composer** (cloud model), the **first pass** at reasoning is directed to the local LLM. The local model is given **up to 128k tokens** of context so it can reason over full conversation history, ContextStream context, and relevant code.

1. Ensure the Ollama MCP server is configured in `.cursor/mcp.json` with `OLLAMA_NUM_CTX=131072` (see [MCP: Ollama](#6-mcp-ollama--context-stream)).
2. The agent uses `ollama_generate` or `chat` to send a prompt and assembled context to the local Ollama instance.
3. **Context assembly:** The agent assembles up to ~120k tokens from: conversation history, ContextStream (`context`, `search`) when that MCP is enabled, relevant file contents, and the current query. Follow `.cursor/skills/contextstream/SKILL.md` for ContextStream usage.
4. Prefer local offload when the sub-task is self-contained reasoning; use `.cursor/rules/00_CoreDirectives.mdc` for minimal-output rules.

---

## 3. MCP Proxy Protocol (DATA_REQUEST)

The local LLM cannot call MCP tools directly. Instead, it uses a **text-based protocol** where the Cursor agent acts as an orchestrator/proxy.

### Protocol Format

When the local LLM needs external data or MCP tool access, it outputs:

```json
{
  "type": "DATA_REQUEST",
  "tool": "server-name.tool_name",
  "input": { /* tool parameters */ }
}
```

The Cursor agent:
1. Parses the `DATA_REQUEST` JSON from the LLM response
2. Executes the requested MCP tool
3. Returns results to the LLM in the next message
4. Repeats until the LLM provides a final answer (no `DATA_REQUEST`)

### Available Tools

| Server | Tool | Purpose | Key Parameters |
|--------|------|---------|----------------|
| `project-0-plugin-contextstream` | `search` | Codebase search | `query`, `mode`, `limit` |
| `project-0-plugin-contextstream` | `session` | Memory, lessons, plans | `action`, `title`, `content` |
| `user-MCP_DOCKER` | `webpage-to-markdown` | Fetch webpages | `url` |

### System Prompt Template

Include this in the `system` parameter when calling Ollama:

```
You can request external data or MCP tool calls using this protocol:

{"type": "DATA_REQUEST", "tool": "server.tool_name", "input": {...}}

Available tools:
- project-0-plugin-contextstream.search: Codebase search (query, mode, limit)
- project-0-plugin-contextstream.session: Memory/lessons (action, title, content)
- user-MCP_DOCKER.webpage-to-markdown: Fetch webpage (url)

When you have enough information, provide your final answer without DATA_REQUEST.
```

---

## 4. Exposing the local API to Cursor (Tunnel - Optional)

If you want local R1 as the **main Chat/Composer model**, Cursor’s coordination runs in the cloud and **cannot reach localhost**. You must expose your local server via a **public HTTPS** URL.

1. **Choose a tunnel**: [ngrok](https://ngrok.com), [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-apps/) (cloudflared), or [Tailscale Funnel](https://tailscale.com/kb/1243/funnel/).
2. **Point the tunnel at your local API**:
   - Ollama: `http://localhost:11434` → e.g. `ngrok http 11434`.
   - LM Studio: usually `http://localhost:1234` → same idea.
3. **Cursor settings** (Settings → Models):
   - Enable custom / override OpenAI API.
   - **Override OpenAI Base URL**: `https://<your-tunnel-host>/v1` (e.g. `https://xxxx.ngrok-free.app/v1`).
   - **Model name**: Match what your server reports (e.g. `deepseek-r1:8b` for Ollama).
   - **Context length**: **128000** (or the maximum allowed; at least 32k to avoid silent truncation).

---

## 5. Cursor skills and agents (Optional)

Cursor builds the prompt (system + user messages, rules, skills, dynamic context) and sends it to the **model selected in the UI**. When that model is your **local DeepSeek-R1** (via base URL override), it receives the **same** `.cursor/rules` and `.cursor/skills` content as any other model. The local LLM can and will see the “skills / agents” defined in Cursor; no extra integration is needed.

**Caveat:** DeepSeek-R1 officially recommends **no system prompt** (all instructions in the user prompt). Cursor typically sends a system or early message with rules/skills. If the model ignores rules or behaves poorly:

- Add a **project or global rule** that states: “When answering, treat the following as mandatory: [critical rules]” so they appear in user-visible context.
- Or run a small **local proxy** that merges the system message into the first user message before forwarding to Ollama (optional, not covered here).

**Tool use:** For the agent to use MCP tools (Context Stream, Ollama, etc.), the local server must support OpenAI-style `tools` / `tool_choice`. Ollama supports this for many models; for full R1 tool-calling, SGLang’s `--tool-call-parser deepseekv3` is the reference.

---

## 6. MCP: Ollama + Context Stream

### Ollama MCP

The project’s `.cursor/mcp.json` includes an Ollama MCP server entry. This bridge allows Cursor to talk to your local Ollama instance.

1. **Install the bridge**: Run the following command globally:
   ```bash
   npm install -g @muhammadmehdi/ollama-mcp-server
   ```
2. **Configure path**: Ensure the path in `.cursor/mcp.json` matches your system's global `node_modules`. 
   - On Windows, it is typically: `C:\Users\<YourName>\AppData\Roaming\npm\node_modules\@muhammadmehdi\ollama-mcp-server\dist\index.js`.
3. **Environment**: The server's `env` in `mcp.json` should set `OLLAMA_NUM_CTX=131072` (to match the daemon) and `OLLAMA_BASE_URL=http://localhost:11434`.

It provides:

- **list_models** — See installed models (e.g. `deepseek-r1:8b`).
- **generate** (or **chat**) — Send a sub-task to the local model and use the result in the thread (offload reasoning work, save cloud tokens).
- **show_model** — Model details.
- **pull** (if supported) — Download models.

This MCP does **not** change which model Cursor uses for Chat/Composer. That is still set by **Override Base URL** and model name in Cursor Settings. The MCP is for tool-driven model management and explicit “ask the local model” steps.

### Context Stream

Context Stream is already configured in `.cursor/mcp.json`. Use it as usual: `init` → `context(user_message=..., mode="fast")`, search-first, `format="minified"`, `max_tokens=400` where appropriate. This reduces context size for the model (including local DeepSeek-R1) and helps with token savings.

### MCP vs script fallback

- **When to use MCP:** Prefer Ollama MCP when it is in the session and `generate` succeeds. Gives integrated tool use and avoids spawning a script.
- **When to use script fallback:** Use `scripts/ollama-call.ps1` when: (1) Ollama MCP is not in the session, or (2) `generate` has failed after up to 3 retries with exponential backoff. The script returns real token counts and duration from Ollama's HTTP API response.
- **Required config:**
  - **.cursor/mcp.json:** Ollama server entry with `command`, `args`, and `env` containing `OLLAMA_BASE_URL` and `OLLAMA_NUM_CTX` (see Ollama MCP above).
  - **Script:** From repo root, `scripts/ollama-call.ps1` must be runnable (PowerShell); parameters: `-Model`, `-Prompt`, `-Think`, `-Raw`.
- **Troubleshooting:** If MCP `generate` fails with a streaming error (e.g. `streamResponse is not async iterable`), use script fallback and see [Testing and debugging Ollama MCP](#7-testing-and-debugging-ollama-mcp) below.

---

## 7. Verification

### Phase 1: Ollama API
Confirm the Ollama daemon is active and has models:
- **Command**: `curl http://localhost:11434/api/tags`
- **Success**: JSON response listing `deepseek-r1:8b`.

### Phase 2: Cursor MCP
Confirm Cursor can call the local model:
1. Open Cursor Settings -> **MCP**.
2. Ensure `ollama` (or `project-0-plugin-ollama`) is listed and **Running**.
3. Open a new chat and ask the agent to call an Ollama tool:
   - "List your local Ollama models." (triggers `list_models`)
   - "Ask the local model to summarize this: [text]" (triggers `generate` or `chat`)
4. **Success**: The agent reports models or a summary from the local R1.

## 8. Troubleshooting

- **Temperature:** 0.6 (range 0.5–0.7).
- **Instructions:** Prefer user prompt; if the model ignores system content, put critical instructions in a user-facing rule or message (see [Skills and agents](#5-cursor-skills-and-agents-optional)).
- **Reasoning:** Optional prompt: “Start your response with `<think>` and reason step by step.”
- **Official README:** [DeepSeek-R1](https://github.com/deepseek-ai/DeepSeek-R1).

---

## 9. Architecture (summary)

- **Chat/Composer:** User → Cursor (prompt assembly + rules + skills) → tunnel → local DeepSeek-R1. Cursor can use MCP (Context Stream, Ollama tools) when building the turn.
- **Offload:** Agent calls Ollama MCP `generate` (or `chat`) → request goes to Ollama on the same machine; result is used in the thread (saves cloud tokens).

---

## References

- [DeepSeek-R1](https://github.com/deepseek-ai/DeepSeek-R1) — model summary, distill variants, usage recommendations.
- [DeepSeek-V3](https://github.com/deepseek-ai/DeepSeek-V3) — full 671B local run (multi-GPU).
- [SGLang DeepSeek](https://docs.sglang.ai/basic_usage/deepseek_v3.html) — SGLang serving and tool-calling.
- [Ollama](https://ollama.com) — local run and OpenAI-compatible API.
- [MCPHost Setup](./MCPHOST-OLLAMA-SETUP.md) — guide for using local Ollama with MCP tools via a standalone CLI.
- Project: [.cursor/mcp.json](../.cursor/mcp.json) — MCP server config; [.cursor/rules](../.cursor/rules) — rules applied to the selected model.
