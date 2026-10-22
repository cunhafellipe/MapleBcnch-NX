using System.Drawing;
using MapleBench.Services.Look;
using MapleLib.WzLib;
using MapleLib.WzLib.WzProperties;

namespace MapleBench.Services;

/// <summary>
/// The avatar a character is composed from, per pose, the way
/// <c>CharLook::draw</c> assembles one.
/// </summary>
/// <remarks>
/// <para>
/// Transliterated from the reference, which is the only place this is defined:
/// <c>CharLook.cpp:84-200</c>, <c>Body.cpp:36-118</c>,
/// <c>BodyDrawInfo.cpp:27-100</c>, <c>Hair.cpp:30-95</c>, <c>Face.cpp:92-97</c>,
/// <c>AvatarLayers.cpp:38-118</c> — all under MPSH
/// <c>clean-client/src/Character/Look/</c>.
/// </para>
///
/// <para><b>Why server-side.</b> One place, matching the reference, keeps the
/// browser's job down to "blit these rectangles". A second implementation in JS
/// would be a second thing that can disagree with the tables below, and those
/// tables only exist because the archive was read correctly.</para>
///
/// <para><b>Five anchors, not one.</b> A single body canvas grounds correctly on
/// its own because its <c>origin</c> already lands on the character's position. A
/// COMPOSED look does not: the reference shifts every part by a per-owner anchor,
/// and each part subtracts its OWN map point on top of that.
///
/// <list type="bullet">
///   <item><c>body</c> — <c>get_body_position()</c>, the stance navel
///   (<c>BodyDrawInfo.cpp:91</c>). Each body part gets
///   <c>body_navel - part.navel</c>; a hand part gets
///   <c>handMove - part.handMove</c> instead (<c>Body.cpp:59-67</c>).</item>
///   <item><c>head</c> — <c>get_head_position()</c>, non-zero: the body's neck
///   minus the head SHEET's own neck (<c>BodyDrawInfo.cpp:97</c>). On
///   <c>stand1/0</c> that is <c>(-4,-32) - (0,15) = (-4,-47)</c> — a head
///   blitted at the body anchor instead of here sits 47 px too low.</item>
///   <item><c>hair</c> — <c>gethairpos()</c>: <c>head.brow - head.neck +
///   body.neck</c> (<c>BodyDrawInfo.cpp:98</c>). Each hair layer then subtracts
///   its OWN <c>brow</c> (<c>Hair.cpp:66-67</c>).</item>
///   <item><c>face</c> — <c>getfacepos()</c>: the head position plus the head
///   sheet's <c>brow</c> (<c>BodyDrawInfo.cpp:99</c>). NOT the body's offset —
///   <c>CharLook.cpp:189-190</c> draws face parts and the face marker through a
///   second <c>DrawArgument</c>.</item>
/// </list>
///
/// <para><b>Order.</b> Descending zmap rank, STABLE — equal rank keeps collection
/// order, which is body parts, then head, then hair, then equips, then the face
/// marker (<c>CharLook.cpp:126-144, 175-179</c>). NX stores <c>z</c> as a LAYER
/// NAME string (<c>"body"</c>, <c>"armOverHair"</c>, <c>"hairOverHead"</c>); the
/// rank is <see cref="Nx.NxLayerOrder"/> reading <c>Base/zmap.img</c>. Sorting
/// that string as if it were a number is the error TASK 11 hit.</para>
///
/// <para><b>Arbitration.</b> 64 slots; where two parts claim one, only the
/// smaller priority survives (<c>CharLook.cpp:155-173</c>). Priority is the
/// smallest zmap rank among the owner's <c>islot</c> codes — lower is stronger
/// (<c>AvatarLayers::priority_of</c>).</para>
/// </remarks>
public sealed class CharLookComposer
{
    /// <summary>One sprite of a composed look: what to blit, where, and how it ranks.</summary>
    public sealed class LookPart
    {
        /// <summary>Session path of the canvas to fetch.</summary>
        public string Path { get; init; } = "";

        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>The canvas's own <c>origin</c>, before the shift.</summary>
        public int OriginX { get; init; }

        public int OriginY { get; init; }

        /// <summary>Net shift this part needs, added to its own origin.</summary>
        public Point Shift { get; init; }

        /// <summary>zmap rank, read from <c>Base/zmap.img</c> by layer name.</summary>
        public int Z { get; init; }

        /// <summary>The layer name the rank came from — a string, never a number.</summary>
        public string ZName { get; init; } = "";

        public ulong Slots { get; init; }

        /// <summary>
        /// The same claims as the two-letter codes the archive spells them in.
        /// <see cref="Slots"/> alone cannot be checked by eye — a mask of 0 is
        /// either "nothing claimed" or "everything claimed but nothing permitted",
        /// and those are different answers about the data.
        /// </summary>
        public string SlotCodes { get; init; } = "";

        public int Priority { get; init; }

        /// <summary>body / head / hair / face — which anchor applied to it.</summary>
        public string Owner { get; init; } = "";

        /// <summary>The leaf name under the pose: "body", "arm", "hair", "head", "face".</summary>
        public string Part { get; init; } = "";

        /// <summary>Where this part's origin lands, relative to the character's position.</summary>
        /// <remarks>
        /// NEGATIVE origin, and that sign is the whole placement rule.
        ///
        /// The reference does not offset a part in code at all: CharLook.cpp:195
        /// calls <c>texture.draw(position)</c>, with the character's
        /// own position, and the blit lands at <c>position - origin</c> because
        /// that is what drawImage's origin means on a canvas whose <c>o</c> vector
        /// points at the character's feet. Adding the origin instead of subtracting
        /// it pushes every part by twice its own origin: the torso's (16,31) sent
        /// it 62 units below the floor the character was standing on, with the
        /// head stranded above it and nothing joining them.
        ///
        /// The check that settles the sign without reading the reference: a
        /// foothold's Y is where the character's FEET are, so the lowest layer has
        /// to END at y=0. Body: -31 + 0 = -31, and -31 + 31 = 0. Head: -17 - 47 =
        /// -64, spanning -64..-29, overlapping the torso by 2 -- the chin resting
        /// on the shoulders.
        /// </remarks>
        public int ScreenX => -OriginX + Shift.X;

        /// <inheritdoc cref="ScreenX"/>
        public int ScreenY => -OriginY + Shift.Y;
    }

    /// <summary>A composed look: the parts in paint order, plus the pose they came from.</summary>
    public sealed class Look
    {
        public string Stance { get; init; } = "";

        public int Frame { get; init; }

        public Point BodyAnchor { get; set; }

        public Point HeadAnchor { get; set; }

        public Point FaceAnchor { get; set; }

        public Point HairAnchor { get; set; }

        public Point ArmAnchor { get; set; }

        public Point HandAnchor { get; set; }

        /// <summary>Frame duration, from the pose's own <c>delay</c> node.</summary>
        public int Delay { get; set; }

        /// <summary>How many frames the body sheet carries under this action.</summary>
        public int FrameCount { get; set; } = 1;

        /// <summary>False when the body sheet's <c>face</c> node says this pose hides it.</summary>
        public bool ShowsFace { get; set; } = true;

        /// <summary>Back to front; index 0 is furthest back. This is the paint order.</summary>
        public List<LookPart> Parts { get; init; } = [];

        /// <summary>Parts dropped by slot arbitration, named. Reported, never silent.</summary>
        public List<string> Hidden { get; init; } = [];

/// <summary>
    /// How many layer names the zmap ranked, and how many the smap gave slot
        /// codes. Carried on the look itself rather than left to the endpoint so
        /// that "arbitration ran on real tables" is a checkable claim: a caller
        /// reading <c>zmapCount: 0</c> knows the masks on these parts are zeros
        /// because there was nothing to read them from, not because arbitration
        /// decided nothing conflicts.
        /// </summary>
        public int ZmapCount { get; init; }

        public int SmapCount { get; init; }

        /// <summary>
        /// Why the tables are what they are, in one line per archive scanned.
        /// "found zmap, no smap" and "found nothing" are different situations
        /// that both end as a zero mask, and this is what tells them apart.
        /// </summary>
        public List<string> TableScan { get; init; } = [];

        /// <summary>
        /// The union of every part's screen rectangle, relative to the character's
        /// own position. The caller sizes its canvas from this instead of guessing.
        /// </summary>
        public (int MinX, int MinY, int MaxX, int MaxY) Bounds
        {
            get
            {
                if (Parts.Count == 0) return (0, 0, 0, 0);
                int minX = int.MaxValue, minY = int.MaxValue;
                int maxX = int.MinValue, maxY = int.MinValue;
                foreach (LookPart p in Parts)
                {
                    minX = Math.Min(minX, p.ScreenX);
                    minY = Math.Min(minY, p.ScreenY);
                    maxX = Math.Max(maxX, p.ScreenX + p.Width);
                    maxY = Math.Max(maxY, p.ScreenY + p.Height);
                }
                return (minX, minY, maxX, maxY);
            }
        }
    }

    private readonly WzSessionService _session;
    private readonly AvatarLayerTables? _tables;
    private readonly List<string> _tableScan = [];

    public CharLookComposer(WzSessionService session)
    {
        _session = session;

        // Both tables live in Base.nx, which is a SEPARATE archive from
        // Character.nx, so neither one alone can answer. A missing table is not an
        // error: z falls back to 0 and slots to 0, which is what the reference
        // does for a layer it does not recognise (AvatarLayers.cpp:75-84, 86-92).
        foreach (OpenFile open in session.Files)
        {
            WzDirectory? dir = open.WzFile?.WzDirectory;
            if (dir is null) continue;
            if (dir.GetImageByName("zmap.img") is not { } zmap)
            {
                _tableScan.Add($"{open.Id} {open.Name}: no zmap.img");
                continue;
            }

            // smap is looked up HERE rather than in the FromImages call, because
            // "zmap present, smap absent" has to be distinguishable from "zmap
            // present and smap parsed to nothing". Both leave every mask zero, and
            // only the scan line says which one happened.
            WzImage? smap = dir.GetImageByName("smap.img");
            AvatarLayerTables? t = AvatarLayerTables.FromImages(zmap, smap);
            _tableScan.Add(
                $"{open.Id} {open.Name}: zmap={zmap.Name} smap={(smap is null ? "ABSENT" : smap.Name)} " +
                $"ranks={t?.ZCount ?? 0} codes={t?.SmapCount ?? 0}" +
                (string.IsNullOrEmpty(t?.SmapDiagnosis) ? "" : $" [{t!.SmapDiagnosis}]"));
            if (t is { ZCount: > 0 }) { _tables = t; break; }
        }
        if (_tableScan.Count == 0) _tableScan.Add("no archives open");
    }

    /// <summary>One line per archive the layer tables were looked for in.</summary>
    public IReadOnlyList<string> TableScan => _tableScan;

    /// <summary>
    /// Every stance the body sheet actually carries, with the number of frames
    /// under each.
    /// </summary>
    /// <remarks>
    /// <para>Stance.cpp:98-135 enumerates 35 stance names and Body.cpp:39-95
    /// walks them against the sheet, skipping any the sheet does not have
    /// (<c>if (!stancenode) continue;</c>). The archive is the authority on
    /// which of those names have art, and it carries 158 of them — far more
    /// than the reference enumerates. Reading the sheet is the only way to
    /// answer "can I draw this pose" without hardcoding a list that the next
    /// archive revision invalidates.</para>
    ///
    /// <para>This is what a caller needs to stop asking for
    /// <c>stand1/7</c>. Without it the only way to find a pose's length is to
    /// request it and discover the answer by getting nothing back.</para>
    /// </remarks>
    public IReadOnlyDictionary<string, int> StanceFrames(string bodyPath)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        if (_session.TryResolve(bodyPath) is not { } body) return result;

        foreach (WzObject stance in _session.EnumerateChildren(body))
        {
            string name = stance.Name ?? "";
            // "info" is the body's own vslot/islot block, not a pose.
            if (name.Length == 0 || name == "info") continue;
            if (PropertiesOf(stance) is null) continue;
            int count = 0;
            while (Frame(stance, count) is not null) count++;
            result[name] = count;
        }
        return result;
    }

    /// <summary>How many zmap ranks the open archives supplied. 0 means no Base.nx is open.</summary>
    public int ZmapCount => _tables?.ZCount ?? 0;

    public int SmapCount => _tables?.SmapCount ?? 0;

    /// <summary>
    /// Compose the avatar for one pose.
    /// </summary>
    /// <param name="bodyPath">session path of <c>00002000.img</c>.</param>
    /// <param name="headPath">session path of <c>00012000.img</c>.</param>
    /// <param name="hairPath">session path of a <c>Hair/....img</c>, or null for no hair.</param>
    /// <param name="facePath">session path of a <c>Face/....img</c>, or null for no face.</param>
    /// <param name="stance">the WZ action — <c>stand1</c>, <c>walk1</c>, …</param>
    /// <param name="frame">the frame index under that action.</param>
    public Look Compose(
        string bodyPath, string headPath, string? hairPath, string? facePath,
        string stance, int frame)
        => ComposeCore(bodyPath, headPath, stance, frame, slotSet =>
        {
            slotSet.Hair = hairPath;
            slotSet.Face = facePath;
        });

    /// <summary>
    /// Compose with an arbitrary set of equipment on, keyed by slot name
    /// (<c>Cap-1</c>, <c>Coat</c>, <c>Weapon-1</c>, … — <see cref="EquipSlots.All"/>).
    /// </summary>
    /// <remarks>
    /// <para>This is the general form the four-argument one above is a case of.
    /// The four slots it took were not a chosen set — they were the four the
    /// signature had room for, and nothing downstream could tell the difference
    /// between "no cap" and "no way to wear a cap".</para>
    ///
    /// <para><b>Paths, not ids.</b> The caller resolves id → image and hands over a
    /// session path, because <see cref="LookService"/> is the one that knows which
    /// archive a folder lives in and which of a folder's images belongs to which
    /// slot. The composer only walks the tree it is given.</para>
    /// </remarks>
    public Look ComposeEquipped(
        string bodyPath, string headPath, IReadOnlyDictionary<string, string> equipped,
        string stance, int frame)
    {
        // Slot names are matched case-insensitively once, here, so a caller sending
        // "cap-1" and a caller sending "Cap-1" compose the same avatar instead of
        // one drawing a hat and the other drawing nothing without saying so.
        Dictionary<string, string> canonical = new(StringComparer.OrdinalIgnoreCase);
        // W-075. Every equipment layer is read at the RESOLVED pose, not the requested
        // name. This line was the one the first fix missed: the body and the head
        // composed and the character kept its shape, while every hat, coat, pair
        // of trousers, shoe, glove and weapon vanished -- the user saw a naked
        // character. The equip sheets key the pose by its TARGET name (measured:
        // f1/Cap/01000000.img carries swingOF, stabOF, swingT3 and swingTF, and
        // carries no `brandish1` at all), so reading them at the alias name found
        // nothing and drew nothing.
        foreach ((string slot, string path) in equipped)
        {
            EquipSlotDef? def = EquipSlots.Find(slot);
            if (def is null) continue;
            canonical[def.Name] = path;
        }

        return ComposeCore(bodyPath, headPath, stance, frame,
            set =>
            {
                set.Hair = Pick(canonical, "Hair-1", "Hair-2");
                set.Face = Pick(canonical, "Face-1", "Face-2");
                foreach ((string slot, string path) in canonical)
                {
                    EquipSlotDef? def = EquipSlots.Find(slot);
                    if (def?.Folder.Length > 0) set.Equips[def.Name] = path;
                }
            });
    }

    /// <summary>The one path a pair of slots shares, preferring the first that was given one.</summary>
    private static string? Pick(IReadOnlyDictionary<string, string> paths, string first, string second)
        => paths.TryGetValue(first, out string? a) ? a : paths.GetValueOrDefault(second);

    /// <summary>
    /// What the caller wants worn. Built by whichever entry point, so the body,
    /// head, hair, face and equips are read once, in one order, for every slot
    /// shape.
    /// </summary>
    private sealed class SlotSet
    {
        public string? Hair;
        public string? Face;
        public readonly Dictionary<string, string> Equips = new(StringComparer.Ordinal);
    }

    private Look ComposeCore(
        string bodyPath, string headPath, string stance, int frame,
        Action<SlotSet> fillSlots)
    {
        SlotSet slotSet = new();
        fillSlots(slotSet);

        Look look = new()
        {
            Stance = stance,
            Frame = frame,
            ZmapCount = _tables?.ZCount ?? 0,
            SmapCount = _tables?.SmapCount ?? 0,
            TableScan = new List<string>(_tableScan),
        };

        WzObject? body = _session.TryResolve(bodyPath);
        WzObject? head = _session.TryResolve(headPath);
        if (body is null) return look;

        // W-074. An ACTION may be an alias rather than a pose. Resolve it ONCE,
        // here, so every reader below agrees on which frame it is looking at.
        //
        // `FrameCount` deliberately keeps counting the REQUESTED stance: brandish1
        // carries 7 alias frames that each point somewhere else, and the client
        // indexes those 7. Counting the target's frames instead would renumber the
        // animation and break the frame the server is asking for.
        (string readPose, int readFrame) =
            ResolveAlias(body, stance, frame) is { } alias ? (alias.Pose, alias.Frame)
                                                            : (stance, frame);

        Dictionary<string, Dictionary<string, Point>> shift = new(StringComparer.Ordinal);
        bool showsFace = true;
        int delay = DefaultDelayMs;

                // The BODY sheet is read FIRST, then the head merges onto it -- the
        // order BodyDrawInfo.cpp:65-90 walks them in, and it matters because
        // TryAdd is first-writer-wins.
        //
        // This call was missing entirely, and the damage was silent and total:
        // every anchor read off the body -- navel, neck, hand, handMove -- came
        // back (0,0), because `shift["body"]` was never created. So the head
        // hung off a zero neck, the arm off a zero hand, and CollectBody
        // subtracted the part's REAL navel from a ZERO one, pushing the torso
        // 21 units below the place its own origin already put it. Six layers,
        // all loaded, none of them where they belonged: head bottom 37, body
        // top 52, and 15 units of nothing between them.
        ReadPose(body, readPose, readFrame, shift, ref showsFace, ref delay);

        bool hasHeadPose = ReadPose(head, readPose, readFrame, shift, ref showsFace, ref delay);
        // BodyDrawInfo::init -- the dead stance has no head sheet of its own.
        if (head is not null && !hasHeadPose && stance == "dead")
            ReadPose(head, "stand1", 0, shift, ref showsFace, ref delay);

        look.FrameCount = CountFrames(body, stance);

        Point bodyNavel = Layer(shift, "body").Navel;
        Point bodyNeck = Layer(shift, "body").Neck;
        Point headNeck = Layer(shift, "head").Neck;
        Point headBrow = Layer(shift, "head").Brow;

        look.BodyAnchor = bodyNavel;
        look.HeadAnchor = Sub(bodyNeck, headNeck);
        look.FaceAnchor = Add(look.HeadAnchor, headBrow);
        look.HairAnchor = Add(Sub(headBrow, headNeck), bodyNeck);
        look.HandAnchor = Layer(shift, "handBelowWeapon").HandMove;
        look.Delay = delay;

        // BodyDrawInfo.cpp:92-93 -- the arm anchor comes off the ARM sheet's own
        // hand/navel pair, or off armOverHair's when this body has no "arm" part.
        string armLayer = shift.ContainsKey("arm") ? "arm" : "armOverHair";
        look.ArmAnchor = Add(Sub(Layer(shift, armLayer).Hand, Layer(shift, armLayer).Navel), bodyNavel);

        // BodyDrawInfo::get_anchor, BodyDrawInfo.cpp:129-141 -- ONE table of
        // every named point on the body or head, already carried into the
        // character's own coordinates. An equip part is placed by lining ITS OWN
        // map point up with the same-named one here, which is why a cap holding a
        // "brow" follows the head and a sword holding a "hand" follows the arm,
        // with neither of them needing to know the other's sheet.
        //
        // Head points hang off the body's neck; body points are already in place.
        // "hand" and "handMove" are not on either sheet's map under those names and
        // are filled in from the arm and handBelowWeapon sheets, per the C++.
        Dictionary<string, Point> anchors = new(StringComparer.Ordinal)
        {
            ["hand"] = look.ArmAnchor,
            ["handMove"] = look.HandAnchor,
        };
        foreach ((string key, Point value) in Layer(shift, "body").BrowByPart)
            anchors[key] = value;
        foreach ((string key, Point value) in Layer(shift, "body").NavelByPart)
            anchors[key] = value;
        foreach ((string key, Point value) in Layer(shift, "body").Named)
            anchors[key] = value;
        foreach ((string key, Point value) in Layer(shift, "head").Named)
            anchors[key] = new Point(value.X - headNeck.X + bodyNeck.X, value.Y - headNeck.Y + bodyNeck.Y);

        List<LookPart> parts = new();

        // Reset per compose: the composer is called once per (stance, frame) and
        // the same instance composes a walk and then a ladder, so a stale true
        // here would keep the face off a character who has climbed down.
        _poseIsBack = false;

        // Body.cpp:59-92 -- every bitmap part of the body sheet, plus the head.
        CollectBody(bodyPath, body, readPose, readFrame, shift, parts);
        if (head is not null) CollectHead(headPath, head, readPose, readFrame, look.HeadAnchor, parts);

        // A pose whose body says `backBody` is drawn from BEHIND, and a back has
        // no face. The archive states this itself, per pose:
        //
        //     f1/00002000.img/ladder/0/body -> z = "backBody"
        //     f1/00002000.img/rope/0/body   -> z = "backBody"
        //     f1/00002000.img/walk1/0/body  -> z = "body"
        //     f1/00002000.img/stand1/0/body -> z = "body"
        //
        // (measured) and the reference agrees at the source: MapleCharacter.cs
        // :546, :563 and :573 all set `FaceDir = FaceDir.None` on the ladder
        // branches, which is the client saying "this character is facing the
        // ladder, not the camera".
        //
        // Without this the eyes are drawn on the back of the head while
        // climbing, because the face part is collected unconditionally at :508
        // and nothing between the pose and the face ever asked which way the
        // character is facing.
        if (_poseIsBack) { }

        // Hair.cpp:59-78 -- each layer subtracts its OWN brow.
        //
        // Only for the calls that predate the slot map, where the caller named a
        // hair image directly. A slot-map caller wearing Hair-1 goes through
        // CollectEquip below instead, and is collected exactly once: routing both
        // drew every hair layer twice, once hidden by arbitration and once not.
        if (slotSet.Hair is { } hairPath && slotSet.Equips.Count == 0 && _session.TryResolve(hairPath) is { } hair)
        {
            ulong slots = OwnerSlots(hairPath);
            int priority = OwnerPriority(hairPath);
            string? codes = OwnerSlotCodes(hairPath);
            foreach (Leaf leaf in Leaves(hairPath, hair, readPose, readFrame))
            {
                // Hair.cpp:66 -- gethairpos() minus this layer's OWN brow, so a
                // hair drawn offset on its sheet still lands where its sheet says.
                leaf.Map.TryGetValue("brow", out Point brow);
                parts.Add(Build(leaf, ZNameOf(leaf.Node), Sub(look.HairAnchor, brow), slots, codes, priority, "hair"));
            }
        }

        // Clothing.cpp:160-186 -- one loop per equipped item. Every bitmap layer of
        // the item's pose, placed by lining ITS OWN map point up with the same
        // point on the body or head, and carrying the item's own slots and
        // priority. The dead stance borrows stand1 for head-worn items, which is
        // the one thing about it that is about the item rather than the body.
        foreach ((string slotName, string path) in slotSet.Equips)
        {
            EquipSlotDef? def = EquipSlots.Find(slotName);
            if (def is null) continue;
            CollectEquip(slotName, def, path, readPose, readFrame, anchors, look.HairAnchor, parts);
        }

        // Face.cpp:92-97 -- ONE marker sprite, whose z and slots come off the
        // default expression's canvas rather than off the image.
        //
        // Skipped for a `backBody` pose -- see the note beside CollectBody above:
        // the archive writes z = "backBody" on every ladder/rope pose, and
        // MapleCharacter.cs:546/:563/:573 set FaceDir = None on the same
        // branches. A back view has no eyes on it.
        if (!_poseIsBack && slotSet.Face is { } facePath && look.ShowsFace)
            CollectFace(facePath, look.FaceAnchor, parts);

        // Back to front, highest rank first, and STABLE: equal z keeps collection
        // order. OrderByDescending is documented stable in LINQ, but the
        // collection index rides along so the tie-break is explicit rather than
        // an implementation promise.
        LookPart[] ordered = parts
            .Select((p, i) => (Part: p, Index: i))
            .OrderByDescending(t => t.Part.Z)
            .ThenBy(t => t.Index)
            .Select(t => t.Part)
            .ToArray();

        int index = 0;
        foreach (Verdict verdict in Verdicts(ordered))
        {
            LookPart part = ordered[index++];
            if (verdict.Visible) look.Parts.Add(part);
            else look.Hidden.Add(verdict.Reason(part));
        }

        return look;
    }

    /// <summary>Whether one part stays, and — when it does not — which slot took it.</summary>
    private readonly record struct Verdict(bool Visible, string LostTo = "", int WinnerPriority = 0, string SlotCodes = "")
    {
        /// <summary>
        /// The line a caller reads to confirm a layer was covered BY RULE rather
        /// than skipped by hand: it names the part, the slot they contested, and
        /// the two priorities that decided it.
        /// </summary>
        public string Reason(LookPart part) =>
            $"{part.Owner}/{part.Part} hidden: slot {SlotCodes} won by {LostTo} " +
            $"(priority {WinnerPriority} < {part.Priority})";
    }

    /// <summary>
    /// The slot mask CharLook.cpp:155-173 builds: for each of 64 slots the
    /// smallest priority claiming it wins, and a part that loses any slot it
    /// claims is hidden entirely.
    /// </summary>
    private Verdict[] Verdicts(LookPart[] ordered)
    {
        int[] strongest = new int[AvatarLayerTables.NumSlots];
        LookPart[] strongestPart = new LookPart[AvatarLayerTables.NumSlots];
        Array.Fill(strongest, short.MaxValue);
        foreach (LookPart part in ordered)
        {
            for (int bit = 0; bit < AvatarLayerTables.NumSlots; bit++)
                if (((part.Slots >> bit) & 1) != 0 && part.Priority < strongest[bit])
                {
                    strongest[bit] = part.Priority;
                    strongestPart[bit] = part;
                }
        }

        Verdict[] verdicts = new Verdict[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            LookPart part = ordered[i];
            Verdict verdict = new(Visible: true);
            for (int bit = 0; bit < AvatarLayerTables.NumSlots && verdict.Visible; bit++)
            {
                if (((part.Slots >> bit) & 1) == 0 || strongest[bit] >= part.Priority) continue;
                // The reason is reported, not just the fact. A layer that vanished
                // because a cap outranked it is a different event from one that
                // never got collected, and from the outside both are an absence.
                LookPart winner = strongestPart[bit];
                verdict = new Verdict(
                    Visible: false,
                    LostTo: $"{winner.Owner}/{winner.Part}",
                    WinnerPriority: strongest[bit],
                    SlotCodes: _tables?.CodeAt(bit) ?? $"bit{bit}");
            }
            verdicts[i] = verdict;
        }
        return verdicts;
    }

    // --------------------------------------------------------------- pose

    /// <summary>The map points of one pose's parts, keyed by LAYER name.</summary>
    private sealed class LayerPoints
    {
        public Point Navel { get; set; }
        public Point Neck { get; set; }
        public Point Brow { get; set; }
        public Point Hand { get; set; }
        public Point HandMove { get; set; }
        public Dictionary<string, Point> BrowByPart { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Point> NavelByPart { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Every named point on this layer's sheet, not just the four the anchors
        /// themselves read.
        /// </summary>
        /// <remarks>
        /// This is what makes <c>get_anchor</c> answerable for an equip. The cap's
        /// part carries <c>brow</c>, the sword's carries <c>hand</c>, and both names
        /// exist on the head and body sheets without those two sheets having heard
        /// of either item — the C++ walks the same map (<c>BodyDrawInfo.cpp:105-109</c>).</remarks>
        public Dictionary<string, Point> Named { get; } = new(StringComparer.Ordinal);
    }

    private static LayerPoints Layer(Dictionary<string, Dictionary<string, Point>> shift, string layer)
    {
        LayerPoints result = new();
        if (!shift.TryGetValue(layer, out Dictionary<string, Point>? map)) return result;

        foreach ((string key, Point value) in map)
        {
            // Every point, under its own name. The four the anchors read are also
            // lifted out by name below; the rest exist only for get_anchor.
            result.Named[key] = value;

            switch (key)
            {
                case "navel": result.Navel = value; result.NavelByPart[key] = value; break;
                case "neck": result.Neck = value; break;
                case "brow": result.Brow = value; result.BrowByPart[key] = value; break;
                case "hand": result.Hand = value; break;
                case "handMove": result.HandMove = value; break;
            }
        }
        return result;
    }

    /// <summary>
    /// Body::layers_by_name — the z string resolved to the BODY LAYER it names.
    /// </summary>
    /// <remarks>
    /// <para>Body.cpp:157-170, transliterated. The reference does
    /// <c>Body::layer_by_name(zstr)</c> and buckets the map points by the
    /// resulting ENUM (BodyDrawInfo.cpp:71-75), so <c>backBody</c> and
    /// <c>body</c> land in the same bucket and <c>backHead</c> lands in HEAD.</para>
    ///
    /// <para><b>Why the whole ladder pose was 21px low.</b> Keying the buckets
    /// by the raw z string makes <c>backBody</c> a bucket of its own. Nothing
    /// ever writes <c>shift["body"]</c> for a climbing character, so
    /// <c>get_body_position</c> answers (0,0) — the C++'s own "not found"
    /// answer — and the body part is shifted by <c>(0,0) - navel</c> instead of
    /// <c>navel - navel</c>. With <c>ladder/0/body</c> navel = (0,-21), that is
    /// a shift of (0,+21): the character hung 21px below the foothold, which is
    /// exactly the +21 the sweep measured. The head was worse — its
    /// <c>backHead</c> map never reached HEAD either, so <c>get_head_position</c>
    /// was (0,0) and the head drew at its own origin, 17px off its own neck.</para>
    ///
    /// <para>The names below are the reference's, exhaustively. A name that is
    /// not in the table resolves to <see cref="string.Empty"/> exactly as
    /// <c>layer_by_name</c> returns <c>Layer::NONE</c> — which is still better
    /// than inventing a bucket, because the anchor reads that follow are all
    /// named lookups that will miss rather than half-hit.</para>
    /// </remarks>
    private static string LayerBucket(string z) => z switch
    {
        "body" or "backBody" => "body",
        "arm" => "arm",
        "armBelowHead" => "armBelowHead",
        "armBelowHeadOverMailChest" => "armBelowHeadOverMailChest",
        "armOverHair" => "armOverHair",
        "armOverHairBelowWeapon" => "armOverHairBelowWeapon",
        "handBelowWeapon" => "handBelowWeapon",
        "handOverHair" => "handOverHair",
        "handOverWeapon" => "handOverWeapon",
        "head" or "backHead" => "head",
        _ => "",
    };

    /// <summary>
    /// BodyDrawInfo::init — gather every part's map points for one pose. First
    /// writer wins per key, matching the reference's <c>emplace</c>.
    /// </summary>
    /// <returns>true when the pose exists under this image.</returns>
    private bool ReadPose(
        WzObject img, string stance, int frame,
        Dictionary<string, Dictionary<string, Point>> shift,
        ref bool showsFace, ref int delay)
    {
        WzObject? st = Pose(img, stance);
        WzObject? fr = Frame(st, frame);
        if (fr is null) return false;

        foreach (WzObject child in _session.EnumerateChildren(fr))
        {
            string name = child.Name ?? "";

            // Body.cpp:53 — the pose's own `face` node is a flag, not a part.
            if (name == "face")
            {
                showsFace = Resolve(child) is not WzImageProperty flag || flag.GetInt() != 0;
                continue;
            }

            if (name == "delay")
            {
                // The delay node under an NX frame is an INTEGER named "delay";
                // under WZ the frame itself carries one. Either way it is read as
                // a number and only a positive one replaces the fallback, which
                // is what Animation.cpp:58-102 does with delay <= 0.
                int value = child is WzImageProperty holder
                    ? holder.GetInt()
                    : PropertiesOf(child)?.FindByName("delay")?.GetInt() ?? 0;
                if (value > 0) delay = value;
                continue;
            }

            if (Resolve(child) is not WzCanvasProperty canvas) continue;

            // Body.cpp:157-170 -- Body::layers_by_name. The z STRING is not the
            // bucket key; it is looked up in that table first, and the ENUM it
            // resolves to is the bucket. Reading the raw string is the bug this
            // replaces: `ladder/0/body` carries z = "backBody" and
            // `ladder/0/head` carries z = "backHead", so "backBody" became its
            // own bucket, `shift["body"]` was never created, and every anchor
            // read off the ladder pose came back (0,0). The visible damage:
            // the whole climbing body shifted by the handMove-less navel path
            // against a ZERO navel, landing its feet 21px BELOW the foothold.
            string z = LayerBucket(ZNameOf(canvas));
            if (!shift.TryGetValue(z, out Dictionary<string, Point>? into))
                shift[z] = into = new(StringComparer.Ordinal);

            foreach ((string key, Point value) in MapPoints(canvas)) into.TryAdd(key, value);
        }
        return true;
    }

    // -------------------------------------------------------------- leaves

    /// <summary>One canvas leaf under a pose, with the map points hanging off it.</summary>
    private sealed class Leaf
    {
        public required WzObject Node { get; init; }
        public required string Path { get; init; }
        public required Dictionary<string, Point> Map { get; init; }
    }

    private List<Leaf> Leaves(string imgPath, WzObject img, string stance, int frame)
    {
        List<Leaf> leaves = new();
        WzObject? fr = Frame(Pose(img, stance), frame);
        if (fr is null) return leaves;

        string parentPath = WzPath.Child(WzPath.Child(imgPath, stance), frame.ToString());
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WzObject child in _session.EnumerateChildren(fr))
        {
            if (Resolve(child) is not WzCanvasProperty) continue;
            string name = child.Name ?? "";
            seen.TryGetValue(name, out int occurrence);
            seen[name] = occurrence + 1;
            leaves.Add(new Leaf
            {
                Node = child,
                Path = WzPath.Child(parentPath, name, occurrence),
                Map = MapPoints(child),
            });
        }
        return leaves;
    }

    /// <summary>
    /// An action ALIAS names a different pose, one frame at a time.
    /// </summary>
    /// <remarks>
    /// W-074. MEASURED 2026-10-17 on f1/00002000.img:
    /// <code>
    ///   stand1/0     -> [arm, body, delay, face]               a pose
    ///   swingT1/0    -> [arm, armOverHair, body, delay, face]   a pose
    ///   brandish1/0  -> [action, delay, frame]                 an alias
    ///
    ///   brandish1/0..3 -> action='swingOF' frame=0..3
    ///   brandish1/4..6 -> action='stabOF'  frame=0..2
    ///   brandish2/0..2 -> action='swingT3' frame=0..2
    ///   brandish2/3..6 -> action='swingTF' frame=0..3
    /// </code>
    /// Reading an alias frame for canvases finds none, so the whole pose composed
    /// to NOTHING: every anchor came back (0,0) and bounds (0,0,0,0). The user saw
    /// a transparent character with only the eyes left, drawn at the FEET --
    /// which is exactly what zero anchors draw at. Skill 1121008's own
    /// <c>action</c> node is <c>['brandish1','brandish2']</c>, so every cast of it
    /// landed here.
    ///
    /// Resolved ONCE, in ComposeCore, and handed to every reader. The first
    /// attempt fixed only <see cref="Leaves"/> and nothing changed, because
    /// <c>ReadPose</c> is what populates <c>shift["body"]</c> and therefore every
    /// anchor -- three readers use the pose name and all three have to agree.
    ///
    /// Followed ONCE. An alias naming another alias stops here rather than
    /// looping; that is what the single hop means and it is recorded here rather
    /// than left to be rediscovered.
    /// </remarks>
    private (string Pose, int Frame)? ResolveAlias(WzObject img, string stance, int frame)
    {
        WzObject? fr = Frame(Pose(img, stance), frame);
        if (fr is null) return null;

        // Children are read with EnumerateChildren and matched by name, NOT with
        // PropertiesOf(). PropertiesOf accepts only WzImage and WzImageProperty
        // (:1049-1054), and a FRAME node is neither -- so the first attempt at
        // this returned null for every alias and changed nothing, which looked
        // exactly like the fix not having been built.
        string? target = null, frameText = null;
        bool hasArt = false;
        foreach (WzObject child in _session.EnumerateChildren(fr))
        {
            if (Resolve(child) is WzCanvasProperty) { hasArt = true; break; }
            if (string.Equals(child.Name, "action", StringComparison.OrdinalIgnoreCase))
                // Same reason as InfoString and ZNameOf: the string is the VALUE,
                // not the wrapper, so it is read off WzValue.
                target = child.WzValue as string;
            else if (string.Equals(child.Name, "frame", StringComparison.OrdinalIgnoreCase))
                frameText = child.WzValue?.ToString();
        }
        if (hasArt) return null;                 // a real pose, not an alias
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (frameText is null || !int.TryParse(frameText, out int at)) return null;
        // The alias must actually resolve, or the caller's zero-anchor behaviour
        // stands -- a name that points nowhere is a data defect, not a reason to
        // draw something invented.
        if (Frame(Pose(img, target), at) is null) return null;
        return (target, at);
    }

    /// <summary>
    /// True when the pose being composed is the character's BACK.
    /// </summary>
    /// <remarks>
    /// Read from the archive rather than from the pose's NAME: the body leaf's own
    /// <c>z</c> node is <c>backBody</c> on every ladder and rope frame
    /// (measured, f1/00002000.img) and <c>body</c> on the walking and standing
    /// ones. A pose that says backBody is drawn from behind, so it has no face --
    /// which is also what MapleCharacter.cs:546/:563/:573 mean by setting
    /// <c>FaceDir = FaceDir.None</c> on the ladder branches.
    /// </remarks>
    private bool _poseIsBack;

    private void CollectBody(
        string imgPath, WzObject img, string stance, int frame,
        Dictionary<string, Dictionary<string, Point>> shift, List<LookPart> parts)
    {
        ulong slots = OwnerSlots(imgPath);
        int priority = OwnerPriority(imgPath);
        string? codes = OwnerSlotCodes(imgPath);

        Point bodyNavel = Layer(shift, "body").Navel;
        Point handMove = Layer(shift, "handBelowWeapon").HandMove;

        foreach (Leaf leaf in Leaves(imgPath, img, stance, frame))
        {
            // Body.cpp:59-67 — hands hang off the moving hand point, everything
            // else off the navel. Both are the ANCHOR minus the part's own map
            // point, so a part drawn elsewhere on its own sheet still lands right.
            Point shiftBy = default;
            if (leaf.Map.TryGetValue("handMove", out Point own) && handMove != default)
                shiftBy = Sub(handMove, own);
            else if (leaf.Map.TryGetValue("navel", out Point navel))
                shiftBy = Sub(bodyNavel, navel);

            // The archive names its own back views. Measured on
            // f1/00002000.img: ladder/0, ladder/1, rope/0 and rope/1 all carry
            // z = "backBody", while walk1/0 and stand1/0 carry z = "body".
            // A pose that says backBody is drawn from behind.
            if (ZNameOf(leaf.Node) == "backBody")
                _poseIsBack = true;

            parts.Add(Build(leaf, ZNameOf(leaf.Node), shiftBy, slots, codes, priority, "body"));
        }
    }

    private void CollectHead(
        string headPath, WzObject head, string stance, int frame,
        Point headAnchor, List<LookPart> parts)
    {
        ulong slots = OwnerSlots(headPath);
        int priority = OwnerPriority(headPath);
        string? codes = OwnerSlotCodes(headPath);
        // Body.cpp:78-92 — the head is ONE shift for the whole sheet: the body's
        // neck minus the head sheet's own neck, and NOT the body's navel.
        Point shiftBy = headAnchor;

        // Body.cpp:80-84 -- the head SHEET is borrowed from stand1/0 for the dead
        // stance, not just its map points. ReadPose above already borrows the
        // map, so the anchors were right while the sprite itself was missing:
        // `dead` came back with a body, a face and hair, and NO HEAD at all,
        // because 00012000.img has no "dead" node for Leaves to walk.
        // Hair.cpp:41-42 does the same borrow, and CollectEquip already does it
        // for head-worn equips — this is the third leg of one rule.
        string sheet = stance;
        if (Pose(head, sheet) is null && stance == "dead") sheet = "stand1";
        if (Pose(head, sheet) is null) return;

        foreach (Leaf leaf in Leaves(headPath, head, sheet, frame))
            parts.Add(Build(leaf, ZNameOf(leaf.Node), shiftBy, slots, codes, priority, "head"));
    }

    // --------------------------------------------------------------- equips

    /// <summary>
    /// One equipped item: every bitmap layer of its pose for this stance and
    /// frame, each placed against the body or head anchor table.
    /// </summary>
    /// <remarks>
    /// <para><b>Every child, not one named layer.</b> A cap is not "the <c>icon</c>
    /// layer": it carries <c>capBelowHead</c>, <c>capOverHair</c>, <c>backCap</c>
    /// and more, each with its own z and its own claims, and picking one of them by
    /// name is how a hat ends up drawn behind the face. The pose's children are the
    /// unit; the zmap already knows what every one of them is.</para>
    ///
    /// <para><b>Placement.</b> <c>Clothing.cpp:160-186</c> takes the LAST vector on
    /// the part's map — <c>parent</c> — looks that name up in the body's anchor
    /// table, and shifts by <c>anchor - parentpos</c>. The loop keeps the last one
    /// because it does not stop at the first, and a layer with no vector at all
    /// shifts by nothing rather than by a guess.</para>
    /// </remarks>
    private void CollectEquip(
        string slotName, EquipSlotDef def, string imgPath, string stance, int frame,
        Dictionary<string, Point> anchors, Point hairAnchor, List<LookPart> parts)
    {
        WzObject? img = _session.TryResolve(imgPath);
        if (img is null) return;

        // Clothing.cpp:145-150 -- the dead stance is a ghost body, and head-worn
        // items ride on the character's own look, so they borrow stand1. Everything
        // the ghost body replaces (a coat, a sword) simply has no dead pose.
        string pose = stance;
        if (Pose(img, pose) is null && pose == "dead" && def.HeadWorn) pose = "stand1";
        if (Pose(img, pose) is null) return;

        ulong slots = OwnerSlots(imgPath);
        int priority = OwnerPriority(imgPath);
        string? codes = OwnerSlotCodes(imgPath);

        foreach (Leaf leaf in Leaves(imgPath, img, pose, frame))
        {
            string zname = ZNameOf(leaf.Node);
            // Clothing.cpp:60 -- a layer whose z names nothing zmap knows lands on
            // the slot's default layer rather than on rank 0, where it would sit
            // behind everything including the body.
            int fallback = def.DefaultZName is { } dz ? _tables?.ZOf(dz, 0) ?? 0 : 0;

            Point shiftBy = default;
            if (slotName.StartsWith("Hair", StringComparison.Ordinal))
            {
                // Hair.cpp:66 -- gethairpos() minus this layer's OWN brow.
                // gethairpos() is head.brow - head.neck + body.neck, which is NOT
                // the same point as the body's "brow" anchor the clothing rule
                // uses, so hair keeps its own rule here rather than being folded
                // into the general one.
                leaf.Map.TryGetValue("brow", out Point brow);
                shiftBy = Sub(hairAnchor, brow);
            }
            else
            {
                foreach ((string key, Point own) in leaf.Map)
                {
                    if (!anchors.TryGetValue(key, out Point anchor)) continue;
                    shiftBy = Sub(anchor, own);
                }
            }

            parts.Add(Build(leaf, zname, shiftBy, slots, codes, priority, $"equip:{slotName}", fallback));
        }
    }

    // --------------------------------------------------------------- face

    private void CollectFace(string facePath, Point faceAnchor, List<LookPart> parts)
    {
        if (PropertiesOf(_session.TryResolve(facePath))?.FindByName("default") is not { } def)
            return;

        string defaultPath = WzPath.Child(facePath, "default");
        Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WzObject child in _session.EnumerateChildren(def))
        {
            if (Resolve(child) is not WzCanvasProperty canvas) continue;
            string name = child.Name ?? "face";
            seen.TryGetValue(name, out int occurrence);
            seen[name] = occurrence + 1;

            string path = WzPath.Child(defaultPath, name, occurrence);
            Leaf leaf = new() { Node = child, Path = path, Map = MapPoints(child) };

            // The face art carries its OWN brow offset, and it is not zero.
            //
            // Face.h:84-95 builds each expression's texture as:
            //
            //     texture  = src["face"];
            //     Point<int16_t> shift = src["face"]["map"]["brow"];
            //     texture.shift(-shift);
            //
            // and Texture::shift is `origin -= amount` (Texture.cpp:126-129), so
            // the shift lands on the ORIGIN. For 00020000.img the archive says
            // map/brow = (-1,-12), so the reference's face origin is (13,8)+(-1,-12)
            // = (12,-4) and it is drawn at faceAnchor - (12,-4) = (-20,-48).
            //
            // This port used the canvas origin raw -- (13,8) -- and drew at
            // (-21,-60): ONE PIXEL too far left and TWELVE PIXELS too high, which
            // is exactly what was reported as "臉的座標不對". Measured, 00020000.img,
            // f1/Face/00020000.img/default/face/map/brow = (-1,-12).
            //
            // Subtracting the brow from the ANCHOR rather than adding it to the
            // origin is the same arithmetic -- `anchor - brow - origin` equals
            // `anchor - (origin + brow)` -- and it is how the sibling hair path
            // already does it (CollectHair, :857), so the two agree by construction.
            Point shiftBy = faceAnchor;
            if (leaf.Map.TryGetValue("brow", out Point brow)) shiftBy = Sub(shiftBy, brow);

            parts.Add(Build(leaf, ZNameOf(child), shiftBy, OwnerSlots(facePath), OwnerSlotCodes(facePath), OwnerPriority(facePath), "face"));
            return;
        }
    }

    // ------------------------------------------------------------- lookups

    /// <summary>
    /// The properties of a node, whether it is an image or a property.
    /// </summary>
    /// <remarks>
    /// Both, deliberately: an NX archive's <c>00002000.img</c> arrives as a
    /// <c>WzImage</c> and a nested pose node as a <c>WzImageProperty</c>, and the
    /// reference addresses both the same way. Matching only one shape returns an
    /// empty look with no error anywhere, which is what a path lookup that
    /// answers 200 with zero parts looks like from the outside.
    /// </remarks>
    private static WzPropertyCollection? PropertiesOf(WzObject? node)
    {
        if (node is WzImage image) { WzSessionService.EnsureParsed(image); return image.WzProperties; }
        if (node is WzImageProperty property) return property.WzProperties;
        return null;
    }

    private static WzObject? Pose(WzObject img, string stance)
        => PropertiesOf(img)?.FindByName(stance);

    private static WzObject? Frame(WzObject? stanceNode, int frame)
        => PropertiesOf(stanceNode)?.FindByName(frame.ToString());

    /// <summary>
    /// How many frames the body sheet carries under an action, counting the
    /// numeric children until one is missing.
    /// </summary>
    /// <remarks>
    /// Stops at the first gap rather than taking the count of numeric children,
    /// because the children are numbered frames and a hole in that run means the
    /// pose ends there. A stance with no frames at all is zero, not one.
    /// </remarks>
    private int CountFrames(WzObject img, string stance)
    {
        if (PropertiesOf(Pose(img, stance)) is not { } props) return 0;
        int count = 0;
        while (props.FindByName(count.ToString()) is not null) count++;
        return count;
    }

    private WzObject? Resolve(WzObject node)
    {
        if (node is WzUOLProperty uol)
        {
            try { return uol.LinkValue; }
            catch { return null; }
        }
        return node;
    }

    /// <summary>AvatarLayers::zname_of — the part's z string, else the part's own name.</summary>
    private string ZNameOf(WzObject part)
    {
        // Same reason as InfoString: the string is the VALUE, not the wrapper.
        // `stand1/0/arm` carries z = "armOverHair" and this used to report "arm",
        // because the test never matched and it fell through to the part's own
        // name. Both are legal layer names, so the wrong one is silent — it
        // renders a body with the arm under the hair instead of over it.
        if (PropertiesOf(Resolve(part))?.FindByName("z")?.WzValue is string z) return z;
        return Resolve(part)?.Name ?? "";
    }

    private Dictionary<string, Point> MapPoints(WzObject part)
    {
        Dictionary<string, Point> result = new(StringComparer.Ordinal);
        if (PropertiesOf(Resolve(part))?.FindByName("map") is not { } map) return result;

        foreach (WzImageProperty point in map.WzProperties)
        {
            if (point is not WzVectorProperty vector) continue;
            result[point.Name ?? ""] = new Point(vector.X?.Value ?? 0, vector.Y?.Value ?? 0);
        }
        return result;
    }

    /// <summary>The owner's <c>info/vslot</c> string, or null when it has no info node.</summary>
    private string? InfoString(string imgPath, string key)
    {
        WzObject? img = _session.TryResolve(imgPath);
        if (PropertiesOf(img)?.FindByName("info") is not { } info) return null;
        // The VALUE carries the string, not the wrapper: `WzValue` is declared
        // `object` and for a WzStringProperty it returns the string inside. An
        // NX image therefore never matches an `is WzStringProperty` test, which is
        // how every vslot and islot in the corpus silently read as absent.
        return PropertiesOf(info)?.FindByName(key)?.WzValue as string;
    }

    private ulong OwnerSlots(string imgPath)
    {
        string? vslot = InfoString(imgPath, "vslot");
        return vslot is null ? 0 : _tables?.Mask(vslot) ?? 0;
    }

    /// <summary>The owner's vslot string itself, for spelling the mask back out.</summary>
    private string? OwnerSlotCodes(string imgPath) => InfoString(imgPath, "vslot");

    private int OwnerPriority(string imgPath)
    {
        string? islot = InfoString(imgPath, "islot");
        return islot is null ? short.MaxValue : _tables?.PriorityOf(islot) ?? short.MaxValue;
    }

    // ---------------------------------------------------------------- build

    private LookPart Build(
        Leaf leaf, string z, Point shiftBy, ulong ownerSlots, string? ownerCodes,
        int ownerPriority, string owner, int zFallback = 0)
    {
        int width = 0, height = 0, originX = 0, originY = 0;
        if (Resolve(leaf.Node) is WzCanvasProperty canvas)
        {
            // One size/origin reader for the whole app. A second one here would
            // be a second thing that can disagree about a canvas's real extent.
            AnimationFrameDto info = AnimationService.DescribeCanvas(canvas, leaf.Node.Name ?? "");
            width = info.Width;
            height = info.Height;
            originX = info.OriginX;
            originY = info.OriginY;
        }

        return new LookPart
        {
            Path = leaf.Path,
            Width = width,
            Height = height,
            OriginX = originX,
            OriginY = originY,
            Shift = shiftBy,
            Z = _tables?.ZOf(z, zFallback) ?? zFallback,
            ZName = z,
            Slots = _tables is null ? 0 : _tables.Claimed(z, ownerSlots),
            SlotCodes = _tables?.ClaimedCodes(z, ownerCodes) ?? "",
            Priority = ownerPriority,
            Owner = owner,
            Part = leaf.Node.Name ?? "",
        };
    }

    // ------------------------------------------------------------ geometry

    /// <summary>
    /// Subtraction that keeps "the other side is missing" distinct from "they are
    /// the same". The C++ has a <c>Point</c> with a null second operand and this
    /// is that: an absent map point is not (0,0).
    /// </summary>
    private static Point Sub(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);

    private static Point Sub(Point a, Point? b) => b is null ? a : new Point(a.X - b.Value.X, a.Y - b.Value.Y);

    private static Point Add(Point a, Point? b) => b is null ? a : new Point(a.X + b.Value.X, a.Y + b.Value.Y);



    /// <summary>Animation.cpp:58-102 — a frame with no <c>delay</c> is 100 ms.</summary>
    private const int DefaultDelayMs = 100;
}