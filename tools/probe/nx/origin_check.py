"""Does the reference put the feet on the character's ground origin?

Two independent checks, both against the real archives, no rendering:

 1. GEOMETRIC. Compose stand1/0 exactly as CharLook does and work out where
    each sprite's rectangle lands relative to the character's origin (the
    foothold Y). The reference says the bottom of the box is the feet.

 2. THE CURRENT FORMULA. Run Avatar.placement()'s arithmetic on the same
    layer set and compare the two.

Usage: python origin_check.py [clientDir]
"""

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from charlook_sim import Look, BITMAP  # noqa: E402


def collect(look, stance, frame):
    look.build_anchors(stance, [frame])
    parts = []
    for p in look.body_parts(stance, frame) + look.hair_parts(stance, frame):
        parts.append(p)
    for e in look.equips:
        parts += look.equip_parts(stance, frame, e)
    parts.sort(key=lambda s: -s["z"])
    return parts


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else r"C:\MUWORK\GAME\MPSHS"
    look = Look(root)
    stance, frame = "stand1", "0"
    parts = collect(look, stance, frame)

    print("=== 1. reference composition, rectangles relative to the character origin ===")
    print("    Texture::draw -> DrawArgument::get_rectangle: top-left = pos - origin,")
    print("    so a part is placed at (shift - origin) once its map anchor is aligned.")
    print()
    top = None
    bottom = None
    left = None
    right = None
    for p in parts:
        x = p["shift"][0] - p["origin"][0]
        y = p["shift"][1] - p["origin"][1]
        b = y + p["h"]
        r = x + p["w"]
        top = y if top is None else min(top, y)
        bottom = b if bottom is None else max(bottom, b)
        left = x if left is None else min(left, x)
        right = r if right is None else max(right, r)
        print("    %-12s z=%-4d shift=%-10s origin=%-10s rect=(%d,%d)..(%d,%d)"
              % (p["name"], p["z"], p["shift"], p["origin"], x, y, r, b))
    print()
    print("    union rect      = (%d,%d)..(%d,%d)" % (left, top, right, bottom))
    print("    height above y=0 = %d   (feet are at +%d)" % (-top, bottom))
    print()

    print("=== 2. what Avatar.placement() does with the same layers ===")
    print()
    anchor_x = max(p["origin"][0] for p in parts)
    anchor_y = max(p["origin"][1] for p in parts)
    frame_w = max(anchor_x - p["origin"][0] + p["w"] for p in parts)
    frame_h = max(anchor_y - p["origin"][1] + p["h"] for p in parts)
    print("    server-composed anchor  = (%d,%d)   [max origin over all layers]"
          % (anchor_x, anchor_y))
    print("    frame box               = %dx%d" % (frame_w, frame_h))
    print()
    print("    oy = -anchorY = %d, so the box spans y %d..%d"
          % (-anchor_y, -anchor_y, -anchor_y + frame_h))
    print("    delta from the character origin at the box bottom = %d px"
          % (-anchor_y + frame_h))
    print()

    # Where the reference puts it: bottom == 0.
    # Where the current formula puts it: bottom == frame_h - anchor_y.
    ref_bottom = bottom
    cur_bottom = -anchor_y + frame_h
    print("=== 3. the two bottoms, relative to the character's ground origin ===")
    print("    reference (navel-aligned)  bottom = %+d" % ref_bottom)
    print("    current  (max-origin)      bottom = %+d" % cur_bottom)
    print("    error = %+d px" % (cur_bottom - ref_bottom))
    print()
    print("    the navel anchor for %s/%s is %s" % (stance, frame, look.body_pos[(stance, frame)]))
    print("    so the sprite the reference places lowest already reaches %d past y=0;" % (-bottom))
    print("    the current formula's box bottom sits %d px BELOW that." % (cur_bottom - ref_bottom))


if __name__ == "__main__":
    main()