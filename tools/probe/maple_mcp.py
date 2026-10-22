#!/usr/bin/env python3
"""
Talks to MapleMcp over stdio JSON-RPC — the same protocol a real MCP client
speaks, so nothing here is privileged.

Why this file exists rather than another curl loop: MapleMcp is the project's
own agent surface (docs/CLI-AND-MCP.md), it knows paths in the shape the backend
spells them (`f7/Map/Map0/000010000.img`), and it answers questions curl cannot
ask cheaply — `wz_map_cast` returns foothold count, the character track, spawns
and portals in one call.

Two traps this encodes, both of which cost a round trip before:

1. `maple-mcp.exe` cannot run. The system `dotnet` on PATH is SDK 8; the binary
   is net10.0 and exits with "You must install .NET 10". The real runtime is the
   scoop SDK 10 build, so we spawn `dotnet maple-mcp.dll`.
2. The backend is NOT on 5100. `WZ_SERVER` overrides the default, and the
   server that is actually up is on 5480.

Usage:
    from maple_mcp import MapleMcp
    with MapleMcp() as m:
        print(m.call("wz_map_doc", {"path": "f7/Map/Map0/000010000.img"}))
"""

import json
import os
import subprocess
import sys
import time

SDK10 = r"C:\MUWORK\app\scoop\apps\dotnet-sdk\10.0.401\dotnet.exe"
ROOT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit"
MCP_DLL = os.path.join(ROOT, "MapleMcp", "bin", "Release", "net10.0-windows", "maple-mcp.dll")
CLIENT = r"C:\MUWORK\GAME\MPSHS"


def find_backend(default=None, candidates=(5480, 5100, 5531, 5510, 5525, 5590)):
    """Return the first base URL whose /api/files answers.

    Probing beats reading the journal: which port is live changes between
    sessions, and a stale number is how you end up reporting "the MCP server is
    broken" when the real problem is that it was pointed at nothing.

    `WZ_SERVER` is the escape hatch for a port not in `candidates` -- it wins,
    but only if it actually answers. A dead override must not win: the C# side
    reads the same variable and would happily talk to nothing, which is exactly
    the silent failure this function exists to prevent.
    """
    import urllib.request

    def alive(url):
        try:
            with urllib.request.urlopen(url + "/api/files", timeout=4) as r:
                return r.status == 200
        except Exception:
            return False

    override = os.environ.get("WZ_SERVER")
    if override and alive(override):
        return override

    for port in candidates:
        url = f"http://127.0.0.1:{port}"
        if alive(url):
            return url

    # Nothing is up. Say so with the ports we tried instead of returning a URL
    # that will 404 -- the caller can then start a backend and retry.
    raise RuntimeError(
        "No MapleBench backend answering on any of: "
        + ", ".join(str(p) for p in candidates)
        + (f" (WZ_SERVER={override} also refused)" if override else "")
        + "\nStart one with:\n"
        + r'  cd "C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\MapleBench\bin\Release\net10.0-windows"'
        + "\n  "
        + r'"C:\MUWORK\app\scoop\apps\dotnet-sdk\10.0.401\dotnet.exe" MapleBenchX.dll --port 5590 --no-browser --no-warmup --allow-multiple'
    )


class MapleMcp:
    def __init__(self, backend=None, timeout=900):
        if not os.path.exists(MCP_DLL):
            raise FileNotFoundError(f"MapleMcp not built: {MCP_DLL}")
        if not os.path.exists(SDK10):
            raise FileNotFoundError(f"SDK10 not found: {SDK10}")
        self.backend = backend or find_backend()
        env = dict(os.environ)
        env["WZ_SERVER"] = self.backend
        self.timeout = timeout
        self._n = 0
        self.p = subprocess.Popen(
            [SDK10, MCP_DLL],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1, env=env,
        )
        self.server = self.send("initialize", {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "hermes-maple-mcp", "version": "1"},
        }).get("result", {}).get("serverInfo", {})
        self._raw({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _raw(self, obj):
        self.p.stdin.write(json.dumps(obj) + "\n")
        self.p.stdin.flush()

    def send(self, method, params=None):
        self._n += 1
        mid = self._n
        self._raw({"jsonrpc": "2.0", "id": mid, "method": method, "params": params or {}})
        t0 = time.time()
        while time.time() - t0 < self.timeout:
            line = self.p.stdout.readline()
            if not line:
                break
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("id") == mid:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg
        raise TimeoutError(f"{method}: no reply in {self.timeout}s")

    def call(self, tool, args=None):
        """Call a tool and return its text. JSON is parsed when it parses."""
        r = self.send("tools/call", {"name": tool, "arguments": args or {}})
        text = "\n".join(c["text"] for c in r.get("result", {}).get("content", [])
                         if c.get("type") == "text")
        try:
            return json.loads(text)
        except ValueError:
            return text

    def schema(self, name):
        for t in self.send("tools/list", {}).get("result", {}).get("tools", []):
            if t["name"] == name:
                return t.get("inputSchema", {})
        return {}

    def tools(self):
        return [t["name"] for t in self.send("tools/list", {}).get("result", {}).get("tools", [])]

    def open_client(self):
        """Open every .nx in the v83 client. Returns the count."""
        n = 0
        for f in sorted(os.listdir(CLIENT)):
            if f.lower().endswith(".nx"):
                self.call("wz_archive_open", {"path": os.path.join(CLIENT, f)})
                n += 1
        return n

    def __enter__(self):
        return self

    def __exit__(self, *a):
        try:
            self.p.kill()
        except Exception:
            pass


if __name__ == "__main__":
    with MapleMcp() as m:
        print(f"backend : {m.backend}")
        print(f"server  : {m.server}")
        print(f"tools   : {len(m.tools())}")
        print(f"client  : {m.open_client()} nx opened")