using System.Runtime.CompilerServices;
using MapleLib.WzLib;

namespace MapleBench.Services.Nx;

/// <summary>
/// The draw order of an NX canvas's <c>z</c> node, read from the archive's own
/// <c>Base/zmap.img</c>.
/// </summary>
/// <remarks>
/// <para>
/// A canvas in NX carries a <c>z</c> child holding a <em>layer name</em>, not a number:
/// <c>stand1/0/body</c> says <c>"body"</c>, <c>stand1/0/arm</c> says <c>"armOverHair"</c>.
/// Which layer sits in front of which is a table in the data, not a rule that can be
/// derived — <c>Base/zmap.img</c> is 151 name-to-rank pairs and nothing in the corpus
/// implies the ranking. So the order is read, never guessed.
/// </para>
///
/// <para><b>Direction.</b> The reference builds its painter's list with
/// <c>Z = 100 + Owner.Z - ZMap.IndexOf(z)</c> (MapleCharacter.cs:1236) and draws the
/// list back-to-front, so a <b>larger</b> <see cref="RankOf"/> means <b>further back</b> and
/// is drawn first. <see cref="Compare"/> returns back-to-front so a caller sorting
/// ascending draws the sprite furthest behind last, which is the order it paints in.
///
/// <c>backBody</c> = 110 outranks <c>body</c> = 76, so the back of the torso is drawn
/// before the front of it. That is the intended reading, and it is the one that makes
/// <c>armOverHair</c> (25) land over <c>hair</c> (34) as its name says.</para>
///
/// <para><b>Caching.</b> The table is 151 entries read once per archive. A node walk asks
/// about every canvas it touches, so the lookup is a dictionary hit rather than a re-read
/// of a shell image. The cache is keyed by archive and holds weakly, so it never keeps a
/// closed archive alive.</para>
///
/// <para><b>An unknown layer is not an error.</b> A canvas may name a layer this table does
/// not carry. It is ranked as if it were behind everything, which is the same answer the
/// reference gives: it checks <c>ZMap.Contains(...)</c> and falls back to a fixed offset
/// (MapleChair.cs:229-232, TamingMob.cs:215-220). Ordering is presentation, and an
/// unknown layer changing where one sprite sits is not a reason to fail a read.</para>
/// </remarks>
public static class NxLayerOrder
{
    /// <summary>Sorts behind every known layer. See the remarks on an unknown layer.</summary>
    public const int UnknownRank = int.MinValue;

    private static readonly object Gate = new();
    private static Dictionary<string, int>? _ranks;

    /// <summary>
    /// Draw order for a layer, largest first. A canvas whose <c>z</c> is missing or unknown
    /// sorts last, i.e. furthest back.
    /// </summary>
    public static int Compare(string? layer, string? other)
    {
        int a = RankOf(layer);
        int b = RankOf(other);
        return b.CompareTo(a);
    }

    /// <summary>
    /// The table's rank for a layer name, or <see cref="UnknownRank"/> when no table has been
    /// loaded or this table carries no such layer.
    /// </summary>
    public static int RankOf(string? layer)
    {
        if (layer is null)
        {
            return UnknownRank;
        }

        lock (Gate)
        {
            return _ranks is not null && _ranks.TryGetValue(layer, out int rank)
                ? rank
                : UnknownRank;
        }
    }

    /// <summary>How many layers the loaded table ranks. 0 until one is registered.</summary>
    public static int Count
    {
        get { lock (Gate) { return _ranks?.Count ?? 0; } }
    }

    /// <summary>
    /// Reads <c>Base/zmap.img</c> out of an NX archive and remembers its ranking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One table per process rather than one per archive, because a layer name is a bare
    /// string that carries no archive with it: the lookup cannot ask which archive it wants.
    /// That is sound because the table is the same in every client folder — it is client
    /// data, read from one 12,768-byte <c>Base.nx</c> — and because an empty table and a
    /// loaded one differ only in ordering, never in what is read or drawn.
    /// </para>
    /// <para>
    /// Idempotent: the first archive that answers wins, and a later one cannot overwrite a
    /// table that already ranks layers. Re-registering after a close leaves the table in
    /// place, which is deliberate — it is 151 integers of client data, not a mapping.
    /// </para>
    /// </remarks>
    internal static void Register(WzImage zmapImage, WzSessionService session)
    {
        lock (Gate)
        {
            if (_ranks is { Count: > 0 })
            {
                return;
            }
        }

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (session.Gate)
        {
            WzSessionService.EnsureParsed(zmapImage);
            if (zmapImage.WzProperties is { } properties)
            {
                foreach (WzImageProperty property in properties)
                {
                    // NX collapsed the numeric width on the way in: every integer here is a
                    // WzLongProperty. Read it as a number and skip anything that is not one,
                    // because a rank is an ordering and a wrong one is worse than a missing
                    // layer (which falls back to UnknownRank and says so).
                    if (property.WzValue is not null
                        && long.TryParse(property.WzValue.ToString(), out long rank))
                    {
                        ranks[property.Name ?? string.Empty] = (int)rank;
                    }
                }
            }
        }

        lock (Gate)
        {
            _ranks ??= ranks;
        }
    }
}