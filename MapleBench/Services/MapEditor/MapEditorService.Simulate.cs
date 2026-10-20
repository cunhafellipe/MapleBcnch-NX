using MapleBench.Services.MapModel;
using MapleBench.Services.Nx;
using MapleLib.WzLib;
using MapleLib.WzLib.Util;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Walkability: does a character actually get across this map?
/// </summary>
/// <remarks>
/// <para>
/// <c>/validate</c> checks that the <c>prev</c>/<c>next</c> chain is unbroken.
/// That is necessary and not sufficient — the chain can be intact and still
/// dead-end into a wall, or sit on a layer the character cannot reach. This
/// answers the question the chain check only approximates.
/// </para>
///
/// <para>
/// Read-only, and deliberately so: it inspects and reports, never repairs. An
/// agent that wanted to be told how to fix a map has to be told by someone who
/// can see the map.
/// </para>
/// </remarks>
public sealed partial class MapEditorService
{
    /// <summary>
    /// What the simulator can do right now, and against what.
    /// </summary>
    public MapSimulatorCapabilitiesDto SimulatorCapabilities()
    {
        lock (_gate)
        {
            MapEditCapabilitiesDto caps = Capabilities();
            return new MapSimulatorCapabilitiesDto
            {
                Available = caps.MapArchives.Count > 0,
                MapArchives = caps.MapArchives,
                MapCount = caps.MapCount,
                Note = caps.MapArchives.Count > 0
                    ? "Pick a map to walk. This surface never writes: it answers whether a character "
                    + "could cross the map, which the editor cannot tell you."
                    : "No map archive is open. The simulator reads maps the backend already has "
                    + "loaded; open a Map archive in the file overview first.",
            };
        }
    }

    /// <summary>
    /// Simulates a character walking right across an open map and reports where
    /// it stops.
    /// </summary>
    /// <param name="path">The map, as an address like <c>f1/Map/Map0/000000001.img</c>.
    /// It does NOT have to be open in the editor.</param>
    /// <param name="startX">
    /// Where to start, or null to begin at the leftmost foothold. Starting at the
    /// left edge is the honest default: it is the question "can this map be
    /// walked through", not "can this particular spot be walked off".
    /// </param>
    /// <param name="startY">
    /// Where to start vertically, or null to land on the surface below
    /// <paramref name="startX"/>'s leftmost foothold.
    /// </param>
    public MapSimulateDto Simulate(string path, int? startX, int? startY)
    {
        lock (_gate)
        {
            // NOT RequireWritable. The simulator must work on a map nobody has
            // opened for editing: the case this exists for is an agent that just
            // generated a map and needs to know whether it can be walked, before
            // anyone has opened it. Loading read-only costs a round trip and
            // touches no editor state, no dirty flag and no undo stack.
            MapDocument map = LoadForReading(path);

            FootholdPhysics physics = new(map);
            MapSimulateDto result = new()
            {
                Path = path,
                Footholds = physics.Footholds.Count,
                UnusableFootholds = physics.Unusable,
            };

            if (physics.Footholds.Count == 0)
            {
                result.Walkable = false;

                // A link stub has no foothold because its geometry lives in ANOTHER
                // map. Calling that "no foothold" would send an agent off to
                // generate footholds that are not the problem -- measured: 3 of the
                // first 10 sampled maps were stubs.
                string? link = map.Find("info")?.Child("link")?.AsText();
                result.IsLinkStub = link is { Length: > 0 };
                result.Link = link ?? "";

                result.Reason = result.IsLinkStub
                    ? $"This map is a link stub pointing at '{result.Link}'. Its geometry is not here, "
                      + "so there is nothing to walk. Open the target to simulate it."
                    : map.Layers.Count > 0
                        ? "This map has layers but no foothold with usable endpoints. Nothing can stand on it."
                        : "This map has no foothold at all.";
                return result;
            }

            (int Left, int Top, int Right, int Bottom) bounds = physics.Bounds!.Value;
            result.Bounds = new MapSimulateBoundsDto(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

            int originX = startX ?? bounds.Left;
            FootholdPhysics.Foot? start = FindStartFoothold(physics, originX, startY);

            // The leftmost foothold is often a vertical wall with no ground on it:
            // measured, all six leftmost footholds on 221024000 are walls
            // (x1==x2). Standing on a wall is meaningless, so step right until
            // there is something to stand on.
            if (start is null && startX is null)
            {
                start = FindFirstStandable(physics, originX, originX + ScanDistance);
                if (start is not null)
                {
                    originX = start.Value.X1;
                    result.StartScannedFrom = bounds.Left;
                }
            }
            if (start is null)
            {
                result.Walkable = false;
                result.Reason =
                    $"No foothold passes under x={originX}, so a character dropped there falls out of the map.";
                result.StartX = originX;
                return result;
            }

            result.StartX = start.Value.X1;
            result.StartY = start.Value.Y1;
            result.StartFoothold = start.Value.Id;

            FootholdWalk walk = new(physics);
            FootholdWalk.WalkResult walked = walk.WalkRight(start.Value, bounds.Right);

            result.ReachedEnd = walked.ReachedEnd;
            result.Reason = walked.Reason;
            result.Steps = walked.Steps;

            // The crossed chain, not just its length: a caller that wants to show
            // WHERE the character got to needs the segments, and the count alone
            // cannot tell it. Bounded because a 5,820-foothold map would
            // otherwise ship a megabyte of geometry per request.
            const int PathCap = 4000;
            foreach (FootholdWalk.WalkStep step in walked.Path)
            {
                if (result.WalkedChain.Count >= PathCap)
                {
                    result.PathTruncated = true;
                    break;
                }
                result.WalkedChain.Add(new MapWalkSegmentDto(step.Id, step.Layer, step.Left, step.Right, step.Top, step.Bottom));
            }

            if (walked.StoppedAt is { } stopped)
            {
                result.StoppedX = stopped.X2;
                result.StoppedY = stopped.Y2;
                result.StoppedFoothold = stopped.Id;
                result.DistanceWalked = stopped.Right - bounds.Left;
                result.MapWidth = bounds.Right - bounds.Left;

                // A walk that stopped short of the right edge is the interesting
                // case; a chain that legitimately ends early is reported as such
                // rather than as a failure, and the number says how short.
                result.PercentOfMap =
                    bounds.Right > bounds.Left
                        ? 100.0 * result.DistanceWalked / result.MapWidth
                        : 0;

                // What the walk proves is REACHABILITY ALONG ITS CHAIN, not that
                // the chain spans the map. Measured: 100040104 ships 428 footholds
                // with ZERO having prev=0 -- it is a multi-level dungeon whose
                // platforms connect by ladders, portals and jumps, not by one
                // continuous chain. Reading "9% of the way across" as a defect
                // would condemn maps that work perfectly in game.
                //
                // So the verdict is: did the walk get stuck? A clean end of chain
                // means the character can walk as far as the data lets it, which
                // is what a foothold chain is FOR. Whether the rest of the map is
                // reachable needs gravity, ladders and portals, which this does
                // not model, and saying so beats inventing a failure.
                result.Walkable = walked.ReachedEnd;
                result.Conclusive = result.PercentOfMap >= 99.5;
                if (walked.ReachedEnd)
                {
                    result.Reason =
                        $"the chain ends at foothold {stopped.Id} after {walked.Steps:N0} footholds, "
                        + $"covering {result.DistanceWalked:N0} px of a {result.MapWidth:N0} px "
                        + $"walkable span ({result.PercentOfMap:N1}%). "
                        + (result.PercentOfMap < 99.5
                            ? "The rest of the map is reached by ladders, portals or jumps, "
                              + "which this walk does not model -- treat this as INCONCLUSIVE, "
                              + "not as a broken map."
                            : "That is the full walkable span.");
                }
            }

            return result;
        }
    }

    /// <summary>
    /// The foothold a character dropped at (x, y) would land on, or the one to
    /// start from when no height was given.
    /// </summary>
    private static FootholdPhysics.Foot? FindStartFoothold(
        FootholdPhysics physics, int x, int? startY)
    {
        if (startY is { } y)
            return physics.FindBelow(x, y);

        // No height given. The leftmost foothold is often a wall's base rather than
        // walkable ground -- measured: on 100040104 the leftmost foothold sits at
        // the BOTTOM of the map (y=-2175 of a -2188..900 range) and starting
        // there reports a map that is perfectly walkable as 9% walkable.
        //
        // So: prefer a foothold that lies DOWNHILL to the right, which is the
        // shape a walkable ledge actually has. Only fall back to the extreme
        // topmost if nothing at all descends.
        FootholdPhysics.Foot? descending = null;
        FootholdPhysics.Foot? topmost = null;
        int topY = int.MinValue;

        foreach (FootholdPhysics.Foot f in physics.Footholds)
        {
            if (f.IsWall || x < f.Left || x > f.Right)
                continue;
            if (f.Top > topY)
            {
                topY = f.Top;
                topmost = f;
            }
            if (f.X2 > f.X1 && descending is null)
                descending = f;
        }

        return descending ?? topmost ?? physics.FindBelow(x, 0);
    }

    /// <summary>How far right the start scan will look before giving up.</summary>
    private const int ScanDistance = 2000;

    /// <summary>
    /// A map model for reading only, with no editor document behind it.
    /// </summary>
    /// <remarks>
    /// Deliberately mirrors <see cref="Open"/>'s two branches rather than
    /// reaching for an open document: a .wz map is a WzImage and goes through the
    /// verified round trip, an NX map is a WzSubProperty and is lifted into a
    /// synthetic image first. Sharing that knowledge is the point -- a second
    /// loader that guessed the shape differently would disagree with the editor
    /// about the same map, which is worse than having no simulator at all.
    /// </remarks>
    private MapDocument LoadForReading(string path) => LoadForReading(path, 0);

    /// <summary>
    /// Follows an <c>info/link</c> stub to the map that actually has the geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A map whose <c>info/link</c> names another map is not an empty map -- it is
    /// the SAME map under a second id, which the archive keeps to avoid storing
    /// the geometry twice. The client never loads the stub. MapleNecrocer,
    /// <c>MainForm.cs:759-765</c>:
    /// </para>
    /// <code>
    /// var Node = Wz.GetNode(NodePath + "/info/link");
    /// if (Node == null)
    ///     Map.ID = ID;
    /// else
    ///     Map.ID = Node.Value.ToString();
    /// </code>
    /// <para>
    /// and then everything after that line -- <c>Map.Img</c>, the footholds, the
    /// portals, the bounds -- is read from <c>Map.ID</c>, never from the id the
    /// caller asked for. <c>MapleMap.LoadMap</c> (:162-199) has no link handling
    /// at all precisely because the id has already been swapped by the time it runs.
    /// </para>
    /// <para>
    /// Without this, a stub parses to a document with no footholds at all and
    /// every later stage reports "nothing to stand on" -- measured over 400
    /// sampled maps: 155 of them refused, and <b>all 155 were link stubs</b>
    /// (99 to <c>100020100</c>, the rest to <c>1030404x0</c> and
    /// <c>103000300</c>), every one of which has a walkable target carrying 41-101
    /// footholds.
    /// </para>
    /// <para>
    /// The hop is bounded rather than looping: a stub whose link chain loops, or
    /// names a map that is not in the archive, must stop and say so instead of
    /// walking forever.
    /// </para>
    /// </remarks>
    private MapDocument LoadForReading(string path, int hops)
    {
        lock (_session.Gate)
        {
            OpenFile file = _session.GetFileForPath(path);
            WzObject node = _session.Resolve(path);

            MapDocument document = ReadMapDocument(node, file, path);

            // The link check runs on the PARSED document rather than on the raw
            // property list, so it reads `info/link` through MapInfo.Link -- the
            // accessor that already handles both spellings the archive ships:
            // String on 6,695 maps and Int on 155. A raw read that accepted only
            // one of them would silently leave 155 stubs unfollowed, which is
            // exactly the population that fails.
            if (document.Info?.Link is { Length: > 0 } target)
            {
                if (hops >= MaxLinkHops)
                    throw new InvalidOperationException(
                        $"'{path}' is a link to '{target}' and the chain did not end after "
                        + $"{MaxLinkHops} hops, so it cannot be read as a map.");
                return LoadForReading(LinkTargetPath(path, target), hops + 1);
            }

            return document;
        }
    }

    /// <summary>
    /// Reads one map image into a document, without following any link it names.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="LoadForReading(string, int)"/> so the link hop is
    /// decided on a document that has already been parsed. The two branches are
    /// unchanged from the single-loader version and carry their own reasons for
    /// existing.
    /// </remarks>
    private MapDocument ReadMapDocument(WzObject node, OpenFile file, string path)
    {
        if (node is WzImage resolved)
        {
            byte[] iv = file.CustomIv ?? WzTool.GetIvByMapleVersion(file.MapleVersion);
            MapLoadResult verified = MapRoundTrip.LoadVerified(resolved, iv);
            if (!verified.Ok)
                throw new InvalidOperationException(verified.Reason ?? "The map could not be read.");
            return verified.Document!;
        }

        if (IsNx(file))
        {
            // No verification round trip for NX, same as the editor: there
            // is no block on disk to verify against. Reading is what matters
            // here and reading is identical in both.
            // No copy. NxMapImage deep-clones every property so the editor
            // can write back safely (MapEditorService.cs:501); a walk has no
            // reason to. An NX map is 0.1 MB in the archive and 2.5 GB cloned,
            // and the process dies of the difference without an exception --
            // measured at 308 MB with two archives open, 3,282 MB after one
            // walk, then two silent deaths.
            string leaf = node.Name is { Length: > 0 } n ? n : "map.img";
            MapLoadResult shared = MapDocument.LoadShared(NxMapProperties(node), leaf);
            if (!shared.Ok)
                throw new InvalidOperationException(shared.Reason ?? "The map could not be read.");
            return shared.Document!;
        }

        throw new InvalidOperationException(
            $"'{path}' is a {node.GetType().Name}, which carries no map geometry to walk.");
    }

    /// <summary>How many <c>info/link</c> hops a load will follow before giving up.</summary>
    /// <remarks>
    /// A stub may name another stub. Real chains are one hop deep, so this only
    /// has to be large enough to catch a loop, and small enough that a cycle
    /// costs a millisecond instead of a stack overflow.
    /// </remarks>
    private const int MaxLinkHops = 8;

    /// <summary>
    /// The path of a link target, built the way the client builds one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MapleMap.LoadMap:157-162</c> splits a map id on its FIRST digit to find
    /// the <c>Map&lt;n&gt;</c> directory:
    /// </para>
    /// <code>
    /// string LeftNum = ID.LeftStr(1);
    /// Map.Img = Wz.GetNode("Map/Map/Map" + LeftNum + "/" + ID + ".img");
    /// </code>
    /// <para>
    /// The directory therefore comes from the TARGET's own first digit, not the
    /// stub's, and a link that crossed a digit would move directory. Everything
    /// above that -- the archive id and the role root -- is shared by both maps
    /// and is carried over verbatim from the stub's own path, which is the only
    /// source that is certainly resolvable (it resolved a moment ago).
    /// </para>
    /// <para>
    /// So this replaces exactly the last TWO segments: <c>f1/Map/Map1/100020101.img</c>
    /// to a target <c>100020100</c> becomes <c>f1/Map/Map1/100020100.img</c>.
    /// </para>
    /// </remarks>
    private static string LinkTargetPath(string stubPath, string target)
    {
        string[] segments = WzPath.SplitRaw(stubPath);
        if (segments.Length < 2)
            throw new InvalidOperationException(
                $"'{stubPath}' is a link to '{target}', but it does not sit under a Map<n> "
                + "directory, so there is no path its target could be read from.");

        string[] rebuilt =
        [
            .. segments[..^2],
            "Map" + target[0],
            target + ".img",
        ];
        return string.Join("/", rebuilt);
    }

    /// <summary>
    /// The leftmost foothold in [from, to] that a character could stand on:
    /// not a wall, and long enough to walk on.
    /// </summary>
    private static FootholdPhysics.Foot? FindFirstStandable(
        FootholdPhysics physics, int from, int to)
    {
        FootholdPhysics.Foot? best = null;
        foreach (FootholdPhysics.Foot f in physics.Footholds)
        {
            if (f.IsWall || f.Right <= from || f.Left >= to)
                continue;
            if (best is null || f.Left < best.Value.Left)
                best = f;
        }

        return best;
    }
}

/// <summary>What the simulator can do against the current session.</summary>
public sealed class MapSimulatorCapabilitiesDto
{
    /// <summary>True when at least one map archive is open.</summary>
    public bool Available { get; set; }

    /// <summary>The open map archives.</summary>
    public List<string> MapArchives { get; set; } = [];

    /// <summary>How many maps those archives hold.</summary>
    public int MapCount { get; set; }

    /// <summary>A sentence explaining the state, in the app's own voice.</summary>
    public string Note { get; set; } = "";
}

/// <summary>The walkability report for one map.</summary>
public sealed class MapSimulateDto
{
    /// <summary>The map that was simulated.</summary>
    public string Path { get; set; } = "";

    /// <summary>Footholds that had usable endpoints.</summary>
    public int Footholds { get; set; }

    /// <summary>
    /// Footholds present in the file that could not be read as a surface. A
    /// non-zero count means /validate's foothold count is counting something the
    /// character cannot stand on.
    /// </summary>
    public int UnusableFootholds { get; set; }

    /// <summary>
    /// Whether the character could walk as far as the data allows, with nothing
    /// blocking it. True does NOT mean the whole map is crossable on foot.
    /// </summary>
    public bool Walkable { get; set; }

    /// <summary>
    /// False when the walk finished cleanly but covered only part of the map,
    /// which means the rest is reached by ladders, portals or jumps. A caller
    /// must not read such a map as broken — see <see cref="Reason"/>.
    /// </summary>
    public bool Conclusive { get; set; } = true;

    /// <summary>True when the chain ended without reaching the map's right edge.</summary>
    public bool ReachedEnd { get; set; }

    /// <summary>Why the walk ended, in the model's own words.</summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// True when the map is only a pointer to another map. Its absence of
    /// footholds is not a defect and must not be read as one.
    /// </summary>
    public bool IsLinkStub { get; set; }

    /// <summary>The map this one points at, when <see cref="IsLinkStub"/> is true.</summary>
    public string Link { get; set; } = "";

    /// <summary>Footholds crossed.</summary>
    public int Steps { get; set; }

    /// <summary>
    /// The chain the walk actually crossed, in order. Enough to draw the path or
    /// to say exactly which segment it died on. Named WalkedChain rather than
    /// Path because <see cref="Path"/> is already this map's address.
    /// </summary>
    public List<MapWalkSegmentDto> WalkedChain { get; set; } = [];

    /// <summary>True when <see cref="WalkedChain"/> stopped at a cap, not at the chain's end.</summary>
    public bool PathTruncated { get; set; }

    /// <summary>Where the walk began.</summary>
    public int StartX { get; set; }

    /// <summary>The Y the walk began at.</summary>
    public int StartY { get; set; }

    /// <summary>The foothold the walk began on.</summary>
    public string StartFoothold { get; set; } = "";

    /// <summary>
    /// Set when the walk could not begin at the map's left edge because there was
    /// only a wall there, and had to start further in. The map's true left edge
    /// is here, so a reader knows the walk did not cover it.
    /// </summary>
    public int? StartScannedFrom { get; set; }

    /// <summary>Where the walk stopped, if it did.</summary>
    public int StoppedX { get; set; }

    /// <summary>The Y it stopped at.</summary>
    public int StoppedY { get; set; }

    /// <summary>The foothold it stopped on.</summary>
    public string StoppedFoothold { get; set; } = "";

    /// <summary>How far it got, in pixels from the map's left edge.</summary>
    public int DistanceWalked { get; set; }

    /// <summary>The walkable width, for the percentage below.</summary>
    public int MapWidth { get; set; }

    /// <summary>How much of the map the walk covered.</summary>
    public double PercentOfMap { get; set; }

    /// <summary>The walkable extent.</summary>
    public MapSimulateBoundsDto? Bounds { get; set; }
}

/// <summary>One foothold the walk crossed, as geometry.</summary>
/// <param name="Id">The foothold id as it appears in the map.</param>
/// <param name="Layer">The foothold layer it belongs to.</param>
/// <param name="Left">Leftmost X of the segment.</param>
/// <param name="Right">Rightmost X of the segment.</param>
/// <param name="Top">Topmost Y of the segment.</param>
/// <param name="Bottom">Bottommost Y of the segment.</param>
public sealed record MapWalkSegmentDto(string Id, string Layer, int Left, int Right, int Top, int Bottom);

/// <summary>A rectangle, as the walk measured it.</summary>
/// <param name="Left">Smallest X.</param>
/// <param name="Top">Smallest Y.</param>
/// <param name="Right">Largest X.</param>
/// <param name="Bottom">Largest Y.</param>
public sealed record MapSimulateBoundsDto(int Left, int Top, int Right, int Bottom);

/// <summary>Request body for the simulation endpoint.</summary>
public sealed class MapSimulateRequest
{
    /// <summary>The open map address.</summary>
    public string Path { get; set; } = "";

    /// <summary>Where to start, or null for the map's left edge.</summary>
    public int? StartX { get; set; }

    /// <summary>Where to start vertically, or null to land on the topmost surface.</summary>
    public int? StartY { get; set; }

    /// <summary>
    /// Which way to walk: 1 for right, -1 for left, 0 for neither.
    /// </summary>
    /// <remarks>
    /// Without it the character only ever walks right, so half of
    /// <see cref="CharacterPhysics.Walk"/> — the branch that steps to
    /// <c>FH.Prev</c> at <c>X &lt;= FH.X1</c> — is unreachable and therefore
    /// unverified. Measured: a walk started at the map's right edge still went
    /// right and reported running out of ground.
    /// </remarks>
    public int Direction { get; set; } = 1;

    /// <summary>Jump every N steps, 0 to never jump.</summary>
    /// <remarks>
    /// The client jumps on LeftAlt (MapleCharacter.cs:533). There is no
    /// "press" here — a walk is a script, not a player — so the closest honest
    /// equivalent is a cadence, and it has to be explicit: a map that needs a
    /// jump to be crossed is a different finding from one that does not.
    /// </remarks>
    public int JumpEvery { get; set; }

    /// <summary>Drop through the current foothold once, at step N. 0 to never.</summary>
    public int DropAtStep { get; set; }

    /// <summary>
    /// Climb: -1 up, 1 down, 0 never. Same reasoning as JumpEvery — a walk is a
    /// script, and the client reads a held key.
    /// </summary>
    public int Climb { get; set; }
}