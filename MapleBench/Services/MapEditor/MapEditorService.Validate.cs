using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Checks a map against the rules a generator has to honour, and says plainly
/// which ones it breaks.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here was found by reading real maps (see
/// <c>docs/MAP-STRUCTURE.md</c>), and every one is easy to break by accident.
/// The foothold chain in particular: a map with a severed chain looks correct
/// on screen and strands the character at runtime. This is the executable copy
/// of those rules.
/// </para>
///
/// <para>
/// Read-only. A check that could also edit would be a check nobody trusts, and
/// the caller has real repair paths in the scene operations.
/// </para>
/// </remarks>
public sealed partial class MapEditorService
{
    /// <summary>
    /// Validates an open map. Every problem is reported, not just the first:
    /// repairing a generated map one error per round trip does not converge.
    /// </summary>
    public MapValidateDto Validate(string path)
    {
        lock (_gate)
        {
            EditorDoc doc = RequireWritable(path);
            MapDocument map = doc.Doc;
            MapValidateDto result = new() { Path = path };

            // ---- 1. Link stub: the geometry is in another map. -----------
            // Measured: 3 of 10 sampled maps were stubs. A generator that
            // treats the stub as an empty map writes a shell with no ground.
            WzNode? info = map.Find("info");
            WzNode? linkNode = info?.Child("link");
            string link = linkNode?.AsText() ?? linkNode?.Name ?? "";
            if (link.Length > 0)
            {
                result.IsLinkStub = true;
                result.Link = link;
                result.Warnings.Add(
                    $"This map is a link stub pointing at '{link}'. Its geometry is not here. " +
                    "Open the target to read or edit the shape.");
            }

            // ---- 2. Foothold chain integrity. ---------------------------
            // The hard rule. prev/next is the character's walking path; a break
            // strands it. Every sampled map that shipped had zero breaks, so a
            // break here is a defect and not a style choice.
            IReadOnlyList<MapFoothold> footholds = map.Footholds;
            result.Footholds = footholds.Count;

            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (MapFoothold fh in footholds)
                ids.Add(fh.Id);

            foreach (MapFoothold fh in footholds)
            {
                foreach (string end in new[] { "prev", "next" })
                {
                    string value = fh.Node.TextAt(end);
                    if (string.IsNullOrEmpty(value) || value == "0")
                        continue;
                    if (!ids.Contains(value))
                    {
                        result.BrokenChains++;
                        if (result.BrokenChains <= 20)
                        {
                            result.Errors.Add(
                                $"Foothold {fh.Id}.{end} points at '{value}', which does not exist. " +
                                "A severed chain strands the character at runtime.");
                        }
                    }
                }
            }

            // ---- 2b. A link that exists but does not touch. -------------
            //
            // Ids are unique only inside a layer/group, so "the id exists" proves
            // nothing about whether the character can step onto it. The client
            // never asks: CreateFootholds resolves the link while building the
            // chain and stores it as an object reference.
            foreach (MapFoothold fh in footholds)
            {
                if (!TryEndpoints(fh, out int ax1, out int ay1, out int ax2, out int ay2))
                    continue;

                foreach (string end in new[] { "prev", "next" })
                {
                    string value = fh.Node.TextAt(end);
                    if (string.IsNullOrEmpty(value) || value == "0")
                        continue;

                    bool meets = false;
                    foreach (MapFoothold cand in footholds)
                    {
                        if (!string.Equals(cand.Id, value, StringComparison.Ordinal))
                            continue;
                        if (TryEndpoints(cand, out int bx1, out int by1, out int bx2, out int by2)
                            && Touches(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2))
                        {
                            meets = true;
                            break;
                        }
                    }

                    if (meets)
                        continue;

                    result.BrokenChains++;
                    if (result.BrokenChains <= 20)
                    {
                        result.Errors.Add(
                            $"Foothold {fh.Id}.{end} points at '{value}', which exists but does not "
                            + $"touch this foothold (ends at x={ax1}..x={ax2}). Ids repeat across "
                            + "layers, so the link resolves to geometry the character cannot step onto.");
                    }
                }
            }

            // ---- 3. Ground with nothing to stand on. --------------------
            if (footholds.Count == 0 && map.Layers.Count > 0)
            {
                result.Warnings.Add(
                    "This map has layers but no footholds. The character cannot stand on anything.");
            }

            // ---- 4. Every spawn needs a foothold. -----------------------
            // fh is how a spawn gets its height. Measured: every sampled life
            // carried one; without it the spawn floats or snaps to nothing.
            foreach (MapLife life in map.Life)
            {
                result.Life++;
                string fh = life.Node.TextAt("fh");
                if (string.IsNullOrEmpty(fh) || fh == "0")
                {
                    result.LifeWithoutFoothold++;
                    if (result.LifeWithoutFoothold <= 10)
                    {
                        result.Errors.Add(
                            $"Life '{life.Id}' has no foothold. It needs fh = <foothold id> " +
                            "to take its height.");
                    }
                }
                else if (ids.Count > 0 && !ids.Contains(fh))
                {
                    result.LifeMissingTarget++;
                    if (result.LifeMissingTarget <= 10)
                    {
                        result.Errors.Add(
                            $"Life '{life.Id}' points at foothold '{fh}', which does not exist.");
                    }
                }
            }

            // ---- 5. Tiles must carry u/no or they draw nothing. ---------
            foreach (MapLayer layer in map.Layers)
            {
                string? ts = layer.Node.TextAt("tS");
                if (!string.IsNullOrEmpty(ts))
                    result.TileSets.Add(ts);

                foreach (MapTile tile in layer.Tiles)
                {
                    result.Tiles++;
                    // Read through the node rather than a typed property: MapTile
                    // exposes geometry only, and u/no are what decide whether
                    // the entry can resolve to art at all.
                    string u = tile.Node.TextAt("u");
                    string no = tile.Node.TextAt("no");
                    if (u.Length == 0 || no.Length == 0)
                    {
                        result.UnresolvableRefs++;
                        if (result.UnresolvableRefs <= 10)
                        {
                            result.Errors.Add(
                                $"A tile in layer {layer.Index} is missing u or no, so nothing can be " +
                                "drawn where it sits.");
                        }
                    }
                }

                foreach (MapObject obj in layer.Objects)
                {
                    result.Objects++;
                    if (obj.Node.TextAt("oS").Length == 0)
                    {
                        result.UnresolvableRefs++;
                        if (result.UnresolvableRefs <= 10)
                        {
                            result.Errors.Add(
                                "An object has no oS (object set), so it cannot resolve to art.");
                        }
                    }
                }
            }

            // ---- 6. Ladders, portals, reactors: counted, not judged. -----
            // They have no invariant the others lack, but a caller generating
            // a map wants the census without walking the document itself.
            result.Ladders = map.LadderRopes.Count;
            result.Portals = map.Portals.Count;
            result.Reactors = map.Reactors.Count;
            result.Backgrounds = map.Backgrounds.Count;

            // ---- 7. Bounds, because a generator needs the extent. -------
            result.Bounds = ComputeBounds(map);

            result.Ok = result.Errors.Count == 0;
            return result;
        }
    }

    /// <summary>
    /// The map's drawn extent, or null when nothing in it has coordinates —
    /// which is itself worth saying, since a map with no bounds is empty.
    /// </summary>
    private static MapBoundsDto? ComputeBounds(MapDocument map)
    {
        long? minX = null, minY = null, maxX = null, maxY = null;

        void See(long x, long y)
        {
            minX = minX is null ? x : Math.Min(minX.Value, x);
            maxX = maxX is null ? x : Math.Max(maxX.Value, x);
            minY = minY is null ? y : Math.Min(minY.Value, y);
            maxY = maxY is null ? y : Math.Max(maxY.Value, y);
        }

        foreach (MapFoothold fh in map.Footholds)
        {
            if (fh.X1 is long x1 && fh.Y1 is long y1) See(x1, y1);
            if (fh.X2 is long x2 && fh.Y2 is long y2) See(x2, y2);
        }

        if (minX is null)
            return null;

        return new MapBoundsDto
        {
            Left = (int)minX.Value,
            Top = (int)minY!.Value,
            Right = (int)maxX!.Value,
            Bottom = (int)maxY!.Value,
        };
    }

    /// <summary>Read a foothold's two endpoints, or false when either is missing.</summary>
    private static bool TryEndpoints(MapFoothold fh, out int x1, out int y1, out int x2, out int y2)
    {
        if (fh.X1 is not { } a || fh.Y1 is not { } b || fh.X2 is not { } c || fh.Y2 is not { } d)
        {
            x1 = y1 = x2 = y2 = 0;
            return false;
        }
        x1 = (int)a; y1 = (int)b; x2 = (int)c; y2 = (int)d;
        return true;
    }

    /// <summary>
    /// Whether two footholds share an endpoint, the way linked ones do.
    /// </summary>
    /// <remarks>
    /// The same test <c>CharacterPhysics.FindLinked</c> uses, kept here so the
    /// validator and the walker cannot disagree about what "linked" means.
    /// </remarks>
    private static bool Touches(int ax1, int ay1, int ax2, int ay2,
                               int bx1, int by1, int bx2, int by2) =>
        (bx1 == ax2 && by1 == ay2) || (bx1 == ax1 && by1 == ay1)
        || (bx2 == ax2 && by2 == ay2) || (bx2 == ax1 && by2 == ay1);
}
