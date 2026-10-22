using System.Collections.Concurrent;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;
using MapleBench.Services.Nx;

namespace MapleBench.Services;

/// <summary>
/// The avatar layer tables the client keeps in <c>Base.nx</c>: <c>zmap.img</c>,
/// which ranks a layer name, and <c>smap.img</c>, which says which body areas
/// a layer claims.
/// </summary>
/// <remarks>
/// <para>
/// Transliterated from the reference, which is the only place these numbers are
/// defined: <c>AvatarLayers.h/.cpp</c> (MPSH
/// <c>clean-client/src/Character/Look/</c>). Three rules, no others.
/// </para>
///
/// <para><b>z</b> — <c>AvatarLayers::z_of(zname, fallback)</c>: the value
/// <c>Base/zmap.img</c> carries for that layer name. An unknown name keeps the
/// caller's fallback rather than inventing a rank.</para>
///
/// <para><b>slots</b> — <c>AvatarLayers::claimed(zname, vslot)</c>. Two masks
/// meet: <c>smap.img[zname]</c> is the set of two-letter codes the LAYER claims,
/// and <c>vslot</c> is the set the OWNER has any right to. The intersection is
/// what the sprite actually holds. Both are two-letter codes packed into bits by
/// <c>Tables::mask</c>, which numbers a code the first time it is seen and caps
/// the table at 64 — so the numbering is a property of the archive, and the same
/// codes get the same bits every run.</para>
///
/// <para><b>priority</b> — <c>AvatarLayers::priority_of(islot)</c>: the smallest
/// zmap value among the codes in the owner's <c>islot</c>. LOWER IS STRONGER, and
/// that is the whole of the arbitration: where two owners claim one slot, the
/// smaller number keeps it.</para>
/// </remarks>
public sealed class AvatarLayerTables
{
    /// <summary>
    /// Reads both tables out of an NX archive's root. Registers nothing — the
    /// tables are per-archive, unlike the process-wide <see cref="NxLayerOrder"/>.
    /// </summary>
    public static AvatarLayerTables? FromArchive(WzFile? file)
    {
        WzDirectory? dir = file?.WzDirectory;
        if (dir is null) return null;
        return FromImages(dir.GetImageByName("zmap.img"), dir.GetImageByName("smap.img"));
    }

    public static AvatarLayerTables? FromImages(WzImage? zmap, WzImage? smap)
    {
        if (zmap is null) return null;
        var t = new AvatarLayerTables();
        WzSessionService.EnsureParsed(zmap);
        if (zmap.WzProperties is { } props)
        {
            foreach (WzImageProperty p in props)
            {
                // NX collapsed the numeric width on the way in, so every rank is a
                // WzLongProperty. Read it as a number and skip anything else: a
                // wrong rank reorders a character, a missing one keeps the
                // caller's fallback and says so.
                if (p.WzValue is not null && long.TryParse(p.WzValue.ToString(), out long v))
                    t._zmap[p.Name ?? ""] = (int)v;
            }
        }
        if (smap is not null)
        {
            WzSessionService.EnsureParsed(smap);
            if (smap.WzProperties is { } sp)
            {
                foreach (WzImageProperty p in sp)
                {
                    // The VALUE is what decides, not the CLR type of the wrapper.
                    //
                    // smap.img has 147 entries and 106 of them are slot-code strings
                    // — the data is fully present. But `p.WzValue is WzStringProperty`
                    // is never true for an NX image: NxToWzFile does build
                    // WzStringProperty wrappers, yet `WzValue` is declared `object`
                    // and returns the string INSIDE the wrapper, so the object being
                    // tested is a System.String, never the wrapper. Testing the
                    // wrapper is what made every mask zero and every priority 32767
                    // while the archive was carrying all of it.
                    //
                    // Read the value the way NxLayerOrder reads zmap: as text.
                    if (p.WzValue is string s && s.Length > 0) t._smap[p.Name ?? ""] = s;
                }
                if (t._smap.Count == 0 && sp.Count > 0)
                {
                    Dictionary<string, int> byType = new(StringComparer.Ordinal);
                    string sample = "";
                    foreach (WzImageProperty p in sp)
                    {
                        string type = p.WzValue?.GetType().Name ?? "(null WzValue)";
                        byType[type] = byType.GetValueOrDefault(type) + 1;
                        if (sample.Length == 0)
                            sample = $"{p.Name}={p.WzValue?.ToString() ?? "<null>"}";
                    }
                    t.SmapDiagnosis =
                        $"{sp.Count} entries, none carrying string text; types: " +
                        string.Join(", ", byType.Select(kv => $"{kv.Key}x{kv.Value}")) +
                        $"; first {sample}";
                }
            }
        }
        return t;
    }

    private readonly Dictionary<string, int> _zmap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _smap = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _codeIndex = new(StringComparer.Ordinal);

    public int ZCount => _zmap.Count;
    public int SmapCount => _smap.Count;

    /// <summary>The codes numbered so far, and how many. Reported, not guessed.</summary>
    public IReadOnlyDictionary<string, int> Codes => _codeIndex;

    /// <summary>
    /// The two-letter code a bit stands for, or the bit number when the code was
    /// never seen.
    /// </summary>
    /// <remarks>
    /// Bit numbers are assigned in first-seen order and are therefore only
    /// meaningful relative to the same walk, so an arbitration report that says
    /// "bit 12" instead of a code has told the reader nothing they can check.
    /// </remarks>
    public string CodeAt(int bit)
    {
        foreach ((string code, int index) in _codeIndex)
            if (index == bit) return code;
        return $"bit{bit}";
    }

    /// <summary>
    /// What smap.img's entries actually were, by CLR type and a sample value.
    /// Only populated when <see cref="SmapCount"/> is 0 while smap.img was found:
    /// that is the one case where "no slots in the archive" and "the slots were
    /// there and the reader skipped them" look identical from the outside, and
    /// only the entry types say which it was.
    /// </summary>
    public string SmapDiagnosis { get; private set; } = "";

    /// <summary>AvatarLayers::z_of.</summary>
    public int ZOf(string? zname, int fallback)
        => zname is not null && _zmap.TryGetValue(zname, out int z) ? z : fallback;

    /// <summary>Tables::mask. Two-letter codes to bits, numbered in first-seen order.</summary>
    public ulong Mask(string? codes)
    {
        if (string.IsNullOrEmpty(codes)) return 0;
        ulong result = 0;
        for (int i = 0; i + 1 < codes.Length; i += 2)
        {
            string code = codes.Substring(i, 2);
            int bit = _codeIndex.GetOrAdd(code, c =>
            {
                // ConcurrentDictionary.GetOrAdd can run the factory more than once
                // under contention, so the count is taken through the dictionary
                // itself rather than from a field that could be read twice.
                int next = _codeIndex.Count;
                return next < NumSlots ? next : -1;
            });
            if (bit >= 0 && bit < NumSlots) result |= 1UL << bit;
        }
        return result;
    }

    /// <summary>AvatarLayers::claimed — the layer's claims intersected with the owner's rights.</summary>
    public ulong Claimed(string? zname, ulong ownerSlots)
    {
        if (zname is null || !_smap.TryGetValue(zname, out string? codes)) return 0;
        return Mask(codes) & ownerSlots;
    }

    /// <summary>
    /// The same intersection as <see cref="Claimed"/>, spelled back out in the
    /// two-letter codes the archive uses.
    /// </summary>
    /// <remarks>
    /// Reported rather than reconstructed from the mask. The bit numbering is
    /// assigned in first-seen order across the whole archive, so bit 7 means one
    /// particular code only because of the order the reader happened to walk
    /// smap.img in — printing the codes is the only form a reader can check
    /// against the file.
    /// </remarks>
    public string ClaimedCodes(string? zname, string? ownerCodes)
    {
        if (zname is null || !_smap.TryGetValue(zname, out string? claims)) return "";
        if (string.IsNullOrEmpty(ownerCodes)) return "";

        // Claimed is an INTERSECTION, so the codes are filtered against the
        // owner's rights the same way the mask was, one two-letter code at a time.
        string result = "";
        for (int i = 0; i + 1 < claims.Length; i += 2)
        {
            string code = claims.Substring(i, 2);
            if (Contains(ownerCodes, code)) result += code;
        }
        return result;
    }

    private static bool Contains(string codes, string code)
    {
        for (int i = 0; i + 1 < codes.Length; i += 2)
            if (codes.Substring(i, 2) == code) return true;
        return false;
    }

    /// <summary>AvatarLayers::priority_of — the smallest zmap rank among the owner's codes.</summary>
    public int PriorityOf(string? islot)
    {
        int result = short.MaxValue;
        if (string.IsNullOrEmpty(islot)) return result;
        for (int i = 0; i + 1 < islot.Length; i += 2)
        {
            int z = ZOf(islot.Substring(i, 2), result);
            if (z < result) result = z;
        }
        return result;
    }

    /// <summary>
    /// 64 slots, CharLook.cpp:155. Not a limit this code imposes — it is the
    /// width of the mask <c>AvatarLayers.h</c> declares.
    /// </summary>
    public const int NumSlots = 64;
}