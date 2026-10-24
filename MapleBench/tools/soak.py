#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
soak.py — MapleBench 自主驗證引擎

跑一場長時間的模擬，把**不變量**（而不是截圖、而不是「有回應」）拿來對，
任何一條被打破就產出一筆可重現的缺陷。

為什麼是這樣：這個專案反覆出現的失敗型態是「跑起來了，但畫面是錯的」。
一個只問「端點回 200 嗎」的檢查會對一個左右瘋狂翻轉的怪物回綠燈。
所以每一條規則都必須是「如果這件事是真的，那這個數字就必須是某個形狀」。

用法
    GATE_BASE=<base> python tools/soak.py --seconds 120 --start-server
    GATE_BASE=<base> python tools/soak.py --seconds 60 --json out.json --start-server
    python tools/soak.py --map f6/Map/Map0/000000002.img

實例來自 GATE_BASE，開機的埠也來自 GATE_BASE，開機本身要 --start-server。
埠上已經有東西在 listen 就明確失敗（印出埠、pid，並說明拒絕沿用）——
量別人開的實例不叫量測（tools/verify/AGENTS.md rule 17）。
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# The instance comes from GATE_BASE and from nowhere else (task-139).  This file
# used to carry a default of 5590, AND to launch its own server on 5590 --
# two literals that could drift apart, and an adopt-what-is-there branch that
# silently used whatever else was already listening on that port.  Now there is
# one variable, and the port it names is the port this file launches on.
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                os.pardir, os.pardir, "tools", "verify", "suites"))
try:
    from archivelib import require_gate_base
except ImportError:
    print(">>> RED: archivelib.py is missing at tools/verify/suites/archivelib.py")
    raise SystemExit(1)

BASE = None
PORT = None


def refuse_no_port():
    """No port means no instance to measure -- refuse, do not guess.

    The port has two jobs here: proving that the port is free (measuring
    somebody else's instance is not a measurement, tools/verify/AGENTS.md
    rule 17), and being the port --start-server launches on.  A GATE_BASE
    that names no port can do neither, and an http:// URL with no port is
    port 80 -- that is not a default, it is a guess (archivelib refuses a
    default port for the same reason: an older instance answers every
    request).

    Why this is a refusal and not a crash: require_base() stored
    urlparse(...).port in PORT, which is None when the URL names no port,
    and port_owner()'s "%d" raised on None -- a traceback says nothing about
    why there is nothing to measure, and reads as a broken tool.
    """
    print(">>> RED: GATE_BASE names no port: %r, so there is no instance to measure."
          % (BASE,), flush=True)
    print("       The port is what proves that port is free (measuring somebody",
          flush=True)
    print("       else's instance is not a measurement) and what --start-server",
          flush=True)
    print("       launches on; without it this tool can do neither.  An http://",
          flush=True)
    print("       URL with no port is port 80 -- that is not a default, it is a",
          flush=True)
    print("       guess.  Point GATE_BASE at an instance WITH a port:",
          flush=True)
    print("         $env:GATE_BASE = 'http://127.0.0.1:<port>'", flush=True)
    raise SystemExit(2)


def require_base():
    """取得實例。刻意**不在** module level 呼叫 —— 那會擋掉 --help，讓「查說明」
    變成一件得先設好環境變數才能做的事（task-145 修的就是這個回退）。
    在 argparse 解析完 argv 之後才呼叫，所以 --help 走不到這裡。
    冪等：已經解析過就直接回傳，所以任何入口（main、或被匯入後直接呼叫）
    都不會拿到 None 當埠號。"""
    global BASE, PORT
    if BASE is not None:
        return BASE
    BASE = require_gate_base()
    PORT = urllib.parse.urlparse(BASE).port
    if PORT is None:
        # No port means there is no "that port": refuse here, so nothing
        # downstream can ever get None as a port number.
        refuse_no_port()
    return BASE
NXRUN = r"C:\MUWORK\app\scoop\apps\dotnet-sdk\10.0.401\dotnet.exe"
BIN = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\MapleBench\bin\Release\net10.0-windows"
MPSHS = r"C:\MUWORK\GAME\MPSHS"

# 連續「沒有輸入」的幀數超過這個數，x 就不該再變。
DRIFT_TOLERANCE = 3      # px
# 沿同一條 foothold 鏈走時，連續轉向的最小間隔（幀）。小於這個就是原地抖動。
MIN_TURN_INTERVAL = 90
# 攻擊框最遠也就 ~110px；離怪超過這個距離揮擊本來就不可能命中。
REACH_PX = 110


# ---------------------------------------------------------------- HTTP

def _req(path, body=None, timeout=120):
    url = require_base() + path
    data = json.dumps(body).encode() if body is not None else None
    hdrs = {"Content-Type": "application/json"} if data else {}
    r = urllib.request.Request(url, data=data, headers=hdrs)
    return json.load(urllib.request.urlopen(r, timeout=timeout))


def get(path, **q):
    if q:
        path += "?" + urllib.parse.urlencode(q)
    return _req(path)


def post(path, body, timeout=300):
    return _req(path, body, timeout)


def alive():
    try:
        get("/api/files", timeout=5)
        return True
    except Exception:
        return False


def port_owner(port):
    """Who is listening on this port.  If the answer is unknown, return an
    empty list, not 0 -- 0 is not a pid.

    A port with no value is refused: the "%d" below raises on None, and a
    traceback says nothing about why there is nothing to measure.
    """
    if port is None:
        refuse_no_port()
    ps = ("$c=Get-NetTCPConnection -State Listen -LocalPort %d "
          "-ErrorAction SilentlyContinue; if($c){$c.OwningProcess}" % port)
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                             capture_output=True, timeout=30)
        pids = sorted({int(t) for t in
                       out.stdout.decode("ascii", "replace").split()
                       if t.isdigit()})
        if pids:
            return pids
    except Exception:
        pass
    try:
        out = subprocess.run(["netstat", "-ano"], capture_output=True, timeout=30)
        for line in out.stdout.decode("ascii", "replace").splitlines():
            f = line.split()
            if (len(f) >= 5 and f[0] == "TCP" and f[1].endswith(":%d" % port)
                    and f[3] == "LISTENING"):
                return sorted({int(f[4])})
    except Exception:
        pass
    return []


def ensure_server(open_nx=True, start=False):
    """只在 --start-server 且埠空閒時才自己開；回傳 True 表示是我開的。

    埠上已經有東西就**明確失敗**，不沿用。量測器指向別人開的實例，就不是量測
    （tools/verify/AGENTS.md rule 17 的家族）。這支檔以前是
    `if alive(): return False` —— 埠已被占用就靜默沿用，而且埠號是檔案自己挑的。
    現在開機的埠只能來自 GATE_BASE，開機只能來自 --start-server。
    """
    require_base()          # 冪等；被匯入後直接呼叫時也拿得到埠號
    owners = port_owner(PORT)
    if owners:
        print(">>> RED: %s 的埠 %d 已經有東西在 listen（pid %s）。"
              % (BASE, PORT, ", ".join(str(p) for p in owners)), flush=True)
        print("       那不是我開的伺服器，所以拒絕沿用 —— 量別人的實例不叫量測。",
              flush=True)
        print("       要跑請先停掉那個 pid，或把 GATE_BASE 指到一個空閒的埠。",
              flush=True)
        raise SystemExit(2)
    if not start:
        print(">>> RED: %s 沒有在 listen，而這支檔沒有 --start-server，所以不開。"
              % BASE, flush=True)
        print("       明確的路徑："
              "  GATE_BASE=<base> python tools/soak.py --start-server", flush=True)
        raise SystemExit(2)
    subprocess.Popen(
        [NXRUN, "MapleBenchX.dll", "--port", str(PORT), "--no-browser", "--no-warmup",
         "--allow-multiple"],
        cwd=BIN, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
    )
    for _ in range(60):
        time.sleep(1)
        if alive():
            break
    else:
        raise SystemExit("伺服器 60 秒內沒起來")

    if open_nx:
        listing = get("/api/files/listing", path=MPSHS)
        post("/api/files/open-many",
             {"paths": [f["path"] for f in listing]}, timeout=900)
    return True


def file_id(name):
    for f in get("/api/files"):
        if f["name"] == name:
            return f["id"]
    raise SystemExit(f"{name} 沒開")


# ---------------------------------------------------------------- 規則

class Finding:
    __slots__ = ("rule", "severity", "detail", "sample")

    def __init__(self, rule, severity, detail, sample=None):
        self.rule, self.severity, self.detail, self.sample = rule, severity, detail, sample

    def as_dict(self):
        return {"rule": self.rule, "severity": self.severity,
                "detail": self.detail, "sample": self.sample}

    def __str__(self):
        s = f"[{self.severity:8s}] {self.rule}: {self.detail}"
        if self.sample:
            s += f"\n           e.g. {self.sample}"
        return s


def check_no_drift(trace):
    """不按任何鍵，角色不該移動。"""
    out = []
    if len(trace) < 60:
        return out
    xs = [t["x"] for t in trace]
    moved = max(xs) - min(xs)
    if moved > DRIFT_TOLERANCE:
        out.append(Finding(
            "idle-drift", "error",
            f"沒按任何鍵，x 卻變動了 {moved:.1f}px（門檻 {DRIFT_TOLERANCE}）",
            f"x {xs[0]:.1f} → {max(xs):.1f}，action={trace[-1]['action']}"))
    return out


def check_in_bounds(trace, bounds):
    """角色不能離開地圖矩形（含 20px margin）。"""
    out = []
    if not bounds:
        return out
    lo, hi = bounds["left"] + 20, bounds["right"] - 20
    bad = [t for t in trace if t["x"] < lo - 0.01 or t["x"] > hi + 0.01]
    if bad:
        out.append(Finding(
            "out-of-bounds", "error",
            f"{len(bad)}/{len(trace)} 幀的 x 在合法區間 [{lo}, {hi}] 之外",
            f"x={bad[0]['x']:.1f}"))
    bot = bounds.get("bottom")
    if bot is not None:
        under = [t for t in trace if t["y"] > bot + 60 + 0.01]
        if under:
            out.append(Finding(
                "out-of-bounds-y", "error",
                f"{len(under)} 幀的 y 超過地板 {bot + 60}",
                f"y={under[0]['y']:.1f}"))
    return out


def check_mob_flip(per_mob):
    """一隻怪不該每幾幀就換一次面向。"""
    out = []
    for idx, (sid, h) in enumerate(sorted(per_mob.items())):
        fac = [x["facing"] for x in h]
        if len(fac) < 40:
            continue
        flips = [(i, fac[i - 1], fac[i])
                 for i in range(1, len(fac)) if fac[i] != fac[i - 1]]
        # 只看「同一段行走期間」的翻轉；站著不動時翻轉是正常的
        moves = [x for x in h if x["direction"] != 0]
        if len(moves) < 40:
            continue
        runs = sum(1 for i in range(1, len(moves))
                   if moves[i]["facing"] != moves[i - 1]["facing"])
        span = max(x["x"] for x in moves) - min(x["x"] for x in moves)
        if runs > len(moves) / 10 and span < runs * 2:
            out.append(Finding(
                "mob-flip-thrash", "error",
                f"怪 #{idx} 在 {len(moves)} 個移動幀裡轉向 {runs} 次，"
                f"但淨行程只有 {span:.0f}px —— 原地抖動",
                "".join("+" if v > 0 else ("-" if v < 0 else "0")
                        for v in [x["facing"] for x in moves][:60])))
    return out


def check_mob_leaves_ground(per_mob):
    """站著的怪不該突然離地 —— 那是掉落而不是巡邏。"""
    out = []
    for idx, (sid, h) in enumerate(sorted(per_mob.items())):
        air = [t for t in h if not t["onGround"]]
        if len(air) > 10 and len(h) > 0:
            out.append(Finding(
                "mob-airborne", "warn",
                f"怪 #{idx} 有 {len(air)}/{len(h)} 幀不在地面上"))
    return out


def check_dead_mob_sent(per_mob):
    """死掉的怪不該再出現在畫面清單裡（SpriteEngine.cs:282-287 是 Free()）。"""
    out = []
    for idx, (sid, h) in enumerate(sorted(per_mob.items())):
        dead = [t for t in h if t.get("dead")]
        if dead:
            out.append(Finding(
                "dead-mob-drawn", "error",
                f"怪 #{idx} 有 {len(dead)} 幀帶 dead=true 卻還在清單裡",
                f"stance={dead[0]['stance']}"))
    return out


def check_combat_lands(trace, per_mob):
    """揮擊時附近有怪，卻從沒造成傷害 —— Swing() 有東西永遠不成立。

    前提很重要：角色必須真的靠近過怪。地圖預設出生點離最近的怪 200px 以上，
    在那裡揮擊本來就該零傷害。第一版規則沒有設這個前提，於是在「攻擊已修好」
    之後仍然報錯 —— 那不是程式壞了，是規則壞了。一條會對正確程式亮紅燈的規則
    比沒有規則更糟，因為它會讓人再去改已經對的程式碼。
    """
    out = []
    swings = in_reach = 0
    for i, t in enumerate(trace):
        if not t.get("_attacking"):
            continue
        swings += 1
        ms = per_mob.get(i, [])
        if ms and min(abs(m["x"] - t["x"]) for m in ms) <= REACH_PX:
            in_reach += 1
    if in_reach >= 30:
        hits = sum(len(t.get("damage") or []) for t in trace)
        if hits == 0:
            out.append(Finding(
                "swing-never-lands", "error",
                f"在怪 {REACH_PX}px 內揮擊 {in_reach} 幀，傷害事件 0 筆 "
                f"—— Swing() 的命中測試從未成立",
                "damage[] 恆空"))
    return out


# ---------------------------------------------------------------- 主迴圈

INPUTS = [
    {},                                    # 什麼都沒按（測漂移）
    {"left": True}, {"right": True},
    {"up": True}, {"down": True},
    {"attack": True},
    {"left": True, "jump": True},
    {"right": True, "attack": True},
]


def run(args):
    started_server = ensure_server(open_nx=not args.no_open,
                                   start=args.start_server)
    mid = file_id(args.map_file)
    rng = random.Random(20261014)

    findings: list[Finding] = []
    rounds = 0
    sessions = 0

    deadline = time.time() + args.seconds
    while time.time() < deadline:
        rounds += 1
        m = rng.choice(args.maps) if len(args.maps) > 1 else args.maps[0]
        path = m if "/" in m.split("/")[0] and m.split("/")[0].startswith("f") \
            else f"{mid}/{m}"
        try:
            s = post("/api/simulate/session",
                     {"path": path, "weapon": rng.choice(
                         ["1302000.img", "01302000.img", "1122000.img"])})
        except Exception as e:
            findings.append(Finding("session-start", "error",
                                    f"{path} 開不起來：{e}"))
            continue
        if s.get("error"):
            findings.append(Finding("session-start", "warn",
                                    f"{path}: {s['error']}"))
            continue

        # Start beside a mob, not at the map's default spawn. The default spawn on
        # Map0/000000002 sits 200+px from the nearest mob behind a platform gap, so
        # a walk from there never tests combat at all -- it only tests that a
        # character cannot cross a gap, which the bounds rule already covers.
        if args.near_mob:
            ms = [m for m in s.get("mobs", []) if not m.get("dead")]
            if ms:
                mob = rng.choice(ms)
                side = rng.choice((-1, 1))
                try:
                    s = post("/api/simulate/session",
                             {"path": path, "weapon": rng.choice(args.weapons),
                              "startX": mob["x"] + side * rng.choice((25, 35, 50)),
                              "startY": mob["y"],
                              "direction": -side})
                    if s.get("error"):
                        s = post("/api/simulate/session",
                                 {"path": path,
                                  "weapon": rng.choice(args.weapons)})
                except Exception:
                    pass
        sessions += 1
        sid = s["sessionId"]
        bounds = s.get("bounds")

        n = len(s.get("mobs", []))
        trace, per_mob = [], {i: [] for i in range(n)}
        idle_trace = []

        for step in range(args.frames_per_session):
            if step < args.idle_frames:
                inp = {}
            else:
                inp = rng.choice(INPUTS[1:])
            try:
                f = post(f"/api/simulate/session/{sid}/frame", inp, timeout=120)
            except Exception as e:
                findings.append(Finding("frame-step", "error",
                                        f"{path} 第 {step} 幀失敗：{e}"))
                break
            if not f.get("sessionId"):
                break
            rec = {"x": f["x"], "y": f["y"], "action": f["action"],
                   "onGround": f["onGround"], "_attacking": bool(inp.get("attack"))}
            trace.append(rec)
            if step < args.idle_frames:
                idle_trace.append(rec)
            ms = f.get("mobs", [])
            for i in range(min(len(ms), n)):
                per_mob[i].append(ms[i])

        for r in check_no_drift(idle_trace):
            r.detail = f"{path} " + r.detail
            findings.append(r)
        for r in check_in_bounds(trace, bounds):
            r.detail = f"{path} " + r.detail
            findings.append(r)
        for r in check_mob_flip(per_mob):
            r.detail = f"{path} " + r.detail
            findings.append(r)
        for r in check_dead_mob_sent(per_mob):
            r.detail = f"{path} " + r.detail
            findings.append(r)
        for r in check_mob_leaves_ground(per_mob):
            r.detail = f"{path} " + r.detail
            findings.append(r)
        for r in check_combat_lands(trace, per_mob):
            r.detail = f"{path} " + r.detail
            findings.append(r)

        try:
            urllib.request.urlopen(urllib.request.Request(
                f"{BASE}/api/simulate/session/{sid}", method="DELETE"), timeout=30)
        except Exception:
            pass

        if args.verbose:
            print(f"  round {rounds:3d} {path}  mobs={n} frames={len(trace)} "
                  f"findings={len(findings)}", flush=True)

    # 依規則去重：同一條規則同一張地圖只留第一次
    seen, uniq = set(), []
    for f in findings:
        k = (f.rule, f.detail.split("怪 #")[0][:80])
        if k in seen:
            continue
        seen.add(k)
        uniq.append(f)

    return {
        "seconds": args.seconds,
        "rounds": rounds,
        "sessions": sessions,
        "startedServer": started_server,
        "findingCount": len(uniq),
        "errors": sum(1 for f in uniq if f.severity == "error"),
        "warnings": sum(1 for f in uniq if f.severity == "warn"),
        "findings": [f.as_dict() for f in uniq],
    }


def main():
    ap = argparse.ArgumentParser(description="MapleBench 自治驗證引擎")
    ap.add_argument("--seconds", type=float, default=120)
    ap.add_argument("--frames-per-session", type=int, default=240)
    ap.add_argument("--idle-frames", type=int, default=90,
                    help="每場前 N 幀完全不按鍵，用來量漂移")
    ap.add_argument("--map-file", default="Map.nx",
                    help="檔案名（用來查 id），不是路徑")
    ap.add_argument("--maps", nargs="*",
                    default=["Map/Map0/000000002.img", "Map/Map0/000030000.img",
                             "Map/Map0/000000001.img", "Map/Map0/000000003.img"])
    ap.add_argument("--json", help="把完整結果寫成 JSON")
    ap.add_argument("--near-mob", action="store_true", default=True,
                    help="每場從某隻怪附近出生，讓戰鬥規則真的被觸發")
    ap.add_argument("--weapons", nargs="*",
                    default=["axe", "axe.img", "barehands", "dagger"],
                    help="兩種寫法都放進去，因為副檔名正規化本身就是被測的東西")
    ap.add_argument("--no-open", action="store_true")
    ap.add_argument("--start-server", action="store_true",
                    help="明確開機：只在 GATE_BASE 的埠空閒時才開，且開在那個埠。"
                         "埠上已有東西就拒絕沿用並結束，不會靜默用別人的實例。")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    # 實例在 argv 解析完之後才要求：--help 由 argparse 自己結束，不該走到這裡。
    require_base()

    print(f"soak: {args.seconds:.0f}s，每場 {args.frames_per_session} 幀，"
          f"地圖 {len(args.maps)} 張", flush=True)
    r = run(args)

    print(f"\n跑了 {r['rounds']} 場 / {r['sessions']} 個 session，"
          f"{r['seconds']:.0f} 秒")
    if r["findings"]:
        print(f"發現 {r['findingCount']} 條"
              f"（error {r['errors']} / warn {r['warnings']}）：\n")
        for f in r["findings"]:
            print("  " + str(f).replace("\n", "\n  "))
    else:
        print("\n沒有發現任何一條不變量被打破。")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(r, fh, ensure_ascii=False, indent=2)
        print(f"\n完整結果 → {args.json}")

    return 1 if r["errors"] else 0


if __name__ == "__main__":
    sys.exit(main())