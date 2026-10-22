using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Where a character standing at a point would be held up, and what they would
/// walk into.
/// </summary>
/// <remarks>
/// <para>
/// This is the geometry HANDOVER §3.1 asks for: after an agent lays tiles, nothing
/// tells it whether a character can actually stand on them. <c>/validate</c> proves
/// the <c>prev</c>/<c>next</c> chain is unbroken, and a broken chain still looks
/// like a correct map on screen — the difference between "looks playable" and "is
/// playable" is the whole project.
/// </para>
///
/// <para>
/// The model borrows MapleNecrocer's <c>Client/Footholds.cs</c> (MIT, Elem8100)
/// for the shape of the queries — a foothold is two endpoints plus prev/next, and
/// "what is under this point" is answered by interpolating Y along every foothold
/// whose X span contains the point and taking the smallest result at or below the
/// character. That is reimplemented here rather than copied because Necrocer
/// keys its footholds by id in a dictionary, and the measured data says ids are
/// NOT unique: 69 maps carry a duplicate. Keying by id silently drops one of the
/// two. Nothing in this file keys anything by foothold id.
/// </para>
///
/// <para>
/// Read-only. It never writes to the map; it answers questions about a
/// <see cref="MapDocument"/> that somebody else owns.
/// </para>
/// </remarks>
/// <summary>
/// The map's own rectangle, read from <c>info</c> the way MapleNecrocer does.
/// </summary>
/// <remarks>
/// <para>
/// <c>MapleMap.cs:171-196</c>, both branches:
/// </para>
/// <code>
/// if (Map.Info.ContainsKey("VRLeft"))
/// {
///     Map.Left   = Map.Info["VRLeft"];
///     Map.Bottom = Map.Info["VRBottom"] + 15;
///     Map.Top    = Map.Info["VRTop"];
///     Map.Right  = Map.Info["VRRight"];
/// }
/// else
/// {
///     Map.Left   = FootholdTree.MinX1.First();
///     Map.Right  = FootholdTree.MaxX2.Last();
///     Map.Bottom = -Map.Info["centerY"] + Map.Info["MapHeight"] - 55;
///     Map.Top    = -Map.Info["centerY"] + 50;
/// }
/// </code>
/// <para>
/// Neither branch is "the extents of every foothold". Deriving all four from
/// foothold extremes — which is what this did until now — disagrees with the
/// client on every shipping map that carries VR. Measured on
/// <c>f1/Map/Map2/200090410.img</c> (VRLeft -420, VRTop -1320, VRRight 380,
/// VRBottom -245):
/// </para>
/// <code>
/// 邊界     foothold 極值   客戶端 VR      差
/// Left        -322        -420        +98
/// Right        149         380       -231
/// Top        -1166       -1320       +154
/// Bottom      -443        -230       -213
/// </code>
/// <para>
/// That is why <c>reachedEdge</c> — "the character crossed the whole map" — was
/// being reported from the wrong rectangle.
/// </para>
/// </remarks>
internal static class MapBounds
{
    /// <summary>Resolve the map rectangle, or null when <paramref name="info"/> has no basis for it.</summary>
    internal static (int, int, int, int)? Resolve(
        MapModel.WzNode? info,
        (int Left, int Top, int Right, int Bottom) footholdExtent)
    {
        (int, int, int, int)? rect = ResolveDeclared(info, footholdExtent);
        if (rect is not { } r || info is null)
            return rect;

        // The declared rectangle is the CAMERA's framing, not the world's
        // extent, and the two disagree on real maps.
        //
        // Measured on f1/Map/Map9/922230001.img: footholds span y -539..511 and
        // the spawn is at y=511, standing on foothold id=2 — a real, walkable
        // ledge. `-centerY + MapHeight - 55` resolves to 470, so the character
        // spawned 41px BELOW `bounds.bottom` and was immediately "out of the
        // map" while standing on solid ground. Nothing about the map was wrong;
        // the rectangle just did not cover it.
        //
        // Every consumer here — the fall clamp, the camera clamp, the spawn
        // validity test — treats these bounds as the world. So they must
        // contain the footholds. Widen, never narrow.
        return (
            Math.Min(r.Item1, footholdExtent.Left),
            Math.Min(r.Item2, footholdExtent.Top),
            Math.Max(r.Item3, footholdExtent.Right),
            Math.Max(r.Item4, footholdExtent.Bottom));
    }

    /// <summary>The rectangle the reference computes, unmodified.</summary>
    private static (int, int, int, int)? ResolveDeclared(
        MapModel.WzNode? info,
        (int Left, int Top, int Right, int Bottom) footholdExtent)
    {
        if (info is null)
            return null;

        // Branch one: the map declares its own rectangle.
        if (info.Child("VRLeft")?.AsInteger() is { } vrLeft)
        {
            int vrTop = (int)(info.Child("VRTop")?.AsInteger() ?? vrLeft);
            int vrRight = (int)(info.Child("VRRight")?.AsInteger() ?? vrLeft);
            int vrBottom = (int)(info.Child("VRBottom")?.AsInteger() ?? vrTop);
            return ((int)vrLeft, vrTop, vrRight, vrBottom + 15);
        }

        // Branch two: no VR, so top and bottom come from the minimap framing.
        // Left and right still come from the footholds, exactly as the client does.
        if (info.Child("centerY")?.AsInteger() is { } centerY
            && info.Child("MapHeight")?.AsInteger() is { } mapHeight)
        {
            int top = (int)-centerY + 50;
            int bottom = (int)-centerY + (int)mapHeight - 55;
            return (footholdExtent.Left, top, footholdExtent.Right, bottom);
        }

        // No VR and no minimap framing: the foothold extents are all there is.
        return footholdExtent;
    }
}

public sealed class FootholdPhysics
{
    private readonly List<Foot> _footholds;
    private readonly List<Ladder> _ladders = [];
    private readonly List<Portal> _portals = [];

    public FootholdPhysics(MapDocument map)
    {
        ArgumentNullException.ThrowIfNull(map);
        MapInfo = map.Find("info");

        _footholds = new List<Foot>(map.Footholds.Count);
        foreach (MapFoothold fh in map.Footholds)
        {
            // A foothold with no endpoints cannot hold anyone up. One missing
            // coordinate is malformed rather than absent, and guessing the other
            // would invent a ledge that is not in the file.
            //
            // WZ stores these as long; the arithmetic here is pixel geometry and
            // a map that needed more than int range would not be a map.
            if (fh.X1 is not { } rawX1 || fh.Y1 is not { } rawY1
                || fh.X2 is not { } rawX2 || fh.Y2 is not { } rawY2)
            {
                Unusable++;
                continue;
            }
            if (rawX1 is < int.MinValue or > int.MaxValue || rawX2 is < int.MinValue or > int.MaxValue
                || rawY1 is < int.MinValue or > int.MaxValue || rawY2 is < int.MinValue or > int.MaxValue)
            {
                Unusable++;
                continue;
            }

            int x1 = (int)rawX1, y1 = (int)rawY1, x2 = (int)rawX2, y2 = (int)rawY2;

            // Z is the LAYER INDEX, not a subnode. Footholds.cs:247:
            //
            //     foreach (WzNode Iter in Layer)      // the layer node
            //     {
            //         FH.Z = Iter.Text.ToInt();       // its NAME, as a number
            //         ...
            //     }
            //
            // Reading IntegerAt("z") finds nothing — foothold records carry no
            // such child — so Z was always 0 and every cross-layer wall test
            // passed vacuously: a wall on another floor was treated as being on
            // the character's own floor. The client's comparison at
            // MapleCharacter.cs:857 is `BelowFH.Z == WallFH.Z`, which only means
            // "same floor" if Z is the floor.
            _footholds.Add(new Foot(
                x1, y1, x2, y2,
                fh.Id, fh.Prev ?? 0, fh.Next ?? 0,
                fh.Layer, fh.Group,
                fh.CantThrough ?? 0, fh.ForbidFallDown ?? 0,
                ParseLayerIndex(fh.Layer)));
        }

        Bounds = ComputeBounds();
        BuildLadders(map);
        BuildPortals(map);
    }

    /// <summary>The map's <c>info</c> node, or null when the map has none.</summary>
    /// <remarks>
    /// Kept because the map rectangle lives here, not in the footholds — see
    /// <see cref="MapBounds"/>. Null is normal: a generated map may not carry it.
    /// </remarks>
    public WzNode? MapInfo { get; }

    /// <summary>The layer node's name as a number, the way the client reads it.</summary>
    /// <remarks>
    /// Footholds.cs:247 is <c>FH.Z = Iter.Text.ToInt()</c> where <c>Iter</c>
    /// walks the layer nodes. The layer is usually named "0".."7" but a map is
    /// free to name them otherwise; anything that is not a number falls back to
    /// 0, which is what the client would do with the same text.
    /// </remarks>
    private static long ParseLayerIndex(string layer) =>
        long.TryParse(layer, out long v) ? v : 0;

    /// <summary>
    /// Reads <c>ladderRope/&lt;index&gt;</c> — the shape MapleNecrocer reads from
    /// the same node (<c>Client/LadderRopes.cs</c>).
    /// </summary>
    private void BuildLadders(MapDocument map)
    {
        WzNode? root = map.Find("ladderRope");
        if (root is null)
            return;

        foreach (WzNode entry in root.Children)
        {
            // Read the integers straight off the node. `root` is the
            // `ladderRope` node itself, so its children are raw nodes rather
            // than the typed MapLadderRope view, and a missing coordinate makes
            // the entry unusable rather than the whole map.
            if (entry.IntegerAt("x") is not { } x
                || entry.IntegerAt("y1") is not { } y1
                || entry.IntegerAt("y2") is not { } y2)
                continue;

            _ladders.Add(new Ladder(
                (int)x, (int)y1, (int)y2,
                entry.IntegerAt("l") ?? 0,
                entry.IntegerAt("uf") ?? 0,
                entry.IntegerAt("page") ?? 0));
        }
    }

    /// <summary>Every foothold that had usable endpoints.</summary>
    public IReadOnlyList<Foot> Footholds => _footholds;

    /// <summary>
    /// Footholds the source carried that this could not read as a surface.
    /// Reported rather than absorbed: a count that quietly disagrees with
    /// <c>/validate</c>'s foothold count is how a broken map looks fine.
    /// </summary>
    public int Unusable { get; private set; }

    /// <summary>The walkable extent, or null when the map has no usable foothold.</summary>
    public (int Left, int Top, int Right, int Bottom)? Bounds { get; }

    /// <summary>
    /// Every ground height at <paramref name="x"/> that lies inside
    /// <c>[<paramref name="top"/>, <paramref name="bottom"/>]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A direct port of the reference's
    /// <c>FootholdTree::get_foothold_range</c>
    /// (<c>Gameplay/Physics/FootholdTree.cpp:609-639</c>, itself labelled
    /// "official CWvsPhysicalSpace2D::GetFootholdRange"), which is what a ground
    /// decal samples to find the floor it should lie on:
    /// </para>
    /// <code>
    /// std::vector&lt;int16_t&gt; get_foothold_range(int16_t x, int16_t top, int16_t bottom) const
    /// {
    ///     std::vector&lt;int16_t&gt; result;
    ///     auto range = footholdsbyx.equal_range(x);
    ///     for (auto iter = range.first; iter != range.second; ++iter)
    ///     {
    ///         const Foothold&amp; fh = footholds.at(iter-&gt;second);
    ///         if (fh.is_wall()) continue;
    ///         int16_t ground = static_cast&lt;int16_t&gt;(std::round(fh.ground_below(x)));
    ///         if (ground &gt;= top &amp;&amp; ground &lt;= bottom)
    ///         {
    ///             bool duplicate = false;
    ///             for (int16_t existing : result)
    ///                 if (std::abs(existing - ground) &lt;= 8) { duplicate = true; break; }
    ///             if (!duplicate) result.push_back(ground);
    ///         }
    ///     }
    ///     return result;
    /// }
    /// </code>
    /// <para>
    /// Three parts of that are load-bearing and all three are kept:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Walls are skipped</b> — <c>is_wall()</c> is a foothold standing on
    /// its end, and <see cref="Foot.SurfaceAt"/> already returns null for one, so
    /// asking it is the same test written once.</item>
    /// <item><b>The 8px dedupe</b> — two footholds meeting at a seam have the same
    /// height at their shared vertex, and without it a stair-step decal draws one
    /// copy of every tile twice.</item>
    /// <item><b><c>round</c>, not truncate</b> — the surface is interpolated
    /// (<see cref="Foot.SurfaceAt"/>) and rounded once here, matching
    /// <c>std::round</c> rather than a cast.</item>
    /// </list>
    /// <para>
    /// <see cref="Foot.SurfaceAt"/> is the one interpolation already in this
    /// file, so this does not re-derive the surface from the endpoints: a second
    /// formula for "how high is the ground at x" is a second answer, and the
    /// walking physics and the decals would disagree on a slope.
    /// </para>
    /// <para>
    /// The reference keeps its footholds in a <c>footholdsbyx</c> multimap indexed
    /// by every X each foothold spans (<c>FootholdTree.cpp:85-89</c>) and this
    /// file has a flat list, so this is a linear scan. It runs a handful of times
    /// per cast and never on a frame — <see cref="FindBelow"/> already accepts the
    /// same cost, and an index would be paid for on every walk step to speed up
    /// something that happens when a skill is cast.
    /// </para>
    /// </remarks>
    public List<int> GetFootholdRange(int x, int top, int bottom)
    {
        var result = new List<int>();
        foreach (Foot f in _footholds)
        {
            int? surface = f.SurfaceAt(x);
            if (surface is not { } ground)
                continue;
            if (ground < top || ground > bottom)
                continue;

            bool duplicate = false;
            foreach (int existing in result)
            {
                if (Math.Abs(existing - ground) <= 8)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                result.Add(ground);
        }
        return result;
    }

    /// <summary>
    /// One foothold as geometry: two endpoints and the links the data carries.
    /// </summary>
    public readonly record struct Foot(
        int X1, int Y1, int X2, int Y2,
        string Id, long Prev, long Next,
        string Layer, string Group,
        long CantThrough, long ForbidFallDown,
        long Z)
    {
        /// <summary>Leftmost X, whichever endpoint is left.</summary>
        public int Left => Math.Min(X1, X2);

        /// <summary>Rightmost X.</summary>
        public int Right => Math.Max(X1, X2);

        /// <summary>Topmost Y.</summary>
        public int Top => Math.Min(Y1, Y2);

        /// <summary>Bottommost Y.</summary>
        public int Bottom => Math.Max(Y1, Y2);

        /// <summary>
        /// A foothold standing on its end rather than lying down. Measured: the
        /// client ships these, and a character that runs into one stops.
        /// </summary>
        public bool IsWall => X1 == X2;

        /// <summary>
        /// The Y of this foothold directly below <paramref name="x"/>, or null
        /// when x is outside its span. Linear interpolation between the endpoints,
        /// which is what makes a slope walkable rather than a staircase.
        /// </summary>
        public int? SurfaceAt(int x)
        {
            if (IsWall)
                return null;
            if (x < Left || x > Right)
                return null;
            return SurfaceAtExact(x) is { } exact
                ? (int)Math.Round(exact, MidpointRounding.AwayFromZero)
                : null;
        }

        /// <summary>
        /// The surface height at an X, without rounding to an integer.
        /// </summary>
        /// <remarks>
        /// Rounding is what made a slope impossible to walk. A 16px-wide foothold
        /// dropping 3px has a true surface that moves less than a pixel per
        /// step, and rounding each sample quantises it into a staircase with
        /// steps up to a pixel tall. The character snapped to one step, the next
        /// sample reported a surface under it, and it fell -- measured: 154px of
        /// an 1,116px map before it dropped out.
        ///
        /// Comparing in float against the exact height removes the staircase
        /// entirely. Rounded values are still what the DTOs and the drawing use,
        /// because a pixel IS the output unit.
        /// </remarks>
        public double? SurfaceAtExact(int x)
        {
            if (IsWall)
                return null;
            if (x < Left || x > Right)
                return null;
            if (X1 == X2)
                return Y1;
            double t = (double)(x - X1) / (X2 - X1);
            return Y1 + t * (Y2 - Y1);
        }

        /// <summary>
        /// The reference's INTEGER ground at an X: <c>y1 + (x - x1) * vdelta / hdelta</c>,
        /// with the division truncating toward zero.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a different number from <see cref="SurfaceAtExact"/>, and the
        /// reference uses it in exactly one place: <c>get_fhid_underneath</c>
        /// (<c>FootholdTree.cpp:561-563</c>).
        /// </para>
        /// <code>
        /// const int32_t xdelta = static_cast&lt;int32_t&gt;(x) - fh.x1();
        /// const int32_t ydelta = static_cast&lt;int32_t&gt;(fh.y2()) - fh.y1();
        /// const int32_t ground = fh.y1() + static_cast&lt;int64_t&gt;(xdelta) * ydelta / fh.hdelta();
        /// </code>
        /// <para>
        /// Two details are load-bearing. The product is taken in 64-bit and the
        /// division is INTEGER, so it truncates TOWARD ZERO, and toward zero is
        /// not toward negative infinity: for a negative quotient the two differ by
        /// one, and the reference's answer is the truncated one. And the whole
        /// expression is done in integers here for the same reason: a double
        /// division would round first and truncate second, which is a different
        /// number at the scale a foothold is measured in.
        /// </para>
        /// </remarks>
        public int GroundInteger(int x)
        {
            if (X1 == X2)
            {
                // A wall. The reference never reaches this -- get_fhid_underneath
                // skips every x1 >= x2 before it computes anything -- but a caller
                // must not divide by zero.
                return Y1;
            }

            long xdelta = (long)x - X1;
            long ydelta = (long)Y2 - Y1;
            long hdelta = (long)X2 - X1;
            return (int)(Y1 + xdelta * ydelta / hdelta);
        }
    }

    /// <summary>
    /// The foothold a character at (x, y) would land on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="falling"/> is what makes this correct, and getting it
    /// wrong is not a subtle difference — measured: without it the character
    /// walks through the air at y=154 while "standing" on a foothold whose
    /// surface is y=-3019, and still reports having crossed the map.
    /// </para>
    /// <para>
    /// Falling (true): the character is above and coming down, so it lands on
    /// the FIRST surface it meets below it — the one with the LARGEST y that is
    /// still >= y. Anything further down is behind that surface, not underfoot.
    /// </para>
    /// <para>
    /// Grounded (false): the character is walking, so it needs the surface it is
    /// already on — the SMALLEST y that is >= y. A steep slope can put a
    /// neighbouring foothold hundreds of pixels lower, and "first one met" would
    /// snap the character off the ledge it is standing on.
    /// </para>
    /// <para>
    /// This is the same shape as MapleNecrocer's FindBelow except that it also
    /// rejects candidates on the wrong side, which its report notes it does not
    /// do — there the answer depends on document order rather than geometry.
    /// </para>
    /// </remarks>
    /// <param name="diagnostics">
    /// Optional. When supplied it is filled with the reason each candidate was
    /// rejected, counted by reason. The parameter is observation-only: passing
    /// null runs no recording statement, and passing one runs recording
    /// statements alongside the same tests.
    ///
    /// NOTE: it is NOT true that passing null makes this method behave as it did
    /// before the `x1 &gt;= x2` test and the 0.5px tolerance were added. Those
    /// two changed the answer and are documented below; this parameter changed
    /// neither. An earlier version of this comment claimed otherwise and was
    /// wrong — the claim was written without diffing against HEAD.
    /// </param>
    public Foot? FindBelow(int x, int y, bool falling = true, FindBelowDiagnostics? diagnostics = null)
    {
        Foot? best = null;
        double bestValue = 0;
        diagnostics?.Begin(_footholds.Count, falling);

        // Compared in FLOAT against the exact interpolated height, so a gentle slope
        // is not quantised into a staircase -- see SurfaceAtExact.
        //
        // The tolerance is NOT here: it is applied below, once, and differs per
        // branch. An earlier version of this comment said "no tolerance" and
        // described a step-query model the caller no longer uses; it sat directly
        // above a 0.5px tolerance and contradicted it. The reasoning that
        // survives is the one at the comparison itself.
        foreach (Foot f in _footholds)
        {
            // GroundEd query only. FootholdTree.cpp:556-559, quoted from
            // C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Physics\FootholdTree.cpp:
            //
            //   // CWvsPhysicalSpace2D::GetFootholdUnderneath/Above only considers
            //   // footholds with x1 < x2; reverse-oriented segments are not supports.
            //   if (fh.x1() >= fh.x2())
            //       continue;
            //
            // It is NOT redundant with the range test below, and that is the part
            // worth stating: Foot.Left/Right are Math.Min/Math.Max of the two
            // endpoints (:359/:362), so a reverse foothold is NORMALISED into a
            // perfectly ordinary [x2, x1] span and sails straight through
            // `x < f.Left || x > f.Right`. The reference has the same normalising
            // pair -- Foothold::l()/r() are m_horizontal.smaller()/greater()
            // (Foothold.cpp:57-65) -- which is exactly why get_fhid_below, the
            // OTHER function, needed no such filter, while get_fhid_underneath,
            // which tests the RAW x1()/x2(), does.
            //
            // Left out of the falling branch on purpose: get_fhid_below uses the
            // normalised l()/r() and keeps reverse segments, and matching one
            // reference function here and the other there is the point.
            //
            // MEASURED 2026-10-21 over all 5,366 maps of Map.nx (333,884 footholds):
            // 432 reverse (x1 > x2) across 130 maps, and 100,673 zero-length
            // (x1 == x2) across 2,314 maps. `>=` excludes the zero-length ones too,
            // which is correct -- Foothold.h:69 calls x1 == x2 a wall -- and it is
            // also the larger half of the change. That is a measurement, not an
            // estimate, and it is the reason this is guarded rather than assumed.
            if (!falling && f.X1 >= f.X2)
            {
                if (diagnostics is not null) { diagnostics.ExcludedByNotForwardOriented++; }
                continue;
            }

            if (x < f.Left || x > f.Right)
            {
                if (diagnostics is not null) { diagnostics.ExcludedByRange++; }
                continue;
            }
            if (f.SurfaceAtExact(x) is not { } sy)
            {
                if (diagnostics is not null) { diagnostics.ExcludedByNoSurface++; }
                continue;
            }

            // The comparison, and which branch gets which NUMBER.
            //
            // The two reference functions differ in three ways, and the branch
            // carries all three:
            //
            //   falling  -> get_fhid_below        (FootholdTree.cpp:493-499)
            //                double ycomp = fh.ground_below(fx);
            //                if (comp >= ycomp && ycomp >= (fy - 0.5))
            //                a DOUBLE, and a 0.5px tolerance.
            //
            //   grounded -> get_fhid_underneath    (FootholdTree.cpp:561-564)
            //                const int32_t ground = fh.y1() + (int64_t)xdelta * ydelta / fh.hdelta();
            //                if (ground >= y && ground < best_y)
            //                an INTEGER, truncated toward zero, and no tolerance.
            //
            // Both of them end at the surface NEAREST the feet, which in this
            // file's vocabulary is the HIGHEST surface (the smallest y):
            // get_fhid_below walks down from the top and get_fhid_underneath
            // walks up from the feet, and both stop at the one the character is
            // on. That is why the selection below is the same comparison on both
            // branches, and why the branch changes the NUMBER rather than the
            // direction.
            //
            // The grounded branch used to keep the other end of the range -- the
            // LOWEST surface, the deepest one below the feet -- which is neither
            // reference function. On a ledge 1200px above a floor it named the
            // floor. MEASURED 2026-10-22 by the position-accuracy judge: 67 of
            // 82 probe points disagreed with get_fhid_underneath, and at x=2905
            // the port named foothold 79 at y=-145 where the reference names
            // foothold 10 at y=-1345.
            //
            // This used to be 1px on the grounded branch and 0 on the falling
            // one, which is the INVERSE of the reference, and the 1px had no
            // citation at all: its comment described a sub-step rounding gap that
            // the model below no longer has, since the caller now holds a foothold
            // and never asks what is under it.
            double candidate = falling ? sy : f.GroundInteger(x);
            if (candidate < y - (falling ? 0.5 : 0))
            {
                if (diagnostics is not null) { diagnostics.ExcludedByTolerance++; }
                continue;
            }

            if (diagnostics is not null) { diagnostics.Eligible++; }

            if (best is null)
            {
                best = f;
                bestValue = candidate;
                continue;
            }

            if (candidate < bestValue)
            {
                best = f;
                bestValue = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Why each candidate foothold was not chosen, counted by reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Diagnostic only, and deliberately produced by <see cref="FindBelow"/>
    /// itself rather than by a second filter in a diagnostic endpoint. A second
    /// filter is a second answer: it would agree with <see cref="FindBelow"/> on
    /// the day it was written and drift from it the first time somebody edits a
    /// condition, which is the shape of failure this project has paid for before.
    /// </para>
    /// <para>
    /// There are four counters because the scan has four <c>continue</c>s. A
    /// zero-length foothold (<c>X1 == X2</c>) is rejected by
    /// <see cref="ExcludedByNotForwardOriented"/> on the grounded branch, and by
    /// <see cref="ExcludedByNoSurface"/> on the falling branch — because
    /// <see cref="Foot.IsWall"/> makes <see cref="Foot.SurfaceAtExact"/> return
    /// null. Reporting the latter as "out of range" would be false: the point is
    /// standing exactly on that foothold.
    /// </para>
    /// </remarks>
    public sealed class FindBelowDiagnostics
    {
        /// <summary>How many footholds the scan looked at.</summary>
        public int Scanned { get; private set; }

        /// <summary>Which branch was asked for. Decides which filters ran at all.</summary>
        public bool Falling { get; private set; }

        /// <summary><c>x</c> outside the foothold's normalised [Left, Right] span.</summary>
        public int ExcludedByRange { get; internal set; }

        /// <summary>
        /// Rejected by the <c>x1 &gt;= x2</c> test. Named for the PREDICATE, not
        /// for a geometric concept, because it covers two populations that mean
        /// different things: reverse segments (<c>X1 &gt; X2</c>, 432 in the corpus)
        /// and zero-length ones (<c>X1 == X2</c>, 100,673).
        ///
        /// <para>
        /// Which counter a given foothold lands in DEPENDS ON THE BRANCH, and
        /// that is not a defect in the reporting — it is what the two reference
        /// functions do. A zero-length foothold hits this counter on the grounded
        /// branch, where the test runs; on the falling branch the test does not
        /// run at all and the same foothold is caught by
        /// <see cref="ExcludedByNoSurface"/> instead, because
        /// <see cref="Foot.IsWall"/> makes <see cref="Foot.SurfaceAtExact"/>
        /// return null. So this counter answers "how many did <c>x1 &gt;= x2</c>
        /// reject", not "how many are not forward-oriented in some wider sense".
        /// </para>
        /// </summary>
        public int ExcludedByNotForwardOriented { get; internal set; }

        /// <summary>
        /// No surface at this x. In practice a wall (<c>X1 == X2</c>), because
        /// that is the only case <see cref="Foot.SurfaceAtExact"/> returns null
        /// for. Never fold this into <see cref="ExcludedByRange"/>: a
        /// zero-length foothold's own x is exactly on it, not outside it.
        /// </summary>
        public int ExcludedByNoSurface { get; internal set; }

        /// <summary>Surface too far above the feet: <c>sy &lt; y - 0.5</c> falling, the reference's integer ground <c>&lt; y</c> grounded.</summary>
        public int ExcludedByTolerance { get; internal set; }

        /// <summary>Footholds that passed every filter. More than one means the branch picked between them.</summary>
        public int Eligible { get; internal set; }

        internal void Begin(int scanned, bool falling)
        {
            Scanned = scanned;
            Falling = falling;
            ExcludedByRange = 0;
            ExcludedByNotForwardOriented = 0;
            ExcludedByNoSurface = 0;
            ExcludedByTolerance = 0;
            Eligible = 0;
        }
    }

    /// <summary>
    /// A vertical foothold the body would meet moving right from
    /// <paramref name="x"/>, or null if the way is clear.
    /// </summary>
    /// <remarks>
    /// Direction-aware, because a wall to the right is an obstacle and the same
    /// wall to the left is not. Only walls whose vertical span actually covers y
    /// block the way — a tall foothold far above the character is not in their
    /// path, and treating it as one is what makes a naive implementation report
    /// every map as sealed.
    /// </remarks>
    /// <summary>
    /// A wall to the left of the character, or null.
    /// </summary>
    /// <remarks>
    /// The probe point and the layer test are both from the reference. Walking
    /// right, MapleCharacter.cs:855-860:
    /// <code>
    /// WallFH = FootholdTree.Instance.FindWallL(new Vector2(X - 4, Y - 4));
    /// if ((WallFH != null) &amp;&amp; (X &gt;= WallFH.X1) &amp;&amp; (BelowFH.Z == WallFH.Z))
    /// { X = WallFH.X1 - 1; SpeedR = 0; }
    /// </code>
    ///
    /// Two details are load-bearing and were both missing:
    ///
    /// <para><b>The probe is (X - 4, Y - 4), not (X, Y).</b> A character standing
    /// at y=216 on a floor whose wall ends at y=215 clears a wall test that
    /// queries y=216, and walks straight through it into the void. Offsetting
    /// the probe up by 4 catches it. This was a real, reproduced fall-through
    /// on f1/Map/Map0/000030000.img.</para>
    ///
    /// <para><b>The layer must match.</b> Without <c>z == wallZ</c>, a wall on
    /// another floor blocks the character on this one — which is why a wall
    /// query used to report nearly every map as sealed.</para>
    /// </remarks>
    public Foot? FindWall(int x, int y, bool movingRight = true, long? z = null)
    {
        // NOTE: x and y arrive already offset by the caller. CharacterPhysics.cs
        // :398-399 does `wallX = X ± 4, wallY = Y - WallProbeUp(4)`, which is the
        // reference's `FindWallL(new Vector2(X - 4, Y - 4))`
        // (MapleCharacter.cs:855). Do not subtract again in here — doing so put
        // the probe at Y-8 and missed walls the reference catches.
        Foot? best = null;
        foreach (Foot f in _footholds)
        {
            if (!f.IsWall)
                continue;
            // MapleCharacter.cs:856 `BelowFH.Z == WallFH.Z` — same floor only.
            // Passing null means "any layer", which is the old behaviour; the
            // caller should always pass the lead ground's Z.
            if (z is { } want && f.Z != want)
                continue;
            if (f.Y1 > y || f.Y2 < y)
                continue;
            // The X comparison is the WHOLE of this function and it was inverted.
            //
            // Footholds.cs:130, the rightward query:
            //     if (F.IsWall() && F.X1 <= P.X && F.Y1 <= P.Y && F.Y2 >= P.Y)
            //         ... keep, then keep the one with the LARGEST X1
            // Footholds.cs:162, the leftward query:
            //     if (F.IsWall() && F.X1 >= P.X && ...)
            //         ... keep, then keep the one with the SMALLEST X1
            //
            // Both ask "which wall am I touching RIGHT NOW" -- the wall is AT or
            // ALREADY PAST the probe column, and the tie-break picks the nearest one.
            // That is why the caller can then say `X = WallFH.X1 + 1` and mean
            // "put me on the near side of the wall I just walked into".
            //
            // This port asked the opposite question: "which wall comes next anywhere
            // on the map" -- keep everything at or ahead of the probe, take the
            // nearest. With no upper bound on the distance.
            //
            // MEASURED, walking right on f4/Map/Map1/100000004.img from the spawn
            // portal at x=1710, probe x=1716, y=-29, layer Z=5:
            //
            //     wall = id 92 at x=3189        <- the map's RIGHT BOUNDARY wall
            //
            // 1473 pixels away, across the whole map. Then Walk's guard
            // `if (moveLeft > 0 && _x <= w2.X1)` is true for any wall ahead of the
            // character however far, so it ran `X = 3189 + 1`, and the handover that
            // follows snapped X back to the next foothold's X1 -- 1800. Net +90px
            // per tick, 36x MaxWalkSpeed, changing foothold every single tick:
            //
            //     x 1710 -> 1800 -> 1890 -> 1980 -> 2070 -> 2160 -> 2250, then
            //     3169 (= bounds.Right - EdgeMargin), airborne, out of the map.
            //
            // 194 of 200 ticks airborne. That is the "walks around and randomly
            // flies off" report, and it is this one comparison.
            if (movingRight ? f.X1 > x : f.X1 < x)
                continue;
            if (best is null
                || (movingRight ? f.X1 > best.Value.X1 : f.X1 < best.Value.X1))
                best = f;
        }

        return best;
    }

    /// <summary>
    /// A ladder or rope the character can climb.
    /// </summary>
    /// <remarks>
    /// The shape and the detection box are MapleNecrocer's, from
    /// <c>Client/LadderRopes.cs</c>:
    /// <code>
    /// if ((P.X &gt; L.X - 10) &amp;&amp; (P.X &lt; L.X + 10) &amp;&amp; (P.Y &lt; L.Y2 + 12) &amp;&amp; (P.Y &gt; L.Y1 - 12))
    /// </code>
    /// A ±10 X box and ±12 Y padding. The X box is the point: a ladder is a
    /// column, and the character has to be within arm's reach of it.
    /// </remarks>
    public readonly record struct Ladder(
        int X, int Y1, int Y2, long IsLadder, long UpperFoothold, long Page)
    {
        /// <summary>Topmost Y, whichever endpoint is up (Y grows downward).</summary>
        public int Top => Math.Min(Y1, Y2);

        /// <summary>Bottommost Y.</summary>
        public int Bottom => Math.Max(Y1, Y2);

        /// <summary>Whether a character may step off the top onto a foothold.</summary>
        public bool CanExitTop => UpperFoothold != 0;
    }

    /// <summary>
    /// A portal: the way off this map onto another.
/// </summary>
/// <remarks>
/// The shape and the detection box are MapleNecrocer's (<c>MapPortal.Find</c>):
/// <code>
/// if ((P.X &gt; Portal.X - 15) &amp;&amp; (P.X &lt; Portal.X + 15) &amp;&amp; (P.Y &lt; Portal.Y + 12) &amp;&amp; (P.Y &gt; Portal.Y - 12))
/// </code>
/// Wider in X than a ladder, because a portal is something you walk INTO rather
/// than a column you climb.
/// </remarks>
public readonly record struct Portal(
        string Name, string ToName, long ToMap, long Type, int X, int Y)
    {
        /// <summary>Whether standing here takes the character to ANOTHER MAP.</summary>
        /// <remarks>
        /// MapleCharacter.cs:596-600 — the cross-map trigger:
        ///
        ///     if (Keyboard.KeyDown(Input.Up) &amp;&amp; (JumpState == jsNone) &amp;&amp; (OnPortal) ...)
        ///     {
        ///         if (Portal.ToMap != "999999999")
        ///         {
        ///             if ((Portal.Type == 2) || (Portal.Type == 1))
        ///         ...
        ///
        /// So the exit set is {1, 2}, and type 10 is NOT in it. A type 10 portal
        /// takes a different branch entirely (MapleCharacter.cs:613-629): it
        /// finds a portal of the same name on this map and drops the character
        /// from 100px above it — same map, no transition. Reporting those as an
        /// exit told callers this map continues elsewhere when it only continues
        /// inside itself.
        ///
        /// The client's filter is only ToMap != 999999999; ToMap != 0 is mine and
        /// stays, because 0 is not a map id in this dataset.
        /// </remarks>
        public bool IsExit => Type is 1 or 2 && ToMap != 999999999 && ToMap != 0;

        /// <summary>
        /// A same-map door: the client drops the character onto the matching
        /// portal on this map (MapleCharacter.cs:613-629).
        /// </summary>
        public bool IsSameMapLink => Type == 10;

        /// <summary>A spawn point (<c>pt == 0</c>); the client respawns here.</summary>
        public bool IsSpawn => Type == 0;
    }

    /// <summary>Every portal on the map.</summary>
    public IReadOnlyList<Portal> Portals => _portals;

    /// <summary>The portal the character is standing in, or null.</summary>
    public Portal? FindPortal(int x, int y)
    {
        foreach (Portal p in _portals)
        {
            if (x > p.X - PortalReachX && x < p.X + PortalReachX
                && y < p.Y + PortalReachY && y > p.Y - PortalReachY)
                return p;
        }
        return null;
    }

    private const int PortalReachX = 15;
    private const int PortalReachY = 12;

    /// <summary>Every ladder and rope on the map.</summary>
    public IReadOnlyList<Ladder> Ladders => _ladders;

    /// <summary>
    /// The ladder the character is within reach of at a point, or null.
    /// </summary>
    /// <remarks>
    /// Linear scan, same reasoning as the foothold queries: a map holds tens of
    /// ladders, not thousands, and the box test has to be exact.
    /// </remarks>
    public Ladder? FindLadder(int x, int y)
    {
        foreach (Ladder l in _ladders)
        {
            if (x > l.X - LadderReachX && x < l.X + LadderReachX
                && y < l.Bottom + LadderReachY && y > l.Top - LadderReachY)
                return l;
        }
        return null;
    }

    /// <summary>Half the X reach, as the client measures it (L.X ± 10).</summary>
    private const int LadderReachX = 10;

    /// <summary>Vertical padding above and below, as the client measures it (±12).</summary>
    private const int LadderReachY = 12;

    /// <summary>
    /// Reads <c>portal/&lt;name&gt;</c> — pn, tn, tm, pt, x, y.
    /// </summary>
    private void BuildPortals(MapDocument map)
    {
        WzNode? root = map.Find("portal");
        if (root is null)
            return;

        foreach (WzNode entry in root.Children)
        {
            _portals.Add(new Portal(
                entry.TextAt("pn") ?? entry.Name,
                entry.TextAt("tn") ?? "",
                entry.IntegerAt("tm") ?? 0,
                entry.IntegerAt("pt") ?? 0,
                (int)(entry.IntegerAt("x") ?? 0),
                (int)(entry.IntegerAt("y") ?? 0)));
        }
    }

    private (int, int, int, int)? ComputeBounds()
    {
        if (_footholds.Count == 0)
            return null;

        int fhLeft = int.MaxValue, fhTop = int.MaxValue;
        int fhRight = int.MinValue, fhBottom = int.MinValue;
        foreach (Foot f in _footholds)
        {
            if (f.Left < fhLeft) fhLeft = f.Left;
            if (f.Top < fhTop) fhTop = f.Top;
            if (f.Right > fhRight) fhRight = f.Right;
            if (f.Bottom > fhBottom) fhBottom = f.Bottom;
        }
        return MapBounds.Resolve(MapInfo, (fhLeft, fhTop, fhRight, fhBottom));
    }
}