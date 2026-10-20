using MapleBench.Services;

namespace MapleBench.Services.Simulate;

/// <summary>One measurable defect, with the numbers that prove it.</summary>
public sealed record AnimationAnomaly(
    string Kind,
    string Frame,
    string Detail,
    int Index,
    string Severity)
{
    public override string ToString() => $"[{Severity}] {Kind} @{Frame} ({Index}): {Detail}";
}

/// <summary>The verdict for one stance of one image.</summary>
public sealed record AnimationAudit(
    string Path,
    string Subject,
    bool IsAnimation,
    int FrameCount,
    int TotalMs,
    IReadOnlyList<AnimationAnomaly> Anomalies)
{
    public bool Clean => Anomalies.Count == 0;
    public int Errors => Anomalies.Count(a => a.Severity == "error");
    public int Warnings => Anomalies.Count(a => a.Severity == "warn");
}

/// <summary>
/// Finds the things that make a stance play wrong, from the numbers
/// <see cref="AnimationService"/> already reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Own-design, declared.</b> The benchmark has no animation auditor — it is an
/// editor client and a stance is either correct or absent there, never measured.
/// So these checks are mine. What is <i>not</i> mine is any of the inputs: every
/// number compared here comes out of the archive, via
/// <see cref="AnimationService.Describe"/>, and the thresholds are the ones the
/// client's own clock implies rather than numbers chosen to make a report come
/// out clean.
/// </para>
/// <para>
/// Each rule names the failure it catches. All five were observed in this
/// project's own history, not imagined:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>missingDelay</c> — a frame with no <c>delay</c> gets the caller's default, so a
/// cycle silently runs at the wrong speed. Measured on mobs: <c>f7/0100100.img/stand/0</c>
/// carries <c>origin</c>, <c>lt</c>, <c>rb</c>, <c>head</c> and NO <c>delay</c>, while the
/// character's <c>f2/00002000.img/stand1/0</c> carries one. A single-frame stance does not
/// need one, so this is a warning, not an error.
/// </description></item>
/// <item><description>
/// <c>sizeJump</c> — a frame whose canvas differs from its neighbour by more than the
/// stance's own median change is a swapped or missing frame. This is the check that
/// catches "the sprite was swapped for the wrong pose".
/// </description></item>
/// <item><description>
/// <c>originOutsideCanvas</c> — an <c>origin</c> past the canvas edge puts the pivot in
/// empty space, so the sprite slides as it animates. Measured live: character body
/// <c>originY 31</c> against a 31px canvas is the normal anchor-at-feet case and is
/// allowed at exactly the edge; beyond it, the anchor is outside the image.
/// </description></item>
/// <item><description>
/// <c>zeroDelay</c> — a zero or negative <c>delay</c> makes the frame unadvanceable on a
/// tick-driven clock, which is how a stance freezes on one frame while the body keeps
/// moving.
/// </description></item>
/// <item><description>
/// <c>layerSetChanged</c> — a stance whose frames disagree about which layers exist.
/// Reported, never an error: measured over 86 frames of <c>00002001.img</c>, 81 carry
/// <c>arm</c> alone and 5 carry <c>armOverHair</c> alongside it, and <c>swingT2</c> has
/// no arm at all, so the set legitimately changes. It is the line a renderer that
/// ignores the swap will get wrong.
/// </description></item>
/// </list>
/// </remarks>
public sealed class AnimationAuditService
{
    private readonly AnimationService _animation;

    public AnimationAuditService(AnimationService animation) => _animation = animation;

    /// <summary>The rules this auditor applies, so a caller can show why.</summary>
    public static readonly string[] Rules =
    {
        "missingDelay", "zeroDelay", "sizeJump", "originOutsideCanvas", "layerSetChanged",
    };

    /// <summary>A stance whose median frame is this many times the largest neighbour jump.</summary>
    private const double SizeJumpFactor = 3.0;

    public AnimationAudit? Audit(string path, string? subject = null)
    {
        AnimationDto? dto;
        try
        {
            dto = _animation.Describe(path);
        }
        catch
        {
            // Describe THROWS for a node the archive does not have rather than
            // returning null -- measured: `f7/0100100.img` carries stand, move, hit1
            // and die1, and asking for `alert` answers "'alert' not found under
            // 'f7/0100100.img'". A missing stance is ABSENT, not broken, so it is
            // this caller's job to skip it. Letting it escape turned an audit of
            // seven stances into a 404 on whichever one this mob happens not to
            // have, and the whole batch read as "the auditor is broken".
            return null;
        }

        if (dto is null || dto.Frames is null || dto.Frames.Count == 0)
            return null;

        var anomalies = new List<AnimationAnomaly>();
        List<AnimationFrameDto> frames = dto.Frames;

        // ---- zeroDelay: a frame the clock cannot leave.
        foreach (AnimationFrameDto f in frames)
        {
            if (f.Delay <= 0)
                anomalies.Add(new AnimationAnomaly("zeroDelay", f.Name,
                    $"delay={f.Delay} — the frame is never left", f.Index, "error"));
        }

        // ---- missingDelay. Only meaningful in a cycle: a one-frame stance is a still
        // and a still does not need a per-frame delay. Reported as a warning, never an
        // error, because the archive is allowed to omit it -- what is NOT allowed is for
        // the omission to be invisible, and `AnimationFrameDto.DelayFromArchive` is what
        // makes it visible. Without that flag this rule cannot exist at all: the reader's
        // 100ms fallback is numerically identical to an archive that says 100.
        if (frames.Count > 1)
        {
            foreach (AnimationFrameDto f in frames)
            {
                if (!f.DelayFromArchive)
                    anomalies.Add(new AnimationAnomaly("missingDelay", f.Name,
                        $"no delay in the archive — playing at the reader's {f.Delay}ms fallback",
                        f.Index, "warn"));
            }
        }

        // ---- sizeJump, measured against the stance's own median change rather than a
        // fixed pixel count: 800x600 screen skills and 26x16 weapon stances differ by two
        // orders of magnitude, and one threshold cannot be honest for both.
        if (frames.Count > 1)
        {
            var deltas = new List<double>();
            for (int i = 1; i < frames.Count; i++)
                deltas.Add(FrameSize(frames[i]) - FrameSize(frames[i - 1]));
            double median = Median(deltas);
            for (int i = 1; i < frames.Count; i++)
            {
                double d = Math.Abs(FrameSize(frames[i]) - FrameSize(frames[i - 1]));
                double threshold = Math.Max(8.0, Math.Abs(median) * SizeJumpFactor);
                if (d > threshold)
                    anomalies.Add(new AnimationAnomaly("sizeJump", frames[i].Name,
                        $"{d:F0}px change (median {median:F0}, threshold {threshold:F0})",
                        frames[i].Index, "warn"));
            }
        }

        // ---- originOutsideCanvas, per layer. An anchor sitting exactly on the edge is
        // the normal "feet on the ground" placement and is fine; past it, the pivot is
        // in empty space and the sprite slides.
        foreach (AnimationFrameDto f in frames)
        {
            foreach (AnimationLayerDto layer in f.Layers ?? new List<AnimationLayerDto>())
            {
                if (layer.OriginX < 0 || layer.OriginY < 0
                    || layer.OriginX > layer.Width || layer.OriginY > layer.Height)
                    anomalies.Add(new AnimationAnomaly("originOutsideCanvas", f.Name,
                        $"{layer.Name} origin ({layer.OriginX},{layer.OriginY}) outside {layer.Width}x{layer.Height}",
                        f.Index, "warn"));
            }
            if (f.Width > 0 && f.OriginX > f.Width)
                anomalies.Add(new AnimationAnomaly("originOutsideCanvas", f.Name,
                    $"frame originX {f.OriginX} outside {f.Width}px", f.Index, "warn"));
        }

        // ---- layerSetChanged: a report, not an accusation.
        //
        // This started as an error and was wrong. Measured over 86 frames of
        // f2/00002001.img: 81 carry `arm` alone, 5 carry `arm` AND `armOverHair`, none
        // carry `armOverHair` alone -- so an arm-only frame is the NORM and the
        // hair-over variant is the exception, not a missing layer. And swingT2 carries
        // no arm in any of its three frames at all. A layer set that changes between
        // frames is how this art is drawn; calling it broken produced one false error
        // on swingT1 frame 2 (`armOverHair` absent) and would produce hundreds across
        // the archive.
        //
        // It stays a warning because the fact itself is worth surfacing -- a stance
        // that swaps z-layers mid-cycle is where a renderer that ignores the swap will
        // show a hand in front of the hair instead of behind it. The caller decides;
        // this service does not.
        if (frames.Count > 1)
        {
            var expected = LayerNames(frames[0]).OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            for (int i = 1; i < frames.Count; i++)
            {
                var actual = LayerNames(frames[i]).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                    anomalies.Add(new AnimationAnomaly("layerSetChanged", frames[i].Name,
                        $"[{string.Join(",", expected)}] -> [{string.Join(",", actual)}]",
                        frames[i].Index, "warn"));
            }
        }

        return new AnimationAudit(path, subject ?? path, dto.IsAnimation, frames.Count,
            dto.TotalMs, anomalies);
    }

    /// <summary>Audit every stance under one image.</summary>
    public List<AnimationAudit> AuditImage(string imagePath, IEnumerable<string>? stances = null)
    {
        var result = new List<AnimationAudit>();
        MissingStances.Clear();
        foreach (string stance in stances ?? DefaultStances)
        {
            string p = $"{imagePath}/{stance}";
            if (Audit(p, $"{imagePath}#{stance}") is { } a)
                result.Add(a);
            else
                MissingStances.Add(stance);
        }
        return result;
    }

    /// <summary>
    /// Stances the last <see cref="AuditImage"/> asked for and the image did not have.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden, because "absent" and "audited and clean" are
    /// different answers and a caller counting stances needs to tell them apart.
    /// Not thread-safe by design: one audit at a time, like the session gate.
    /// </remarks>
    public List<string> MissingStances { get; } = new();

    /// <summary>
    /// The stances to walk when a caller does not name them.
    /// </summary>
    /// <remarks>
    /// The names are the ones this v83 archive is measured to carry on mobs. The
    /// auditor tolerates their absence — <see cref="Audit"/> returns null for a stance
    /// the image does not have, and it is skipped — so a caller passing a mob without
    /// <c>move</c> gets a smaller list, not an error.
    /// </remarks>
    public static readonly string[] DefaultStances =
    {
        "stand", "move", "alert", "attack1", "hit1", "die1", "regen",
    };

    private static IEnumerable<string> LayerNames(AnimationFrameDto f) =>
        (f.Layers ?? new List<AnimationLayerDto>()).Select(l => l.Name ?? l.Layer ?? "?");

    private static double FrameSize(AnimationFrameDto f) => (double)f.Width * f.Height;

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        List<double> sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}