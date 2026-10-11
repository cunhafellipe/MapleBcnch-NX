import nxparse, glob, os
print("Do NX parents that share a node share an IDENTICAL child range?")
print(f"{'file':16s} {'multiParent':>11s} {'identicalRange':>14s} {'partialShare':>12s}")
for p in [r"C:\MUWORK\GAME\MPSH\Morph.nx", r"C:\MUWORK\GAME\MPSH\Item.nx", r"C:\MUWORK\GAME\MPSH\Character.nx"]:
    f = nxparse.NxFile(p)
    parents = {}
    for n in f.nodes:
        for c in range(n["children"], n["children"] + n["num"]):
            parents.setdefault(c, []).append((n["children"], n["num"]))
    multi = {k: v for k, v in parents.items() if len(v) > 1}
    identical = sum(1 for v in multi.values() if len(set(v)) == 1)
    print(f"{os.path.basename(p):16s} {len(multi):11,} {identical:14,} {len(multi)-identical:12,}")
