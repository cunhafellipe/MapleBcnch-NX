# GeneratedTown

Build a new static town candidate from an original generated PNG using MapleBench's
existing MapleLib and NxWzExport implementation. Does not clone any source map.

```powershell
dotnet build tools/GeneratedTown/GeneratedTown.csproj -c Release
& './tools/GeneratedTown/bin/Release/net10.0-windows/GeneratedTown.exe' '<scene.png>' '<new-output-directory>' <nine-digit-id> <standing-row-in-image> '<client-wz-directory>' '<server-root>'
```

The standing row must be selected visually; this tool does not infer walkable terrain.
It creates a world-anchored Obj image, a map with a flat linked foothold chain, spawn,
minimap, server XML and a String merge fragment. It refuses existing output directories,
checks the map id against the supplied client Map.nx and server filenames, checks the
custom object name and BGM, then verifies semantic IMG roundtrips and every canvas pixel.
Use explicit lossless Format2 canvases to avoid automatic color quantization.

This is a candidate generator, not an NX merger or publisher. The scene is one composed
object, without separate buildings, animation, NPCs or exits. Deployment and gameplay
verification remain separate work. No source archives or old map candidates are modified.
