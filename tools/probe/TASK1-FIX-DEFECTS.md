# TASK 1 — Fix two confirmed defects in the live-walk client path

You are working in a C#/ASP.NET repo. A live keyboard-driven character exists in
the web client. Two defects were confirmed by reading the files. Fix exactly
these two. **Nothing else.**

## Defect 1 — the session is never released

- `MapleBench/wwwroot/js/livewalk.js:101` calls
  `post('/api/simulate/session/${id}/end', {})`
- but the only route is `MapDelete("/session/{id}")` at
  `MapleBench/Api/SimulatorEndpoints.cs:147`

A POST to `/session/{id}/end` matches no route, so the session is never removed
from the `MapSimSessionStore` singleton and leaks for the life of the server.

**Fix:** make the client call the route that exists. Check
`MapleClient.cs` for the established DELETE helper before inventing one.

## Defect 2 — the batch walk clobbers the live body

In `MapleBench/wwwroot/js/simulator.js`, `await this.live.start(...)` is at
line 166 and `await this.viewer.loadWalk({...})` at line 171 — the batch walk
runs *after* the live body starts. `loadWalk` overwrites `viewer.ticks` and
resets `frame` to 0, so the live path and the replay path fight over the same
viewer state.

**Fix:** the two must not share mutable state. Decide which one owns the viewer
at a time and make the other explicitly stand down. State plainly in a comment
why they cannot both drive the viewer.

## Constraints

- Read the actual files before editing. Do not guess line numbers; they were
  verified at the time of writing but may have moved.
- Match the surrounding code style (comment style, naming, error handling).
- Do not add new features, do not refactor anything else, do not touch
  physics constants.
- Do NOT modify the reference repo at
  `C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer`.

## Build and verify before reporting

```
cd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit"
"/c/MUWORK/app/scoop/apps/dotnet-sdk/current/dotnet.exe" build MapleBench/MapleBenchX.csproj -c Release --nologo -v q
```

The build MUST succeed with 0 errors. Then report:

1. What you changed in each file, with the actual diff
2. The build result (verbatim last lines)
3. Anything you could NOT do, and why
4. Anything you found that looks wrong but did not touch

Do not claim a fix works. Report what you ran and what it printed.