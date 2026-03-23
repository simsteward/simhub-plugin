"""
Call ContextStream MCP tool "memory" via stdio (same as Cursor), using env from .cursor/mcp.json.
Usage: python scripts/contextstream_memory_call.py path/to/args.json
args.json: object passed as tools/call params.arguments (e.g. {"action":"list_docs","query":"..."}).
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def _load_mcp_env() -> dict[str, str]:
    cfg_path = _repo_root() / ".cursor" / "mcp.json"
    raw = json.loads(cfg_path.read_text(encoding="utf-8"))
    env = raw["mcpServers"]["contextstream"]["env"]
    return {k: str(v) for k, v in env.items()}


def _read_json_message(line: str) -> dict:
    line = line.strip()
    if not line:
        return {}
    return json.loads(line)


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python scripts/contextstream_memory_call.py <args.json>", file=sys.stderr)
        return 2
    args_path = Path(sys.argv[1]).resolve()
    arguments = json.loads(args_path.read_text(encoding="utf-8"))

    env = os.environ.copy()
    env.update(_load_mcp_env())
    # Allow API key auth for non-interactive stdio (matches many hosted setups).
    env.setdefault("CONTEXTSTREAM_ALLOW_HEADER_AUTH", "true")

    proc = subprocess.Popen(
        ["cmd", "/c", "contextstream-mcp"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        cwd=str(_repo_root()),
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
    )
    assert proc.stdin and proc.stdout

    def send(obj: dict) -> None:
        proc.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    def readline() -> str:
        line = proc.stdout.readline()
        if not line:
            err = proc.stderr.read() if proc.stderr else ""
            raise RuntimeError(f"MCP stdout closed. stderr: {err[:2000]}")
        return line

    send(
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "contextstream_memory_call", "version": "0.1"},
            },
        }
    )
    while True:
        line = readline()
        msg = _read_json_message(line)
        if msg.get("id") == 1:
            break

    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    send(
        {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/call",
            "params": {"name": "memory", "arguments": arguments},
        }
    )
    while True:
        line = readline()
        msg = _read_json_message(line)
        if msg.get("id") == 2:
            print(json.dumps(msg, ensure_ascii=False, indent=2))
            break

    proc.stdin.close()
    proc.terminate()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
