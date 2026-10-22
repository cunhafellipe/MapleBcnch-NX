namespace MapleBench.Services.Look;

/// <summary>
/// One equip slot: where its art lives in <c>Character/</c>, which ids belong to
/// it, and the layer name its parts land on when their own <c>z</c> is unknown.
/// </summary>
/// <remarks>
/// <para>
/// The folder and the id filters are the reference's, verbatim
/// (<c>AvatarForm.cs:494-620</c>): <c>CharacterDir</c> is <c>Weapon</c> for both
/// weapon slots, <c>Cap</c> for both cap slots, <c>Accessory</c> for
/// <c>FaceAcc</c>/<c>Glass</c>/<c>Earring</c>, the slot's own name otherwise.
/// The two cap slots, the two weapon slots and the two hair slots are ONE folder
/// split by id, which is why they are pairs and not six folders.
/// </para>
///
/// <para><b>Why the filters live here and not in the composer.</b> They exist to
/// tell a picker which images a slot may offer — "weapon 2 is the one whose id
/// starts 0170". They are not part of drawing. Keeping them on the slot means a
/// caller listing a folder gets the same answer the reference's grid gives,
/// without the composer having to know what a picker is.</para>
/// </remarks>
public sealed class EquipSlotDef
{
    /// <summary>The name the reference's designer uses (<c>Cap-1</c>, <c>Longcoat</c>, …).</summary>
    public required string Name { get; init; }

    /// <summary>The folder under <c>Character/</c>. Empty for the body sheets.</summary>
    public required string Folder { get; init; }

    /// <summary>Inclusive <c>imgId / 1000</c> range, or null when the slot takes anything.</summary>
    public int[]? IdRange { get; init; }

    /// <summary>Four-character id prefix this slot takes, or null.</summary>
    public string? IdPrefix { get; init; }

    /// <summary>
    /// Four-character id prefix this slot refuses, or null. Weapon-1 is the only
    /// one: it is "everything except 0170", because 0170 is a different client.
    /// </summary>
    public string? ExcludePrefix { get; init; }

    /// <summary>
    /// The layer a part lands on when its own <c>z</c> names nothing zmap knows —
    /// <c>Clothing::default_zname</c>, <c>Clothing.cpp:274-302</c>. Null for the
    /// two sheets that are not clothing and carry no such fallback.
    /// </summary>
    public string? DefaultZName { get; init; }

    /// <summary>
    /// Four-character id prefix this slot takes, for a slot that has no folder.
    ///
    /// <para>
    /// Head and Body are the two sheets the whole character is built on, and the
    /// reference addresses them with an empty <c>CharacterDir</c>
    /// (<c>AvatarForm.cs:496-499</c>) -- so they are enumerated at the ARCHIVE ROOT,
    /// not inside a folder, and told apart by the leading four digits:
    /// <c>AvatarForm.cs:559-566</c> drops <c>0000*</c> from Head and <c>0001*</c> from
    /// Body. Inverting both, Head takes <c>0001*</c> (the head sheets, 00012000 on)
    /// and Body takes <c>0000*</c> (the body sheets, 00002000 on).
    /// </para>
    /// </summary>
    public string? RootPrefix { get; init; }

    /// <summary>True when the dead stance borrows the item's own <c>stand1</c> pose.</summary>
    public bool HeadWorn { get; init; }

    public bool Accepts(int imgId)
    {
        string digits = imgId.ToString("D8");
        if (RootPrefix is not null && !digits.StartsWith(RootPrefix, StringComparison.Ordinal)) return false;
        if (IdPrefix is not null && !digits.StartsWith(IdPrefix, StringComparison.Ordinal)) return false;
        if (ExcludePrefix is not null && digits.StartsWith(ExcludePrefix, StringComparison.Ordinal)) return false;
        if (IdRange is not null)
        {
            int bucket = imgId / 1000;
            if (bucket < IdRange[0] || bucket > IdRange[1]) return false;
        }
        return true;
    }
}

/// <summary>
/// The 19 slots the reference's avatar editor offers, and the rules that resolve
/// an id into art: which folder, and which items belong to the slot.
/// </summary>
public static class EquipSlots
{
    private static EquipSlotDef Def(
        string name, string folder, string? defaultZ,
        int[]? range = null, string? prefix = null, string? exclude = null,
        bool headWorn = false, string? rootPrefix = null)
        => new()
        {
            Name = name,
            Folder = folder,
            DefaultZName = defaultZ,
            IdRange = range,
            IdPrefix = prefix,
            ExcludePrefix = exclude,
            HeadWorn = headWorn,
            RootPrefix = rootPrefix,
        };

    /// <summary>
    /// AvatarForm.Designer.cs: <c>Head, Body, Weapon-1, Weapon-2, Cap-1, Cap-2,
    /// Coat, Pants, Longcoat, Cape, Shield, Glove, Shoes, Hair-1, Hair-2,
    /// Face-1, Face-2, FaceAcc, Glass</c> — 19 slots over 12 folders.
    /// </summary>
    public static readonly IReadOnlyList<EquipSlotDef> All =
    [
        Def("Head", "", null, headWorn: true, rootPrefix: "0001"),
        Def("Body", "", null, rootPrefix: "0000"),
        Def("Weapon-1", "Weapon", "weapon", exclude: "0170"),
        Def("Weapon-2", "Weapon", "weapon", prefix: "0170"),
        Def("Cap-1", "Cap", "cap", range: [1000, 1003], headWorn: true),
        Def("Cap-2", "Cap", "cap", range: [1004, 1006], headWorn: true),
        Def("Coat", "Coat", "mailChest"),
        Def("Pants", "Pants", "pants"),
        Def("Longcoat", "Longcoat", "mailChest"),
        Def("Cape", "Cape", "cape"),
        Def("Shield", "Shield", "shield"),
        Def("Glove", "Glove", "glove"),
        Def("Shoes", "Shoes", "shoes"),
        Def("Hair-1", "Hair", "hairOverHead", range: [30, 56], headWorn: true),
        Def("Hair-2", "Hair", "hairOverHead", range: [57, 85], headWorn: true),
        Def("Face-1", "Face", "face", range: [20, 23], headWorn: true),
        Def("Face-2", "Face", "face", range: [24, 65], headWorn: true),
        Def("FaceAcc", "Accessory", "accessoryFace", prefix: "0101", headWorn: true),
        Def("Glass", "Accessory", "accessoryEye", prefix: "0102", headWorn: true),
    ];

    private static readonly Dictionary<string, EquipSlotDef> ByName =
        All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static EquipSlotDef? Find(string? name)
        => name is not null && ByName.TryGetValue(name, out EquipSlotDef? d) ? d : null;

    /// <summary>
    /// The slots that share one folder and differ only by id, so a caller equipping
    /// a cap does not have to know it was "cap 1".
    /// </summary>
    public static IEnumerable<EquipSlotDef> InFolder(string folder)
        => All.Where(s => s.Folder.Length > 0 && s.Folder == folder);

    /// <summary>
    /// The slots that share a folder, minus the one a given id already belongs to —
    /// which is how "which cap slot does this image go in?" is answered without
    /// hardcoding either name.
    /// </summary>
    public static EquipSlotDef? ForImage(string folder, int imgId)
        => InFolder(folder).FirstOrDefault(s => s.Accepts(imgId));
}