using System.Globalization;
using MapleBench.Models;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

// NOTE ON CITATIONS. The `.cs`/`.js` citations below name MapleNecrocer
// (C:\MUWORK\GAME\_necrocer_src\MapleNecrocer) -- this port's own source,
// which has no counterpart in the authoritative reference: clean-client
// (C:\MUWORK\GAME\MPSH\clean-client\src) contains zero `.cs` files. Where a
// MapleNecrocer citation disagrees with clean-client, clean-client wins -- see
// pm/PARITY.md, "The C++ wins". These were not re-audited for content; some
// record a port's deliberate divergence.

namespace MapleBench.Services.Simulate;

/// <summary>
/// The stat totals one piece of equipment declares, read off its own
/// <c>info</c> node.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node names are not invented — both references agree on them.</b>
/// C++ <c>Data/EquipData.cpp:43-57</c> reads
/// <c>character/&lt;category&gt;/&lt;0000xxxxx&gt;.img/info</c> for
/// <c>incSTR incDEX incINT incLUK incPAD incPDD incMAD incMDD incMHP incMMP incACC
/// incEVA incHANDS incSPEED incJUMP</c>; the C# reference's
/// <c>Gear.CreateFromNode</c> (<c>WzComparerR2.Common/CharaSim/Gear.cs:840-854</c>)
/// takes the <c>info</c> child's NAME as the <c>GearPropType</c> enum member.
/// </para>
///
/// <para>
/// <b>MEASURED on this archive, 2026-10-16</b> — 70 items sampled across each of
/// the 12 stat-bearing categories (840 images, read only through
/// <c>/api/inspect</c>):
/// </para>
/// <list type="table">
/// <item><term>incPDD 326</term><description>incDEX 134, incMDD 129, incSTR 123,
/// incLUK 105, incINT 70, incPAD 69, incMMP 62, incEVA 42, incMHP 39, incACC 31,
/// incSpeed 27, incMAD 18, incJump 15</description></item>
/// </list>
/// <para>
/// Three things that measurement caught and a name-based lookup would not have:
/// </para>
/// <list type="number">
/// <item><b>The archive spells speed and jump <c>incSpeed</c>/<c>incJump</c></b>,
/// not the C++ reference's <c>incSPEED</c>/<c>incJUMP</c>. 27 and 15 occurrences
/// respectively — an exact-case read returns 0 for both.</item>
/// <item><b><c>Glove/01082143.img</c> spells it <c>incLUk</c></b> — a lower-case
/// k. One item, and an exact-case reader silently drops its LUK. Every read here
/// is therefore OrdinalIgnoreCase.</item>
/// <item><b><c>Weapon/01372038.img</c> carries <c>incRMAI</c> and
/// <c>incRMAL</c></b>, which are in neither reference's stat table. A reader that
/// throws on an unknown node fails on that wand; a reader that maps them to a stat
/// would be inventing one. They are read and discarded.</item>
/// </list>
/// <para>
/// <b>Absent on all 840:</b> every <c>r</c>-suffixed rate form
/// (<c>incSTRr incPADr incMHPr …</c>), every merged key
/// (<c>incPAD_incMAD incPDD_incMDD incACC_incEVA incMHP_incMMP incAllStat …</c>),
/// and <c>incHANDS</c>. The rate forms matter to the reference's
/// <c>CharaProp.GetSum()</c> (<c>CharaProp.cs:82-92</c>), which multiplies by
/// <c>(100 + rate) / 100</c>; with no rate node anywhere it is always ×1 here, so
/// this service does not carry a percentage path it can never take.
/// <c>incHANDS</c> is absent because BOTH references derive hand count from the
/// item id (<c>Weapon.cpp</c> / <c>Gear.GetGearType</c>), not from a node.
/// </para>
/// <para>
/// <b>Why the archive at all.</b> The C++ client does NOT read these nodes for
/// damage: <c>Net/Handlers/Helpers/ItemParser.cpp:87-90</c> takes fifteen shorts in
/// <c>EquipStat</c> enum order straight out of the SERVER PACKET, and
/// <c>EquipData::defstats</c> is used only by tooltips and quality banding
/// (<c>EquipTooltip.cpp:356,407,593</c>, <c>EquipQuality.cpp:34</c>). This project
/// has no server packet, so the archive is the only source — and it is the source
/// the reference's own TOOLTIP uses, which makes these the same numbers a WzComparer
/// user reads off a piece of gear. Declared here rather than left as a silent
/// substitution.
/// </para>
/// </remarks>
public sealed class EquipStatsService
{
    /// <summary>
    /// The archive's <c>info</c> child name for each stat, and where it lands.
    /// </summary>
    /// <remarks>
    /// Keys are COMPARED CASE-INSENSITIVELY and the spelling below is the one
    /// measured most often on this archive; see the class note for the two
    /// spellings that differ from it.
    /// </remarks>
    private static readonly (string Node, EquipStatId Stat)[] NodeMap =
    {
        ("incSTR", EquipStatId.Str),
        ("incDEX", EquipStatId.Dex),
        ("incINT", EquipStatId.Int),
        ("incLUK", EquipStatId.Luk),
        ("incMHP", EquipStatId.Hp),
        ("incMMP", EquipStatId.Mp),
        ("incPAD", EquipStatId.Watk),
        ("incMAD", EquipStatId.Magic),
        ("incPDD", EquipStatId.Wdef),
        ("incMDD", EquipStatId.Mdef),
        ("incACC", EquipStatId.Acc),
        ("incEVA", EquipStatId.Avoid),
        ("incSpeed", EquipStatId.Speed),
        ("incJump", EquipStatId.Jump),
    };

    private readonly WzSessionService _session;

    public EquipStatsService(WzSessionService session) => _session = session;

    /// <summary>
    /// The totals for one item id, in any of the widths an equip slot carries it.
    /// </summary>
    /// <param name="itemId">
    /// The item id as given. Every width is tried because the equip slot and the
    /// frame already carry the eight-digit form ("01302000") while the session's
    /// own box hands over the bare id ("1302000"); parsing and reformatting one
    /// into the other is how <see cref="CombatService.WeaponAttackSpeed"/> used
    /// to return 0 for every weapon in the game.
    /// </param>
    public EquipStatsDto Describe(string? itemId)
    {
        var dto = new EquipStatsDto { ItemId = (itemId ?? "").Trim() };
        if (dto.ItemId.Length == 0) return dto;
        if (!int.TryParse(dto.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return dto;

        dto.NumericId = numeric;
        // C++ Weapon::by_value is `id / 10000` -- the GearType ordinal the weapon
        // damage table is switched on. 1302000 -> 130.
        dto.WeaponCode = numeric / 10000;
        dto.WeaponType = GearTypeName(dto.WeaponCode);

        lock (_session.Gate)
        {
            foreach (string candidate in ImageNames(numeric, dto.ItemId))
            {
                foreach (OpenFile file in _session.SelectRoleSources("Character", null))
                {
                    foreach (string prefix in CategoryPrefixes)
                    {
                        // NOT `is WzImage`. MEASURED 2026-10-16: under NX a
                        // Character.nx equip resolves as a SubProperty, not an
                        // Image — `f1/Weapon/01302000.img` reports type
                        // "SubProperty" — while a Mob.nx image at the archive root
                        // does resolve as WzImage. Testing for WzImage here made
                        // every single item report found:false.
                        if (_session.TryResolve(WzPath.Join(file.Id, prefix, candidate + ".img")) is not { } image)
                            continue;

                        dto.Found = true;
                        dto.Category = prefix.Length == 0 ? "" : prefix;
                        ReadInfo(dto, image);
                        return dto;
                    }
                }
            }
        }
        return dto;
    }

    /// <summary>
    /// The folders a Character.nx equip can live in, and "" for the root.
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-10-16: the open Character.nx holds 17 folders — Accessory,
    /// Afterimage, Cap, Cape, Coat, Dragon, Face, Glove, Hair, Longcoat, Pants,
    /// PetEquip, Ring, Shield, Shoes, TamingMob, Weapon. The reference maps an id
    /// prefix to exactly one of these (<c>ItemData::get_eqcategory</c>,
    /// ItemData.cpp:124-151); this tries them all instead, for the same reason
    /// <c>WeaponAttackSpeed</c> tries every id width — a wrong guess is an item
    /// that silently reports no stats, which is indistinguishable from a starter
    /// piece. Hair/Face/Afterimage/Dragon carry no inc* nodes at all and are
    /// listed only so the set is the measured one.
    /// </remarks>
    private static readonly string[] CategoryPrefixes =
    {
        "", "Cap", "Accessory", "Coat", "Longcoat", "Pants", "Shoes", "Glove",
        "Shield", "Cape", "Ring", "Weapon", "PetEquip", "TamingMob",
    };

    private void ReadInfo(EquipStatsDto dto, WzObject image)
    {
        WzObject? info = null;
        foreach (WzObject child in Children(image))
        {
            if (string.Equals(child.Name, "info", StringComparison.OrdinalIgnoreCase))
            {
                info = child;
                break;
            }
        }
        if (info is null) return;

        foreach (WzObject leaf in Children(info))
        {
            // islot/vslot are Strings, so they are read BEFORE the numeric guard.
            // Reading them after it is a silent loss: the guard `continue`s on any
            // leaf that is not a number, and a String leaf never is.
            if (leaf.Name.Equals("islot", StringComparison.OrdinalIgnoreCase)
                && leaf is WzStringProperty slot)
                dto.Slot = slot.Value;
            if (leaf.Name.Equals("vslot", StringComparison.OrdinalIgnoreCase)
                && leaf is WzStringProperty vslot)
                dto.VisualSlot = vslot.Value;

            long? value = ReadLong(leaf);
            if (value is not { } number) continue;

            foreach ((string node, EquipStatId stat) in NodeMap)
            {
                if (!leaf.Name.Equals(node, StringComparison.OrdinalIgnoreCase)) continue;

                // incLUk on Glove/01082143.img lands here exactly like incLUK, which
                // is the whole reason the comparison above is case-insensitive.
                if (stat == EquipStatId.Luk && !string.Equals(leaf.Name, "incLUK", StringComparison.Ordinal))
                    dto.SpellingAnomalies.Add(leaf.Name);
                dto.Set(stat, (int)Math.Clamp(number, int.MinValue, int.MaxValue));
                break;
            }

            if (leaf.Name.Equals("attackSpeed", StringComparison.OrdinalIgnoreCase))
                dto.AttackSpeed = (int)Math.Clamp(number, int.MinValue, int.MaxValue);
            if (leaf.Name.Equals("reqLevel", StringComparison.OrdinalIgnoreCase))
                dto.RequiredLevel = (int)Math.Clamp(number, int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The reference's GearType names for the codes that matter here.</summary>
    private static string? GearTypeName(int code) => code switch
    {
        130 => "ohSword",
        131 => "ohAxe",
        132 => "ohBlunt",
        133 => "dagger",
        137 => "wand",
        138 => "staff",
        139 => "barehand",
        140 => "thSword",
        141 => "thAxe",
        142 => "thBlunt",
        143 => "spear",
        144 => "polearm",
        145 => "bow",
        146 => "crossbow",
        147 => "throwingGlove",
        148 => "knuckle",
        149 => "gun",
        152 => "dualBow",
        153 => "handCannon",
        _ => null,
    };

    /// <summary>
    /// The eight-digit form the archive uses first, then the caller's own string.
    /// </summary>
    private static IEnumerable<string> ImageNames(int numeric, string asGiven)
    {
        var names = new List<string>();
        foreach (string candidate in new[]
                 {
                     asGiven,
                     numeric.ToString("D8", CultureInfo.InvariantCulture),
                     numeric.ToString("D7", CultureInfo.InvariantCulture),
                     numeric.ToString(CultureInfo.InvariantCulture),
                 })
        {
            if (!names.Contains(candidate)) names.Add(candidate);
        }
        return names;
    }

    private IEnumerable<WzObject> Children(WzObject node)
    {
        try { return _session.EnumerateChildren(node).ToList(); }
        catch { return Array.Empty<WzObject>(); }
    }

    private static long? ReadLong(WzObject leaf) => leaf switch
    {
        WzIntProperty i => i.Value,
        WzShortProperty s => s.Value,
        WzLongProperty l => l.Value,
        WzStringProperty s when long.TryParse(
            s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
        _ => null,
    };
}

/// <summary>The fifteen stat totals of <c>EquipStat.h</c>, plus what else the item said.</summary>
public enum EquipStatId
{
    Str, Dex, Int, Luk, Hp, Mp, Watk, Magic, Wdef, Mdef, Acc, Avoid, Speed, Jump
}

/// <summary>What one piece of equipment declares.</summary>
public sealed class EquipStatsDto
{
    /// <summary>The id as asked for, and the eight-digit form the archive keys on.</summary>
    public string ItemId { get; set; } = "";

    /// <summary>0 when the id is not a number.</summary>
    public int NumericId { get; set; }

    /// <summary>False when no open Character archive holds this item.</summary>
    public bool Found { get; set; }

    /// <summary>The archive folder it was found under — "" at the root.</summary>
    public string Category { get; set; } = "";

    /// <summary><c>id / 10000</c>: the GearType ordinal, 0 for a non-weapon.</summary>
    public int WeaponCode { get; set; }

    /// <summary>The reference's name for <see cref="WeaponCode"/>, when it has one.</summary>
    public string? WeaponType { get; set; }

    /// <summary><c>info/attackSpeed</c>: 1-4 fast, 5-6 normal, 7-9 slow.</summary>
    public int AttackSpeed { get; set; }

    public int RequiredLevel { get; set; }

    /// <summary><c>info/islot</c>, the inventory slot ("Cp", "Wp", "Ma" …).</summary>
    public string Slot { get; set; } = "";

    /// <summary><c>info/vslot</c>, the visible slots a two-handed piece covers.</summary>
    public string VisualSlot { get; set; } = "";

    public int Str { get; private set; }
    public int Dex { get; private set; }
    public int Int { get; private set; }
    public int Luk { get; private set; }
    public int Hp { get; private set; }
    public int Mp { get; private set; }
    public int Watk { get; private set; }
    public int Magic { get; private set; }
    public int Wdef { get; private set; }
    public int Mdef { get; private set; }
    public int Acc { get; private set; }
    public int Avoid { get; private set; }
    public int Speed { get; private set; }
    public int Jump { get; private set; }

    /// <summary>
    /// Stat nodes whose spelling differs from the common one, e.g. the
    /// <c>incLUk</c> on <c>Glove/01082143.img</c>. Reported rather than hidden:
    /// a reader that silently accepts a typo is one typo away from silently
    /// accepting a wrong number.
    /// </summary>
    public List<string> SpellingAnomalies { get; } = new();

    internal void Set(EquipStatId stat, int value)
    {
        switch (stat)
        {
            case EquipStatId.Str: Str = value; break;
            case EquipStatId.Dex: Dex = value; break;
            case EquipStatId.Int: Int = value; break;
            case EquipStatId.Luk: Luk = value; break;
            case EquipStatId.Hp: Hp = value; break;
            case EquipStatId.Mp: Mp = value; break;
            case EquipStatId.Watk: Watk = value; break;
            case EquipStatId.Magic: Magic = value; break;
            case EquipStatId.Wdef: Wdef = value; break;
            case EquipStatId.Mdef: Mdef = value; break;
            case EquipStatId.Acc: Acc = value; break;
            case EquipStatId.Avoid: Avoid = value; break;
            case EquipStatId.Speed: Speed = value; break;
            case EquipStatId.Jump: Jump = value; break;
        }
    }

    /// <summary>False when the item declares none of the fourteen stat nodes.</summary>
    public bool AnyStat =>
        (Str | Dex | Int | Luk | Hp | Mp | Watk | Magic | Wdef | Mdef | Acc | Avoid | Speed | Jump) != 0;
}
