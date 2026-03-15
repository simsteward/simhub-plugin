# MCPHost with Local Ollama LLM

This guide covers setting up **MCPHost** ([mark3labs/mcphost](https://github.com/mark3labs/mcphost)) to enable your local Ollama models to interact with tools via the Model Context Protocol (MCP).

## 1. Prerequisites

- **Go 1.23+**: Required to install and run MCPHost.
- **Ollama**: Installed and running locally (`ollama serve`).
- **Function-Calling Model**: At least one model pulled that supports function calling (e.g., `deepseek-r1:8b`, `qwen2.5:3b`, or `mistral`).
- **Environment**: (Optional) Set `OLLAMA_HOST` if your Ollama instance is not running on the default `http://localhost:11434`.

## 2. Installation

### All platforms

Install MCPHost using the Go toolchain:

```bash
go install github.com/mark3labs/mcphost@latest
```

After installation, ensure that your Go binary directory is in your system's `PATH` (see platform-specific steps below), then verify:

```bash
mcphost --help
```

### Windows

1. **Install Go** (if not already installed):
   - Download the Windows installer from [go.dev/download](https://go.dev/dl/) (choose the MSI for 64-bit).
   - Run the installer and complete the setup. The installer adds Go to your user `PATH` by default.
   - Open a **new** PowerShell or Command Prompt and run: `go version` (must be 1.23 or later).

2. **Install MCPHost** in PowerShell or Command Prompt:

   ```powershell
   go install github.com/mark3labs/mcphost@latest
   ```

3. **Add Go bin to PATH** (if `mcphost` is not found):
   - Go installs binaries to `%USERPROFILE%\go\bin` (e.g. `C:\Users\<YourName>\go\bin`).
   - **Temporary** (current session only):
     ```powershell
     $env:Path += ";$env:USERPROFILE\go\bin"
     ```
   - **Permanent**: Add `%USERPROFILE%\go\bin` to your user PATH:
     - Press Win + R, type `sysdm.cpl`, Enter → **Advanced** → **Environment Variables**.
     - Under "User variables", select **Path** → **Edit** → **New** → enter `%USERPROFILE%\go\bin` → OK.
     - Close and reopen your terminal.

4. **Verify**:

   ```powershell
   mcphost --help
   ```

   You should see MCPHost's usage and flags. If you see "command not found", ensure the PATH step above was applied and that you opened a new terminal after changing PATH.

### Linux / macOS

Ensure `$HOME/go/bin` is on your `PATH` (e.g. add `export PATH="$HOME/go/bin:$PATH"` to `~/.bashrc` or `~/.zshrc`), then run `mcphost --help`.

## 3. Configuration

MCPHost uses a JSON configuration file to define the MCP servers it can connect to.

### Global Configuration
By default, MCPHost looks for a configuration file at `~/.mcp.json` (or `%USERPROFILE%\.mcp.json` on Windows).

Example `~/.mcp.json`:
```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\winth\\dev\\sim-steward\\plugin"]
    }
  }
}
```

### Project-Specific Configuration
You can also use a project-level configuration file by passing the `--config` flag. This is useful for sharing tool definitions within a repository.

Example:
```bash
mcphost --config mcphost.json -m ollama:deepseek-r1:8b
```

## 4. Usage and Verification

### Running MCPHost
Launch an interactive session with an Ollama model:

```bash
mcphost -m ollama:deepseek-r1:8b
```

If you are using a specific configuration file:
```bash
mcphost --config mcphost.json -m ollama:deepseek-r1:8b
```

### Interactive Commands
Inside the interactive session, you can use the following commands:
- `/tools`: List all available tools provided by the configured MCP servers.
- `/servers`: List the status of connected MCP servers.
- `/history`: Show the current conversation history.
- `/quit`: Exit the session.

### Verifying Tool Use
To verify that the setup is working, ask the model to perform a task that requires a tool. For example, if you have the `filesystem` server configured:
> "List the files in the current directory."

The model should recognize the tool call, execute it via MCPHost, and report the results.

## 5. References
- [MCPHost GitHub Repository](https://github.com/mark3labs/mcphost)
- [Ollama Documentation](https://ollama.com)
- [DeepSeek-R1 Cursor Setup](./DEEPSEEK-R1-CURSOR-SETUP.md)
