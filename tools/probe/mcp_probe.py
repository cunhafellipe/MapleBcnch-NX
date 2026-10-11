#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Drive the MapleBench MCP server over stdio and print what it actually says.

This exists because "it compiled" is not evidence the tools work. It speaks real
JSON-RPC to the real server binary: initialize, tools/list, then a tools/call,
and prints the raw result.

Usage:
  python mcp_probe.py tools                      # what the server exposes
  python mcp_probe.py call <name> '<json-args>'  # invoke one tool
  python mcp_probe.py batch                      # the split acceptance run
"""
import json
import os
import subprocess
import sys
import threading
import time

DOTNET_ROOT = r'C:\Users\e7896\AppData\Local\dotnet10'
EXE = (r'C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX'
       r'\MapleMcp\bin\Release\net10.0-windows\maple-mcp.exe')


class Mcp:
    def __init__(self, exe=EXE):
        env = dict(os.environ, DOTNET_ROOT=DOTNET_ROOT,
                   PATH=DOTNET_ROOT + os.pathsep + os.environ['PATH'])
        self.p = subprocess.Popen(
            [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, env=env, text=True,
            encoding='utf-8', errors='replace', bufsize=1)
        # PIPE always gives real streams here; the asserts stop a type checker
        # from treating every later use of them as a possible None dereference.
        assert self.p.stdin and self.p.stdout and self.p.stderr
        self.n = 0
        self.lock = threading.Lock()

    def send(self, method, params=None, notify=False):
        with self.lock:
            self.n += 1
            mid = self.n
            msg = {'jsonrpc': '2.0', 'method': method}
            if not notify:
                msg['id'] = mid
            if params is not None:
                msg['params'] = params
        assert self.p.stdin
        self.p.stdin.write(json.dumps(msg) + '\n')
        self.p.stdin.flush()
        if notify:
            return None
        return self._await(mid)

    def _await(self, mid):
        assert self.p.stdout and self.p.stderr
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError('server closed stdout: ' + self.p.stderr.read())
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue          # a stray log line; stdout is the RPC channel
            if msg.get('id') == mid:
                if 'error' in msg:
                    raise RuntimeError(f"{msg['error'].get('code')}: "
                                       f"{msg['error'].get('message')}")
                return msg['result']

    def init(self):
        r = self.send('initialize', {
            'protocolVersion': '2024-11-05',
            'capabilities': {},
            'clientInfo': {'name': 'mcp_probe', 'version': '1'}})
        self.send('notifications/initialized', {}, notify=True)
        return r

    def tools(self):
        return self.send('tools/list')['tools']

    def text(self, res):
        return '\n'.join(
            c.get('text', '') for c in (res.get('content') or [])
            if c.get('type') == 'text')

    def call(self, name, args):
        res = self.send('tools/call', {'name': name, 'arguments': args})
        # The SDK answers a throwing tool with a generic "An error occurred
        # invoking 'x'" and puts the real exception on stderr. An agent that
        # cannot see stderr is left with no way to find out what went wrong, so
        # this surfaces it instead of swallowing it.
        text = self.text(res)
        if res.get('isError') and 'An error occurred invoking' in text:
            real = self.drain_stderr()
            if real:
                text += ('\n--- server stderr ---\n' + real)
        return text

    def drain_stderr(self, wait=1.5):
        """Read whatever the server logged to stderr without blocking forever."""
        assert self.p.stderr
        lines = []
        deadline = time.time() + wait
        os.set_blocking(self.p.stderr.fileno(), False)
        try:
            while time.time() < deadline:
                line = self.p.stderr.readline()
                if not line:
                    time.sleep(0.1)
                    if not lines:
                        continue
                    break
                lines.append(line.rstrip())
                deadline = time.time() + 0.5
        except Exception:
            pass
        return '\n'.join(lines[-40:])

    def close(self):
        try:
            self.p.stdin.close()
        except Exception:
            pass
        self.p.terminate()


def cmd_tools():
    m = Mcp()
    info = m.init() or {}
    print('server  :', info['serverInfo']['name'], info['serverInfo']['version'])
    inst = (info.get('instructions') or '').strip()
    print('instructions:', len(inst), 'chars')
    print()
    ts = m.tools()
    print(f'tools ({len(ts)}):')
    for t in sorted(ts, key=lambda x: x['name']):
        print(f"  {t['name']:26s} {(t.get('description') or '').splitlines()[0][:70]}")
    m.close()


def cmd_call(name, args):
    m = Mcp()
    m.init()
    print(m.call(name, json.loads(args)))
    m.close()


def cmd_batch():
    """Acceptance run: the same path an agent would take, end to end."""
    DATA = r'C:\MUWORK\GAME\gamania Games\MapleStory\Data'
    OUT = r'C:\Users\e7896\AppData\Local\hermes\cache\scratch\mcp-split'

    m = Mcp()
    info = m.init() or {}
    print('server  :', info['serverInfo']['name'], info['serverInfo']['version'])
    names = {t['name'] for t in m.tools()}
    want = {'wz_archive_capabilities', 'wz_archive_scan', 'wz_archive_open',
            'wz_archive_close', 'wz_archive_children', 'wz_archive_node',
            'wz_archive_export_img', 'wz_archive_split'}
    print('all 8 new tools present:', want <= names,
          '' if want <= names else f'missing {want - names}')
    print()

    print('=== 1. wz_archive_capabilities ===')
    print(m.text(m.call('wz_archive_capabilities', {}))[:400])
    print()

    print('=== 2. wz_archive_split (dry run, Npc + 2 packs + a _Canvas + a pointer) ===')
    archives = [
        os.path.join(DATA, 'Npc', 'Npc_000.wz'),
        os.path.join(DATA, 'Packs', 'Mob_00000.ms'),
        os.path.join(DATA, 'Mob', '_Canvas', '_Canvas_000.wz'),
        os.path.join(DATA, 'Mob.wz'),
    ]
    print(m.text(m.call('wz_archive_split', {
        'archives': archives, 'outDir': OUT, 'dryRun': True})))
    print()

    print('=== 3. wz_archive_split (real, capped at 40 files) ===')
    print(m.text(m.call('wz_archive_split', {
        'archives': archives, 'outDir': OUT, 'limitFiles': 40})))
    print()

    print('=== 4. wz_archive_open + children(kind=Image) + node ===')
    opened = m.text(m.call('wz_archive_open',
                           {'path': os.path.join(DATA, 'Npc', 'Npc_000.wz')}))
    print('open:', opened.replace('\n', ' ')[:220])
    fid = json.loads(opened)['id']
    kids = m.text(m.call('wz_archive_children',
                         {'path': fid, 'kind': 'Image'}))
    arr = json.loads(kids)
    print(f'children(Image): {len(arr):,}   first: {arr[0]["name"]}  {arr[0]["path"]}')
    node = m.text(m.call('wz_archive_node', {'path': arr[0]['path']}))
    print('node:', node.replace('\n', ' ')[:220])
    print()

    print('=== 5. wz_archive_export_img (single node) ===')
    one = os.path.join(OUT, 'single.img')
    print(m.text(m.call('wz_archive_export_img',
                        {'path': arr[0]['path'], 'outPath': one})))
    print()

    print('=== 6. wz_archive_close ===')
    print(m.text(m.call('wz_archive_close', {'fileId': fid})))
    m.close()

    print()
    print('=== files actually on disk ===')
    total = 0
    for dp, _dn, fs in os.walk(OUT):
        imgs = [f for f in fs if f.endswith('.img')]
        if imgs:
            b = sum(os.path.getsize(os.path.join(dp, f)) for f in imgs)
            total += len(imgs)
            print(f"  {os.path.relpath(dp, OUT):28s} {len(imgs):4d} files  {b:9,d} B")
    print(f'  TOTAL {total} .img')
    ref = os.path.join(OUT, 'single.img')
    if os.path.exists(ref):
        d = open(ref, 'rb').read()
        print(f'  single.img: {len(d):,d} bytes, first 8 = '
              f'{d[:8].hex(" ")}')


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    cmd = sys.argv[1]
    if cmd == 'tools':
        cmd_tools()
    elif cmd == 'call':
        # Bash mangles backslashes in inline JSON, and every path here is a
        # Windows one, so args come from a file.
        cmd_call(sys.argv[2], open(sys.argv[3], encoding='utf-8').read())
    elif cmd == 'batch':
        cmd_batch()
    else:
        print('unknown command', cmd)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())