"""Reproduce CharLook::draw() from MPSH clean-client against the REAL archives.

This is the C++ transliterated, node for node, with no shortcuts:

  BodyDrawInfo::init   (BodyDrawInfo.cpp:33-116)  -- per pose anchors
  Body ctor            (Body.cpp:48-100)          -- body + head parts, z/slots/priority
  Hair ctor            (Hair.cpp:29-80)           -- hair parts off the brow anchor
  Face ctor            (Face.cpp:66-95)           -- face marker
  Clothing ctor        (Clothing.cpp:78-190)      -- equips off their map point
  CharLook::draw       (CharLook.cpp:82-207)      -- collect, arbitrate, sort, anchor

Every number printed comes out of the archive. Nothing is assumed.

Usage: python charlook_sim.py [clientDir] [stance] [frame]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from nxparse import NxFile, INTEGER, STRING, VECTOR, BITMAP  # noqa: E402

INT16_MAX = 32767

# Body.cpp:157, layers_by_name verbatim.
LAYER_BY_NAME = {
    "body": "BODY",
    "backBody": "BODY",
    "arm": "ARM",
    "armBelowHead": "ARM_BELOW_HEAD",
    "armBelowHeadOverMailChest": "ARM_BELOW_HEAD_OVER_MAIL",
    "armOverHair": "ARM_OVER_HAIR",
    "armOverHairBelowWeapon": "ARM_OVER_HAIR_BELOW_WEAPON",
    "handBelowWeapon": "HAND_BELOW_WEAPON",
    "handOverHair": "HAND_OVER_HAIR",
    "handOverWeapon": "HAND_OVER_WEAPON",
    "head": "HEAD",
}


class Look:
    def __init__(self, root_dir, hair="00030000.img", face="00020000.img",
                 equips=("01000000.img",)):
        self.dir = root_dir
        self.base = NxFile(os.path.join(root_dir, "Base.nx"))
        self.ch = NxFile(os.path.join(root_dir, "Character.nx"))
        self.zmap = self._table("zmap.img", INTEGER)
        self.smap = self._table("smap.img", STRING)
        self.codes = {}          # Tables::mask first-seen order, process-wide
        self.body = self._img("00002000.img")
        self.head = self._img("00012000.img")
        self.hair = self._folder("Hair", hair)
        self.face = self._folder("Face", face)
        self.equips = [self._folder("Cap", n) for n in equips]
        self.anchors = {}
        self.body_pos = {}
        self.face_pos = {}
        self.hair_pos = {}
        self.head_pos = {}
        self.hand_pos = {}

    # -- archive plumbing ------------------------------------------------
    def _table(self, name, typ):
        root = self.base.nodes[0]
        for c in self.base.child_nodes(root):
            if self.base.string_at(c["name"]) == name:
                out = {}
                for e in self.base.child_nodes(c):
                    if e["type"] == typ:
                        out[self.base.string_at(e["name"])] = (e["i"] if typ == INTEGER
                                                             else self.base.string_at(e["s"]))
                return out
        return {}

    def _kids(self, node, f=None):
        f = f or self.ch
        return {f.string_at(c["name"]): c for c in f.child_nodes(node)}

    def _img(self, name):
        for c in self.ch.child_nodes(self.ch.nodes[0]):
            if self.ch.string_at(c["name"]) == name:
                return c
        return None

    def _folder(self, folder, name):
        root = self._kids(self.ch.nodes[0])
        return self._kids(root[folder]).get(name)

    def _info(self, node):
        m = self._kids(node).get("info")
        if m is None:
            return "", ""
        im = self._kids(m)
        v = im.get("vslot")
        i = im.get("islot")
        return (self.ch.string_at(v["s"]) if v and v["type"] == STRING else "",
                self.ch.string_at(i["s"]) if i and i["type"] == STRING else "")

    # -- AvatarLayers.cpp ------------------------------------------------
    def z_of(self, name, fallback):
        return self.zmap.get(name, fallback)

    def mask(self, codes):
        r = 0
        for k in range(0, len(codes) - 1, 2):
            code = codes[k:k + 2]
            if code not in self.codes:
                if len(self.codes) >= 64:
                    continue
                self.codes[code] = len(self.codes)
            r |= 1 << self.codes[code]
        return r

    def claimed(self, zname, vslot):
        return self.mask(self.smap.get(zname, "")) & self.mask(vslot)

    def priority_of(self, islot):
        r = INT16_MAX
        for k in range(0, len(islot) - 1, 2):
            r = min(r, self.z_of(islot[k:k + 2], r))
        return r

    def zname_of(self, part):
        m = self._kids(part)
        z = m.get("z")
        if z is not None and z["type"] == STRING:
            return self.ch.string_at(z["s"])
        return self.ch.string_at(part["name"])

    # -- BodyDrawInfo.cpp:33-116 ----------------------------------------
    def build_anchors(self, stance="stand1", frames=("0",)):
        for frame in frames:
            bs = self._body_frame(stance, frame)
            if bs is None:
                continue
            shift = {}     # Body::Layer -> {point: (x,y)}
            for part in self.ch.child_nodes(bs):
                name = self.ch.string_at(part["name"])
                if name in ("delay", "face"):
                    continue
                pm = self._kids(part)
                zp = pm.get("z")
                # BodyDrawInfo.cpp:76 reads `std::string zstr = partnode["z"]`, so in
                # NX the z node is a STRING ("body", "armOverHair") and the layer
                # name goes through Body::layers_by_name (Body.cpp:157).
                zstr = self.ch.string_at(zp["s"]) if zp is not None and zp["type"] == STRING else ""
                layer = LAYER_BY_NAME.get(zstr, "NONE")
                bucket = shift.setdefault(layer, {})
                mp = pm.get("map")
                if mp is not None:
                    for k, v in self._kids(mp).items():
                        if v["type"] == VECTOR:
                            bucket.setdefault(k, v["xy"])

            hs = self._head_frame(stance, frame)
            hshift = {}
            if hs is not None:
                mp = self._kids(hs).get("map")
                if mp is not None:
                    for k, v in self._kids(mp).items():
                        if v["type"] == VECTOR:
                            hshift[k] = v["xy"]
            if not hshift:      # dead stance -> stand1[0] head (BodyDrawInfo.cpp:88-92)
                hs = self._head_frame("stand1", "0")
                hshift = {}
                if hs is not None:
                    mp = self._kids(hs).get("map")
                    if mp is not None:
                        for k, v in self._kids(mp).items():
                            if v["type"] == VECTOR:
                                hshift[k] = v["xy"]
            shift["HEAD"] = hshift

            body = shift.get("BODY", {})
            head = shift.get("HEAD", {})
            self.body_pos[(stance, frame)] = body.get("navel", (0, 0))
            arm = shift.get("ARM")
            if arm is None:
                arm = shift.get("ARM_OVER_HAIR", {})
            # BodyDrawInfo.cpp:97-98.
            self.head_pos[(stance, frame)] = (
                body.get("neck", (0, 0))[0] - head.get("neck", (0, 0))[0],
                body.get("neck", (0, 0))[1] - head.get("neck", (0, 0))[1])
            self.hand_pos[(stance, frame)] = shift.get("HAND_BELOW_WEAPON", {}).get("handMove", (0, 0))
            self.face_pos[(stance, frame)] = (
                (body.get("neck", (0, 0))[0] - head.get("neck", (0, 0))[0]
                 + head.get("brow", (0, 0))[0]),
                (body.get("neck", (0, 0))[1] - head.get("neck", (0, 0))[1]
                 + head.get("brow", (0, 0))[1]))
            self.hair_pos[(stance, frame)] = (
                head.get("brow", (0, 0))[0] - head.get("neck", (0, 0))[0] + body.get("neck", (0, 0))[0],
                head.get("brow", (0, 0))[1] - head.get("neck", (0, 0))[1] + body.get("neck", (0, 0))[1])

            a = {}
            for k, v in head.items():
                a[k] = (v[0] - head.get("neck", (0, 0))[0] + body.get("neck", (0, 0))[0],
                        v[1] - head.get("neck", (0, 0))[1] + body.get("neck", (0, 0))[1])
            for k, v in body.items():
                a[k] = v
            self.anchors[(stance, frame)] = a

    def _body_frame(self, stance, frame):
        node = self.body
        st = self._kids(node).get(stance)
        return self._kids(st).get(frame) if st else None

    def _head_frame(self, stance, frame):
        node = self.head
        st = self._kids(node).get(stance)
        if st is None:
            return None
        fr = self._kids(st).get(frame)
        if fr is None:
            return None
        return self._kids(fr).get("head")

    # -- AvatarPart builders ---------------------------------------------
    def part(self, node, zn, zfallback, slots, priority, shift, owner, layer):
        return {"name": self.ch.string_at(node["name"]), "zname": zn,
                "z": self.z_of(zn, zfallback), "slots": self.claimed(zn, slots),
                "priority": priority, "shift": shift, "owner": owner,
                "layer": layer,
                "w": node["bm"][1], "h": node["bm"][2],
                "origin": self._kids(node).get("origin")["xy"] if "origin" in self._kids(node) else (0, 0)}

    def body_parts(self, stance, frame):
        bs = self._body_frame(stance, frame)
        if bs is None:
            return []
        vslot, islot = self._info(self.body)
        slots, prio = self.mask(vslot), self.priority_of(islot)
        out = []
        for n in self.ch.child_nodes(bs):
            name = self.ch.string_at(n["name"])
            if name in ("delay", "face") or n["type"] != BITMAP:
                continue
            zn = self.zname_of(n)
            mp = self._kids(n).get("map")
            mk = self._kids(mp) if mp else {}
            if "handMove" in mk:
                # Body.cpp:64-66, hands hang off the moving hand point.
                hp = self.hand_pos[(stance, frame)]
                shift = (hp[0] - mk["handMove"]["xy"][0], hp[1] - mk["handMove"]["xy"][1])
            else:
                # Body.cpp:71-72, everything else off the navel.
                bp = self.body_pos[(stance, frame)]
                own = mk["navel"]["xy"] if "navel" in mk else (0, 0)
                shift = (bp[0] - own[0], bp[1] - own[1])
            out.append(self.part(n, zn, 0, vslot, prio, shift, "body", "BODY"))
        hs = self._head_frame(stance, frame)
        if hs is not None:
            hv, hi = self._info(self.head)
            # Body.cpp:88, the head hangs off the body's neck, not its navel.
            shift = self.head_pos[(stance, frame)]
            zn = self.zname_of(hs)
            out.append(self.part(hs, zn, 0, hv, self.priority_of(hi), shift, "head", "BODY"))
        return out

    def hair_parts(self, stance, frame):
        if self.hair is None:
            return []
        vslot, islot = self._info(self.hair)
        st = self._kids(self.hair).get(stance)
        if st is None:
            return []
        fr = self._kids(st).get(frame)
        if fr is None:
            return []
        hp = self.hair_pos[(stance, frame)]
        out = []
        for n in self.ch.child_nodes(fr):
            name = self.ch.string_at(n["name"])
            if name == "hairShade":
                continue
            if n["type"] != BITMAP:
                continue
            zn = self.zname_of(n)
            mp = self._kids(n).get("map")
            brow = self._kids(mp).get("brow")["xy"] if mp and "brow" in self._kids(mp) else (0, 0)
            out.append(self.part(n, zn, 0, vslot, self.priority_of(islot),
                                 (hp[0] - brow[0], hp[1] - brow[1]), "hair", "BODY"))
        return out

    def face_parts(self):
        if self.face is None:
            return [], None
        vslot, islot = self._info(self.face)
        dflt = self._kids(self.face).get("default")
        if dflt is None:
            return [], None
        zn = self.zname_of(dflt)
        marker = {"name": "<face marker>", "zname": zn, "z": self.z_of(zn, 0),
                  "slots": self.claimed(zn, vslot), "priority": self.priority_of(islot),
                  "anchor": "FACE", "owner": "face"}
        return [], marker

    def equip_parts(self, stance, frame, node):
        vslot, islot = self._info(node)
        st = self._kids(node).get(stance)
        if st is None:
            return []
        fr = self._kids(st).get(frame)
        if fr is None:
            return []
        defaultz = self.z_of(self.ch.string_at(node["name"]).replace(".img", "") or "", 0)
        out = []
        for n in self.ch.child_nodes(fr):
            if n["type"] != BITMAP:
                continue
            zn = self.zname_of(n)
            mp = self._kids(n).get("map")
            parent, ppos = "", (0, 0)
            if mp is not None:
                for k, v in self._kids(mp).items():
                    if v["type"] == VECTOR:
                        parent, ppos = k, v["xy"]
            anchor = self.anchors.get((stance, frame), {}).get(parent, (0, 0))
            out.append(self.part(n, zn, defaultz, vslot, self.priority_of(islot),
                                 (anchor[0] - ppos[0], anchor[1] - ppos[1]),
                                 "equip:" + self.ch.string_at(node["name"]), "BODY"))
        return out

    # -- CharLook::draw --------------------------------------------------
    def draw(self, stance, frame):
        self.build_anchors(stance, [frame])
        sprites = []
        sprites += self.body_parts(stance, frame)
        sprites += self.hair_parts(stance, frame)
        for e in self.equips:
            sprites += self.equip_parts(stance, frame, e)
        fparts, marker = self.face_parts()
        sprites += fparts
        showface = True
        bs = self._body_frame(stance, frame)
        if bs is not None and "face" in self._kids(bs):
            f = self._kids(bs)["face"]
            showface = f["type"] != INTEGER or f["i"] != 0
        if showface and marker is not None:
            marker["anchor"] = "FACE"
            sprites.append(marker)

        strongest = [INT16_MAX] * 64
        for s in sprites:
            v = s["slots"]
            for bit in range(64):
                if (v >> bit) & 1:
                    strongest[bit] = min(strongest[bit], s["priority"])

        def visible(p):
            v = p["slots"]
            for bit in range(64):
                if ((v >> bit) & 1) and strongest[bit] < p["priority"]:
                    return False
            return True

        sprites.sort(key=lambda s: -s["z"])     # std::stable_sort, z DESC
        return sprites, visible, strongest


def main():
    root_dir = sys.argv[1] if len(sys.argv) > 1 else r"C:\MUWORK\GAME\MPSHS"
    stance = sys.argv[2] if len(sys.argv) > 2 else "stand1"
    frame = sys.argv[3] if len(sys.argv) > 3 else "0"

    look = Look(root_dir)
    look.build_anchors(stance, [frame])
    print("Base.nx zmap %d, smap %d" % (len(look.zmap), len(look.smap)))
    print()
    print("pose anchors %s/%s:" % (stance, frame))
    print("  body navel   = %s   (BodyDrawInfo.cpp:91)" % (look.body_pos.get((stance, frame)),))
    print("  face pos     = %s   (BodyDrawInfo.cpp:96)" % (look.face_pos.get((stance, frame)),))
    print("  hair pos     = %s   (BodyDrawInfo.cpp:97)" % (look.hair_pos.get((stance, frame)),))
    anchors = look.anchors.get((stance, frame), {})
    print("  equip anchors: " + ", ".join("%s=%s" % (k, v) for k, v in sorted(anchors.items())))
    print()

    sprites, visible, strongest = look.draw(stance, frame)

    print("sprites (collection order -> draw order). z from Base/zmap.img.")
    print("  %-3s %-14s %-12s %5s %5s %-18s %-18s %s"
          % ("#", "owner", "zname", "z", "prio", "slots(hex)", "strongest∩", "draw"))
    for i, s in enumerate(sprites):
        v = visible(s)
        print("  %-3d %-14s %-12s %5d %5d 0x%016x 0x%016x %s"
              % (i, s["owner"], s["zname"], s["z"], s["priority"], s["slots"],
                 s["slots"], "draw" if v else "HIDDEN"))
    print()
    hidden = [s for s in sprites if not visible(s)]
    print("hidden by slot arbitration: %s"
          % ([(s["owner"], s["name"], s["zname"], s["priority"]) for s in hidden] or "none"))


if __name__ == "__main__":
    main()