import nxparse
from collections import defaultdict, deque
f = nxparse.NxFile(r"C:\MUWORK\GAME\MPSH\Character.nx")
# depth per node via BFS (first visit wins)
depth = {0:0}
q = deque([0])
while q:
    i = q.popleft()
    n = f.nodes[i]
    for c in range(n["children"], n["children"]+n["num"]):
        if c not in depth:
            depth[c] = depth[i]+1
            q.append(c)
parents = defaultdict(list)
for n in f.nodes:
    for c in range(n["children"], n["children"]+n["num"]):
        parents[c].append(n["children"])
same_depth = diff_depth = 0
for c, ps in parents.items():
    if len(ps) > 1:
        ds = {depth[p] for p in ps}
        if len(ds) == 1: same_depth += 1
        else: diff_depth += 1
print("shared nodes:", len([1 for v in parents.values() if len(v)>1]))
print("  both parents at SAME BFS depth:", same_depth)
print("  parents at DIFFERENT depths   :", diff_depth)
