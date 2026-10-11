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
public sealed class FootholdPhysics
{
    private readonly List<Foot> _footholds;
    private readonly List<Ladder> _ladders = [];
    private readonly List<Portal> _portals = [];

    public FootholdPhysics(MapDocument map)
    {
        ArgumentNullException.ThrowIfNull(map);

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

            // Read straight off the node: MapFoothold does not expose `z`, and the
                // layer test in CharacterPhysics needs it -- a wall on another
                // floor is scenery, not an obstacle (MapleNecrocer compares Z at
                // MapleCharacter.cs:857).
                _footholds.Add(new Foot(
                x1, y1, x2, y2,
                fh.Id, fh.Prev ?? 0, fh.Next ?? 0,
                fh.Layer, fh.Group,
                fh.CantThrough ?? 0, fh.ForbidFallDown ?? 0,
                fh.Node.IntegerAt("z") ?? 0));
        }

        Bounds = ComputeBounds();
        BuildLadders(map);
        BuildPortals(map);
    }

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
    public Foot? FindBelow(int x, int y, bool falling = true)
    {
        Foot? best = null;
        double bestSurface = 0;

        // Compared in FLOAT against the exact interpolated height, so a gentle slope
        // is not quantised into a staircase -- see SurfaceAtExact.
        //
        // No tolerance. The version that needed one was the step-query model,
        // where the character asked "what is under me" and the answer had to
        // absorb a rounding gap that did not exist in the geometry. The model
        // that replaced it holds a foothold and never asks, so the question is
        // back to being exact: a surface strictly below the feet, or nothing.
        foreach (Foot f in _footholds)
        {
            if (x < f.Left || x > f.Right)
                continue;
            if (f.SurfaceAtExact(x) is not { } sy)
                continue;
            // One pixel of slack, and only for a GROUNDED query. The feet rest
            // ON a surface, but the sub-step that put them there advances in whole
            // pixels while the surface is interpolated, so they can settle a
            // fraction under it. A strict test then cannot see the ground the
            // character is standing on. Measured: after a jump it stood at y=369
            // on a surface of y=368 and was reported as having fallen out.
            if (sy < y - (falling ? 0 : 1))
                continue;

            if (best is null)
            {
                best = f;
                bestSurface = sy;
                continue;
            }

            // Falling: take the highest surface still below. Grounded: take the
            // lowest, which is the one the feet are actually resting on.
            bool better = falling ? sy < bestSurface : sy > bestSurface;
            if (better)
            {
                best = f;
                bestSurface = sy;
            }
        }

        return best;
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
    public Foot? FindWall(int x, int y, bool movingRight = true)
    {
        Foot? best = null;
        foreach (Foot f in _footholds)
        {
            if (!f.IsWall)
                continue;
            if (f.Y1 > y || f.Y2 < y)
                continue;
            if (movingRight ? f.X1 < x : f.X1 > x)
                continue;
            if (best is null
                || (movingRight ? f.X1 < best.Value.X1 : f.X1 > best.Value.X1))
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
        /// <summary>
        /// Whether standing here would actually take the character somewhere.
        /// </summary>
        /// <remarks>
        /// MapleNecrocer only builds sprites for types 2 and 10 (MapPortal.cs:73),
        /// and a portal with <c>tm == 999999999</c> is a one-way end that goes
        /// nowhere. Reporting those as exits would tell an agent a map leads
        /// somewhere it does not.
        /// </remarks>
        public bool IsExit => Type is 2 or 10 && ToMap != 999999999 && ToMap != 0;
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

        int left = int.MaxValue, top = int.MaxValue;
        int right = int.MinValue, bottom = int.MinValue;
        foreach (Foot f in _footholds)
        {
            if (f.Left < left) left = f.Left;
            if (f.Top < top) top = f.Top;
            if (f.Right > right) right = f.Right;
            if (f.Bottom > bottom) bottom = f.Bottom;
        }
        return (left, top, right, bottom);
    }
}