using MapleBench.Services.MapEditor;

namespace MapleBench.Services.MapEditor;

/// <summary>
/// Point queries against a map's footholds — one point in, one answer out.
/// </summary>
/// <remarks>
/// <para>
/// Its own file because <c>SimulateWalk</c> means "a walk trace" and this is not
/// one. A reader asking how the ground under a point is decided should not have
/// to know the answer lives at the end of the walk file, and a file named for
/// walks should not quietly also own every point query.
/// </para>
/// <para>
/// Read-only, like every other simulator read. It builds a throwaway
/// <see cref="FootholdPhysics"/> from the same <c>LoadForReading</c> the walk and
/// the probe use, so no two of them can disagree about what is in the map.
/// </para>
/// </remarks>
public sealed partial class MapEditorService
{
    /// <summary>
    /// What <see cref="FootholdPhysics.FindBelow"/> answers for one point, and
    /// what it threw away on the way there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProbeGround"/> already answers "which foothold" for both
    /// branches. What it cannot answer is the other half: a point with nothing
    /// under it produced the same empty answer whether there was no ground or the
    /// filters had thrown some away. With 100,673 zero-length footholds in the
    /// corpus — 30% of all of them — that silence is where the interesting part
    /// was.
    /// </para>
    /// <para>
    /// <paramref name="falling"/> selects between two reference functions with
    /// opposite rules, so the two branches disagree by construction. It is not
    /// decoration and it is not a filter of last resort.
    /// </para>
    /// </remarks>
    public MapFootholdAtDto FootholdAt(string path, int x, int y, bool falling)
    {
        lock (_gate)
        {
            FootholdPhysics physics = new(LoadForReading(path));
            FootholdPhysics.FindBelowDiagnostics diag = new();
            FootholdPhysics.Foot? foot = physics.FindBelow(x, y, falling, diag);

            return new MapFootholdAtDto
            {
                Path = path,
                X = x,
                Y = y,
                Falling = falling,
                Hit = foot is not null,
                FootholdId = foot?.Id,
                SurfaceY = foot?.SurfaceAtExact(x),
                SurfaceYInt = foot?.SurfaceAt(x),
                Layer = foot?.Layer,
                Group = foot?.Group,
                Z = foot?.Z,
                X1 = foot?.X1,
                Y1 = foot?.Y1,
                X2 = foot?.X2,
                Y2 = foot?.Y2,
                Scanned = diag.Scanned,
                Eligible = diag.Eligible,
                ExcludedByRange = diag.ExcludedByRange,
                ExcludedByTolerance = diag.ExcludedByTolerance,
                ExcludedByNotForwardOriented = diag.ExcludedByNotForwardOriented,
                ExcludedByNoSurface = diag.ExcludedByNoSurface,
            };
        }
    }
}

/// <summary>
/// One point, what FindBelow picked, and what it rejected on the way.
/// </summary>
/// <remarks>
/// The four exclusion counters sum to <c>Scanned - Eligible</c> on both branches.
/// If they ever do not, a filter was added without a counter — which is the one
/// way this payload can lie, so the arithmetic is published rather than asserted.
/// </remarks>
public sealed class MapFootholdAtDto
{
    public string Path { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Which branch was asked. Decides which filters ran at all.</summary>
    public bool Falling { get; set; }

    /// <summary>True when a foothold was chosen.</summary>
    public bool Hit { get; set; }

    public string? FootholdId { get; set; }

    /// <summary>Surface at x in exact float. The value FindBelow compared against.</summary>
    public double? SurfaceY { get; set; }

    /// <summary>The same surface rounded, which is what the DTOs and the drawing use.</summary>
    public int? SurfaceYInt { get; set; }

    public string? Layer { get; set; }
    public string? Group { get; set; }
    public long? Z { get; set; }
    public int? X1 { get; set; }
    public int? Y1 { get; set; }
    public int? X2 { get; set; }
    public int? Y2 { get; set; }

    public int Scanned { get; set; }

    /// <summary>Footholds that passed every filter. More than one means the branch chose between them.</summary>
    public int Eligible { get; set; }

    public int ExcludedByRange { get; set; }
    public int ExcludedByTolerance { get; set; }

    /// <summary>
    /// Rejected by the <c>x1 &gt;= x2</c> test. Named for the predicate: the
    /// same zero-length foothold lands here on the grounded branch and in
    /// <see cref="ExcludedByNoSurface"/> on the falling one, because the test
    /// only runs when grounded.
    /// </summary>
    public int ExcludedByNotForwardOriented { get; set; }

    public int ExcludedByNoSurface { get; set; }
}
