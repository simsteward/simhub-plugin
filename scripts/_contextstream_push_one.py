"""One-off: push a single memory payload JSON (action + fields) via MCP stdio."""
from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

try:
    from mcp import ClientSession
    from mcp.client.stdio import StdioServerParameters, stdio_client
except ImportError as e:
    print("Install mcp: python -m pip install mcp", file=sys.stderr)
    raise SystemExit(1) from e

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get("LOCALAPPDATA", "")) / "ContextStream" / "contextstream-mcp.exe"


async def run(payload_path: Path) -> None:
    args = json.loads(payload_path.read_text(encoding="utf-8"))
    server_params = StdioServerParameters(
        command=str(EXE),
        args=[],
        env={**os.environ},
        cwd=str(ROOT),
    )
    async with stdio_client(server_params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            result = await session.call_tool("memory", args)
            print(result)


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python scripts/_contextstream_push_one.py <payload.json>", file=sys.stderr)
        return 2
    p = Path(sys.argv[1])
    if not p.is_file():
        print(f"Missing {p}", file=sys.stderr)
        return 1
    if not EXE.is_file():
        print(f"Missing MCP exe: {EXE}", file=sys.stderr)
        return 1
    asyncio.run(run(p))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
