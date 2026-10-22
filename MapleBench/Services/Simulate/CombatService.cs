using System.Globalization;
using MapleBench.Services.MapEditor;
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
/// Where a swing reaches, read from the archive rather than written down here.
/// </summary>
/// <remarks>
/// <para>
/// Every number in this file came out of a WZ node. None of them is tuned.
/// The two that matter most, and what they were measured to be:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>barehands.img/0/stabO1</c>: <c>lt(-64,-33) rb(-27,-18)</c> -- 37x15
/// </item>
/// <item>
/// <c>axe.img/0/swingT1</c>: <c>lt(-99,-90) rb(-51,18)</c> -- 48x108
/// </item>
/// </list>
/// <para>
/// A 15px-tall box for a bare-handed thrust is the fact that matters: a swing
/// that "feels wrong" in this simulator is almost always a hitbox that was
/// guessed at 60x60 instead of measured. An axe at 108px tall is the other
/// fact -- reach scales with the weapon by a factor of seven in height alone,
/// so any single hardcoded box is wrong for every weapon but one.
/// </para>
/// </remarks>
public sealed class CombatService
{
    /// <summary>
    /// The weapon and stance used when the archive carries nothing better.
    /// </summary>
    /// <remarks>
    /// Bare hands, because the simulator's character has no equipment: the
    /// reference's <c>get_weapon</c> (Afterimage.cpp) reads the equipped weapon
    /// and there is nothing here to read. Every player with nothing equipped is
    /// <c>barehands.img</c> in the reference too, so this is the honest answer
    /// for this character rather than a stand-in for a missing feature.
    /// </remarks>
    public const string DefaultWeapon = "barehands";

    /// <summary>Weapon level 0 -- the only level these archives carry.</summary>
    public const int DefaultLevel = 0;

    /// <summary>
    /// The one-handed thrust. The reference's default attack name for it is
    /// <c>stabO1</c>, spelled without a 'b' in every archive this project can
    /// open.
    /// </summary>
    public const string DefaultStance = "stabO1";

    private readonly WzSessionService _session;

    public CombatService(WzSessionService session)
    {
        _session = session;
    }

    /// <summary>
    /// The box a swing of <paramref name="weapon"/> covers, in the character's
    /// own local coordinates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The box a swing covers, in the character's own local coordinates, and the
    /// reference's own chain for it:
    /// <c>WeaponData.cpp:52</c> reads <c>info/afterImage</c> off the weapon image,
    /// <c>Char.cpp:950</c> takes it as <c>ai_name</c>,
    /// <c>Afterimage.cpp:39</c> resolves
    /// <c>character["Afterimage"][name + ".img"][level / 10][stance]</c>, and
    /// <c>RegularAttack.cpp:49</c> — also <c>Skill.cpp:486,517</c> — makes that
    /// afterimage's RANGE the attack's reach, after <c>Combat.cpp:1730</c> has set
    /// it for the stance being played.
    /// </para>
    /// <para>
    /// <b>MEASURED DEFECT, fixed here.</b> This method built its path from the
    /// WEAPON ID, i.e. it asked for <c>Afterimage/01302000.img</c>. That image does
    /// not exist: the folder holds 15 weapon CLASSES —
    /// <c>axe barehands blank bow crossBow gun hit knuckle mace poleArm spear
    /// swordOL swordOS swordTL swordTS</c>. Every lookup therefore fell through to
    /// the bare-hands fallback, and <c>/api/simulate/debug/attack-box?weapon=01302000.img</c>
    /// reported <c>barehands.img/stabO1</c>, box (-64,-33)-(-27,-18) — 37x15px, the
    /// smallest in the game, lying entirely BEHIND the character. At a mob standing
    /// 9px away the two boxes did not overlap, so <b>no swing ever landed</b>.
    /// </para>
    /// <para>
    /// The mapping was available all along: <c>Weapon/01302000.img/info</c> carries
    /// <c>afterImage = "swordOL"</c>, which is what the reference reads.
    /// </para>
    /// <para>
    /// Falls back to bare hands, and says so. <c>Afterimage::load</c>
    /// (Afterimage.cpp:28-41) treats a missing node as "no attack box at all",
    /// which for a viewer is indistinguishable from "cannot fight" — so the
    /// fallback keeps a weaponless character able to fight, which is what the
    /// reference does for an unarmed one.
    /// </para>
    /// </remarks>
    public AttackBox AttackBox(string? weapon, int level = DefaultLevel, string stance = DefaultStance)
    {
        lock (_session.Gate)
        {
            foreach ((string image, string tryStance) in Candidates(AfterimageName(weapon), stance))
            {
                foreach (OpenFile file in _session.SelectRoleSources("Character", null))
                {
                    foreach (string prefix in new[] { "Afterimage", "Character/Afterimage" })
                    {
                        // Segment by segment. WzPath.Join escapes slashes inside a
                        // segment, so a multi-segment string handed to it becomes
                        // one literal name and resolves to nothing.
                        string imagePath = Path(file.Id, prefix, image);
                        if (TryReadBox(_session.TryResolve(imagePath), level, out Box raw, tryStance))
                            return new AttackBox(raw, $"{image}/{tryStance}", imagePath, true);
                    }
                }
            }

            // Only now, with every archive and every spelling exhausted.
            foreach (OpenFile file in _session.SelectRoleSources("Character", null))
            {
                string imagePath = Path(file.Id, "Afterimage", DefaultWeapon + ".img");

                if (TryReadBox(_session.TryResolve(imagePath), level, out Box bare, DefaultStance))
                    return new AttackBox(bare, DefaultWeapon, imagePath, false);
            }

            // No Character archive at all. A box of zero is not a guess: a
            // character that cannot fight is the truth when there is no archive
            // to say otherwise, and the frame reports the swing connected with
            // nothing.
            return new AttackBox(Box.Empty, "", "", false);
        }
    }

    /// <summary>
    /// The weapon's own <c>info/afterImage</c> — its Afterimage class name.
    /// </summary>
    /// <remarks>
    /// WeaponData.cpp:52 and :125. Returns <see cref="DefaultWeapon"/> when the
    /// weapon is null, unreadable, or declares none, which is the same fallback
    /// the reference's own empty node produces.
    /// </remarks>
    private string AfterimageName(string? weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return DefaultWeapon;
        // The caller may say "01302000.img" or "01302000". Strip the extension
        // BEFORE parsing: int.TryParse rejects the first, and a silently
        // unresolvable weapon lands back on the bare-hands box — which is the
        // whole defect this method was rewritten to remove, one prefix away.
        string bare = weapon.Trim();
        if (bare.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) bare = bare[..^".img".Length];
        if (!int.TryParse(bare, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return DefaultWeapon;

        // Every id width, for the same reason WeaponAttackSpeed does: the equip
        // slot carries "01302000" while the session's own box says "1302000",
        // and one does not parse into the other.
        var names = new List<string> { bare };
        foreach (string extra in new[]
                 {
                     numeric.ToString("D8", CultureInfo.InvariantCulture),
                     numeric.ToString("D7", CultureInfo.InvariantCulture),
                     numeric.ToString(CultureInfo.InvariantCulture),
                 })
        {
            if (!names.Contains(extra)) names.Add(extra);
        }

        foreach (OpenFile file in _session.SelectRoleSources("Character", null))
        {
            foreach (string candidate in names)
            {
                foreach (string prefix in new[] { "Weapon", "Character/Weapon", "" })
                {
                    if (_session.TryResolve(Path(file.Id, prefix, candidate + ".img")) is not { } image)
                        continue;
                    foreach (WzObject child in Children(image))
                    {
                        if (!child.Name.Equals("info", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (WzObject leaf in Children(child))
                        {
                            if (!leaf.Name.Equals("afterImage", StringComparison.OrdinalIgnoreCase)) continue;
                            if (leaf is WzStringProperty s && !string.IsNullOrWhiteSpace(s.Value))
                                return s.Value;
                        }
                    }
                }
            }
        }
        return DefaultWeapon;
    }

    /// <summary>
    /// The weapons to try, most specific first.
    /// </summary>
    /// <remarks>
    /// Two spellings per weapon because the archives disagree: the level node
    /// holds several stances and not every weapon has the same one, so a weapon
    /// that carries only <c>swingT1</c> must not be looked up under
    /// <c>stabO1</c>.
    /// </remarks>
    /// <summary>
    /// The weapon images to try, in order, each with the stance to read from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The IMAGE is what the caller builds a path from; the stance is looked up
    /// inside it. Returning a full <c>axe.img/0/swingT1</c> as one string was
    /// the first version of this and it produced paths that resolve to nothing:
    /// <c>WzPath.Join</c> escapes a segment's slashes, so joining
    /// <c>f3 + "Afterimage/axe.img/0/swingT1.img"</c> asked for a node literally
    /// named <c>Afterimage/axe.img/0/swingT1.img</c>. The slash-joining is the
    /// job of WzPath.Child, one segment at a time.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Image, string Stance)> Candidates(string? weapon, string stance)
        {
            string w = string.IsNullOrWhiteSpace(weapon) ? DefaultWeapon : weapon.Trim();

            // The archives are named `axe.img` and the caller may say either `axe` or
            // `axe.img` — the browser's own simulator request sends the full name.
            // Appending ".img" unconditionally turned the second spelling into
            // `axe.img.img`, which resolves to nothing, so the candidate loop fell
            // through every weapon and ended on the bare-hands fallback: 37x15px,
            // the smallest box in the game, and the reason no attack ever connected.
            // Normalise once, here, so the caller's spelling stops mattering.
            if (w.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                w = w[..^".img".Length];

            yield return ($"{w}.img", stance);

        // Several stances, because a weapon's level node carries whichever ones
        // that weapon has: an axe has swingT1, a hand has stabO1. Asking only for
        // the caller's stance would report "no box" for every weapon but the one
        // whose spelling happens to match.
        foreach (string s in new[] { "stabO1", "stabT1", "swingT1", "swingP1", "swingT2" })
            yield return ($"{w}.img", s);

        yield return ($"{DefaultWeapon}.img", stance);
            }

            /// <summary>
            /// The stances a weapon's swings may pick from, transcribed from the
            /// reference's own table.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <c>CharLook::getattackstance</c> (clean-client <c>CharLook.cpp:648</c>,
            /// Beidou <c>:580</c>) indexes a static array by the weapon's
            /// <c>info/attack</c> INTEGER — not by a weapon-type enum, and not by the
            /// Afterimage image name this class otherwise deals in (that one is read
            /// separately at <c>WeaponData.cpp:52</c>, far below). <c>WeaponData.cpp:35</c>
            /// is where the attack integer is read; the <c>twohanded</c> flag computed
            /// at <c>WeaponData.cpp:30</c> is NOT consulted by
            /// <c>getattackstance</c>, so one-handed vs two-handed is decided
            /// entirely by the data.
            /// </para>
            /// <para>
            /// <c>WeaponData.cpp:28-30</c> is the two-handed rule this port follows
            /// in <see cref="IsTwoHanded"/>:
            /// <c>prefix = equipid / 10000</c>, then
            /// <c>prefix == STAFF || (prefix &gt;= SWORD_2H &amp;&amp; prefix &lt;= POLEARM) || prefix == CROSSBOW</c>.
            /// </para>
            /// <para>
            /// The table, transcribed whole from <c>CharLook.cpp:682-696</c>:
            /// <c>0 -> {NONE}</c>, <c>1 -> {stabO1, stabO2, swingO1, swingO2, swingO3}</c>,
            /// <c>2 -> {stabT1, swingP1}</c>, <c>3 -> {shoot1}</c>, <c>4 -> {shoot2}</c>,
            /// <c>5 -> {stabO1, stabO2, swingT1, swingT2, swingT3}</c>,
            /// <c>6 -> {swingO1, swingO2}</c>, <c>7 -> {swingO1, swingO2}</c>,
            /// <c>8 -> {stabO1, stabO2}</c>, <c>9 -> {SHOT}</c>.
            /// </para>
            /// <para>
            /// Measured on this archive's <c>Character.nx</c>: <c>Weapon/01302000.img</c>
            /// (sword) carries <c>info/attack = 1</c> and <c>Weapon/01402000.img</c>
            /// carries <c>5</c>. Both groups are present as stances
            /// (<c>/api/simulate/look/stances</c>: swingO1/2/3 and swingT1/2/3), which is
            /// the check that matters — a group naming stances the archive does not carry
            /// would silently draw nothing.
            /// </para>
            /// <para>
            /// Unarmed is not a member of this table's business: <c>CharLook.cpp:594</c>
            /// returns before the array is indexed when no weapon is equipped, so the
            /// stance is left UNCHANGED. That is why an unarmed character gets no swing
            /// pose at all, and it is returned here as an empty group rather than as a
            /// default.
            /// </para>
            /// </remarks>
            public static string[] AttackStances(int attack) => attack switch
            {
                1 => new[] { "stabO1", "stabO2", "swingO1", "swingO2", "swingO3" },
                2 => new[] { "stabT1", "swingP1" },
                3 => new[] { "shoot1" },
                4 => new[] { "shoot2" },
                5 => new[] { "stabO1", "stabO2", "swingT1", "swingT2", "swingT3" },
                6 => new[] { "swingO1", "swingO2" },
                7 => new[] { "swingO1", "swingO2" },
                8 => new[] { "stabO1", "stabO2" },
                9 => new[] { "shot" },
                _ => Array.Empty<string>(),
            };

            /// <summary>
            /// The swing group for an equipped weapon image, or empty when unarmed.
            /// </summary>
            /// <param name="weaponItemId">
            /// The WZ weapon image id, e.g. <c>01302000</c>. Null/blank means no weapon,
            /// which is the <c>CharLook.cpp:594</c> early return, not attack 0.
            /// </param>
            public string[] AttackStancesFor(string? weaponItemId)
            {
                if (string.IsNullOrWhiteSpace(weaponItemId))
                    return Array.Empty<string>();

                string id = weaponItemId.Trim();
                if (id.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                    id = id[..^".img".Length];

                // TryReadIntChild, not a string read: `info/attack` is an INTEGER in
                // the WZ, and routing it through a string would quietly accept a
                // non-numeric value and fail later, far from the archive.
                //
                // The archive is NOT a hardcoded id. Every other lookup in this file
                // asks the session which archives play the Character role and tries
                // both spellings of the folder (see AttackBox :82-93); this one
                // asked f2, which has no Weapon folder at all. Measured on this
                // build: f2 (Base.nx) has 3 children -- StandardPDD.img, smap.img,
                // zmap.img; f7 (Item.nx) has 6 -- Cash, Consume, Etc, Install, Pet,
                // Special. The weapons are under f1/Weapon, where 01302000.img's
                // info block carries 21 children including `attack`.
                //
                // The failure was silent and total: an empty group meant Swing()
                // never reached SwingPose(), so every swing went out with
                // attackStance="" and avatar.js:196 had nothing to play. Measured
                // before the fix: 8 swings at a mob, attackStance empty on 8 of 8,
                // while /look/attack-stances?attack=1 happily returned the group.
                // The endpoint took `attack` directly, so it looked healthy; only
                // the `weapon` parameter -- the one Swing() actually uses -- was
                // broken, and it returned HTTP 200 with an empty array.
                lock (_session.Gate)
                {
                    // Every id width, for the reason WeaponAttackSpeed documents:
                    // the equip slot carries the eight-digit "01302000" while the
                    // session's own box can say "1302000", and parsing one into the
                    // other is not something a caller is asked to do. Asking only
                    // for the caller's spelling meant a seven-digit id returned an
                    // EMPTY GROUP — no swing pose, therefore no pose to lock, and
                    // HTTP 200 saying everything was fine.
                    var names = new List<string> { id };
                    if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        foreach (string extra in new[]
                                 {
                                     parsed.ToString("D8", CultureInfo.InvariantCulture),
                                     parsed.ToString("D7", CultureInfo.InvariantCulture),
                                     parsed.ToString(CultureInfo.InvariantCulture),
                                 })
                        {
                            if (!names.Contains(extra)) names.Add(extra);
                        }
                    }

                    foreach (OpenFile file in _session.SelectRoleSources("Character", null))
                    {
                        foreach (string candidate in names)
                        {
                            foreach (string prefix in new[] { "Weapon", "Character/Weapon" })
                            {
                                string imagePath = Path(file.Id, prefix, candidate + ".img");
                                if (_session.TryResolve(imagePath) is { } image
                                    && TryReadIntChild(image, "info", "attack", out int attack))
                                {
                                    return AttackStances(attack);
                                }
                            }
                        }
                    }
                }

                // The image resolved but carries no `info/attack`, or the id did
                // not resolve at all. Either way there is no group to name, and an
                // unarmed body is exactly what the reference draws -- CharLook.cpp:594
                // leaves the stance unchanged rather than reaching for a default.
                return Array.Empty<string>();
            }

    /// <summary>
    /// Build a session path from a file id and a slash-separated relative path.
    /// </summary>
    /// <remarks>
    /// One helper for every lookup in this file, because <c>WzPath.Join</c>
    /// escapes a slash INSIDE a segment and hands the whole relative path as
    /// one segment it therefore turns
    /// <c>f3 + "Afterimage/barehands.img"</c> into a request for a node
    /// literally named <c>Afterimage%2Fbarehands.img</c>. It resolves to null,
    /// so a typo or a wrong assumption about the archive's shape fails SILENTLY
    /// as "no box" or "no mob" rather than loudly -- the worst possible failure
    /// mode for the one code whose whole job is to find what the archive says.
    /// </remarks>
    private static string Path(string fileId, params string[] segments)
    {
        string path = WzPath.Join(fileId);
        foreach (string segment in segments)
        {
            // Split on '/' INSIDE each argument too. A caller that passes
            // "Character/Afterimage" as one argument means two segments, and
            // handing the pair to Child escapes the slash and asks for a node
            // with a slash in its name.
            foreach (string part in segment.Split('/', StringSplitOptions.RemoveEmptyEntries))
                path = WzPath.Child(path, part);
        }
        return path;
    }

    /// <summary>
    /// Read <c>lt</c>/<c>rb</c> off one stance node under a weapon's level.
    /// </summary>
    /// <remarks>
    /// The node has no name of its own — the level index IS the name, which is
    /// why this walks children rather than resolving a literal "0" segment.
    /// </remarks>
    private bool TryReadBox(WzObject? node, int level, out Box box, string stance)
    {
        box = Box.Empty;
        // Not `is WzImage`. An NX archive's image node is a WzSubProperty, and
        // requiring WzImage made every lookup on an NX archive return "not
        // found" -- silently, and identically to a genuinely absent box.
        if (node is null)
            return false;
        WzObject image = node;

        WzObject? levelNode = LevelChild(image, level);
        if (levelNode is null)
            return false;

        // The level node holds the STANCES (stabO1, proneStab, ...), and the
        // box lives on the STANCE -- lt/rb are siblings of the frame canvases
        // under `barehands.img/0/stabO1`, not under `.../0`. Reading them off
        // the level node finds nothing and reports "no box", which is the same
        // wrong answer as the vector bug above for a different reason.
        string wanted = stance;
        WzObject? stanceNode = null;
        foreach (WzObject child in Children(levelNode))
        {
            if (child.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                stanceNode = child;
                break;
            }
        }
        if (stanceNode is null)
            return false;

        return TryReadCorners(stanceNode, out box);
    }

    /// <summary>
    /// One level node out of a weapon image, whose children are named "0", "1", ...
    /// </summary>
    /// <remarks>
    /// Read through <c>WzSessionService.EnumerateChildren</c> rather than
    /// <c>node.Children</c>: an NX node's children are properties, and
    /// <c>WzProperties</c> is the empty answer for them. That is the whole
    /// reason the service exposes the walk, and skipping it here would report
    /// "no swing box" on every NX archive — the failure is silent, not loud.
    /// </remarks>
    private WzObject? LevelChild(WzObject image, int level)
    {
        // Level nodes are named "0", "1", ... but a writer may leave the name
        // blank, so the numeric name is preferred and the first child stands in
        // when it is absent. Only for level 0: a missing level 3 must not be
        // answered with a level that is not it.
        WzObject? first = null;
        foreach (WzObject child in _session.EnumerateChildren(image))
        {
            first ??= child;
            if (int.TryParse(child.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                && n == level)
                return child;
        }
        return level == 0 ? first : null;
    }

    /// <summary>The children of a node, as a list.</summary>
    private List<WzObject> Children(WzObject node)
    {
        List<WzObject> result = new();
        foreach (WzObject child in _session.EnumerateChildren(node))
            result.Add(child);
        return result;
    }

    /// <summary>
    /// <c>lt</c> and <c>rb</c> as corners.
    /// </summary>
    /// <remarks>
    /// Both nodes must be present and both must carry an <c>x</c>. A box with
    /// one corner is not a box: <c>die1</c> carries no corners at all
    /// (measured), and reporting that as a zero-size rectangle at the origin
    /// would make a corpse sit exactly on the player's feet and be hittable.
    /// </remarks>
    private bool TryReadCorners(WzObject node, out Box box)
    {
        box = Box.Empty;
        WzObject? lt = null;
        WzObject? rb = null;
        foreach (WzObject child in Children(node))
        {
            if (child.Name.Equals("lt", StringComparison.OrdinalIgnoreCase))
                lt = child;
            else if (child.Name.Equals("rb", StringComparison.OrdinalIgnoreCase))
                rb = child;
        }

        if (lt is null || rb is null)
            return false;
        if (!TryReadVector(lt, out int lx, out int ly) || !TryReadVector(rb, out int rx, out int ry))
            return false;

        box = new Box(lx, ly, rx, ry);
        return true;
    }

    /// <summary>
    /// Read an <c>{x,y}</c> vector node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A vector is a <see cref="WzVectorProperty"/>, which extends
    /// <c>WzExtended</c>: its x and y are PROPERTIES on the node, not child
    /// nodes. Walking children for them returns an empty list and every vector
    /// reads as missing -- which is silent, not loud, and looks exactly like
    /// "the archive does not have this box".
    /// </para>
    /// <para>
    /// The child walk stays as a fallback because a WZ-format archive can spell
    /// the same pair as real sub-nodes, and the simulator has to read both.
    /// </para>
    /// </remarks>
    private bool TryReadVector(WzObject node, out int x, out int y)
    {
        x = 0;
        y = 0;

        // X and Y are themselves WzIntProperty, not int -- the property wraps
        // the value rather than exposing it. `.Pos` is the library's own
        // (int,int) projection and is used for that reason.
        if (node is WzVectorProperty vector)
        {
            System.Drawing.Point pos = vector.Pos;
            x = pos.X;
            y = pos.Y;
            return true;
        }

        int? vx = null;
        int? vy = null;
        foreach (WzObject child in Children(node))
        {
            if (!TryReadInt(child, out int value))
                continue;
            if (child.Name.Equals("x", StringComparison.OrdinalIgnoreCase))
                vx = value;
            else if (child.Name.Equals("y", StringComparison.OrdinalIgnoreCase))
                vy = value;
        }

        if (vx is null || vy is null)
            return false;
        x = vx.Value;
        y = vy.Value;
        return true;
    }

    private static bool TryReadInt(WzObject node, out int value)
    {
        switch (node)
        {
            case WzIntProperty i:
                value = i.Value;
                return true;
            case WzShortProperty s:
                value = s.Value;
                return true;
            case WzLongProperty l:
                // Same clamp as ReadInt below, which this file's other long->int
                // reader already had. Under NX every one of these nodes arrives as
                // WzLongProperty, so the reader without the clamp was not an edge
                // case — it was the path taken by every value in the archive.
                value = (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue);
                return true;
            case WzStringProperty str when int.TryParse(
                str.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                value = parsed;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// One mob's hit points, from its own <c>info</c> node.
    /// </summary>
    /// <remarks>
    /// <c>maxHP</c> is measured on the mob this simulator is pointed at:
    /// <c>f2/9409000.img/info/maxHP = 8</c>. Reading it rather than fixing it is
    /// the whole reason a fight can be lost — a hardcoded number makes every
    /// mob a piñata on every map.
    /// </remarks>
    public int MobMaxHp(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is { } imageNode
                            && TryReadIntChild(imageNode, "info", "maxHP", out int hp)
                            && hp > 0)
                            return hp;
                    }
                }
            }
            // SimTuning.MobHp, reached only when no open Mob.nx carries the id.
            return SimTuning.MobHp;
        }
    }

    /// The label an NPC carries above itself, and whether it is allowed to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MapleNecrocer/Client/Npc.cs:171-186</c> builds a <c>NpcText</c> sprite per
    /// NPC at spawn time, not per frame, and reads exactly three things:
    /// </para>
    /// <code>
    /// Npc.cs:67   npcStringPath = "String/Npc.img/" + ID.IntID();
    /// Npc.cs:172  NpcText.NpcName = ...GetStr("name");
    /// Npc.cs:173  NpcText.NpcFunc = ...GetStr("func");
    /// Npc.cs:174-175  if (NpcFunc != "") HasFunc = true;      // absence, not emptiness
    /// Npc.cs:185  NpcText.HideName = GetBool("Npc/" + ID + ".img/info/hideName");
    /// </code>
    /// <para>
    /// <c>HasFunc</c> keys on the string being non-empty rather than on the node
    /// existing, so an NPC with a blank <c>func</c> shows no function line even
    /// though the node is there. That distinction is the reference's and is kept.
    /// </para>
    /// <para>
    /// <c>ID.IntID()</c> (<c>MapleNecrocer/Extension.cs:47-50</c>) is
    /// <c>Int32.Parse(Number).ToString()</c> — the id with its leading zeros
    /// STRIPPED. That is not a width sweep and the archive proves it: MEASURED on
    /// <c>f4/Map/Map0/000000002.img</c>, all six of its NPCs have a name, but only
    /// at the stripped form for the four-digit-lineage ids and at the verbatim
    /// form for the 7-digit ones —
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>0002000</c> → <c>String.nx/Npc.img/2000</c> = 罗杰;
    /// <c>0002001</c> → <c>2001</c> = 珊; <c>0002101</c> → <c>2101</c> = 希娜;
    /// <c>0002102</c> → <c>2102</c> = 妮娜.</description></item>
    /// <item><description><c>9101002</c> → <c>9101002</c> = 透德; <c>9101003</c> →
    /// <c>9101003</c> = 彼特.</description></item>
    /// </list>
    /// <para>
    /// Both forms are the same number, so "strip the zeros" answers all six where
    /// the verbatim id alone answered two. This code built the path from
    /// <c>npcId</c> unchanged, so four of the six NPCs on that map had no name.
    /// </para>
    /// </remarks>
    public (string Name, string Func, bool HideName) NpcText(string npcId)
    {
        lock (_session.Gate)
        {
            bool hasHide = false;
            foreach (OpenFile file in _session.SelectRoleSources("Npc", null))
            {
                foreach (string candidate in ImageNames(npcId))
                {
                    string img = Path(file.Id, candidate + ".img");
                    if (_session.TryResolve(img) is not { } imageNode) continue;

                    string name = "";
                    string func = "";
                    foreach (OpenFile strings in _session.SelectRoleSources("String", null))
                    {
                        foreach (string stringId in StringIds(npcId))
                        {
                            string basePath = $"{strings.Id}/Npc.img/{stringId}";
                            if (name.Length == 0
                                && _session.TryResolve($"{basePath}/name") is { } n)
                                name = AsText(n) ?? "";
                            if (func.Length == 0
                                && _session.TryResolve($"{basePath}/func") is { } f)
                                func = AsText(f) ?? "";
                        }
                        if (name.Length > 0 || func.Length > 0) break;
                    }

                    if (_session.TryResolve($"{img}/info/hideName") is { } h)
                    {
                        string raw = AsText(h) ?? "";
                        hasHide = raw.Length > 0 && raw != "0";
                    }
                    return (name, func, hasHide);
                }
            }
            return ("", "", false);
        }
    }

    private static string? AsText(WzObject node) => node switch
    {
        WzStringProperty s => s.Value,
        WzIntProperty i => i.Value.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Whether a mob can land a contact hit, and how hard.</summary>
    /// <remarks>
    /// <para>
    /// The gate and the number both come out of the archive, not out of this
    /// codebase. The C++ client reads the flag and builds the hit from it:
    /// </para>
    /// <code>
    /// Mob.cpp:69   touchdamage = info["bodyAttack"].get_bool();
    /// Mob.cpp:1848 MobAttack Mob::create_touch_attack() const {
    /// Mob.cpp:1850     if (!touchdamage) return MobAttack();
    /// Mob.cpp:1853     int32_t power = watk + status_value(WATK);
    /// Mob.cpp:1856     int32_t attack = randomizer.next_int(minattack, maxattack);
    /// </code>
    /// <para>
    /// This replaces an invented number. <c>MobPatrol.MobDamage</c> used to be
    /// <c>MaxHp / 8</c>, which is not in the reference and is not in the archive:
    /// on the mobs this simulator is pointed at it produced 43 and 37 where the
    /// archive says <c>info/PADamage = 90</c> for both
    /// (<c>f2/2220100.img</c> and <c>f2/2110200.img</c>, measured).
    /// </para>
    /// <para>
    /// What the reference does NOT contain is the TRIGGER: the C++ client
    /// receives the touch attack as a packet from a server
    /// (<c>MapMobs::create_attack</c>, MapMobs.cpp:306-312, only forwards to
    /// <c>create_touch_attack</c>), so when it fires is the server's decision and
    /// there is no client code to copy. This project has no server, so the
    /// trigger stays what the C# reference states -- a mob engages only after
    /// being struck (MapleNecrocer/Client/Mob.cs:434-460) -- and only the
    /// permission and the magnitude come from the archive.
    /// </para>
    /// <para>
    /// Measured over every mob in the open Mob.nx: 2,053 images, all reachable,
    /// <c>info/bodyAttack</c> on 1,945 of them, <c>info/PADamage</c> read by
    /// these two. The 108 without it cannot touch-attack, and now do not.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether a mob will pick the player up WITHOUT being struck first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two references DISAGREE here, and the studio rules (§7.6) make the C++
    /// the authority:
    /// </para>
    /// <code>
    /// MapleNecrocer/Client/Mob.cs:434-460  a mob engages only after being hit
    ///                                    -- "there is no aggro radius in the client"
    /// clean-client/Mob.cpp:79             first_attack = info["firstAttack"].get_bool();
    /// clean-client/Mob.cpp:671            chasing = ... &amp;&amp; (first_attack || aggro);
    /// clean-client/MobAttack.cpp:81       if (first_attack &amp;&amp;
    ///                                        (|dx|/10 + |dy|/3 &lt;= 40 || !available.empty()))
    ///                                    aggro = true;
    /// </code>
    /// <para>
    /// The archive sides with the C++: <b>522 of 2,053</b> mobs carry
    /// <c>info/firstAttack</c>, and it is a bare <c>1</c> with no children (measured
    /// over every image in the open Mob.nx; a 400-name sample read <c>1</c> 43 times
    /// and nothing else). Following the C# would leave that flag dead data across a
    /// quarter of the bestiary.
    /// </para>
    /// <para>
    /// The <c>!available.empty()</c> half of the C++ test is NOT ported.
    /// <b>The reason recorded here before was wrong.</b> It claimed
    /// <c>info/attack1</c> is on 0 of 2,053 mobs, which is what a sweep of
    /// <c>f5/&lt;id&gt;.img/info/attack1</c> reports -- but <c>Mob.cpp:146</c> reads
    /// <c>src["attack1"]["info"]</c>, the ATTACK ACTION's own info node, with
    /// <c>node["info"]</c> as the fallback. Re-measured on two disjoint ~205-image
    /// slices: 87 and 88 mobs (about 43%) carry one, 143 and 152 attack slots in
    /// total. So the branch is reachable for those mobs and is a real gap, not a
    /// branch that "cannot run".
    /// </para>
    /// </remarks>
    public bool MobFirstAttack(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is not { } imageNode) continue;
                        if (!TryReadIntChild(imageNode, "info", "firstAttack", out int v))
                            return false;
                        return v != 0;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The archive's <c>info/noFlip</c> and <c>info/notAttack</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Mob.cpp:73-74</c> reads both once at construction:
    /// <c>noflip = info["noFlip"].get_bool(); notattack = info["notAttack"].get_bool();</c>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>noFlip</b> — <c>MobAttack.cpp:119</c> <c>bool move_facing = noflip ? flip : dx &gt; 0;</c>.
    /// The mob walks toward the player but never turns to face it, so it hits
    /// with the other hand. Measured 17/158 and 16/158 in two disjoint slices.
    /// </description></item>
    /// <item><description>
    /// <b>notAttack</b> — <c>MobAttack.cpp:50</c> returns from the whole attack update.
    /// A mob carrying it can never acquire, never chase and never swing.
    /// <c>f5/9409000.img</c> carries it.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both are read through the same missing-means-false rule as
    /// <see cref="MobFirstAttack"/>: <c>get_bool()</c> on an absent node is false,
    /// and an absent node must not be read as a "yes".
    /// </para>
    /// </remarks>
    public (bool NoFlip, bool NotAttack) MobFlags(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is not { } imageNode) continue;

                        bool noFlip = TryReadIntChild(imageNode, "info", "noFlip", out int nf) && nf != 0;
                        bool notAttack = TryReadIntChild(imageNode, "info", "notAttack", out int na) && na != 0;
                        return (noFlip, notAttack);
                    }
                }
            }
            return (false, false);
        }
    }

    /// <summary>
    /// The mob's walk speed in px/frame, from the archive's <c>info/speed</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Mob.cs:147-151</c>, transcribed:
    /// </para>
    /// <code>
    /// if (InfoNode.Get("speed") != null) Mob.MoveSpeed = (1 + (float)InfoNode.GetInt("speed") / 100) * 2;
    /// else                           Mob.MoveSpeed = 2;
    /// </code>
    /// <para>
    /// 0 means "the node is absent", not "stand still" — the standing case is
    /// <c>Mob.cs:166-170</c>, where a mob with no <c>move</c> node has its
    /// <c>MoveSpeed</c> forced to 0, and the caller decides that from the
    /// animation set. This returns the archive's own number and lets the caller
    /// apply the override.
    /// </para>
    /// <para>
    /// Measured, and it is not a cosmetic difference: the four mobs these tests are
    /// pointed at carry <c>speed</c> of -40 (2220000), 5 (2220100), -20 (2110200)
    /// and -60 (9409000), which are 1.2, 2.1, 1.6 and 0.8 px/frame — every one of
    /// them different from the 2 this simulator hardcoded.
    /// </para>
    /// </remarks>
    public float MobSpeed(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is not { } imageNode) continue;
                        return TryReadIntChild(imageNode, "info", "speed", out int s)
                            ? (1 + s / 100f) * 2f
                            : 2f;
                    }
                }
            }
            return 2f;
        }
    }

    /// <summary>
    /// The weapon's own attack speed, from <c>info/attackSpeed</c>.
    /// </summary>
    /// <remarks>
    /// <c>clean-client\src\Data\WeaponData.cpp:32-35</c>:
    /// <code>
    /// nl::node src = nl::nx::character["Weapon"]["0" + std::to_string(equipid) + ".img"]["info"];
    /// attackspeed = static_cast&lt;uint8_t&gt;(src["attackSpeed"]);
    /// attack     = static_cast&lt;uint8_t&gt;(src["attack"]);
    /// </code>
    /// The leading <c>"0"</c> is the 7-digit normalisation: the equip slot holds
    /// <c>1302000</c> and the image is <c>01302000.img</c>. Both spellings are
    /// tried, for the same reason <see cref="ImageNames"/> yields both.
    ///
    /// <para>
    /// MEASURED, 0 when absent: 01302000=4, 01312000=5, 01322000=5, 01402000=6,
    /// 01412000=6, 01432000=8, 01452000=5. It varies per weapon, which is the whole
    /// point: <c>WeaponData::getspeedstring()</c> reads 1-4 as 快, 5-6 as 普通 and
    /// 7-9 as 慢.
    /// </para>
    /// <para>
    /// This is a SEPARATE axis from <c>info/attack</c>, which
    /// <see cref="AttackStancesFor"/> already reads and which selects the pose
    /// NAME. Nothing read it before: a swing locked its pose for a flat 48 ticks
    /// (800ms) for every weapon in the game.
    /// </para>
    /// </remarks>
    /// <summary>
    /// W-084. How far the character reaches with the weapon in hand.
    /// </summary>
    /// <remarks>
    /// <c>CharStats.cpp:57</c>:
    /// <c>projectilerange = Weapon::is_bow_or_crossbow(weapontype) ? 300 : 250;</c>
    /// and the type is the first four digits of the equip id
    /// (<c>WeaponData.cpp:28</c> <c>int32_t prefix = equipid / 10000;</c>), with
    /// <c>BOW = 145</c> and <c>CROSSBOW = 146</c>
    /// (<c>Character/Inventory/Weapon.h</c>).
    /// <para>
    /// Not read from the archive: the reference does not either, it divides the id.
    /// <c>CharStats.cpp:371</c> <c>add_projectilerange</c> widens it further from a
    /// passive (<c>PassiveBuffs.cpp:98,114</c> read the buff's own `range` node),
    /// and nothing in this port carries buffs, so that half is not modelled.
    /// </para>
    /// </remarks>
    public static int ProjectileRange(string? weaponId)
    {
        if (string.IsNullOrWhiteSpace(weaponId)) return 250;
        if (!int.TryParse(weaponId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int equipid))
            return 250;
        int prefix = equipid / 10000;
        return prefix is 145 or 146 ? 300 : 250;
    }

    /// <summary>
    /// Whether the equipped weapon is a two-hander, as the reference decides it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WeaponData.cpp:33-35</c>:
    /// </para>
    /// <code>
    /// int32_t prefix = equipid / 10000;
    /// twohanded = prefix == Weapon::Type::STAFF
    ///           || (prefix >= Weapon::Type::SWORD_2H && prefix <= Weapon::Type::POLEARM)
    ///           || prefix == Weapon::Type::CROSSBOW;
    /// </code>
    /// <para>
    /// The enum values are in <c>Character/Inventory/Weapon.h</c>: STAFF 138,
    /// SWORD_2H 140, AXE_2H 141, MACE_2H 142, SPEAR 143, POLEARM 144,
    /// CROSSBOW 146. So the two-handed set is <b>138, 140-144, 146</b> — and
    /// note that 145 (BOW) is NOT one of them, which is why the reference lists
    /// CROSSBOW separately.
    /// </para>
    /// <para>
    /// Not read from the archive, because the reference does not either: this is
    /// a property of the weapon TYPE, not a flag in the item's data.
    /// </para>
    /// <para>
    /// Why it matters here: <c>SkillAction.cpp:58-70</c> —
    /// <c>actions[false] = src["action"]["0"]</c> is the ONE-handed pose and
    /// <c>actions[true] = src["action"]["1"]</c> the TWO-handed one, chosen by
    /// <c>target.is_twohanded()</c>. A skill with two actions and a one-handed
    /// weapon therefore plays <c>action/0</c>, and with a two-handed weapon
    /// <c>action/1</c>. Reading index 0 always left the second pose unreachable:
    /// MEASURED 2026-10-18, 1121008 cast three times and <c>brandish2</c> never
    /// appeared.
    /// </para>
    /// </remarks>
    public static bool IsTwoHanded(string? weaponId)
    {
        if (string.IsNullOrWhiteSpace(weaponId)) return false;
        if (!int.TryParse(weaponId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int equipid))
            return false;
        int prefix = equipid / 10000;
        return prefix == 138
            || (prefix >= 140 && prefix <= 144)
            || prefix == 146;
    }

    public int WeaponAttackSpeed(string? weaponId)
    {
        if (string.IsNullOrWhiteSpace(weaponId))
            return 0;
        if (!int.TryParse(weaponId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return 0;

        lock (_session.Gate)
        {
            // The RAW id first, and only then the reformatted ones. The equip slot
            // and the frame already carry "01302000"; parsed as an int that is
            // 1302000, whose D7 form is "1302000" -- a DIFFERENT image. That is
            // what made this return 0 for every weapon and left every swing on the
            // unarmed 1.7x clock. The C++ avoids it with the literal
            // `"0" + std::to_string(equipid)`; trying the string as given covers the
            // same ground without reformatting anything.
            var names = new List<string> { weaponId.Trim() };
            if (int.TryParse(weaponId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId))
            {
                string d7 = parsedId.ToString("D7", CultureInfo.InvariantCulture);
                string plain = parsedId.ToString(CultureInfo.InvariantCulture);
                foreach (string extra in new[] { d7, "0" + d7, plain })
                    if (!names.Contains(extra)) names.Add(extra);
            }

            foreach (OpenFile file in _session.SelectRoleSources("Character", null))
            {
                foreach (string candidate in names)
                {
                    foreach (string prefix in new[] { "Weapon", "Character/Weapon" })
                    {
                        string imagePath = Path(file.Id, prefix, candidate + ".img");
                        if (_session.TryResolve(imagePath) is { } image
                            && TryReadIntChild(image, "info", "attackSpeed", out int speed))
                            return Math.Max(0, speed);
                    }
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// The archive's own frame delays, in ms, for one action of the BODY image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MapleNecrocer\Client\MapleCharacter.cs:392-397</c> writes them into
    /// <c>Equip.Data</c> at equip time and <c>:1120-1121</c> reads them every frame:
    /// </para>
    /// <code>
    /// foreach (var Iter2 in Iter.Nodes)
    ///     if (Part == PartName.Body)
    ///         Equip.Data.AddOrReplace("body/" + Iter.Text + "/" + Iter2.Text + "/delay",
    ///                                 Math.Abs(Iter2.GetInt("delay")));
    /// </code>
    ///
    /// <para>
    /// <b>The delay is on the BODY image, not the weapon's.</b> MEASURED:
    /// <c>Character/00002000.img/swingO1/{0,1,2}/delay</c> = 300 / 150 / 350 (800ms
    /// total) and <c>swingT1</c> the same, while
    /// <c>Character/Weapon/01302000.img/swingO1/{0,1,2}</c> carries only a
    /// <c>weapon</c> canvas and no delay node at all. Reading the weapon's frames
    /// returns nothing.
    /// </para>
    /// <para>
    /// Empty when the action or the archive is missing, which is the caller's cue
    /// to fall back -- and the fallback is the archive-free number, not a guess at
    /// the archive's.
    /// </para>
    /// </remarks>
    public int[] BodyActionDelays(string? stance)
    {
        if (string.IsNullOrWhiteSpace(stance))
            return [];

        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Character", null))
            {
                foreach (string prefix in new[] { "", "Character/" })
                {
                    string actionPath = Path(file.Id, prefix, LookService.BodyImage, stance);
                    if (_session.TryResolve(actionPath) is not { } action)
                        continue;

                    List<int> delays = [];
                    foreach (WzObject frame in Children(action).OrderBy(f => int.TryParse(f.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : int.MaxValue))
                    {
                        if (!int.TryParse(frame.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                            continue;
                        // Math.Abs, as MapleCharacter.cs:395 does -- a negative delay
                        // must not stall the frame clock forever.
                        if (TryFindChild(frame, "delay", out WzObject? delayNode) && delayNode is not null
                            && TryReadInt(delayNode, out int d))
                        {
                            delays.Add(Math.Abs(d));
                        }
                        else
                        {
                            // BodyDrawInfo.cpp:56-61 -- `delay <= 0 -> 100`. A frame
                            // with no delay node is not a frame that lasts forever.
                            delays.Add(100);
                        }
                    }
                    if (delays.Count > 0)
                        return delays.ToArray();
                }
            }
            return [];
        }
    }

    /// <summary>
    /// How fast this weapon's swing pose runs, as the client's multiplier.
    /// </summary>
    /// <remarks>
    /// <c>clean-client\src\Character\Char.cpp:607-613</c>:
    /// <code>
    /// float Char::get_real_attackspeed() const
    /// {
    ///     int8_t speed = get_integer_attackspeed();
    ///     return 1.7f - static_cast&lt;float&gt;(speed) / 10;
    /// }
    /// </code>
    /// and <c>Player.cpp:1199-1212</c> builds that speed as
    /// <c>stats.get_attackspeed() + weapon.get_speed()</c>. This port has no stat
    /// sheet, so only the weapon's own half is there -- stated rather than hidden,
    /// because a character with a stat sheet would swing faster than this.
    ///
    /// <para>
    /// <b>The multiplier divides, and a bigger multiplier is FASTER</b>: a HIGHER
    /// <c>attackSpeed</c> gives a SMALLER <c>fspeed</c>, so the same frame delays
    /// take LONGER. That is the right way round and matches
    /// <c>WeaponData::getspeedstring()</c> reading 7-9 as 慢.
    /// </para>
    /// </remarks>
    public static float RealAttackSpeed(int weaponAttackSpeed)
        => 1.7f - weaponAttackSpeed / 10f;

    /// <summary>
    /// How many ticks one swing pose should lock, from the archive and the weapon.
    /// </summary>
    /// <remarks>
    /// The pose's own length comes from the body image's frame delays (see
    /// <see cref="BodyActionDelays"/>); the weapon scales it by
    /// <see cref="RealAttackSpeed"/>. MEASURED against the open archives, using
    /// swingO1's 300/150/350 = 800ms:
    /// <list type="bullet">
    /// <item><description>01302000 (1H sword, attackSpeed 4) -> 615ms -> 37 ticks</description></item>
    /// <item><description>01402000 (2H sword, 6) -> 727ms -> 44 ticks</description></item>
    /// <item><description>01432000 (2H, 8) -> 889ms -> 53 ticks</description></item>
    /// <item><description>unarmed (0) -> 471ms -> 28 ticks</description></item>
    /// </list>
    /// against a flat 48 ticks for every weapon before this.
    /// </remarks>
    public static int SwingPoseTicks(int[] frameDelaysMs, int weaponAttackSpeed, int fallbackTicks)
    {
        if (frameDelaysMs.Length == 0)
            return fallbackTicks;

        float fspeed = RealAttackSpeed(weaponAttackSpeed);
        if (fspeed <= 0.01f)
            return fallbackTicks;

        double totalMs = frameDelaysMs.Sum() / fspeed;
        int ticks = (int)Math.Round(totalMs / (1000.0 / 60.0));
        return Math.Max(1, ticks);
    }

    /// <summary>
    /// How many ticks after the swing starts its hit actually lands.
    /// </summary>
    /// <remarks>
    /// <c>Char.cpp:615-622</c>:
    /// <code>
    /// uint16_t Char::get_attackdelay(size_t no) const
    /// {
    ///     uint8_t  first_frame = afterimage.get_first_frame();
    ///     uint16_t delay = look.get_attackdelay(no, first_frame);
    ///     float    fspeed = get_real_attackspeed();
    ///     return static_cast&lt;uint16_t&gt;(delay / fspeed);
    /// }
    /// </code>
    /// The hit waits for the frames BEFORE the first attack frame, on the same
    /// weapon-scaled clock as the pose. Before this the hit was applied inside
    /// the same <c>Swing</c> call that the attack key entered -- no delay at all.
    /// </remarks>
    public static int SwingHitDelayTicks(int[] frameDelaysMs, int weaponAttackSpeed, int fallbackTicks)
    {
        if (frameDelaysMs.Length <= 1)
            return 0;

        float fspeed = RealAttackSpeed(weaponAttackSpeed);
        if (fspeed <= 0.01f)
            return fallbackTicks;

        // The hit lands on the first frame, so the delay is every delay BEFORE it.
        double leadMs = frameDelaysMs.Take(frameDelaysMs.Length - 1).Sum() / fspeed;
        return Math.Max(0, (int)Math.Round(leadMs / (1000.0 / 60.0)));
    }

    /// <summary>
    /// One of a mob's attacks, read from <c>&lt;img&gt;/attackN/info</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built by <c>Mob.cpp:142-177</c>, which loops <c>for (uint8_t index = 0;
    /// index &lt; 8; ++index)</c> over <c>"attack" + (index + 1)</c>, takes its
    /// data from <c>src[action]["info"]</c> (falling back to the same node read
    /// off the action), and skips the slot unless BOTH a frame 0 and that info
    /// node exist.
    /// </para>
    /// <para>
    /// MEASURED over two disjoint ~205-image slices of Mob.nx: 87 and 88 mobs
    /// (about 43%) carry one, with 143 and 152 attack slots between them. The
    /// <c>type</c> distribution over 143 slots is 0 = 76, absent = 20 (which the
    /// C++ reads as 0), 2 = 21, 1 = 18, 3 = 8.
    /// </para>
    /// </remarks>
    public sealed record MobAttackSpec(
        int Index,
        int Type,
        int Power,
        bool Magic,
        int ConMp,
        int DelayMs,
        bool Inactive,
        Box Range,
        int SourceX,
        int SourceY,
        int Radius,
        int AreaStart,
        int AreaCount,
        int AttackCount,
        int DurationMs,
        /// <summary>
        /// <c>info/bulletSpeed</c>, <c>Mob.cpp:161</c>: a default of 100, clamped
        /// to at least 1. MEASURED 2026-10-16 over the archive: present on 77 of
        /// 1536 attack slots, ten distinct values, and one NEGATIVE (-200).
        /// </summary>
        int BulletSpeed,
        /// <summary>
        /// The sprite-cache action name for this attack's projectile art, or ""
        /// when it has none — <c>Mob.cpp:165</c>, <c>attack.ball =
        /// data["ball"]</c>.
        /// </summary>
        /// <remarks>
        /// MEASURED over the open Mob.nx: <c>ball</c> is present on 172 of 1,536
        /// attack slots. <c>Mob.cpp:1121</c> draws it only for
        /// <c>geometry.type == 2 &amp;&amp; has_ball &amp;&amp; entry.elapsed &gt;=
        /// entry.delay</c>, so a type-0 attack's <c>ball</c> is never drawn and
        /// this name goes unread for it.
        /// </remarks>
        string BallAction,
        /// <summary>How many frames the projectile art carries. 0 when it has none.</summary>
        int BallFrameCount,
        /// <summary>
        /// Each frame of the projectile art's own delay, in ms, in frame order.
        /// Empty when the art has none.
        /// </summary>
        /// <remarks>
        /// <c>Animation.cpp:64-67</c>:
        /// <c>delay = src["delay"]; if (delay == 0) delay = 100;</c> — the 100 is
        /// the FALLBACK for a frame with no delay node, not the rate every ball
        /// runs at. MEASURED 2026-10-18: the ball's frame index was advanced by a
        /// hardcoded 100 ms per index, so a ball whose frames declare 30 ms or
        /// 60 ms animated at the wrong rate, and `ballart-verify` reported "the
        /// ball frame index never advances".
        /// </remarks>
        IReadOnlyList<int> BallFrameDelays)
    {
        /// <summary>The action name this attack plays: <c>attack1</c> … <c>attack8</c>.</summary>
        public string Action => "attack" + Index.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether the segment <c>(fromX,fromY)-(toX,toY)</c> crosses a rectangle.
        /// </summary>
        /// <remarks>
        /// <c>MobAttackGeometry.h:30-44</c>, a Liang-Barsky clip: `clip(p, q)` for
        /// each of the four edges, accumulating an entry and an exit parameter and
        /// answering true while entry &lt;= exit.
        ///
        /// Needed because a type-2 attack is a PROJECTILE: at 350 px/s it covers
        /// ~5.8px per frame and at 200 px/s over a 400px radius its whole flight
        /// lasts three seconds — but a 44px-wide character standing still can still
        /// be crossed in one step, and a box test at the current position would
        /// report it was never touched. The reference tests the SEGMENT for
        /// exactly this reason (MobAttack.cpp:205-209).
        /// </remarks>
        public static bool Crosses(double fromX, double fromY, double toX, double toY, Box rect)
        {
            double entry = 0.0, exit = 1.0;
            bool Clip(double p, double q)
            {
                if (p == 0) return q >= 0;
                double t = q / p;
                if (p < 0) entry = Math.Max(entry, t);
                else exit = Math.Min(exit, t);
                return entry <= exit;
            }

            return Clip(fromX - toX, fromX - rect.Left)
                && Clip(toX - fromX, rect.Right - fromX)
                && Clip(fromY - toY, fromY - rect.Top)
                && Clip(toY - fromY, rect.Bottom - fromY);
        }

        /// <summary>
        /// Whether this attack reaches the target, transcribed from
        /// <c>MobAttackGeometry.h:98-117</c>.
        /// </summary>
        /// <param name="originX">The mob's own x.</param>
        /// <param name="originY">The mob's own y (its feet).</param>
        /// <param name="flipped">
        /// <c>current_facing &amp;&amp; !noflip</c> (MobAttack.cpp:69) — whether the
        /// rectangle is mirrored.
        /// </param>
        /// <param name="target">
        /// The character's body rectangle. <c>MobAttack.cpp:57-58</c> builds it as
        /// <c>{-22, +22, -65, 0}</c> around the character.
        /// </param>
        public bool Reaches(int originX, int originY, bool flipped, Box target)
        {
            if (Type == 0)
                return World(Range, originX, originY, flipped).Overlaps(target);

            if (Type is 1 or 2)
            {
                // MobAttackGeometry.h:104-105 -- THE CONE DOES NOT START AT THE MOB.
                //
                //     auto start = launch(origin, flipped);
                //     return check_cone(start, radius, target, flipped);
                //
                // `launch` (:52-55) is `{origin.x + (flipped ? -source.x : source.x),
                // origin.y + source.y}` — the hand/mouth point of the attack, mirrored
                // by the facing. MEASURED on the open archive, `source.x` is -93 on
                // 4220000 attack1, -129 on 8220004, -111 on 9500308: a cone drawn from
                // the mob's own feet sits 93..129px away from the one the reference
                // tests, so it fires at ranges it should not and stays silent at the
                // ones it should. Both axes move, which also moves the cone's
                // half-height origin.
                int startX = originX + (flipped ? -SourceX : SourceX);
                int startY = originY + SourceY;

                // check_cone, MobAttackGeometry.h:60-96. The cone is sliced in
                // 20px columns whose half-height grows as dist_x/4.
                if (Radius <= 0) return false;
                int endX = flipped ? startX + Radius : startX - Radius;
                int curX = startX, distX = 0;
                bool toLeft = startX > endX;
                while (toLeft ? curX > endX : curX < endX)
                {
                    int sliceLeft, sliceRight;
                    if (toLeft)
                    {
                        sliceRight = curX;
                        curX -= 20;
                        distX += 20;
                        sliceLeft = Math.Max(endX, curX);
                    }
                    else
                    {
                        sliceLeft = curX;
                        curX += 20;
                        distX += 20;
                        sliceRight = Math.Min(endX, curX);
                    }
                    int halfH = distX / 4;
                    Box slice = new(sliceLeft, startY - halfH, sliceRight, startY + halfH + 1);
                    if (slice.Overlaps(target)) return true;
                }
                return false;
            }

            if (Type is 3 or 4 && AreaCount > 0)
            {
                // MobAttackGeometry.h:107-115: the union of the area cells.
                int first = AreaX(0, originX, flipped, Range);
                int last = AreaX(AreaCount - 1, originX, flipped, Range);
                int minX = Math.Min(first, last) - Range.Width / 2;
                int maxX = Math.Max(first, last) + Range.Width / 2;
                return new Box(minX, originY + Range.Top, maxX, originY + Range.Bottom).Overlaps(target);
            }

            return false;
        }

        /// <summary>
        /// Where an area cell sits, <c>MobAttackGeometry.h:56-59</c>.
        /// </summary>
        private int AreaX(int cell, int originX, bool flipped, Box range) =>
            originX + (flipped ? 1 : -1) * (range.Width / 2 + range.Width * (cell + AreaStart));

        /// <summary>
        /// The rectangle in world space, <c>MobAttackGeometry.h:45-51</c>: mirrored
        /// by the facing, then shifted onto the mob.
        /// </summary>
        /// <remarks>
        /// PUBLIC because <c>MobAttack.cpp:147</c> builds the attack entry's box with
        /// it too — <c>entry.range = world(data.geometry.range, origin, flipped
        /// &amp;&amp; !noflip)</c> — and an entry that shifted without mirroring is a
        /// different rectangle on the half of the mobs that face left.
        /// </remarks>
        public static Box World(Box rect, int originX, int originY, bool flipped)
            => (flipped ? new Box(-rect.Right, rect.Top, -rect.Left, rect.Bottom) : rect)
                .Shift(originX, originY);
    }

    /// <summary>
    /// Every attack this mob has, or an empty list.
    /// </summary>
    /// <remarks>
    /// The list is the reference's <c>attacks</c> map, built once per mob the way
    /// the reference builds it once per mob (<c>Mob.cpp:142-177</c> is in the
    /// constructor). Callers cache it; re-reading the archive per frame would be
    /// a different program from the one being ported.
    /// </remarks>
    public IReadOnlyList<MobAttackSpec> MobAttacks(string mobId)
    {
        var result = new List<MobAttackSpec>();
        if (string.IsNullOrWhiteSpace(mobId))
            return result;

        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        if (_session.TryResolve(Path(file.Id, prefix, candidate + ".img")) is not { } image)
                            continue;

                        // Mob.cpp:59-61 -- `watk = info["PADamage"]; matk =
                        // info["MADamage"];`, the MOB'S OWN numbers, and the
                        // fallback for every attack that declares no damage of
                        // its own. Read from the mob's own info, never from the
                        // mob its `info/link` points at: the link borrows
                        // GRAPHICS only (Mob.cpp:48-57). Read before the loop
                        // because it does not vary with the attack.
                        int mobWatk = TryReadIntChild(image, "info", "PADamage", out int ownWatk) ? ownWatk : 0;
                        int mobMatk = TryReadIntChild(image, "info", "MADamage", out int ownMatk) ? ownMatk : 0;

                        // Mob.cpp:48-57 -- `info/link` borrows another mob's
                        // GRAPHICS, and the attack's animation comes from the mob
                        // that owns the graphics.
                        WzObject animSource = AnimationSource(file, image);

                        // Mob.cpp:142-177, transcribed.
                        for (int i = 1; i <= 8; i++)
                        {
                            string action = "attack" + i.ToString(CultureInfo.InvariantCulture);
                            // `nl::node node = anisrc[action];`
                            if (!TryFindChild(animSource, action, out WzObject? act) || act is null)
                                continue;
                            // `if (!node["0"] || !data) continue;` -- both must exist.
                            if (!TryFindChild(act, "0", out _))
                                continue;
                            // `nl::node data = src[action]["info"]; if (!data)
                            // data = node["info"];` -- the attack's own numbers
                            // come from the mob that OWNS the attack, and only
                            // from the borrowed mob's attack when the owner
                            // declares no info of its own.
                            WzObject? info = null;
                            if (TryFindChild(image, action, out WzObject? ownAct) && ownAct is not null)
                                TryFindChild(ownAct, "info", out info);
                            if (info is null)
                                TryFindChild(act, "info", out info);
                            if (info is null)
                                continue;

                            int type = ReadInt(info, "type") ?? 0;
                            bool magic = (ReadInt(info, "magic") ?? 0) != 0;

                            // Mob.cpp:160
                            //   attack.power = attack.magic ? (MADamage ? MADamage : matk)
                            //                                          : (PADamage ? PADamage : watk);
                            // The condition in each ternary is an nl::node, so it is
                            // the node's EXISTENCE (node.cpp:152-154), never its
                            // value: a PADamage of 0 is this attack's own answer and
                            // must NOT fall back to the mob's. The fallback is the
                            // mob's own number, and a STRING is parsed
                            // (node.cpp:168-169 is std::stoll), so 8820003/attack3's
                            // "900" is 900 and not the mob's 1440.
                            //
                            // MEASURED 2026-10-22 with attack-damage.py over Mob.nx:
                            // 1,162 of the archive's 1,677 attack nodes declare no
                            // damage of their own, and 1,148 of those had a mob stat
                            // that is not 0. The old `?? 0` therefore published a 0
                            // damage for attacks the reference gives a number, and
                            // MobPatrol.cs adds this value to what the player takes
                            // -- they hit, and did nothing.
                            int power = magic
                                ? (TryFindChild(info, "MADamage", out _)
                                    ? ReadInt(info, "MADamage") ?? 0
                                    : mobMatk)
                                : (TryFindChild(info, "PADamage", out _)
                                    ? ReadInt(info, "PADamage") ?? 0
                                    : mobWatk);

                            // Mob.cpp:169-175
                            int rangeLeft = 0, rangeTop = 0, rangeRight = 0, rangeBottom = 0;
                            if (TryFindChild(info, "range", out WzObject? range) && range is not null
                                && TryReadVectorChild(range, "lt", out int lx, out int ly)
                                && TryReadVectorChild(range, "rb", out int rx, out int ry))
                            {
                                // Rectangle.h:30 takes them in node order, but a map
                                // that writes them the other way round must still give
                                // a usable rectangle.
                                rangeLeft = Math.Min(lx, rx);
                                rangeRight = Math.Max(lx, rx);
                                rangeTop = Math.Min(ly, ry);
                                rangeBottom = Math.Max(ly, ry);
                            }
                            int spx = 0, spy = 0;
                            if (TryFindChild(info, "range", out WzObject? r2) && r2 is not null)
                                TryReadVectorChild(r2, "sp", out spx, out spy);
                            int radius = TryFindChild(info, "range", out WzObject? r3) && r3 is not null
                                ? Math.Clamp(ReadInt(r3, "r") ?? 0, 0, MaxAttackRadiusPx) : 0;
                            int areaStart = TryFindChild(info, "range", out WzObject? r4) && r4 is not null
                                ? ReadInt(r4, "start") ?? 0 : 0;
                            int areaCount = TryFindChild(info, "range", out WzObject? r5) && r5 is not null
                                ? ReadInt(r5, "areaCount") ?? 0 : 0;
                            int attackCount = TryFindChild(info, "range", out WzObject? r6) && r6 is not null
                                ? ReadInt(r6, "attackCount") ?? 0 : 0;

                            result.Add(new MobAttackSpec(
                                Index: i,
                                Type: type,
                                Power: power,
                                Magic: magic,
                                // Mob.cpp:155 -- conMP, and the MP question is NOT
                                // resolved (see the journal); the value is read so the
                                // cost is visible, not spent.
                                ConMp: ReadInt(info, "conMP") ?? 0,
                                // Mob.cpp:153 -- `attackAfter`, the delay before the
                                // attack's hit resolves.
                                DelayMs: Math.Max(0, ReadInt(info, "attackAfter") ?? 0),
                                Inactive: (ReadInt(info, "inactive") ?? 0) != 0,
                                Range: new Box(rangeLeft, rangeTop, rangeRight, rangeBottom),
                                SourceX: spx,
                                SourceY: spy,
                                Radius: radius,
                                AreaStart: areaStart,
                                AreaCount: areaCount,
                                AttackCount: attackCount,
                                // Mob.cpp:161 — `bullet_speed = node["bulletSpeed"]`,
                                // defaulting to 100 and floored at 1.
                                BulletSpeed: Math.Max(1,
                                    ReadInt(info, "bulletSpeed") is { } bs ? bs : 100),
                                // Mob.cpp:151-152 -- the sum of the action's own frame
                                // delays, which is also the pose's real length.
                                DurationMs: ActionDelaysMs(act),
                                // Mob.cpp:163-165 -- `attack.has_ball = bool(data["ball"]["0"])`,
                                // `attack.effect = data["effect"]` and `attack.ball = data["ball"]`.
                                // The art is what Mob.cpp:1121 draws for a type-2
                                // entry, so without it a mob's projectile is a
                                // position with no picture.
                                BallAction: BallActionName(action, info, act),
                                BallFrameCount: BallFrameCount(info, act),
                                BallFrameDelays: BallFrameDelays(info, act)));
                        }
                        if (result.Count > 0)
                            return result;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The mob image an attack's ANIMATION comes from: the mob's own, or the
    /// one its <c>info/link</c> borrows.
    /// </summary>
    /// <remarks>
    /// <c>Mob.cpp:48-57</c>:
    /// <code>
    /// nl::node anisrc = src;
    /// if (nl::node link = info["link"])
    /// {
    ///     std::string linkstrid = string_format::extend_id(link.get_integer(), 7);
    ///     nl::node linksrc = nl::nx::mob[linkstrid + ".img"];
    ///     if (linksrc["stand"] || linksrc["fly"])
    ///         anisrc = linksrc;
    /// }
    /// </code>
    /// ⇒ the link borrows GRAPHICS ONLY. The mob's own numbers
    /// (<c>info/PADamage</c>, <c>info/MADamage</c>, <c>Mob.cpp:60-61</c>) and the
    /// attack's own info node are still read from the mob that OWNS the attack.
    /// <para>
    /// MEASURED 2026-10-22 over the real Mob.nx with attack-frame-timing.py:
    /// 439 of the 2,100 mob images carry an <c>info/link</c>, and 237 of those
    /// have no attack node of their own at all -- for those the reference gives
    /// the borrowed mob's attacks and the port published none. 3400000 is one:
    /// its link names 9410005, whose attack1 is 1,800 ms.
    /// </para>
    /// </remarks>
    private WzObject AnimationSource(OpenFile file, WzObject image)
    {
        if (!TryReadIntChild(image, "info", "link", out int linkId))
            return image;

        // `extend_id(link.get_integer(), 7)` -- the padded spelling first, the
        // same list every other mob lookup in this file uses.
        foreach (string name in ImageNames(linkId.ToString(CultureInfo.InvariantCulture)))
        {
            foreach (string prefix in new[] { "", "Mob/" })
            {
                if (_session.TryResolve(Path(file.Id, prefix, name + ".img")) is not { } linked)
                    continue;
                // `if (linksrc["stand"] || linksrc["fly"]) anisrc = linksrc;`
                // -- a borrowed mob that has neither is not borrowed at all.
                if (TryFindChild(linked, "stand", out _) || TryFindChild(linked, "fly", out _))
                    return linked;
                return image;
            }
        }
        return image;
    }

    /// <summary>Sum of a mob action's per-frame delays, in ms.</summary>
    /// <remarks>
    /// <c>Animation.cpp:58-67</c>: <c>delay = src["delay"]; if (delay == 0)
    /// delay = 100;</c> -- the 100 is the FALLBACK, so a frame with no delay
    /// node contributes 100 and a frame that declares 0 contributes 100 as
    /// well. This is the rule <see cref="BallFrameDelays"/> already applies to
    /// the ball's frames, and they are the same rule on purpose: the mob
    /// action's length and the projectile art's tempo both come from that one
    /// constructor, so a port that applies it to one and not the other is
    /// reading the archive two different ways in one file.
    /// <para>
    /// MEASURED 2026-10-22 with attack-frame-timing.py over the real Mob.nx:
    /// 85 slots disagreed on duration, the first being 2400049/attack2 at
    /// 1,800 ms against the reference's 2,300 ms, and 2400049/attack3 -- whose
    /// 20 frames declare no delay at all -- reporting 0 ms against 2,000 ms.
    /// </para>
    /// </remarks>
    private int ActionDelaysMs(WzObject action)
    {
        int total = 0;
        foreach (WzObject frame in Children(action))
        {
            if (!int.TryParse(frame.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                continue;
            int delay = ReadInt(frame, "delay") ?? 0;
            total += delay == 0 ? 100 : delay;
        }
        return total;
    }

    /// <summary>
    /// The sprite-cache action name for an attack's projectile art, or "" —
    /// <c>Mob.cpp:165</c>, <c>attack.ball = data["ball"]</c>.
    /// </summary>
    /// <remarks>
    /// The node is read off the ATTACK's info and, failing that, off the action
    /// itself — the same two-node rule the rest of the spec uses
    /// (<c>Mob.cpp:146-147</c>). The name reported is the one
    /// <c>MobSpriteService</c> publishes for that node, which is
    /// <c>&lt;attack&gt;/ball</c>.
    /// </remarks>
    private string BallActionName(string actionName, WzObject info, WzObject action)
    {
        return FindBallNode(info, action) is null ? "" : actionName + "/ball";
    }

    /// <summary>How many numbered frames an attack's <c>ball</c> carries.</summary>
    private int BallFrameCount(WzObject info, WzObject action)
    {
        WzObject? ball = FindBallNode(info, action);
        if (ball is null)
            return 0;
        int count = 0;
        foreach (WzObject frame in Children(ball))
            if (int.TryParse(frame.Name, NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out _))
                count++;
        return count;
    }

    /// <summary>
    /// Each frame of an attack's <c>ball</c> own delay, in frame order.
    /// </summary>
    /// <remarks>
    /// <c>Animation.cpp:64-67</c>: <c>delay = src["delay"]; if (delay == 0)
    /// delay = 100;</c> — the 100 is the FALLBACK, so a frame with no delay node
    /// contributes 100 and a frame with one contributes its own.
    /// </remarks>
    private IReadOnlyList<int> BallFrameDelays(WzObject info, WzObject action)
    {
        WzObject? ball = FindBallNode(info, action);
        if (ball is null)
            return Array.Empty<int>();
        var frames = new List<(int Id, WzObject Node)>();
        foreach (WzObject frame in Children(ball))
            if (int.TryParse(frame.Name, NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out int id))
                frames.Add((id, frame));
        frames.Sort((a, b) => a.Id.CompareTo(b.Id));
        var delays = new List<int>(frames.Count);
        foreach ((int _, WzObject node) in frames)
        {
            int delay = 0;
            foreach (WzObject child in Children(node))
            {
                if (!string.Equals(child.Name, "delay", StringComparison.OrdinalIgnoreCase))
                    continue;
                delay = child switch
                {
                    WzIntProperty i => i.Value,
                    WzLongProperty l => (int)Math.Clamp(l.Value, 0L, int.MaxValue),
                    WzShortProperty s => s.Value,
                    _ => 0,
                };
                break;
            }
            delays.Add(delay == 0 ? 100 : delay);
        }
        return delays;
    }

    /// <summary>
    /// The <c>ball</c> node an attack declares, or null when it declares none that
    /// has a frame 0 — <c>Mob.cpp:163</c>, <c>bool(data["ball"]["0"])</c>.
    /// </summary>
    private WzObject? FindBallNode(WzObject info, WzObject action)
    {
        foreach (WzObject host in new[] { info, action })
        {
            foreach (WzObject child in Children(host))
            {
                if (!child.Name.Equals("ball", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (WzObject frame in Children(child))
                {
                    if (frame.Name == "0")
                        return child;
                }
            }
        }
        return null;
    }

    /// <summary>An integer child of a node, or null when it is absent.</summary>
    private int? ReadInt(WzObject node, string key)
    {
        if (!TryFindChildStatic(node, key, out WzObject? leaf) || leaf is null)
            return null;
        return leaf switch
        {
            WzIntProperty i => i.Value,
            WzShortProperty s => s.Value,
            // Under NX these nodes arrive as WzLongProperty. Without this case
            // `type`, `PADamage`, `conMP`, `attackAfter` and every per-frame
            // `delay` read as null and the whole spec came back zeroed while
            // still LOOKING right -- the ranges come from WzVectorProperty and
            // were fine, so a spot check on geometry passed and the damage and
            // the timing did not.
            WzLongProperty l => (int)Math.Clamp(l.Value, int.MinValue, int.MaxValue),
            WzStringProperty str when int.TryParse(
                str.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Ceiling on an attack radius, in world pixels. MEASURED, not guessed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED 2026-10-20 over the real <c>Mob.nx</c> (710,584 nodes, 332 of them
    /// named <c>r</c>): min -1000, median 280, p99 700, <b>max 1200</b>. The corpus
    /// never approaches this ceiling.
    /// </para>
    /// <para>
    /// What it buys is bounded work, because the cone test slices the range in 20px
    /// columns — iterations are radius/20 — and it is called twice per attack spec
    /// (facing and flipped facing) for every mob on every frame. An unclamped
    /// radius of 2,000,000,000 is a hundred million iterations for ONE call, and
    /// the value comes out of the archive, so no request asked for it.
    /// 20,000px is roughly sixteen times the widest measured attack and over ten
    /// times the widest map, while capping a call at a thousand iterations.
    /// </para>
    /// </remarks>
    private const int MaxAttackRadiusPx = 20_000;

    private bool TryFindChildStatic(WzObject node, string name, out WzObject? found)
    {
        foreach (WzObject child in Children(node))
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return true;
            }
        }
        found = null;
        return false;
    }

    public (bool CanTouch, int Damage) MobTouchAttack(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is not { } imageNode) continue;

                        // Mob.cpp:1850 -- the flag gates everything, and an absent
                        // one is a "no", not a "yes": `get_bool()` on a missing node
                        // is false, and a mob with no bodyAttack must not hit.
                        if (!TryReadIntChild(imageNode, "info", "bodyAttack", out int body)
                            || body == 0)
                            return (false, 0);

                        // `watk` in the client is the mob's own physical attack; with
                        // no stat model in this port that IS the archive's number.
                        //
                        // Mob.cpp:60 `watk = info["PADamage"];` -- and an ABSENT node
                        // is 0, not 1: vendor/NoLifeNx/nlnx/node.cpp:155-157
                        // `if (!m_data) return def;` with def = 0. The old
                        // `Math.Max(1, pd)` turned the archive's own 0 into a 1, so
                        // every mob whose PADamage is 0 hit for 1 point. The
                        // reference hits for 0: Mob.cpp:1856 rolls
                        // `next_int(minattack, maxattack)` with both at 0, and
                        // Randomizer.h:72-73 `if (from >= to) return from;` returns
                        // 0 there. Measured on the open Mob.nx: 25 of the mobs that
                        // carry `info/bodyAttack` carry `info/PADamage = 0`, and
                        // every one of them was hitting the character for 1.
                        return TryReadIntChild(imageNode, "info", "PADamage", out int pd)
                            ? (true, pd)
                            : (true, 0);
                    }
                }
            }
            return (false, 0);
        }
    }

    /// <summary>
    /// The mob's physical defence, from its own <c>info/PDDamage</c>, or 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// C++ <c>Mob.cpp:62</c>: <c>wdef = info["PDDamage"];</c> — and
    /// <c>Mob.cpp:1577-1601</c> subtracts 0.6 of it from a minimum swing and 0.5
    /// from a maximum one.
    /// </para>
    /// <para>
    /// MEASURED 2026-10-16 over 343 sampled mobs: <c>info/PDDamage</c> is present
    /// on 342. An earlier note in this project called the node <c>info/wdef</c>;
    /// <b>no mob on this archive has a <c>wdef</c> node at all</b>, and that wrong
    /// name is why the defence term had been dropped from the damage roll as
    /// "unavailable" when it was available the whole time.
    /// </para>
    /// </remarks>
    public int MobDefence(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is { } imageNode
                            && TryReadIntChild(imageNode, "info", "PDDamage", out int wdef))
                            return Math.Max(0, wdef);
                    }
                }
            }
            return 0;
        }
    }

    /// <summary>The mob's level, or 0 when the archive does not say.</summary>
    public int MobLevel(string mobId)    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is { } imageNode
                            && TryReadIntChild(imageNode, "info", "level", out int level)
                            && level > 0)
                            return level;
                    }
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// The EXP a kill of this mob awards, from its archive's <c>info/exp</c>.
    /// </summary>
    /// <remarks>
    /// Same lookup shape as <see cref="MobMaxHp"/> and <see cref="MobLevel"/>,
    /// reading the sibling key in the same <c>info</c> node. Returns 0 when the
    /// archive does not carry one, which is the honest answer -- awarding a made
    /// up amount would put experience on the sheet that no v83 player could have
    /// earned from that mob.
    /// </remarks>
    public long MobExp(string mobId)
    {
        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", "Mob/" })
                    {
                        string path = Path(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(path) is { } imageNode
                            && TryReadLongChild(imageNode, "info", "exp", out long exp)
                            && exp > 0)
                            return exp;
                    }
                }
            }
            return 0;
        }
    }

    /// <summary>
    /// The box one mob frame occupies, in map coordinates, or null when it has
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null is the answer for <c>die1</c>, and it is the answer that makes a
    /// corpse untouchable without a special case: <c>die1/0</c> carries no
    /// <c>lt</c>/<c>rb</c> at all (measured on <c>f2/9409000.img</c>), so a
    /// dead mob is excluded by the geometry rather than by a flag that could
    /// disagree with it.
    /// </para>
    /// <para>
    /// The <c>head</c> vector is read here too, because it is the same node's
    /// sibling and the same reason: <c>DamageNumber::get_pos</c>
    /// (DamageNumber.cpp:61) raises the number from the head, and a number
    /// raised from the feet reads as a miss.
    /// </para>
    /// </remarks>
    /// <summary>
    /// One action's draw origin — where its frame 0 sits relative to the mob's own
    /// feet.
    /// </summary>
    /// <remarks>
    /// This is the number that answers "does the mob shift when it is hit".
    /// <c>DrawArgument.h:143</c> computes the drawn rectangle as
    /// <c>pos - center - origin</c>, so a frame whose origin differs from
    /// <c>stand</c>'s is genuinely drawn offset while the mob's position does not
    /// move. Measured 2026-10-16: <c>2220100</c> stand <c>(27,58)</c> vs hit1
    /// <c>(28,65)</c>, so a flinching mob is drawn 7px higher and 1px left.
    ///
    /// <c>WzCanvasProperty</c> has no <c>OriginX</c>/<c>OriginY</c>; it exposes
    /// <c>GetCanvasOriginPosition()</c>, which returns a PointF. The compile
    /// error is what proved it.
    /// </remarks>
    public bool TryMobOrigin(string mobId, string action, int frameIndex,
        out int originX, out int originY)
    {
        originX = 0;
        originY = 0;
        return TryMobFrameOrigin(mobId, action, frameIndex, "m", out originX, out originY);
    }

    /// <summary>
    /// A canvas's draw origin. <see cref="WzCanvasProperty.OriginPropertyName"/>
    /// is <c>"origin"</c>, so a frame with no explicit origin has a zero origin
    /// and is drawn from its top-left — which is a fact, not a failure, and is
    /// why a missing origin is reported as (0,0) rather than as "unknown".
    /// </summary>
    private static bool ReadCanvasOrigin(WzObject node, out int originX, out int originY)
    {
        originX = 0;
        originY = 0;
        if (node is not WzCanvasProperty canvas) return false;
        System.Drawing.PointF point = canvas.GetCanvasOriginPosition();
        originX = (int)Math.Round(point.X);
        originY = (int)Math.Round(point.Y);
        return true;
    }

    /// <summary>Frame 0 of <c>stand</c> — the pose every other is compared against.</summary>
    public bool TryMobStandOrigin(string mobId, out int originX, out int originY)
        => TryMobOrigin(mobId, "stand", 0, out originX, out originY);

    private bool TryMobFrameOrigin(string mobId, string action, int frameIndex, string kind,
        out int originX, out int originY)
    {
        originX = 0;
        originY = 0;
        if (string.IsNullOrWhiteSpace(action)) return false;
        if (!int.TryParse(mobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            return false;

        lock (_session.Gate)
        {
            foreach (OpenFile file in _session.SelectRoleSources("Mob", null))
            {
                foreach (string candidate in new[]
                         {
                             numeric.ToString("D7", CultureInfo.InvariantCulture),
                             numeric.ToString(CultureInfo.InvariantCulture),
                         })
                {
                    if (_session.TryResolve(Path(file.Id, candidate + ".img")) is not { } image) continue;
                    if (!TryFindChild(image, action, out WzObject? node) || node is null) continue;

                    // Frame index is a child NAME on this archive, so frame 0 is "0"
                    // and a frame past the end resolves to nothing rather than to
                    // the wrong frame.
                    foreach (WzObject child in Children(node))
                    {
                        if (!int.TryParse(child.Name, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out int index)) continue;
                        if (index != frameIndex) continue;
                        if (ReadCanvasOrigin(child, out originX, out originY)) return true;
                        if (TryFindChild(child, "0", out WzObject? layer)
                            && ReadCanvasOrigin(layer, out originX, out originY)) return true;
                    }
                }
            }
        }
        return false;
    }

    public MobFrameGeometry? MobFrame(string mobId, string action, int frameIndex, string kind = "m")
        {
            if (string.IsNullOrWhiteSpace(action))
                return null;

            // The stance the patrol reports can name an action the archive does not
            // carry. Measured on f2/9300048.img: the children are attack1, die1,
            // hit1, info, regen, stand -- there is NO `move` -- yet a walking mob
            // reports stance="move". The client plays stand in that case.
            //
            // Answering null instead left every swing with no target, on every mob
            // whose walk animation is simply absent, and it looked exactly like a
            // hitbox that never reaches. So: the requested action, then stand.
            MobFrameGeometry? found = MobFrameFor(mobId, action, frameIndex, kind);
            return found ?? MobFrameFor(mobId, "stand", frameIndex, kind);
        }

        private MobFrameGeometry? MobFrameFor(string mobId, string action, int frameIndex, string kind)
        {
            lock (_session.Gate)
        {
            foreach (OpenFile file in Candidates(kind))
            {
                foreach (string candidate in ImageNames(mobId))
                {
                    foreach (string prefix in new[] { "", kind == "n" ? "Npc/" : "Mob/" })
                    {
                        string imagePath = WzPath.Join(file.Id, prefix + candidate + ".img");
                        if (_session.TryResolve(imagePath) is not { } image)
                            continue;
                        if (!TryFindChild(image, action, out WzObject? actionNode))
                            continue;
                        if (!TryFindChild(actionNode, frameIndex.ToString(CultureInfo.InvariantCulture),
                                                        out WzObject? frameNode))
                                                    continue;
                                                // `continue`, not `return null`: a frame with no corners
                                                // (die1 carries none) means this candidate has no box, and
                                                // the next image name or prefix may still have one.
                                                if (!TryReadCorners(frameNode, out Box frameBox))
                                                    continue;

                                                // The box is on the FRAME, not on the action. Measured on
                                                // f2/9300048.img: `stand` holds only frame numbers 0-5,
                                                // while `stand/0` carries lt=(-46,-49) rb=(14,0).
                                                //
                                                // Requiring lt/rb on the ACTION node made every lookup
                                                // return null -- silently, identically to a mob that
                                                // genuinely has no box -- so no swing could ever connect,
                                                // on any mob, on any map. The frame is the box.
                                                //
                                                // An action-level box is still honoured when one exists:
                                                // some frames carry their own and some inherit, so the
                                                // two are unioned only when the action really has a box.
                                                int left = frameBox.Left;
                                                int top = frameBox.Top;
                                                int right = frameBox.Right;
                                                int bottom = frameBox.Bottom;
                                                if (TryReadCorners(actionNode, out Box actionBox))
                                                {
                                                    left = Math.Min(left, actionBox.Left);
                                                    top = Math.Min(top, actionBox.Top);
                                                    right = Math.Max(right, actionBox.Right);
                                                    bottom = Math.Max(bottom, actionBox.Bottom);
                                                }

                        TryReadVectorChild(frameNode, "head", out int hx, out int hy);
                        TryReadVectorChild(actionNode, "head", out int ahx, out int ahy);
                        return new MobFrameGeometry(left, top, right, bottom, hx, hy, ahx, ahy);
                    }
                }
            }
            return null;
        }
    }

    private IEnumerable<OpenFile> Candidates(string kind)
        => _session.SelectRoleSources(kind == "n" ? "Npc" : "Mob", null);

    /// <summary>Padded and unpadded image names — see MobSpriteService for why both.</summary>
    private static IEnumerable<string> ImageNames(string mobId)
    {
        if (!int.TryParse(mobId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            yield break;
        yield return numeric.ToString("D7", CultureInfo.InvariantCulture);
        yield return numeric.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The spellings a STRING POOL entry may be keyed by, verbatim id first and the
    /// <c>IntID()</c> form second.
    /// </summary>
    /// <remarks>
    /// <c>MapleNecrocer/Extension.cs:47-50</c> —
    /// <c>Number.ToInt().ToString()</c>, i.e. the leading zeros stripped, which is
    /// what <c>Npc.cs:67</c> builds its string path from. MEASURED on the open
    /// archive: <c>String.nx/Npc.img</c> answers <c>2000</c>, <c>2001</c>,
    /// <c>2101</c> and <c>2102</c> for map ids <c>0002000</c>, <c>0002001</c>,
    /// <c>0002101</c> and <c>0002102</c>, and answers <c>9101002</c> /
    /// <c>9101003</c> verbatim — which is the same number in both cases, so the
    /// stripped form is the whole rule and the verbatim one is only a fallback for
    /// a pool that really did pad its keys.
    ///
    /// <para>
    /// NOT the same thing as <see cref="ImageNames"/>: that one is about IMAGE
    /// names, where the archive mixes 7-digit mob ids with 4-digit NPCs and both
    /// spellings are real. The string pools are keyed by number.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> StringIds(string id)
    {
        yield return id;
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            yield return numeric.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryFindChild(WzObject node, string name, out WzObject? found)
    {
        foreach (WzObject child in Children(node))
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = child;
                return true;
            }
        }
        found = null;
        return false;
    }

    private bool TryReadIntChild(WzObject node, string group, string key, out int value)
    {
        value = 0;
        if (!TryFindChild(node, group, out WzObject? parent) || parent is null)
            return false;
        if (!TryFindChild(parent, key, out WzObject? leaf) || leaf is null)
            return false;
        return TryReadInt(leaf, out value);
    }

    /// <summary>
    /// The <see cref="TryReadIntChild"/> shape for a long value.
    /// </summary>
    /// <remarks>
    /// Not a cast of the int reader: an EXP field is a WzLongProperty and the
    /// int path truncates it at 2^31, which silently turns a large award into a
    /// small one. Narrowing here is what makes the bug possible; widening back
    /// after the truncation would not recover it.
    /// </remarks>
    private bool TryReadLongChild(WzObject node, string group, string key, out long value)
    {
        value = 0;
        if (!TryFindChild(node, group, out WzObject? parent) || parent is null)
            return false;
        if (!TryFindChild(parent, key, out WzObject? leaf) || leaf is null)
            return false;
        switch (leaf)
        {
            case WzLongProperty l:
                value = l.Value;
                return true;
            case WzIntProperty i:
                value = i.Value;
                return true;
            case WzShortProperty sh:
                value = sh.Value;
                return true;
            case WzStringProperty str when long.TryParse(
                str.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private bool TryReadVectorChild(WzObject node, string key, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!TryFindChild(node, key, out WzObject? leaf) || leaf is null)
            return false;
        return TryReadVector(leaf, out x, out y);
    }
}

/// <summary>
/// An axis-aligned rectangle in the coordinate space it was read from.
/// </summary>
/// <remarks>
/// Corner order is normalised on read, so <c>Left &lt;= Right</c> always holds
/// and no caller has to know that the archive wrote them the other way round in
/// one frame and not the next. A zero-area box carries a different claim — "no
/// box here" — and <see cref="IsEmpty"/> is how a caller says so.
/// </remarks>
public readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The box a swing has when the archive says nothing.</summary>
    public static Box Empty => new(0, 0, 0, 0);

    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>
    /// True when this rectangle cannot contain or be contained by anything.
    /// </summary>
    /// <remarks>
    /// Width and height are both checked because a zero-width box still has a
    /// height, and testing only the height would let a degenerate box overlap
    /// anything it passed through.
    /// </remarks>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>
    /// Whether two rectangles share any area.
    /// </summary>
    /// <remarks>
    /// The client's own test. <c>Rectangle::overlaps</c> (Rectangle.h:74-79)
    /// delegates each axis to <c>Range::overlaps</c> (Range.h:93-96),
    /// <c>smaller &lt;= other.greater &amp;&amp; greater &gt;= other.smaller</c>,
    /// i.e. <c>left &lt;= other.right &amp;&amp; right &gt;= other.left</c> on both
    /// axes. Touching edges count as overlapping, which is what makes a
    /// swing reach the mob standing exactly at its boundary.
    /// </remarks>
    public bool Overlaps(Box other)
        => Left <= other.Right && Right >= other.Left
        && Top <= other.Bottom && Bottom >= other.Top;

    /// <summary>Shift the whole box by a position — how a mob's local box reaches the map.</summary>
    public Box Shift(float x, float y)
        => new(
            Left + (int)MathF.Round(x),
            Top + (int)MathF.Round(y),
            Right + (int)MathF.Round(x),
            Bottom + (int)MathF.Round(y));

    /// <summary>Reflect X about the origin — the left-facing form of a swing.</summary>
    public Box MirrorX()
        => new(-Right, Top, -Left, Bottom);
}

/// <summary>A swing's box plus the archive it came from.</summary>
/// <param name="Box">The rectangle, in the character's local space.</param>
/// <param name="Source">The weapon image and stance that produced it.</param>
/// <param name="Path">The session path it was read from.</param>
/// <param name="Resolved">
/// False when this is the bare-hands fallback rather than the weapon asked for.
/// </param>
public sealed record AttackBox(Box Box, string Source, string Path, bool Resolved)
{
    /// <summary>
        /// The far edge of a right-facing swing, past the mirrored rectangle.
        /// </summary>
        /// <remarks>
        /// <c>AfterImage.cs:65</c> computes <c>Right = (int)X - LT.X + 18</c> — a
        /// right-facing swing reaches 18px further than the mirror alone would give.
        /// It is on the right branch only; the left branch at :69-70 has no such term.
        /// </remarks>
        public const int FarEdgeReach = 18;

        /// <summary>The box in map coordinates, in front of the body, for this facing.</summary>
        /// <remarks>
        /// <para>
        /// Read from <c>AfterImage.cs:62-72</c> together with what FlipX means, which
        /// is <c>MapleCharacter.cs:1649-1652</c>: pressing Right sets
        /// <c>FlipX = true</c>, so <b>FlipX=true is the RIGHT-facing body</b>.
        /// </para>
        /// <para>
        /// With bare hands <c>lt=-64, rb=-27</c> the reference gives
        /// <c>FlipX=false</c> (left) <c>X+lt .. X+rb = X-64..X-27</c>, and
        /// <c>FlipX=true</c> (right) <c>X-rb .. X-lt+18 = X+27..X+82</c>. Both land
        /// <i>in front of</i> the body, as an attack box must.
        /// </para>
        /// <para>
        /// So the archive's raw lt/rb pair is the LEFT-facing form and the
        /// right-facing form is its mirror — the opposite of what the ternary here
        /// used to claim, which put every swing behind the character and made every
        /// attack miss no matter how close it was.
        /// </para>
        /// </remarks>
        public Box At(float originX, float originY, bool facingLeft)
        {
            Box local = facingLeft
                ? Box
                : new Box(-Box.Right, Box.Top, -Box.Left + FarEdgeReach, Box.Bottom);
            return local.Shift(originX, originY);
        }
}

/// <summary>One mob frame's box and head position, in map coordinates.</summary>
/// <param name="Box">The rectangle, before the mob's position is added.</param>
/// <param name="HeadX">The frame's own <c>head</c> X, 0 when it carries none.</param>
/// <param name="HeadY">The frame's own <c>head</c> Y.</param>
/// <param name="ActionHeadX">The action's <c>head</c> X, used when the frame has none.</param>
/// <param name="ActionHeadY">The action's <c>head</c> Y.</param>
public sealed record MobFrameGeometry(
    int Left, int Top, int Right, int Bottom,
    int HeadX, int HeadY,
    int ActionHeadX, int ActionHeadY)
{
    /// <summary>The frame's own head if it has one, else the action's.</summary>
    public (int X, int Y) Head => HeadX != 0 || HeadY != 0 ? (HeadX, HeadY) : (ActionHeadX, ActionHeadY);

    /// <summary>Where the box sits once the mob's position is added.</summary>
    public Box At(float x, float y)
        => new Box(Left, Top, Right, Bottom).Shift(x, y);

    /// <summary>The head position in map coordinates.</summary>
    public (float X, float Y) HeadAt(float x, float y)
    {
        (int hx, int hy) = Head;
        return (x + hx, y + hy);
    }
}
