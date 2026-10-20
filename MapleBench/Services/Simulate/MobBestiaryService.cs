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
/// Everything one mob's bestiary page shows: the numbers, the trait tags, the
/// elemental profile, and the monster-book entry.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the field list comes from.</b> The reference is
/// <c>CharaSimControl/MobTooltipRenderer.cs</c> (378 lines). Its
/// <c>Render()</c> builds, in order: the title (<c>:42-49</c>), the trait string
/// (<c>:54-133</c>), the disappear line (<c>:135-139</c>), then one line each for
/// Type / Level / HP / MP / HP Recovery / MP Recovery / Physical Damage / Magic
/// Damage / Physical DEF Rate / Magic DEF Rate / Knockback / EXP / the five
/// attribute EXPs / Weapon Points / Elements (<c>:141-197</c>), and finally the
/// revive list (<c>:201-230</c>). Every one of those is read here.
/// </para>
///
/// <para>
/// <b>What the open Mob.nx actually carries — measured, not assumed.</b> A sweep
/// of <c>info</c> over 343 mobs sampled across all 2,053 (step 6, 2026-10-16,
/// read only through <c>/api/inspect</c>):
/// <list type="bullet">
/// <item>present on 343/343: <c>level</c>, <c>maxHP</c>, <c>maxMP</c>,
/// <c>PADamage</c>, <c>MADamage</c>, <c>fs</c></item>
/// <item>342/343: <c>PDDamage</c>, <c>MDDamage</c>, <c>acc</c>, <c>eva</c>,
/// <c>pushed</c></item>
/// <item>~320: <c>exp</c>, <c>summonType</c>; ~310: <c>speed</c>,
/// <c>bodyAttack</c>; ~290: <c>undead</c></item>
/// <item>sparse: <c>mobType</c> 72, <c>boss</c> 63, <c>elemAttr</c> 60,
/// <c>firstAttack</c> 40, <c>hpRecovery</c> 35, <c>mpRecovery</c> 42,
/// <c>removeAfter</c> 13, <c>revive</c> 15, <c>invincible</c> 5,
/// <c>partyBonusMob</c> 4, <c>category</c> 14</item>
/// <item>never seen: <c>icon</c> (0 of 2,053, measured 2026-10-15 — the mob
/// picker shows <c>stand/0</c> for that reason), <c>charismaEXP</c>,
/// <c>senseEXP</c>, <c>insightEXP</c>, <c>willEXP</c>, <c>craftEXP</c>,
/// <c>charmEXP</c>, <c>wp</c>, <c>fixedBodyAttackDamageR</c>,
/// <c>onlyHittedByCommonAttack</c>, <c>allyMob</c>, <c>ignoreDamage</c>,
/// <c>ignoreMovable</c>, <c>noDebuff</c>, <c>notAttack</c></item>
/// </list>
/// So the reference's attribute-EXP block, Weapon Points, and four of its trait
/// tags read nothing on this archive. They are still implemented and reported as
/// <c>null</c> — a field that exists in the reference and is absent here is a
/// gap in the DATA, not code to delete, and showing 0 would be a lie.
/// </para>
///
/// <para>
/// <b>Absent is null, not zero.</b> <c>exp</c> is missing on roughly 1 in 45 mobs
/// and <c>category</c> on 96% of them. A zero there is indistinguishable from a
/// real zero, so every numeric field is a <c>long?</c> and the UI prints a dash
/// for a null.
/// </para>
///
/// <para>
/// <b>The element letters and the resistance digits.</b> The C++ client wins on
/// conflict (studio rule 7.6): <c>Mob.cpp:2122-2128</c> maps
/// <c>F I L S H D P</c> to fire / ice / lightning / poison / holy / darkness /
/// physical, and <c>ElementDamage.h:9-15</c> (v95 <c>get_damage_adjusted_by_elemAttr</c>,
/// 0x7231e0) fixes the digits: 0 NORMAL, 1 IMMUNE, 2 RESISTANT, 3 WEAK. The C#
/// reference's <c>GetElemAttrString</c> agrees on every letter but emits its own
/// order, so the order below is the C++ one.
/// </para>
/// </remarks>
public sealed class MobBestiaryService
{
    /// <summary>The reference's trait strings, MobTooltipRenderer.cs:56-126.</summary>
    private static readonly (string Key, string Tag)[] TraitTags =
    {
        ("miniBoss", "[Mini-Boss]"),
        ("boss", "[Boss]"),
        ("undead", "[Undead]"),
        ("firstAttack", "[Auto-Aggressive]"),
        ("noBodyAttack", "[No Touch Damage]"),
        ("damagedByMob", "[Vulnerable to Monsters]"),
        ("changeableMob", "[Level Scaled]"),
        ("allyMob", "[Friendly]"),
        ("invincible", "[Invincible]"),
        ("notAttack", "[Non-Aggressive]"),
        ("ignoreDamage", "[Ignores Damage]"),
        ("ignoreMoveImpact", "[Immune to Rush]"),
        ("ignoreMovable", "[Immune to Stun/Bind]"),
        ("noDebuff", "[Immune to Debuffs]"),
        ("onlyNormalAttack", "[Damaged by Basic Attacks only]"),
        ("onlyHittedByCommonAttack", "[Hit by Basic Attacks only]"),
    };

    /// <summary>
    /// MobTooltipRenderer.cs:334-348. The open archive carries
    /// <c>info/category</c> on 14 of the 343 sampled mobs, with the values 0, 1,
    /// 7 and 8 only; the other 96% have no node and are reported as null rather
    /// than as "None", because "the archive does not say" and "the archive says
    /// category 0" are different facts.
    /// </summary>
    private static readonly Dictionary<int, string> CategoryNames = new()
    {
        [1] = "Mammal",
        [2] = "Plant",
        [3] = "Fish",
        [4] = "Reptile",
        [5] = "Spirit",
        [6] = "Devil",
        [7] = "Undead",
        [8] = "Enchanted",
    };

    /// <summary>Mob.cpp:2122-2128, in the order MobTooltipRenderer.cs:314-323 lists them.</summary>
    private static readonly (char Key, string Name)[] Elements =
    {
        ('F', "Fire"),
        ('I', "Ice"),
        ('L', "Lightning"),
        ('S', "Poison"),
        ('H', "Holy"),
        ('D', "Dark"),
        ('P', "Physical"),
    };

    /// <summary>ElementDamage.h:9-15. Index is the digit after the letter.</summary>
    private static readonly string[] ResistanceNames =
        { "neutral", "immune", "strong", "weak" };

    private readonly WzSessionService _session;
    private readonly StringPoolService _strings;

    public MobBestiaryService(WzSessionService session, StringPoolService strings)
    {
        _session = session;
        _strings = strings;
    }

    /// <summary>
    /// The bestiary page for one mob id.
    /// </summary>
    /// <param name="fileId">One open archive, or null for every open one.</param>
    /// <param name="mobId">A mob id, padded or not.</param>
    public MobBestiaryDto Describe(string? fileId, string mobId)
    {
        if (!int.TryParse(mobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            return new MobBestiaryDto { Id = mobId, Name = "", HasInfo = false };
        }

        var dto = new MobBestiaryDto
        {
            Id = numeric.ToString("D7", CultureInfo.InvariantCulture),
            Name = _strings.GetMobName(numeric) ?? "",
        };

        lock (_session.Gate)
        {
            if (TryMobImage(fileId, mobId, out WzImage? image) && image is not null)
            {
                dto.HasInfo = ReadInfo(dto, image);
            }

            dto.Book = ReadBook(numeric);
        }

        // The traits are derived, not read, so they are built last: MobTooltipRenderer
        // :55-61 shows [Mini-Boss] and [Boss] as the same `boss` node under two
        // different conditions, and a panel that printed both would be wrong.
        if (dto.Boss)
            dto.Traits.Add(dto.PartyBonusMob ? TraitTags[0].Tag : TraitTags[1].Tag);
        foreach ((string key, string tag) in TraitTags.Skip(2))
        {
            if (dto.Flag(key)) dto.Traits.Add(tag);
        }
        if (dto.RemoveAfter is { } removeAfter && removeAfter > 0)
            dto.DisappearAfterSeconds = removeAfter;

        return dto;
    }

    /// <summary>
    /// The reference reads this line unconditionally (:141) and the rest only when
    /// the number is non-zero, so every line here is nullable and the caller
    /// decides what to print.
    /// </summary>
    private bool ReadInfo(MobBestiaryDto dto, WzImage image)
    {
        WzObject? info = Child(image, "info");
        if (info is null) return false;

        dto.Level = ReadLong(info, "level");
        dto.MaxHp = ReadLong(info, "maxHP");
        dto.MaxMp = ReadLong(info, "maxMP");
        dto.Exp = ReadLong(info, "exp");
        dto.PaDamage = ReadLong(info, "PADamage");
        dto.MaDamage = ReadLong(info, "MADamage");
        dto.PdDamage = ReadLong(info, "PDDamage");
        dto.MdDamage = ReadLong(info, "MDDamage");
        dto.PdRate = ReadLong(info, "PDRate");
        dto.MdRate = ReadLong(info, "MDRate");
        dto.Acc = ReadLong(info, "acc");
        dto.Eva = ReadLong(info, "eva");
        dto.Pushed = ReadLong(info, "pushed");
        dto.Speed = ReadLong(info, "speed");
        dto.HpRecovery = ReadLong(info, "hpRecovery");
        dto.MpRecovery = ReadLong(info, "mpRecovery");
        dto.RemoveAfter = ReadLong(info, "removeAfter");
        dto.FixedDamage = ReadLong(info, "fixedDamage");
        dto.FixedBodyAttackDamageRate = ReadLong(info, "fixedBodyAttackDamageR");
        dto.CharismaExp = ReadLong(info, "charismaEXP");
        dto.SenseExp = ReadLong(info, "senseEXP");
        dto.InsightExp = ReadLong(info, "insightEXP");
        dto.WillExp = ReadLong(info, "willEXP");
        dto.CraftExp = ReadLong(info, "craftEXP");
        dto.CharmExp = ReadLong(info, "charmEXP");
        dto.WeaponPoints = ReadLong(info, "wp");

        // `fs` is the one field the archive stores as a Double on every mob
        // sampled (10 on 333 of 343), so it gets its own reader.
        dto.Fs = ReadDouble(info, "fs");

        // `mobType` is a String on some mobs ("1N", "7N", "4H") and a Long on
        // others, so it is reported as written rather than coerced.
        dto.MobType = ReadText(info, "mobType");

        if (ReadLong(info, "category") is { } category)
        {
            dto.Category = category;
            dto.CategoryName = CategoryNames.TryGetValue((int)category, out string? name)
                ? name
                : "None";
        }

        if (ReadText(info, "elemAttr") is { Length: > 0 } attr)
            dto.Elements.AddRange(ParseElemAttr(attr));

        dto.Boss = Flag(info, "boss");
        dto.PartyBonusMob = Flag(info, "partyBonusMob");
        dto.Undead = Flag(info, "undead");
        dto.FirstAttack = Flag(info, "firstAttack");
        // MobTooltipRenderer.cs:71-74 prints [No Touch Damage] when bodyAttack is
        // FALSE, so an absent bodyAttack node is the tag, not its absence.
        dto.BodyAttack = Flag(info, "bodyAttack");
        dto.DamagedByMob = Flag(info, "damagedByMob");
        dto.ChangeableMob = Flag(info, "changeableMob");
        dto.AllyMob = Flag(info, "allyMob");
        dto.Invincible = Flag(info, "invincible");
        dto.NotAttack = Flag(info, "notAttack");
        dto.IgnoreDamage = Flag(info, "ignoreDamage");
        dto.IgnoreMoveImpact = Flag(info, "ignoreMoveImpact");
        dto.IgnoreMovable = Flag(info, "ignoreMovable");
        dto.NoDebuff = Flag(info, "noDebuff");
        dto.OnlyNormalAttack = Flag(info, "onlyNormalAttack");
        dto.OnlyHittedByCommonAttack = Flag(info, "onlyHittedByCommonAttack");

        if (Child(info, "revive") is { } revive)
        {
            foreach (WzObject child in Children(revive))
            {
                if (ReadValueLong(child) is { } id) dto.RevivesInto.Add(id);
            }
        }

        return true;
    }

    /// <summary>
    /// The monster-book page: <c>String.nx/MonsterBook.img/&lt;id&gt;</c>.
    /// </summary>
    /// <remarks>
    /// 344 entries in the open String.nx (2026-10-16): 333 keyed by the padded
    /// seven-digit id and 11 by the bare six-digit one, each with an
    /// <c>episode</c> paragraph, a <c>map</c> list of map ids and a <c>reward</c>
    /// list of item ids. Both key shapes are tried because only one of them is
    /// right for any given entry and there is no key that would find all 344.
    /// The ids are returned RAW: turning a map id into a name is
    /// <c>String.nx/Map.img</c>'s job and an item id's into a name is
    /// <c>String.nx/Eqp.img</c>'s, and guessing either here would print a wrong
    /// label next to a right number.
    /// </remarks>
    private MobBookDto? ReadBook(int numeric)
    {
        foreach (string key in new[] { numeric.ToString("D7", CultureInfo.InvariantCulture), numeric.ToString(CultureInfo.InvariantCulture) })
        {
            WzObject? entry = null;
            foreach (OpenFile file in _session.SelectRoleSources("String", null))
            {
                if (_session.TryResolve(WzPath.Join(file.Id, "MonsterBook.img", key)) is { } found)
                {
                    entry = found;
                    break;
                }
            }
            if (entry is null) continue;

            var book = new MobBookDto { Episode = Unescape(ReadText(entry, "episode") ?? "") };
            ReadIdList(Child(entry, "map"), book.Maps);
            ReadIdList(Child(entry, "reward"), book.Rewards);
            return book;
        }
        return null;
    }

    /// <summary>
    /// The archive stores paragraph breaks as a literal backslash-n, not as a
    /// newline: <c>String.nx/MonsterBook.img/0100100</c> reads
    /// <c>"等级：Lv.1\n形态：动物型\n\n在冒险岛…"</c> with two characters. Passing
    /// that straight through renders a wall of <c>\n</c> in the panel, so the
    /// escapes are turned back into line breaks here rather than in the caller.
    /// A real newline passes through unchanged, because this runs a backslash
    /// that is not an n over it and leaves it alone.
    /// </summary>
    private static string Unescape(string text)
        => text.Replace("\\r\\n", "\n", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal);

    private void ReadIdList(WzObject? parent, List<string> into)
    {
        if (parent is null) return;
        foreach (WzObject child in Children(parent))
            if (ReadValueLong(child) is { } id)
                into.Add(id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <c>info/elemAttr</c> — a run of letter/digit pairs, e.g. <c>"I2F3L2H2S2"</c>.
    /// A digit of 0 is NORMAL and is dropped, exactly as the reference drops a
    /// Normal element (:326-329).
    /// </summary>
    private static List<MobElementDto> ParseElemAttr(string attr)
    {
        var parsed = new List<MobElementDto>();
        for (int i = 0; i + 1 < attr.Length; i += 2)
        {
            char letter = char.ToUpperInvariant(attr[i]);
            if (!int.TryParse(attr[i + 1].ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int level))
                continue;
            level = Math.Clamp(level, 0, ResistanceNames.Length - 1);
            if (level == 0) continue;

            string? name = Elements.FirstOrDefault(e => e.Key == letter).Name;
            if (name is null) continue;

            parsed.Add(new MobElementDto
            {
                Key = letter.ToString(),
                Name = name,
                Resistance = level,
                Label = $"{name} {ResistanceNames[level]}",
            });
        }
        return parsed;
    }

    /// <summary>True when the named flag node exists and is not zero.</summary>
    private bool Flag(WzObject parent, string key)
        => ReadLong(parent, key) is { } v && v != 0;

    /// <summary>
    /// <c>100100</c> and <c>0100100</c> are the same mob, and the archive does not
    /// always agree on which to use — MobSpriteService says why, and probes both.
    /// </summary>
    private bool TryMobImage(string? fileId, string mobId, out WzImage? image)
    {
        image = null;
        if (!int.TryParse(mobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return false;
        foreach (string candidate in new[] { numeric.ToString("D7", CultureInfo.InvariantCulture), numeric.ToString(CultureInfo.InvariantCulture) })
        {
            foreach (OpenFile file in Candidates(fileId))
            {
                foreach (string prefix in new[] { "", "Mob/" })
                {
                    if (_session.TryResolve(WzPath.Join(file.Id, prefix + candidate + ".img")) is WzImage found)
                    {
                        image = found;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private List<OpenFile> Candidates(string? fileId)
        => fileId is null
            ? _session.SelectRoleSources("Mob", null)
            : _session.SelectRoleSources("Mob", fileId);

    private IEnumerable<WzObject> Children(WzObject node)
    {
        try { return _session.EnumerateChildren(node).ToList(); }
        catch { return Array.Empty<WzObject>(); }
    }

    private WzObject? Child(WzObject parent, string name)
        => Children(parent).FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private long? ReadLong(WzObject parent, string key)
        => Child(parent, key) is { } leaf ? ReadValueLong(leaf) : null;

    private long? ReadValueLong(WzObject leaf)
        => leaf switch
        {
            WzIntProperty i => i.Value,
            WzShortProperty s => s.Value,
            // Under NX these arrive as WzLongProperty; without this case every
            // stat on this archive read as null (CombatService.cs:1244-1250 has
            // the same note).
            WzLongProperty l => l.Value,
            WzStringProperty s when long.TryParse(
                s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => null,
        };

    private double? ReadDouble(WzObject parent, string key)
        => Child(parent, key) switch
        {
            WzDoubleProperty d => d.Value,
            WzFloatProperty f => f.Value,
            WzLongProperty l => l.Value,
            WzIntProperty i => i.Value,
            WzShortProperty s => s.Value,
            WzStringProperty s when double.TryParse(
                s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };

    private string? ReadText(WzObject parent, string key)
        => Child(parent, key) switch
        {
            WzStringProperty s => s.Value,
            WzIntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
            WzShortProperty s => s.Value.ToString(CultureInfo.InvariantCulture),
            WzLongProperty l => l.Value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
}

/// <summary>One mob's bestiary page.</summary>
public sealed class MobBestiaryDto
{
    /// <summary>The id, normalised to the seven digits the archive uses.</summary>
    public string Id { get; set; } = "";

    /// <summary>From <c>String.nx/Mob.img</c>; empty when the archive has no name.</summary>
    public string Name { get; set; } = "";

    /// <summary>False when no open Mob archive holds this mob at all.</summary>
    public bool HasInfo { get; set; }

    public long? Level { get; set; }
    public long? MaxHp { get; set; }
    public long? MaxMp { get; set; }
    public long? Exp { get; set; }
    public long? PaDamage { get; set; }
    public long? MaDamage { get; set; }
    public long? PdDamage { get; set; }
    public long? MdDamage { get; set; }

    /// <summary><c>PDRate</c> / <c>MDRate</c> — damage reduction, as a percent.</summary>
    public long? PdRate { get; set; }
    public long? MdRate { get; set; }

    public long? Acc { get; set; }
    public long? Eva { get; set; }

    /// <summary>Knockback (<c>info/pushed</c>), the reference's "Knockback" line.</summary>
    public long? Pushed { get; set; }

    public long? Speed { get; set; }
    public long? HpRecovery { get; set; }
    public long? MpRecovery { get; set; }

    /// <summary><c>info/fs</c> — the one field stored as a Double on this archive.</summary>
    public double? Fs { get; set; }

    /// <summary><c>info/category</c>, or null when the mob carries none.</summary>
    public long? Category { get; set; }

    /// <summary>"Mammal" … "Enchanted", or "None" for a stated 0 / unknown value.</summary>
    public string? CategoryName { get; set; }

    /// <summary><c>info/mobType</c> as written — a Long on some mobs, "1N" on others.</summary>
    public string? MobType { get; set; }

    public List<MobElementDto> Elements { get; } = new();

    public bool Boss { get; set; }
    public bool PartyBonusMob { get; set; }
    public bool Undead { get; set; }
    public bool FirstAttack { get; set; }
    public bool BodyAttack { get; set; }
    public bool DamagedByMob { get; set; }
    public bool ChangeableMob { get; set; }
    public bool AllyMob { get; set; }
    public bool Invincible { get; set; }
    public bool NotAttack { get; set; }
    public bool IgnoreDamage { get; set; }
    public bool IgnoreMoveImpact { get; set; }
    public bool IgnoreMovable { get; set; }
    public bool NoDebuff { get; set; }
    public bool OnlyNormalAttack { get; set; }
    public bool OnlyHittedByCommonAttack { get; set; }

    public long? RemoveAfter { get; set; }
    public long? FixedDamage { get; set; }
    public long? FixedBodyAttackDamageRate { get; set; }
    public long? CharismaExp { get; set; }
    public long? SenseExp { get; set; }
    public long? InsightExp { get; set; }
    public long? WillExp { get; set; }
    public long? CraftExp { get; set; }
    public long? CharmExp { get; set; }
    public long? WeaponPoints { get; set; }

    /// <summary>Mob ids this one leaves behind, from <c>info/revive</c>.</summary>
    public List<long> RevivesInto { get; } = new();

    /// <summary>The trait tags, already ordered as MobTooltipRenderer.cs emits them.</summary>
    public List<string> Traits { get; } = new();

    /// <summary>
    /// <c>[Disappears after N seconds]</c> when <c>info/removeAfter</c> is set —
    /// the reference prints it as its own line (:135-139), not inside the tag
    /// string, and this keeps it out of <see cref="Traits"/> so the caller can
    /// style the two differently as the reference does.
    /// </summary>
    public long? DisappearAfterSeconds { get; set; }

    /// <summary>The monster-book entry, or null when String.nx has none for this mob.</summary>
    public MobBookDto? Book { get; set; }

    /// <summary>Reads one trait flag by its key, for the tags the panel derives.</summary>
    public bool Flag(string key) => key switch
    {
        "undead" => Undead,
        "firstAttack" => FirstAttack,
        "noBodyAttack" => !BodyAttack,
        "damagedByMob" => DamagedByMob,
        "changeableMob" => ChangeableMob,
        "allyMob" => AllyMob,
        "invincible" => Invincible,
        "notAttack" => NotAttack,
        "ignoreDamage" => IgnoreDamage,
        "ignoreMoveImpact" => IgnoreMoveImpact,
        "ignoreMovable" => IgnoreMovable,
        "noDebuff" => NoDebuff,
        "onlyNormalAttack" => OnlyNormalAttack,
        "onlyHittedByCommonAttack" => OnlyHittedByCommonAttack,
        _ => false,
    };
}

/// <summary>One elemental resistance, decoded from a letter/digit pair.</summary>
public sealed class MobElementDto
{
    /// <summary>The archive's letter: F I L S H D P.</summary>
    public string Key { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>0 neutral, 1 immune, 2 strong, 3 weak (ElementDamage.h:9-15).</summary>
    public int Resistance { get; set; }

    /// <summary>"Holy weak" — the reference's own two-word form (:328).</summary>
    public string Label { get; set; } = "";
}

/// <summary>The <c>String.nx/MonsterBook.img</c> page for one mob.</summary>
public sealed class MobBookDto
{
    public string Episode { get; set; } = "";

    /// <summary>Map ids, raw.</summary>
    public List<string> Maps { get; } = new();

    /// <summary>Item ids, raw.</summary>
    public List<string> Rewards { get; } = new();
}
