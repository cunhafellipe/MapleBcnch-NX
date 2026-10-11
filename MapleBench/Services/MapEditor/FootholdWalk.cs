using MapleBench.Services.MapModel;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Walks a character across a map and reports where it stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this proves, precisely.</b> It answers one question: starting at a
/// point, moving right one foothold at a time, does the character reach the far
/// side of the map? It does NOT simulate gravity, jumping, ladders or falling —
/// those exist in the client and are not modelled here. It is the cheapest
/// question that separates "a map that looks right" from "a map a character can
/// walk", which is the gap HANDOVER §3.1 names.
/// </para>
///
/// <para>
/// The traversal follows <c>next</c> rather than scanning for whatever is
/// nearest. Following links is what makes this a test of the DATA: a chain that
/// <c>/validate</c> calls intact but that walks into a wall shows up here as a
/// stop, not as a pass. Scanning geometrically would paper over exactly the
/// defect worth finding.
/// </para>
///
/// <para>
/// Read-only. Nothing here writes.
/// </para>
/// </remarks>
public sealed class FootholdWalk
{
    private readonly FootholdPhysics _physics;

    public FootholdWalk(FootholdPhysics physics)
    {
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
    }

    /// <summary>How far a character may walk before the simulation gives up.</summary>
    private const int MaxSteps = 200_000;

    /// <summary>
    /// Walk right from <paramref name="from"/>, following each foothold's
    /// <c>next</c> link.
    /// </summary>
    /// <param name="from">
    /// A starting point. The foothold directly below it is the first step.
    /// </param>
    /// <param name="mapRight">
    /// The rightmost walkable X, or null when unknown. A chain that ends AT the
    /// edge is correct; one that ends short of it is a dead end.
    /// </param>
    public WalkResult WalkRight(FootholdPhysics.Foot from, int? mapRight = null)
    {
        List<WalkStep> path = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        FootholdPhysics.Foot? current = from;
        int steps = 0;

        while (current is { } foot)
        {
            // Ids repeat across maps, so identity is the geometry too. A cycle in
            // the data would otherwise spin until MaxSteps without saying why.
            string key = $"{foot.Layer}/{foot.Group}/{foot.Id}/{foot.X1},{foot.Y1}->{foot.X2},{foot.Y2}";
            if (!seen.Add(key))
                return new WalkResult(false, "the chain loops back on itself", path, current, steps);

            path.Add(new WalkStep(foot.Layer, foot.Id, foot.Left, foot.Right, foot.Top, foot.Bottom));
            steps++;

            if (steps >= MaxSteps)
                return new WalkResult(false, $"walked {MaxSteps:N0} footholds without reaching the end", path, foot, steps);

            // Next is 0 or absent at the end of a chain. Whether that is a legitimate
            // end or a BROKEN one depends on the other direction: /validate
            // counts 6,744 footholds client-wide where prev does not name this
            // one back as its next — those are legal forks, not damage. What is
            // never legitimate is a next that names nothing.
            long nextId = foot.Next;
            if (nextId == 0)
            {
                // Nothing follows. A chain that ENDS at the map's edge is correct --
                // measured on 101000200, where the last foothold sits at the
                // rightmost x the map covers and names no next, which is what
                // "the ground stops here" looks like in the data.
                //
                // What is NOT correct is stopping in open ground: the character
                // arrives somewhere with more map to its right and no foothold
                // to continue on. That is the case worth reporting, and telling
                // the two apart is the whole reason this branch exists.
                //
                // `mapRight` is the walkable extent, supplied by the caller.
                bool atMapEdge = mapRight is { } edge && foot.Right >= edge;
                bool hasBefore = foot.Prev != 0 && FindById(foot.Prev, foot) is not null;
                bool deadEnd = !atMapEdge && hasBefore;
                return new WalkResult(
                    !deadEnd,
                    deadEnd
                        ? $"the chain stops at foothold {foot.Id} at x={foot.Right} while the map "
                          + $"still extends to x={mapRight}: a character walking right runs out of "
                          + "ground here, short of the edge"
                        : $"the chain ends at foothold {foot.Id}"
                          + (atMapEdge ? " at the map's right edge" : ""),
                    path, foot, steps);
            }

            FootholdPhysics.Foot? ahead = FindById(nextId, foot);
            if (ahead is null)
            {
                return new WalkResult(false,
                    $"foothold {foot.Id} points at {nextId}, which is not a foothold it can stand on "
                    + "— the chain is broken here",
                    path, foot, steps);
            }

            // A chain may legitimately double back -- measured on 240000006, where
            // the main span is one foothold and branches run both ways. Refusing
            // to walk backwards would condemn a working map, so the walk follows
            // the data and records how far it went rather than judging direction.
            // What still matters is not going in circles, which `seen` catches.

            current = ahead;
        }

        return new WalkResult(false, "there was no foothold to start from", path, null, steps);
    }

    /// <summary>
    /// The foothold named by <paramref name="id"/> that this foothold could
    /// plausibly be linking to.
    /// </summary>
    /// <remarks>
    /// Ids are NOT unique — 69 maps ship a duplicate — so this matches on the id
    /// AND on adjacency: a real link meets its neighbour at a shared endpoint.
    /// Matching on id alone would let a duplicate in another group hijack the
    /// walk, which is the exact failure the measured data warns about.
    /// </remarks>
    private FootholdPhysics.Foot? FindById(long id, FootholdPhysics.Foot from)
    {
        FootholdPhysics.Foot? byId = null;
        foreach (FootholdPhysics.Foot f in _physics.Footholds)
        {
            if (!long.TryParse(f.Id, out long fid) || fid != id)
                continue;

            byId ??= f;

            // The meeting-point test: a genuine link has one of its endpoints on
            // one of this one's. That is the invariant the data keeps on all but
            // 38 footholds out of 784,735.
            if ((f.X1 == from.X2 && f.Y1 == from.Y2) || (f.X1 == from.X1 && f.Y1 == from.Y1)
                || (f.X2 == from.X2 && f.Y2 == from.Y2) || (f.X2 == from.X1 && f.Y2 == from.Y1))
                return f;
        }

        return byId;
    }

    // StartUnder was a convenience wrapper nobody called -- the service picks its
    // own start foothold. Removed with the same reasoning as FindWall*: a public
    // method nobody uses advertises a capability this does not have.

    /// <summary>One foothold the walk crossed.</summary>
    public readonly record struct WalkStep(string Layer, string Id, int Left, int Right, int Top, int Bottom);

    /// <summary>What the walk found.</summary>
    public readonly record struct WalkResult(
        bool ReachedEnd,
        string Reason,
        IReadOnlyList<WalkStep> Path,
        FootholdPhysics.Foot? StoppedAt,
        int Steps);
}