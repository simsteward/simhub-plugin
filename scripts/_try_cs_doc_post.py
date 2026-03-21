"""Probe ContextStream REST for doc create (local dev only; uses .cursor/mcp.json)."""
from __future__ import annotations

import json
import ssl
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CFG = json.loads((ROOT / ".cursor" / "mcp.json").read_text(encoding="utf-8"))
ENV = CFG["mcpServers"]["contextstream"]["env"]
API_KEY = ENV["CONTEXTSTREAM_API_KEY"]
BASE = ENV["CONTEXTSTREAM_API_URL"].rstrip("/")
PROJECT = ENV["CONTEXTSTREAM_PROJECT_ID"]
WORKSPACE = ENV["CONTEXTSTREAM_WORKSPACE_ID"]

payload = json.loads((ROOT / "_grafana_create.json").read_text(encoding="utf-8"))
action = payload.pop("action", None)
if action != "create_doc":
    raise SystemExit(f"expected create_doc, got {action!r}")

paths = [
    f"{BASE}/api/v1/memory/docs",
    f"{BASE}/api/v1/docs",
    f"{BASE}/v1/memory/docs",
]


def try_post(url: str, headers: dict[str, str]) -> tuple[int, str]:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="POST", headers=headers)
    ctx = ssl.create_default_context()
    try:
        with urllib.request.urlopen(req, context=ctx, timeout=60) as resp:
            return resp.status, resp.read()[:500].decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read()[:800].decode("utf-8", "replace")


header_sets = [
    {"Content-Type": "application/json", "Authorization": f"Bearer {API_KEY}"},
    {"Content-Type": "application/json", "X-API-Key": API_KEY},
    {"Content-Type": "application/json", "Authorization": f"ApiKey {API_KEY}"},
]

for url in paths:
    for h in header_sets:
        code, body = try_post(url, h)
        print(url, list(h.keys())[1], code, body[:200].replace("\n", " "))
    print("---")
